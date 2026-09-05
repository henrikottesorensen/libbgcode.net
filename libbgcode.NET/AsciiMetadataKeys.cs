// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace libbgcode.NET;

/// <summary>
/// Which <c>; key = value</c> comment lines of an ASCII G-code file belong to the printer and
/// print metadata blocks, in the order the blocks list them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Established by observation, not by reading the reference converter.</b> A real
/// PrusaSlicer file converted to ASCII and back through Prusa's own <c>pybgcode</c> shows the
/// routing: these keys are gathered from anywhere in the file - the head comments, the print
/// statistics after the G-code, even the config section - into the two blocks in this fixed
/// order, the first occurrence of a key winning; comment lines carrying them leave the G-code;
/// every other comment stays. Keys appearing in both lists land in both blocks. The head comment
/// <c>objects_info</c> is the only key that lives nowhere else, which is how the probe told the
/// head apart from the config section.
/// </para>
/// <para>
/// <b>The order is PrusaSlicer's, not the reference converter's.</b> Readers parse these blocks
/// by key, so order only matters for fidelity - and the producer of nearly every file anyone
/// will read is PrusaSlicer, which writes the printer block in one fixed order, identical across
/// 409 files from three releases (2.9.4 to 2.9.6). The reference converter orders four things
/// differently (<c>max_layer_z</c> before <c>extruder_colour</c>, <c>objects_info</c> last, the
/// <c>filament used</c> group, the wipe-tower line); its choice is only observable through
/// pybgcode and is not what printers see. The print block's order is the same in both.
/// PrusaSlicer also writes the printer keys as the head comments of its ASCII output, which is
/// what firmware reads from a plain <c>.gcode</c> file's first kilobytes.
/// </para>
/// </remarks>
internal static class AsciiMetadataKeys
{
    /// <summary>The printer metadata keys, in the order PrusaSlicer writes the block.</summary>
    public static readonly string[] Printer =
    [
        "printer_model",
        "filament_type",
        "filament_abrasive",
        "nozzle_diameter",
        "nozzle_high_flow",
        "bed_temperature",
        "brim_width",
        "fill_density",
        "layer_height",
        "temperature",
        "ironing",
        "support_material",
        "extruder_colour",
        "max_layer_z",
        "objects_info",
        "filament used [mm]",
        "filament used [g]",
        "filament cost",
        "filament used [cm3]",
        "total filament used for wipe tower [g]",
        "estimated printing time (normal mode)",
        "estimated printing time (silent mode)",
    ];

    /// <summary>The print metadata keys, in block order.</summary>
    public static readonly string[] Print =
    [
        "filament used [mm]",
        "filament used [cm3]",
        "filament used [g]",
        "filament cost",
        "total filament used [g]",
        "total filament cost",
        "total filament used for wipe tower [g]",
        "estimated printing time (normal mode)",
        "estimated first layer printing time (normal mode)",
        "estimated printing time (silent mode)",
        "estimated first layer printing time (silent mode)",
    ];

    /// <summary>Every key either block gathers, for a fast membership test while scanning.</summary>
    public static readonly FrozenSet<string> All = FrozenSet.ToFrozenSet([.. Printer, .. Print], StringComparer.Ordinal);

    /// <summary>The text of a metadata block: the given keys, in order, for the values captured.</summary>
    public static string BlockText(string[] keys, IReadOnlyDictionary<string, string> captured)
    {
        System.Text.StringBuilder text = new();

        foreach (string key in keys)
        {
            if (captured.TryGetValue(key, out string? value))
            {
                text.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return text.ToString();
    }
}
