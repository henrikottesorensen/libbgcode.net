using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

using MeatPack.NET;

namespace libbgcode.NET;

/// <summary>
/// Writes the binary G-code (<c>bgcode</c>) container: the file header, then blocks in the order
/// the specification fixes, each compressed, encoded and checksummed as asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writer will not produce a file the reference reader refuses.</b> Blocks must come in
/// the specification's order - file metadata, printer metadata, thumbnails, print metadata,
/// slicer metadata, G-code - and the reference reader treats printer, print and slicer metadata as
/// mandatory before the G-code, so the writer refuses to skip them. Slicer metadata may be written
/// twice, INI and then JSON, which is how PrusaSlicer 3 writes it. A call out of order throws
/// <see cref="InvalidOperationException"/>; nothing is written by a call that throws.
/// </para>
/// <para>
/// <b>G-code is chunked the way the reference binarizer chunks it</b>: whole lines accumulate
/// until the next one would push a block past 64 KiB of source text, a single longer line becomes
/// a block of its own, and every block gets fresh MeatPack state, so each decodes standalone.
/// </para>
/// <para>
/// <b>The inputs are the caller's own data</b>, so unlike <see cref="BgcodeReader"/> this throws
/// on mistakes rather than answering null. Stream failures propagate as the stream throws them.
/// </para>
/// </remarks>
public sealed class BgcodeWriter : IDisposable
{
    /// <summary>
    /// The reference binarizer's G-code block cut: source text accumulates up to this many bytes
    /// per block, on line boundaries.
    /// </summary>
    public const int GCodeBlockSourceBytes = 64 * 1024;

    private readonly Stream _stream;

    private readonly bool _leaveOpen;

    private Stage _stage = Stage.Header;

    private int _slicerMetadataBlocks;

    private bool _disposed;

    private enum Stage
    {
        Header,
        FileMetadata,
        PrinterMetadata,
        Thumbnails,
        PrintMetadata,
        SlicerMetadata,
        GCode,
    }

    /// <summary>Starts a file: the header is written at once, so the stream is positioned for blocks.</summary>
    /// <param name="stream">Where the file goes. Written forward only; never read or sought.</param>
    /// <param name="checksumType">Whether every block gets a CRC-32 trailer. Slicers write one.</param>
    /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
    public BgcodeWriter(Stream stream, BgcodeChecksumType checksumType = BgcodeChecksumType.Crc32, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("The writer needs a writable stream.", nameof(stream));
        }

