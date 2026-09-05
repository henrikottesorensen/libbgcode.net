// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.IO.Hashing;

using HeatshrinkDotNet;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// Builds well-formed binary G-code files byte by byte, so hostile variants are one deliberate
/// lie away from a file that verifiably reads.
/// </summary>
internal static class TestBgcode
{
    /// <summary>The ten-byte file header: magic, version, checksum type. Little-endian throughout.</summary>
    public static byte[] FileHeader(uint version = 1, ushort checksumType = 0)
    {
        byte[] header = new byte[10];

        "GCDE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), checksumType);

        return header;
    }

    /// <summary>A block header: 8 bytes uncompressed, 12 with a compressed size.</summary>
    public static byte[] BlockHeader(ushort type, ushort compression, uint uncompressedSize, uint? compressedSize)
    {
        byte[] header = new byte[compressedSize is null ? 8 : 12];

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), uncompressedSize);

        if (compressedSize is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), compressedSize.Value);
        }

        return header;
    }

    /// <summary>
    /// A whole file holding one printer metadata block with the given stored payload and declared
    /// sizes, the given encoding parameter (INI unless said otherwise), and - when the checksum
    /// type says so - a correct CRC-32.
    /// </summary>
    public static byte[] MetadataBlockFile(ushort compression,
                                           byte[] storedPayload,
                                           uint declaredUncompressedSize,
                                           ushort checksumType = 0,
                                           ushort encoding = 0)
    {
        byte[] blockHeader = BlockHeader(type: 3,
                                         compression,
                                         declaredUncompressedSize,
                                         compression == 0 ? null : (uint)storedPayload.Length);
        byte[] block = [.. blockHeader, (byte)(encoding & 0xFF), (byte)(encoding >> 8), .. storedPayload];

        if (checksumType == 1)
        {
            byte[] crc = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.HashToUInt32(block));

            return [.. FileHeader(checksumType: 1), .. block, .. crc];
        }

        return [.. FileHeader(), .. block];
    }

    /// <summary>One payload compressed as a raw heatshrink stream, the way a block stores it.</summary>
    public static byte[] HeatshrinkCompress(byte[] payload, int windowBits, int lookaheadBits)
    {
        HeatshrinkEncoder encoder = new(windowBits, lookaheadBits);

        using System.IO.MemoryStream output = new();

        byte[] chunk = new byte[4096];
        int sunk = 0;
        bool finished = false;

        while (!finished)
        {
            if (sunk < payload.Length)
            {
                encoder.Sink(payload, sunk, payload.Length - sunk, out int count);
                sunk += count;
            }

            EncoderPollResult poll;

            do
            {
                poll = encoder.Poll(chunk, out int polled);
                output.Write(chunk, 0, polled);
            }
            while (poll == EncoderPollResult.More);

            if (sunk == payload.Length)
            {
                finished = encoder.Finish() == EncoderFinishResult.Done;
            }
        }

        return output.ToArray();
    }
}
