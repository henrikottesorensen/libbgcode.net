// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace MeatPack.NET;

/// <summary>
/// Encodes G-code text as MeatPack, the way binary G-code writers and Marlin serial hosts do.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scheme is Scott Mudge's MeatPack</b> (BSD-3-Clause; see <c>LICENSE.meatpack</c>), and
/// this encoder follows the reference packer's line treatment exactly: whole-line comments are
/// either dropped or carried verbatim between packing toggles; inline comments are cut off code
/// lines; a <c>G</c>-command line has its lowercase axis letters raised and its spaces stripped,
/// and a serial-protocol <c>*</c> checksum on such a line is recomputed over the stripped text;
/// odd-length lines are padded with a newline. <see cref="MeatPackDecoder"/> documents the
/// reconstruction those choices demand.
/// </para>
/// <para>
/// <b>The packing is lossy on purpose</b> - what survives is equivalent G-code, not the input
/// bytes - so <c>Unpack(Pack(text))</c> is the identity only for canonical text: single spaces
/// before parameter groups, uppercase axis letters, no blank lines, no inline comments.
/// </para>
/// <para>
/// <b>Any text encodes, and encoding never throws.</b> Non-ASCII input rides as UTF-8 bytes in
/// full-width escapes (<c>0xFF</c> cannot occur in UTF-8, so nothing collides with the signal
/// byte), and the output is bounded by a small constant multiple of the input.
/// </para>
/// </remarks>
public static class MeatPackEncoder
{
    private const byte SignalByte = 0xFF;

    private const byte CommandEnablePacking = 251;

    private const byte CommandDisablePacking = 250;

    private const byte CommandResetAll = 249;

    private const byte CommandEnableNoSpaces = 247;

    /// <summary>The MeatPack encoding of <paramref name="text"/>.</summary>
    /// <param name="text">The G-code, lines separated by <c>\n</c>.</param>
    /// <param name="keepComments">
    /// Whether whole-line comments ride along verbatim between packing toggles - binary G-code's
    /// MeatPackComments encoding - or are dropped, its plain MeatPack encoding. Inline comments
    /// on code lines are cut either way, as the reference packer cuts them.
    /// </param>
    /// <param name="omitSpaces">
    /// Whether to use the no-spaces table variant, in which <c>E</c> takes the space's slot and
    /// spaces ride full-width where they survive at all. Every binary G-code writer uses it; it
    /// is a toggle for serial hosts that do not.
    /// </param>
    public static byte[] Pack(string text, bool keepComments = true, bool omitSpaces = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<byte> output = [SignalByte, SignalByte, CommandEnablePacking];

        if (omitSpaces)
        {
            output.AddRange([SignalByte, SignalByte, CommandEnableNoSpaces]);
        }

        bool packing = true;

        foreach (ReadOnlySpan<char> rawLine in EnumerateLinesWithNewline(text))
        {
            PackLine(rawLine, keepComments, omitSpaces, output, ref packing);
        }

        output.AddRange([SignalByte, SignalByte, CommandResetAll]);

        return [.. output];
    }

    private static IEnumerable<string> EnumerateLinesWithNewline(string text)
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

    private static void PackLine(ReadOnlySpan<char> line, bool keepComments, bool omitSpaces, List<byte> output, ref bool packing)
    {
        ReadOnlySpan<char> trimmed = line.TrimStart();

        if (keepComments && trimmed.Length > 0 && trimmed[0] == ';')
        {
            if (packing)
            {
                output.AddRange([SignalByte, SignalByte, CommandDisablePacking]);
                packing = false;
            }

            AppendUtf8(output, line);

            return;
        }

        if (trimmed.Length == 0 || trimmed[0] is ';' or '\n' or '\r' || line.Length < 2)
        {
            return;
        }

        // Inline comments are cut off code lines whatever the comment policy; only whole comment
        // lines are ever carried.
        ReadOnlySpan<char> code = line.TrimEnd('\n');
        int semicolon = code.IndexOf(';');

        if (semicolon >= 0)
        {
            code = code[..semicolon];
        }

        code = code.Trim();

        if (code.Length == 0)
        {
            return;
        }

        string body = ToPackedForm(code.ToString(), omitSpaces) + "\n";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        List<byte> packed = new(bodyBytes.Length);

        for (int i = 0; i < bodyBytes.Length; i += 2)
        {
            byte first = bodyBytes[i];
            byte second = i == bodyBytes.Length - 1 ? (byte)'\n' : bodyBytes[i + 1];
            int firstValue = PackedValue(first, omitSpaces);
            int secondValue = PackedValue(second, omitSpaces);

            if (firstValue >= 0 && secondValue >= 0)
            {
                packed.Add((byte)((secondValue << 4) | firstValue));
            }
            else if (firstValue >= 0)
            {
                packed.Add((byte)(0xF0 | firstValue));
                packed.Add(second);
            }
            else if (secondValue >= 0)
            {
                packed.Add((byte)((secondValue << 4) | 0x0F));
                packed.Add(first);
            }
            else
            {
                packed.Add(0xFF);
                packed.Add(first);
                packed.Add(second);
            }
        }

        if (!packing && packed.Count > 0)
        {
            output.AddRange([SignalByte, SignalByte, CommandEnablePacking]);
            packing = true;
        }

        output.AddRange(packed);
    }

    /// <summary>
    /// The reference "unified method": a G-command line has its lowercase axis letters raised and
    /// its spaces stripped, and a serial-protocol <c>*</c> checksum is recomputed over the result;
    /// every other line packs as it stands.
    /// </summary>
    private static string ToPackedForm(string code, bool omitSpaces)
    {
        int g = code.IndexOf('G');

        if (g < 0 || g + 1 >= code.Length || !char.IsAsciiDigit(code[g + 1]))
        {
            return code;
        }

        string raised = code.Replace('x', 'X').Replace('g', 'G');

        if (omitSpaces)
        {
            raised = raised.Replace('e', 'E');
        }

        string stripped = raised.Replace(" ", string.Empty);

        int asterisk = stripped.IndexOf('*');

        if (asterisk < 0)
        {
            return stripped;
        }

        // The line carried a serial-protocol checksum; stripping the spaces changed what it
        // covers, so it is recomputed the way the wire defines it: XOR over everything before it.
        string payload = stripped[..asterisk].Replace("*", string.Empty);
        int checksum = 0;

        foreach (char c in payload)
        {
            checksum ^= c;
        }

        return payload + "*" + checksum;
    }

    private static int PackedValue(byte b, bool omitSpaces)
    {
        return b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            (byte)'.' => 10,
            (byte)' ' when !omitSpaces => 11,
            (byte)'E' when omitSpaces => 11,
            (byte)'\n' => 12,
            (byte)'G' => 13,
            (byte)'X' => 14,
            _ => -1,
        };
    }

    private static void AppendUtf8(List<byte> output, ReadOnlySpan<char> line)
    {
        Span<byte> buffer = line.Length <= 256 ? stackalloc byte[1024] : new byte[Encoding.UTF8.GetMaxByteCount(line.Length)];
        int written = Encoding.UTF8.GetBytes(line, buffer);

        output.AddRange(buffer[..written]);
    }
}