        if (checksumType is not (BgcodeChecksumType.None or BgcodeChecksumType.Crc32))
        {
            throw new ArgumentOutOfRangeException(nameof(checksumType), checksumType, "Unknown checksum type.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        FileHeader = new BgcodeFileHeader(1, checksumType);

        Span<byte> header = stackalloc byte[BgcodeReader.HeaderSize];

        BgcodeReader.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], FileHeader.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], (ushort)checksumType);
        _stream.Write(header);
    }

    /// <summary>The header this writer put at the start of the stream.</summary>
    public BgcodeFileHeader FileHeader { get; }

    /// <summary>Writes the optional file metadata block. Must come first if it comes at all.</summary>
    /// <param name="ini">INI text: one <c>key=value</c> pair per line.</param>
    /// <param name="compression">How to store it. Slicers leave this block uncompressed.</param>
    public void WriteFileMetadata(string ini, BgcodeCompression compression = BgcodeCompression.None)
    {
        ArgumentNullException.ThrowIfNull(ini);
        Advance(Stage.FileMetadata, "file metadata comes first, before the printer metadata");
        WriteMetadataBlock(BgcodeBlockType.FileMetadata, BgcodeMetadataEncoding.Ini, ini, compression);
    }

    /// <summary>Writes the printer metadata block - mandatory, and the first thing a printer reads.</summary>
    /// <param name="ini">INI text: one <c>key=value</c> pair per line.</param>
    /// <param name="compression">How to store it. Slicers leave this block uncompressed so printers can read it cheaply.</param>
    public void WritePrinterMetadata(string ini, BgcodeCompression compression = BgcodeCompression.None)
    {
        ArgumentNullException.ThrowIfNull(ini);
        Advance(Stage.PrinterMetadata, "printer metadata follows the file metadata and precedes everything else");
        WriteMetadataBlock(BgcodeBlockType.PrinterMetadata, BgcodeMetadataEncoding.Ini, ini, compression);
    }

    /// <summary>Writes one thumbnail block. Any number may follow the printer metadata.</summary>
    /// <param name="parameters">Image format and pixel size.</param>
    /// <param name="image">The image bytes, as the format encodes them.</param>
    /// <param name="compression">How to store it. Images are already compressed; slicers leave this at none.</param>
    public void WriteThumbnail(BgcodeThumbnailParameters parameters, ReadOnlySpan<byte> image, BgcodeCompression compression = BgcodeCompression.None)
    {
        Advance(Stage.Thumbnails, "thumbnails follow the printer metadata and precede the print metadata");

        Span<byte> blockParameters = stackalloc byte[6];

        BinaryPrimitives.WriteUInt16LittleEndian(blockParameters[..2], (ushort)parameters.Format);
        BinaryPrimitives.WriteUInt16LittleEndian(blockParameters[2..4], parameters.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(blockParameters[4..6], parameters.Height);
        WriteBlock(BgcodeBlockType.Thumbnail, compression, blockParameters, image);
    }

    /// <summary>Writes the print metadata block - mandatory, after any thumbnails.</summary>
    /// <param name="ini">INI text: one <c>key=value</c> pair per line.</param>
    /// <param name="compression">How to store it. Slicers deflate this block.</param>
    public void WritePrintMetadata(string ini, BgcodeCompression compression = BgcodeCompression.Deflate)
    {
        ArgumentNullException.ThrowIfNull(ini);
        Advance(Stage.PrintMetadata, "print metadata follows the thumbnails and precedes the slicer metadata");
        WriteMetadataBlock(BgcodeBlockType.PrintMetadata, BgcodeMetadataEncoding.Ini, ini, compression);
    }

    /// <summary>
    /// Writes a slicer metadata block - mandatory, at most two: an INI block, optionally followed
    /// by a JSON one, which is how PrusaSlicer 3 writes both its legacy and its current form.
    /// </summary>
    /// <param name="text">The metadata in the given encoding.</param>
    /// <param name="encoding">INI or JSON.</param>
    /// <param name="compression">How to store it. Slicers deflate this block.</param>
    public void WriteSlicerMetadata(string text,
                                    BgcodeMetadataEncoding encoding = BgcodeMetadataEncoding.Ini,
                                    BgcodeCompression compression = BgcodeCompression.Deflate)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (encoding is not (BgcodeMetadataEncoding.Ini or BgcodeMetadataEncoding.Json))
        {
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown metadata encoding.");
        }

        if (_slicerMetadataBlocks >= 2)
        {
            throw new InvalidOperationException("At most two slicer metadata blocks are written: INI, then JSON.");
        }

        if (_slicerMetadataBlocks == 1 && encoding == BgcodeMetadataEncoding.Ini)
        {
            throw new InvalidOperationException("A second slicer metadata block must be the JSON one.");
        }

        Advance(Stage.SlicerMetadata, "slicer metadata follows the print metadata and precedes the G-code");
        WriteMetadataBlock(BgcodeBlockType.SlicerMetadata, encoding, text, compression);
        _slicerMetadataBlocks++;
    }

    /// <summary>
    /// Writes G-code, as one block per 64 KiB of source text cut on line boundaries. May be called
    /// repeatedly; each call starts a new block.
    /// </summary>
    /// <param name="gcode">The G-code text, lines separated by <c>\n</c>.</param>
    /// <param name="encoding">Plain text, or MeatPack with comments dropped or kept. Slicers keep them.</param>
    /// <param name="compression">How to store it. Slicers use heatshrink 12/4.</param>
    public void WriteGCode(string gcode,
                           BgcodeGCodeEncoding encoding = BgcodeGCodeEncoding.MeatPackWithComments,
                           BgcodeCompression compression = BgcodeCompression.Heatshrink12)
    {
        ArgumentNullException.ThrowIfNull(gcode);

        if (encoding is not (BgcodeGCodeEncoding.None or BgcodeGCodeEncoding.MeatPack or BgcodeGCodeEncoding.MeatPackWithComments))
        {
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown G-code encoding.");
        }

        Advance(Stage.GCode, "G-code comes last, after the slicer metadata");

        Span<byte> parameters = stackalloc byte[2];

        BinaryPrimitives.WriteUInt16LittleEndian(parameters, (ushort)encoding);

        foreach (string chunk in ChunkOnLines(gcode))
        {
            byte[] payload = encoding switch
            {
                BgcodeGCodeEncoding.None => Encoding.UTF8.GetBytes(chunk),
                BgcodeGCodeEncoding.MeatPack => MeatPackEncoder.Pack(chunk, keepComments: false),
                _ => MeatPackEncoder.Pack(chunk, keepComments: true),
            };

            WriteBlock(BgcodeBlockType.GCode, compression, parameters, payload);
        }
    }

    /// <summary>Flushes, and closes the stream unless asked to leave it open.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Flush();

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>
    /// The reference binarizer's cut: lines accumulate until the next would push the block past
    /// the limit, and a line longer than the limit on its own becomes its own block.
    /// </summary>
    internal static IEnumerable<string> ChunkOnLines(string gcode)
    {
        StringBuilder block = new();
        int blockBytes = 0;
        int start = 0;

        while (start < gcode.Length)
        {
            int end = gcode.IndexOf('\n', start);
            string line = end < 0 ? gcode[start..] : gcode[start..(end + 1)];
            int lineBytes = Encoding.UTF8.GetByteCount(line);

            start = end < 0 ? gcode.Length : end + 1;

            if (blockBytes + lineBytes > GCodeBlockSourceBytes && blockBytes > 0)
            {
                yield return block.ToString();

                block.Clear();
                blockBytes = 0;
            }

            if (lineBytes > GCodeBlockSourceBytes)
            {
                yield return line;

                continue;
            }

            block.Append(line);
            blockBytes += lineBytes;
        }

        if (blockBytes > 0)
        {
            yield return block.ToString();
        }
    }

    private void Advance(Stage target, string rule)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Two kinds of step: the repeatable stages (thumbnails, slicer metadata, G-code) accept
        // another block at the same stage; every stage refuses to be revisited from later on, and
        // the mandatory ones refuse to be skipped.
        bool repeatable = target is Stage.Thumbnails or Stage.SlicerMetadata or Stage.GCode;

        if (target < _stage || (target == _stage && !repeatable))
        {
            throw new InvalidOperationException($"Out of order: {rule}.");
        }

        if (target > Stage.PrinterMetadata && _stage < Stage.PrinterMetadata)
        {
            throw new InvalidOperationException("The printer metadata block is mandatory and must come before this one.");
        }

        if (target > Stage.PrintMetadata && _stage < Stage.PrintMetadata)
        {
            throw new InvalidOperationException("The print metadata block is mandatory and must come before this one.");
        }

        if (target > Stage.SlicerMetadata && _stage < Stage.SlicerMetadata)
        {
            throw new InvalidOperationException("A slicer metadata block is mandatory and must come before the G-code.");
        }

        _stage = target;
    }

    private void WriteMetadataBlock(BgcodeBlockType type, BgcodeMetadataEncoding encoding, string text, BgcodeCompression compression)
    {
        Span<byte> parameters = stackalloc byte[2];

        BinaryPrimitives.WriteUInt16LittleEndian(parameters, (ushort)encoding);
        WriteBlock(type, compression, parameters, Encoding.UTF8.GetBytes(text));
    }

    private void WriteBlock(BgcodeBlockType type, BgcodeCompression compression, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> payload)
    {
        byte[] stored = compression switch
        {
            BgcodeCompression.None => payload.ToArray(),
            BgcodeCompression.Deflate => ZLibCompress(payload),
            BgcodeCompression.Heatshrink11 => HeatshrinkBlock.Encode(payload, windowBits: 11, lookaheadBits: 4),
            BgcodeCompression.Heatshrink12 => HeatshrinkBlock.Encode(payload, windowBits: 12, lookaheadBits: 4),
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unknown compression."),
        };

        int headerSize = compression == BgcodeCompression.None ? 8 : 12;
        byte[] block = new byte[headerSize + parameters.Length + stored.Length];
        Span<byte> header = block.AsSpan(0, headerSize);

        BinaryPrimitives.WriteUInt16LittleEndian(header[..2], (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..4], (ushort)compression);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)payload.Length);

        if (compression != BgcodeCompression.None)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], (uint)stored.Length);
        }

        parameters.CopyTo(block.AsSpan(headerSize));
        stored.CopyTo(block.AsSpan(headerSize + parameters.Length));
        _stream.Write(block);

        if (FileHeader.ChecksumType == BgcodeChecksumType.Crc32)
        {
            // The checksum covers the block from its header through its stored data.
            Span<byte> crc = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.HashToUInt32(block));
            _stream.Write(crc);
        }
    }

    private static byte[] ZLibCompress(ReadOnlySpan<byte> payload)
    {
        // zlib-wrapped, matching what the reference reader inflates.
        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(payload);
        }

        return output.ToArray();
    }
}
