// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Linq;
using System.Text;

using AwesomeAssertions;

using CsCheck;

using Xunit;

namespace MeatPack.NET.Test;

/// <summary>
/// The production encoder against two independent judges: byte-for-byte agreement with the
/// test-side reference packer, and round-trips through the decoder.
/// </summary>
public class MeatPackEncoderTests
{
    private static readonly Gen<string> GenNumber =
        Gen.Select(Gen.Int[0, 999], Gen.Int[-1, 99],
                   (whole, fraction) => fraction < 0 ? $"{whole}" : $"{whole}.{fraction}");

    private static readonly Gen<string> GenGLine =
        Gen.Select(Gen.OneOfConst("G0", "G1", "G28", "G29"),
                   Gen.Char["XYZEF"].Select(GenNumber, (letter, number) => $" {letter}{number}").Array[1, 4],
                   (command, parameters) => command + string.Concat(parameters));

    private static readonly Gen<string> GenMLine =
        Gen.Select(Gen.Int[0, 999],
                   Gen.Char["SPRT"].Select(GenNumber, (letter, number) => $" {letter}{number}").Array[0, 3],
                   (code, parameters) => $"M{code}" + string.Concat(parameters));

    private static readonly Gen<string> GenComment =
        Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789 _"].Array[0, 24]
           .Select(chars => ("; " + new string(chars)).TrimEnd());

    private static readonly Gen<string> GenText =
        Gen.OneOf(GenGLine, GenMLine, GenComment).Array[1, 40]
           .Select(lines => string.Concat(lines.Select(line => line + "\n")));

    /// <summary>
    /// On canonical text the production encoder and the independently ported reference packer
    /// must agree to the byte - the strongest claim two implementations that share no code can
    /// make about each other.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchesTheReferencePackerByteForByte(bool keepComments)
    {
        GenText.Sample(text =>
        {
            byte[] produced = MeatPackEncoder.Pack(text, keepComments);
            byte[] reference = TestMeatPackPacker.Pack(text, keepComments);

            produced.Should().Equal(reference);
        }, iter: 500);
    }

    /// <summary>Canonical text survives the whole round trip through the production pair.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTripsThroughTheDecoder(bool keepComments)
    {
        GenText.Sample(text =>
        {
            string expected = keepComments
                ? text
                : string.Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Where(line => !line.StartsWith(';'))
                                    .Select(line => line + "\n"));

            Unpack(MeatPackEncoder.Pack(text, keepComments)).Should().Be(expected);
        }, iter: 500);
    }

    /// <summary>
    /// A serial-protocol checksum is recomputed after the spaces it covered are stripped -
    /// carrying the old value would hand the printer a line that fails its own check.
    /// </summary>
    [Fact]
    public void RecomputesTheSerialChecksumOfAStrippedGLine()
    {
        // "G1X10" XORs to 47, whatever the checksum in the input claimed.
        Unpack(MeatPackEncoder.Pack("G1 X10*30\n")).Should().Be("G1 X10*47\n");
    }

    /// <summary>Inline comments are cut off code lines, whichever whole-line comment policy runs.</summary>
    [Fact]
    public void CutsInlineCommentsOffCodeLines()
    {
        Unpack(MeatPackEncoder.Pack("G1 X5 ; the move\n", keepComments: true)).Should().Be("G1 X5\n");
    }

    /// <summary>Without no-spaces mode the space packs in its own table slot and E rides full-width.</summary>
    [Fact]
    public void PacksSpacesWhenNoSpacesModeIsOff()
    {
        byte[] packed = MeatPackEncoder.Pack("M104 S200\n", omitSpaces: false);

        ContainsCommand(packed, 247).Should().BeFalse("no-spaces mode was not requested");
        Unpack(packed).Should().Be("M104 S200\n");
    }

    /// <summary>Dropping comments drops whole comment lines and nothing else.</summary>
    [Fact]
    public void DropsWholeCommentLinesWhenAsked()
    {
        Unpack(MeatPackEncoder.Pack("; setup\nG1 X5\n; teardown\n", keepComments: false)).Should().Be("G1 X5\n");
    }

    /// <summary>A comment that is not ASCII rides as UTF-8 bytes and comes back intact.</summary>
    [Fact]
    public void CarriesNonAsciiCommentsIntact()
    {
        Unpack(MeatPackEncoder.Pack("; æøå smørrebrød\nG1 X5\n")).Should().Be("; æøå smørrebrød\nG1 X5\n");
    }

    /// <summary>Whatever the string, encoding answers with bytes and never throws.</summary>
    [Fact]
    public void AnyTextEncodes()
    {
        Gen.String[0, 200].Sample(text =>
        {
            MeatPackEncoder.Pack(text).Should().NotBeNull();
            MeatPackDecoder.Unpack(MeatPackEncoder.Pack(text)).Should().NotBeNull();
        }, iter: 300);
    }

    private static string Unpack(byte[] packed)
    {
        return Encoding.UTF8.GetString(MeatPackDecoder.Unpack(packed));
    }

    private static bool ContainsCommand(byte[] packed, byte command)
    {
        for (int i = 0; i + 2 < packed.Length; i++)
        {
            if (packed[i] == 0xFF && packed[i + 1] == 0xFF && packed[i + 2] == command)
            {
                return true;
            }
        }

        return false;
    }
}
