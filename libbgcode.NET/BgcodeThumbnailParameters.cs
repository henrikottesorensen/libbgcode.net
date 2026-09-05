// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace libbgcode.NET;

/// <summary>
/// A thumbnail block's parameters: what image it holds and at which pixel size.
/// </summary>
/// <param name="Format">The image format, as declared. An unknown wire value survives the cast.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
public readonly record struct BgcodeThumbnailParameters(BgcodeThumbnailFormat Format, ushort Width, ushort Height);
