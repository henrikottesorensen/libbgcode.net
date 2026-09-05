// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Text;

namespace MeatPack.NET.Test;

/// <summary>
/// A test-only MeatPack packer, ported from Scott Mudge's BSD reference implementation with the
/// flags binary G-code writers use: no-spaces mode always on, comments either kept (carried raw
/// between packing toggles) or dropped.
/// </summary>
/// <remarks>
/// Living on the test side of the fence is the point: the decoder under test and the packer
/// producing its input must not share code, or a shared misunderstanding passes as agreement.
/// </remarks>
internal static class TestMeatPackPacker
{
    private const byte Signal = 0xFF;

    private const byte EnablePacking = 251;

    private const byte DisablePacking = 250;

    private const byte ResetAll = 249;

    private const byte EnableNoSpaces = 247;

    /// <summary>Packs whole G-code text, line by line, the way a binary G-code writer does.</summary>
    public static byte[] Pack(string text, bool keepComments)
    {
        List<byte> output = [Signal, Signal, EnablePacking, Signal, Signal, EnableNoSpaces];
        bool packing = true;

        foreach (string line in Lines(text))
        {
            PackLine(line, keepComments, output, ref packing);
        }

        output.AddRange([Signal, Signal, ResetAll]);

        return [.. output];
    }

    private static IEnumerable<string> Lines(string text)
    {
        int start = 0;

        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);

            if (end < 0)
            {
                yield return text[start..];

                yield break;
            }

            yield return text[start..(end + 1)];

            start = end + 1;
        }
    }

    private static void PackLine(string line, bool keepComments, List<byte> output, ref bool packing)
    {
        string trimmed = line.TrimStart();

        if (keepComments && trimmed.StartsWith(';'))
        {
            if (packing)
            {
                output.AddRange([Signal, Signal, DisablePacking]);
                packing = false;
            }

            output.AddRange(Encoding.ASCII.GetBytes(line));

            return;
        }

        if (trimmed.Length == 0 || trimmed[0] is ';' or '\n' or '\r' || line.Length < 2)
        {
            return;
        }

        string body = ToPackedForm(line.TrimEnd('\n'));

        if (body.Length == 0)
        {
            return;
        }

        body += "\n";

        List<byte> packed = [];

        for (int i = 0; i < body.Length; i += 2)
        {
            char first = body[i];
            char second = i == body.Length - 1 ? '\n' : body[i + 1];
            bool firstPackable = IsPackable(first);
            bool secondPackable = IsPackable(second);

            if (firstPackable && secondPackable)
            {
                packed.Add(PackPair(first, second));
            }
            else if (firstPackable)
            {
                packed.Add(PackPair(first, '\0'));
                packed.Add((byte)second);
            }
            else if (secondPackable)
            {
                packed.Add(PackPair('\0', second));
                packed.Add((byte)first);
            }
            else
            {
                packed.Add(0xFF);
                packed.Add((byte)first);
                packed.Add((byte)second);
            }
        }

        if (!packing && packed.Count > 0)
        {
            output.AddRange([Signal, Signal, EnablePacking]);
            packing = true;
        }

        output.AddRange(packed);
    }

    /// <summary>
    /// The reference "unified method": a G-command line has its lowercase axis letters raised and
    /// its spaces stripped; every other line is packed as it stands.
    /// </summary>
    private static string ToPackedForm(string line)
    {
        int g = line.IndexOf('G');

        if (g < 0 || g + 1 >= line.Length || !char.IsAsciiDigit(line[g + 1]))
        {
            return line;
        }

        return line.Replace('e', 'E').Replace('x', 'X').Replace('g', 'G').Replace(" ", string.Empty);
    }

    private static bool IsPackable(char c)
    {
        // No-spaces mode: E is packable in the space's table slot, the space itself is not.
        return c is (>= '0' and <= '9') or '.' or '\n' or 'G' or 'X' or 'E';
    }

    private static byte PackPair(char first, char second)
    {
        return (byte)((Value(second) << 4) | Value(first));
    }

    private static int Value(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            '.' => 10,
            'E' => 11,
            '\n' => 12,
            'G' => 13,
            'X' => 14,
            _ => 15,
        };
    }
}
