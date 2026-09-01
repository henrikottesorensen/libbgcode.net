using System;
using System.Linq;
using System.Text;

using AwesomeAssertions;

using CsCheck;

using Xunit;

namespace MeatPack.NET.Test;

/// <summary>
/// Round-trip properties: canonical G-code through the test-only reference packer and back
/// through the decoder must be the identity, across the whole grammar - packed pairs, full-width
/// escapes, both comment policies, the no-spaces table, odd-length padding and G-line re-spacing.
/// </summary>
/// <remarks>
/// Canonical means what a slicer emits: single spaces before parameter groups, no trailing
/// blanks, uppercase axis letters. The packing is lossy in exactly the ways the decoder's
/// reconstruction undoes, so on canonical input the round trip is exact; CsCheck prints a seed on
/// failure that reproduces the shrunk counterexample.
/// </remarks>
public class MeatPackRoundTripTests
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

    /// <summary>Comments kept: the decoder restores the packer's input exactly.</summary>
    [Fact]
    public void RoundTripsCanonicalGCodeWithComments()
    {
        GenText.Sample(text =>
        {
            string decoded = Unpack(TestMeatPackPacker.Pack(text, keepComments: true));

            decoded.Should().Be(text);
        }, iter: 500);
    }

    /// <summary>Comments dropped: the decoder restores everything the packer did not discard.</summary>
    [Fact]
    public void RoundTripsCanonicalGCodeWithoutComments()
    {
        GenText.Sample(text =>
        {
            string expected = string.Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                .Where(line => !line.StartsWith(';'))
                                                .Select(line => line + "\n"));
            string decoded = Unpack(TestMeatPackPacker.Pack(text, keepComments: false));

            decoded.Should().Be(expected);
        }, iter: 500);
    }

    /// <summary>
    /// A full-width first character whose pair partner is the line's newline: the newline is
    /// buffered behind the escape and must still come out after it, in order.
    /// </summary>
    [Fact]
    public void ANewlineBufferedBehindAFullWidthCharacterSurvives()
    {
        // "M4\nM\n": the second line is (M, \n) - M full-width, the newline packed and buffered.
        string decoded = Unpack(TestMeatPackPacker.Pack("M4\nM\n", keepComments: true));

        decoded.Should().Be("M4\nM\n");
    }

    /// <summary>A signal byte dangling at the end of the stream emits nothing and throws nothing.</summary>
    [Fact]
    public void ADanglingSignalByteIsIgnored()
    {
        byte[] packed = [0xFF, 0xFF, 251, 0x1D, 0xCC, 0xFF];

        Unpack(packed).Should().Be("G1\n");
    }

    /// <summary>A command word cut off before its command byte emits nothing and throws nothing.</summary>
    [Fact]
    public void ATruncatedCommandWordIsIgnored()
    {
        byte[] packed = [0xFF, 0xFF, 251, 0x1D, 0xCC, 0xFF, 0xFF];

        Unpack(packed).Should().Be("G1\n");
    }

    /// <summary>A full-width announcement with nothing behind it emits nothing and throws nothing.</summary>
    [Fact]
    public void AStarvedFullWidthQueueIsIgnored()
    {
        byte[] packed = [0xFF, 0xFF, 251, 0xFF];

        Unpack(packed).Should().Be(string.Empty);
    }

    /// <summary>Turning no-spaces mode back off returns the table slot to the space.</summary>
    [Fact]
    public void NoSpacesToggledOffRestoresTheSpace()
    {
        byte[] packed = [0xFF, 0xFF, 251, 0xFF, 0xFF, 247, 0xB1, 0xCC, 0xFF, 0xFF, 246, 0xB1, 0xCC];

        Unpack(packed).Should().Be("1E\n1 \n");
    }

    /// <summary>Reset-all returns the stream to passthrough, exactly like disable-packing.</summary>
    [Fact]
    public void ResetAllReturnsToPassthrough()
    {
        byte[] packed = [0xFF, 0xFF, 251, 0x1D, 0xCC, 0xFF, 0xFF, 249, (byte)'a', (byte)'b'];

        Unpack(packed).Should().Be("G1\nab");
    }

    private static string Unpack(byte[] packed)
    {
        return Encoding.UTF8.GetString(MeatPackDecoder.Unpack(packed));
    }
}
