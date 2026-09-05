// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace libbgcode.NET;

/// <summary>
/// How a G-code block's payload is encoded, with the specification's wire values.
/// </summary>
public enum BgcodeGCodeEncoding
{
    /// <summary>Plain UTF-8 text.</summary>
    None = 0,

    /// <summary>MeatPack-packed text; comment lines were dropped by the writer.</summary>
    MeatPack = 1,

    /// <summary>
    /// MeatPack-packed text with comment lines kept, carried verbatim between packing toggles.
    /// The decoding is identical to <see cref="MeatPack"/> - the value records what the writer
    /// chose to pack, not a different scheme.
    /// </summary>
    MeatPackWithComments = 2,
}
