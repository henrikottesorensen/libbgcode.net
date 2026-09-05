// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace libbgcode.NET;

/// <summary>
/// The image format of a thumbnail block, with the specification's wire values.
/// </summary>
public enum BgcodeThumbnailFormat
{
    /// <summary>PNG.</summary>
    Png = 0,

    /// <summary>JPEG.</summary>
    Jpg = 1,

    /// <summary>QOI, the "Quite OK Image" format.</summary>
    Qoi = 2,
}
