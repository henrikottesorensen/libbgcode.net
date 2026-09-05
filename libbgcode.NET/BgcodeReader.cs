// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

using MeatPack.NET;

namespace libbgcode.NET;

/// <summary>
/// Reads the binary G-code (<c>bgcode</c>) container: the file header, the block walk, and each
/// block's payload on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implemented from the published specification</b> (<c>doc/specifications.md</c> in Prusa's
/// <c>libbgcode</c> repository), which fixes the header layout, the block ordering, the
/// 8-versus-12-byte block header rule and every wire constant. The two facts the specification
/// omits - deflate being zlib-wrapped, and what the CRC-32 covers - are established from real
/// PrusaSlicer output and pinned by the interop tests.
/// </para>
/// <para>
/// <b>Untrusted input.</b> Every size on the wire is attacker-influenced, so nothing is allocated
/// from a declared size without a bound and nothing is trusted to end where it claims to. A
/// malformed file yields <c>null</c> from whichever call discovered it, never an exception; the
/// only exceptions thrown are argument checks on the caller's own inputs.
/// </para>
/// <para>
/// <b>Blocks are descriptors.</b> <see cref="NextBlock"/> reads headers and parameters only,
/// seeking past payloads, so walking a file's structure costs a few reads per block regardless of
/// size. How many blocks to visit is the caller's policy - the walk itself cannot be made to spin,
/// because every step advances the stream position past a validated block.
/// </para>
/// </remarks>
public sealed class BgcodeReader
{
    private const uint SupportedVersion = 1;

    /// <summary>
    /// Block parameters are two bytes of encoding for every block except a thumbnail, which
    /// carries format, width and height.
    /// </summary>
    private const int MetadataParametersSize = 2;

    private const int ThumbnailParametersSize = 6;

    private readonly Stream _stream;

    private readonly BgcodeReaderOptions _options;

    private long _nextBlockPosition;

    private bool _failed;

    private BgcodeReader(Stream stream, BgcodeFileHeader fileHeader, BgcodeReaderOptions options)
    {
        _stream = stream;
        _options = options;
        FileHeader = fileHeader;
        _nextBlockPosition = HeaderSize;
    }

    /// <summary><c>GCDE</c>, the four bytes a binary G-code file starts with.</summary>
    /// <remarks>
    /// <b>The file name does not decide this and must not be consulted.</b> PrusaSlicer honours
    /// the printer profile's <c>binary_gcode</c> setting and not the name it was asked to write,
    /// so a file called <c>.gcode</c> is routinely binary.
    /// </remarks>
    public static ReadOnlySpan<byte> Magic => "GCDE"u8;

    /// <summary>The size in bytes of the file header, which is where the first block starts.</summary>
    public static int HeaderSize => 10;

    /// <summary>The file header the stream declared.</summary>
    public BgcodeFileHeader FileHeader { get; }

    /// <summary>
    /// Whether the walk reached the end of the file cleanly. Distinguishes the two ways
    /// <see cref="NextBlock"/> answers null: after the last block, or on a malformed one.
    /// </summary>
    public bool AtEnd { get; private set; }

    /// <summary>
    /// A reader positioned before the first block, or null if the stream does not start with a
    /// readable binary G-code header.
    /// </summary>
    /// <remarks>
    /// A version this does not know is refused rather than read optimistically: an unreadable file
    /// makes no claims, where a misread one makes wrong ones. An unknown checksum algorithm is
    /// refused for a harder reason - its width is what every block offset is computed from, so the
    /// walk could not continue past the first block anyway.
    /// </remarks>
    /// <param name="stream">Positioned anywhere; the reader seeks. Must be seekable and readable.</param>
    /// <param name="options">Bounds and verification policy; the defaults when omitted.</param>
    public static BgcodeReader? Open(Stream stream, BgcodeReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The reader walks blocks by seeking.", nameof(stream));
        }

