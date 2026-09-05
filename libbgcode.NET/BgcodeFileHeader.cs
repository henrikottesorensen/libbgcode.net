// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace libbgcode.NET;

/// <summary>
/// The ten-byte file header: magic, version, checksum algorithm.
/// </summary>
/// <param name="Version">The binarisation version the file declares. Version 1 is the current one.</param>
/// <param name="ChecksumType">The per-block checksum algorithm, which also fixes each block's trailer size.</param>
public readonly record struct BgcodeFileHeader(uint Version, BgcodeChecksumType ChecksumType)
{
    /// <summary>The number of bytes a CRC-32 trailer adds to every block, or zero without checksums.</summary>
    public int ChecksumSize => ChecksumType == BgcodeChecksumType.Crc32 ? 4 : 0;
}
