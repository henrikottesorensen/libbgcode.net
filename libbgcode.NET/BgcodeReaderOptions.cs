// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace libbgcode.NET;

/// <summary>
/// What a <see cref="BgcodeReader"/> is willing to spend on a file it does not trust.
/// </summary>
public sealed class BgcodeReaderOptions
{
    /// <summary>
    /// Largest payload <see cref="BgcodeReader.ReadData"/> will hold in memory, checked against
    /// both the stored and the declared-uncompressed size before anything is allocated.
    /// </summary>
    /// <remarks>
    /// Every size in the container is attacker-influenced, so a declared size is a claim to bound,
    /// never a number to allocate from. The default accommodates any real slicer output with two
    /// orders of magnitude to spare; a caller reading only small metadata blocks should lower it
    /// to match what it expects.
    /// </remarks>
    public int MaxDataBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// Whether <see cref="BgcodeReader.ReadData"/> verifies each block's CRC-32 against the stored
    /// trailer, on files that carry one. Off by default: a reader after one metadata block rarely
    /// wants to pay for reading the block twice.
    /// </summary>
    public bool VerifyChecksum { get; init; }

    /// <summary>The defaults, used when <see cref="BgcodeReader.Open"/> is given nothing else.</summary>
    internal static BgcodeReaderOptions Default { get; } = new();
}