        try
        {
            if (stream.Length < HeaderSize)
            {
                return null;
            }

            Span<byte> header = stackalloc byte[HeaderSize];

            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(header);

            if (!header[..4].SequenceEqual(Magic))
            {
                return null;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != SupportedVersion)
            {
                return null;
            }

            ushort checksumType = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]);

            if (checksumType is not (0 or 1))
            {
                return null;
            }

            BgcodeFileHeader fileHeader = new(SupportedVersion, (BgcodeChecksumType)checksumType);

            return new BgcodeReader(stream, fileHeader, options ?? BgcodeReaderOptions.Default);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The next block's descriptor, or null - at the end of the file with <see cref="AtEnd"/> set,
    /// or on a malformed block with it unset. After a malformed block the walk stays refused:
    /// every later offset would be computed from the lie.
    /// </summary>
    /// <remarks>
    /// A block of an unknown type is malformed for the walk's purposes even though the block
    /// itself may be fine: its parameter size is unknown, and the parameter size is part of where
    /// the next block starts. An unknown <em>compression</em> does not stop the walk - the header
    /// still declares the stored size - and only refuses <see cref="ReadData"/>.
    /// </remarks>
    public BgcodeBlock? NextBlock()
    {
        if (_failed || AtEnd)
        {
            return null;
        }

        try
        {
            long remaining = _stream.Length - _nextBlockPosition;

            if (remaining == 0)
            {
                AtEnd = true;

                return null;
            }

            Span<byte> header = stackalloc byte[12];
            long headerPosition = _nextBlockPosition;

            if (remaining < 8)
            {
                return Refuse();
            }

            _stream.Seek(_nextBlockPosition, SeekOrigin.Begin);
            _stream.ReadExactly(header[..8]);

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header[..2]);
            ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(header[2..4]);
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            uint dataSize = uncompressedSize;
            int headerSize = 8;

            if (compression != (ushort)BgcodeCompression.None)
            {
                if (remaining < 12)
                {
                    return Refuse();
                }

                _stream.ReadExactly(header[8..12]);
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
                headerSize = 12;
            }

            if (type > (ushort)BgcodeBlockType.Thumbnail)
            {
                return Refuse();
            }

            BgcodeBlockType blockType = (BgcodeBlockType)type;
            int parametersSize = blockType == BgcodeBlockType.Thumbnail ? ThumbnailParametersSize : MetadataParametersSize;

            // The whole block - parameters, payload and trailer - must fit in the file before
            // anything more is read. A block extending past the end is truncation whether the
            // file was cut short or the size is a lie, and a seek target computed from an
            // unchecked size can itself throw on an array-backed stream.
            if (parametersSize + (long)dataSize + FileHeader.ChecksumSize > remaining - headerSize)
            {
                return Refuse();
            }

            Span<byte> parameters = stackalloc byte[ThumbnailParametersSize];

            _stream.ReadExactly(parameters[..parametersSize]);

            ushort rawEncoding = BinaryPrimitives.ReadUInt16LittleEndian(parameters[..2]);
            BgcodeThumbnailParameters? thumbnail = null;

            if (blockType == BgcodeBlockType.Thumbnail)
            {
                thumbnail = new BgcodeThumbnailParameters((BgcodeThumbnailFormat)rawEncoding,
                                                          BinaryPrimitives.ReadUInt16LittleEndian(parameters[2..4]),
                                                          BinaryPrimitives.ReadUInt16LittleEndian(parameters[4..6]));
            }

            long dataPosition = headerPosition + headerSize + parametersSize;

            _nextBlockPosition = dataPosition + dataSize + FileHeader.ChecksumSize;

            return new BgcodeBlock(blockType,
                                   (BgcodeCompression)compression,
                                   uncompressedSize,
                                   dataSize,
                                   rawEncoding,
                                   thumbnail,
                                   headerPosition,
                                   dataPosition);
        }
        catch (EndOfStreamException)
        {
            return Refuse();
        }
        catch (IOException)
        {
            return Refuse();
        }
    }

    /// <summary>
    /// A block's payload, decompressed, or null if it cannot be produced: a size over the
    /// configured bound, a compression this cannot decode, a payload that does not decompress to
    /// exactly its declared size, or - when verification is on - a checksum that does not match.
    /// </summary>
    /// <remarks>
    /// The declared size is the bound on the output, not a hint: deflate expands up to ~1000:1, so
    /// a stream read to its natural end would let a small upload allocate a gigabyte. A payload
    /// that stops short of its declaration or keeps going past it has lied about a size, and a lie
    /// about a size is malformed.
    /// </remarks>
    /// <param name="block">A descriptor this reader produced.</param>
    public byte[]? ReadData(BgcodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (block.UncompressedSize > _options.MaxDataBytes || block.DataSize > _options.MaxDataBytes)
        {
            return null;
        }

        try
        {
            if (_options.VerifyChecksum && !ChecksumMatches(block))
            {
                return null;
            }

            byte[] data = new byte[block.DataSize];

            _stream.Seek(block.DataPosition, SeekOrigin.Begin);
            _stream.ReadExactly(data);

            return block.Compression switch
            {
                BgcodeCompression.None => data,
                BgcodeCompression.Deflate => Inflate(data, block.UncompressedSize),
                BgcodeCompression.Heatshrink11 => HeatshrinkBlock.Decode(data, block.UncompressedSize, windowBits: 11, lookaheadBits: 4),
                BgcodeCompression.Heatshrink12 => HeatshrinkBlock.Decode(data, block.UncompressedSize, windowBits: 12, lookaheadBits: 4),
                _ => null,
            };
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            // Deflate refusing a payload that claimed to be one.
            return null;
        }
        catch (IOException)
        {
            // The inflater's other refusal: zlib error codes .NET maps to an internal
            // ZLibException rather than InvalidDataException - a header demanding a preset
            // dictionary, for one. Its only public ancestor is IOException, which fits the
            // contract regardless of what threw it: a payload this cannot read answers null,
            // never an exception.
            return null;
        }
    }

    /// <summary>
    /// A block's payload as text, or null if the block does not carry text this can decode: a
    /// thumbnail, an encoding this does not know, or a payload <see cref="ReadData"/> refuses.
    /// </summary>
    /// <remarks>
    /// For the four metadata block types this is the INI or JSON text as stored - which of the
    /// two is <see cref="BgcodeBlock.MetadataEncoding"/>'s answer, and matters for slicer
    /// metadata, which PrusaSlicer 3 writes as two blocks, one of each. For a G-code block it is
    /// the G-code, MeatPack-decoded when the block says so - which reconstructs what the packing
    /// discarded, so the text is equivalent G-code rather than the slicer's original bytes.
    /// </remarks>
    /// <param name="block">A descriptor this reader produced.</param>
    public string? ReadText(BgcodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (block.MetadataEncoding is BgcodeMetadataEncoding.Ini or BgcodeMetadataEncoding.Json)
        {
            byte[]? data = ReadData(block);

            return data is null ? null : Encoding.UTF8.GetString(data);
        }

        switch (block.GCodeEncoding)
        {
            case BgcodeGCodeEncoding.None:
            {
                byte[]? data = ReadData(block);

                return data is null ? null : Encoding.UTF8.GetString(data);
            }

            case BgcodeGCodeEncoding.MeatPack:
            case BgcodeGCodeEncoding.MeatPackWithComments:
            {
                byte[]? data = ReadData(block);

                return data is null ? null : Encoding.UTF8.GetString(MeatPackDecoder.Unpack(data));
            }

            default:
                return null;
        }
    }

    private static byte[]? Inflate(byte[] data, uint uncompressedSize)
    {
        // zlib-wrapped, not raw: the payload carries the two-byte zlib header. Established from
        // real PrusaSlicer output; the specification says only "deflate".
        using MemoryStream compressed = new(data);
        using ZLibStream decompressor = new(compressed, CompressionMode.Decompress);

        byte[] plain = new byte[uncompressedSize];
        int read = decompressor.ReadAtLeast(plain, plain.Length, throwOnEndOfStream: false);

        if (read < plain.Length || decompressor.ReadByte() != -1)
        {
            return null;
        }

        return plain;
    }

    private bool ChecksumMatches(BgcodeBlock block)
    {
        if (FileHeader.ChecksumType != BgcodeChecksumType.Crc32)
        {
            return true;
        }

        // The checksum covers the block from its header through its data, as stored. Streamed in
        // chunks so verification never allocates proportionally to the block.
        Crc32 crc = new();
        long remaining = (block.DataPosition - block.HeaderPosition) + block.DataSize;
        byte[] buffer = new byte[64 * 1024];

        _stream.Seek(block.HeaderPosition, SeekOrigin.Begin);

        while (remaining > 0)
        {
            int read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

            if (read == 0)
            {
                return false;
            }

            crc.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }

        Span<byte> stored = stackalloc byte[4];

        _stream.ReadExactly(stored);

        return BinaryPrimitives.ReadUInt32LittleEndian(stored) == crc.GetCurrentHashAsUInt32();
    }

    private BgcodeBlock? Refuse()
    {
        _failed = true;

        return null;
    }
}
