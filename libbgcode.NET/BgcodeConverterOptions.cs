// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace libbgcode.NET;

/// <summary>
/// How <see cref="BgcodeConverter.ToBinary"/> stores each part of a file. The defaults are what
/// PrusaSlicer writes.
/// </summary>
public sealed class BgcodeConverterOptions
{
    /// <summary>Compression of the file metadata block.</summary>
    public BgcodeCompression FileMetadataCompression { get; init; } = BgcodeCompression.None;

    /// <summary>Compression of the printer metadata block; left uncompressed so printers read it cheaply.</summary>
    public BgcodeCompression PrinterMetadataCompression { get; init; } = BgcodeCompression.None;

    /// <summary>Compression of the thumbnail blocks; images are already compressed.</summary>
    public BgcodeCompression ThumbnailCompression { get; init; } = BgcodeCompression.None;

    /// <summary>Compression of the print metadata block.</summary>
    public BgcodeCompression PrintMetadataCompression { get; init; } = BgcodeCompression.Deflate;

    /// <summary>Compression of the slicer metadata blocks.</summary>
    public BgcodeCompression SlicerMetadataCompression { get; init; } = BgcodeCompression.Deflate;

    /// <summary>Compression of the G-code blocks.</summary>
    public BgcodeCompression GCodeCompression { get; init; } = BgcodeCompression.Heatshrink12;

    /// <summary>Encoding of the G-code blocks.</summary>
    public BgcodeGCodeEncoding GCodeEncoding { get; init; } = BgcodeGCodeEncoding.MeatPackWithComments;

    /// <summary>Whether every block gets a CRC-32 trailer.</summary>
    public BgcodeChecksumType ChecksumType { get; init; } = BgcodeChecksumType.Crc32;

    /// <summary>The defaults, used when a conversion is given nothing else.</summary>
    internal static BgcodeConverterOptions Default { get; } = new();
}
