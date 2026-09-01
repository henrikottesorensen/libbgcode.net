using System.Text;

using AwesomeAssertions;

using Xunit;

namespace MeatPack.NET.Test;

/// <summary>
/// The MeatPack decoder: the packing grammar, the command words, and the reconstruction the
/// packing's lossiness demands.
/// </summary>
/// <remarks>
/// The packed sequences here are hand-assembled from the scheme's grammar; the end-to-end proof
/// that the decoder agrees with what real writers emit is the G-code block test in the
/// libbgcode.NET suite next door, whose expected text is cross-checked against Prusa's own
/// tooling by that repository's interop tests.
/// </remarks>
public class MeatPackDecoderTests
{
    private const byte Signal = 0xFF;

    private const byte EnablePacking = 251;

    private const byte DisablePacking = 250;

    private const byte EnableNoSpaces = 247;

    /// <summary>Two packable characters share a byte: low nibble first, high nibble second.</summary>
    [Fact]
    public void UnpacksAPackedPair()
    {
        // "G1" - 'G' is 13 (0b1101), '1' is 1 (0b0001) - then "\n\n" padding-style pair.
        byte[] packed = [Signal, Signal, EnablePacking, 0x1D, 0xCC];

        Unpack(packed).Should().Be("G1\n");
    }

    /// <summary>
    /// A <c>0b1111</c> low nibble announces a full-width character carried after the pair, with
    /// the high nibble's character following it.
    /// </summary>
    [Fact]
    public void UnpacksAFullWidthFirstCharacter()
    {
        // "M4": 'M' is not in the table, '4' is 4 - so 0x4F, then the full-width 'M'.
        byte[] packed = [Signal, Signal, EnablePacking, 0x4F, (byte)'M'];

        Unpack(packed).Should().Be("M4");
    }

    /// <summary>A <c>0b1111</c> high nibble announces a full-width second character.</summary>
    [Fact]
    public void UnpacksAFullWidthSecondCharacter()
    {
        // "4M": '4' packs low, 'M' full-width second.
        byte[] packed = [Signal, Signal, EnablePacking, 0xF4, (byte)'M'];

        Unpack(packed).Should().Be("4M");
    }

    /// <summary>An <c>0xFF</c> byte announces two full-width characters.</summary>
    [Fact]
    public void UnpacksTwoFullWidthCharacters()
    {
        byte[] packed = [Signal, Signal, EnablePacking, 0xFF, (byte)'M', (byte)'k'];

        Unpack(packed).Should().Be("Mk");
    }

    /// <summary>Before any enable command, bytes pass through untouched - comment lines ride here.</summary>
    [Fact]
    public void PassesThroughWhenPackingIsDisabled()
    {
        Unpack(Encoding.ASCII.GetBytes("; a comment\n")).Should().Be("; a comment\n");
    }

    /// <summary>
    /// The no-spaces variant reassigns the space's table slot to <c>E</c>; the command toggles it.
    /// </summary>
    [Fact]
    public void NoSpacesModeDecodesTheSpaceSlotAsE()
    {
        // "1E" packed as '1' (1) + slot 11, which is 'E' only in no-spaces mode.
        byte[] pair = [0xB1, 0xCC];
        byte[] without = [Signal, Signal, EnablePacking, .. pair];
        byte[] with = [Signal, Signal, EnablePacking, Signal, Signal, EnableNoSpaces, .. pair];

        Unpack(without).Should().Be("1 \n");
        Unpack(with).Should().Be("1E\n");
    }

    /// <summary>
    /// <b>The second character of a newline-first pair is the writer's padding</b>, added to even
    /// out odd-length lines, and is dropped - otherwise every odd-length line would grow a blank
    /// line after it.
    /// </summary>
    [Fact]
    public void DropsThePaddingAfterANewline()
    {
        // 'G' '1' then '\n' (12) paired with '0': the '0' is padding and must not appear.
        byte[] packed = [Signal, Signal, EnablePacking, 0x1D, 0x0C];

        Unpack(packed).Should().Be("G1\n");
    }

    /// <summary>
    /// <b>The packing strips spaces from <c>G</c>-command lines, and the decoder owes them back</b>:
    /// one space before each parameter letter, so a G-code parser sees separated words again.
    /// </summary>
    [Fact]
    public void ReinsertsSpacesIntoGLines()
    {
        // "G1X15Y2\n" packed without spaces, as a writer emits it.
        byte[] packed = [Signal, Signal, EnablePacking, 0x1D, 0x1E, 0xF5, (byte)'Y', 0xC2];

        Unpack(packed).Should().Be("G1 X15 Y2\n");
    }

    /// <summary>An <c>M</c>-command line gets no spaces re-inserted; its spaces were never stripped.</summary>
    [Fact]
    public void LeavesNonGLinesAlone()
    {
        Unpack(Encoding.ASCII.GetBytes("M204 P7000\n")).Should().Be("M204 P7000\n");
    }

    /// <summary>Consecutive newlines collapse to one, whatever mode produced them.</summary>
    [Fact]
    public void CollapsesConsecutiveNewlines()
    {
        Unpack(Encoding.ASCII.GetBytes("A\n\n\nB\n")).Should().Be("A\nB\n");
    }

    /// <summary>An unknown command byte is ignored, and decoding continues.</summary>
    [Fact]
    public void IgnoresAnUnknownCommand()
    {
        byte[] packed = [Signal, Signal, 42, Signal, Signal, EnablePacking, 0x1D, 0xCC];

        Unpack(packed).Should().Be("G1\n");
    }

    /// <summary>Disabling packing mid-stream returns to passthrough.</summary>
    [Fact]
    public void TogglesPackingMidStream()
    {
        byte[] packed =
        [
            Signal, Signal, EnablePacking, 0x1D, 0xCC,
            Signal, Signal, DisablePacking, (byte)';', (byte)'c', (byte)'\n',
            Signal, Signal, EnablePacking, 0x2D, 0xCC,
        ];

        Unpack(packed).Should().Be("G1\n;c\nG2\n");
    }

    /// <summary>Whatever the bytes, the decoder answers with text and never throws.</summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
                                                     Justification = "The randomness generates hostile test inputs; a fixed seed making failures reproducible is the point.")]
    public void AnyByteSequenceDecodes()
    {
        System.Random rng = new(20260831);

        for (int round = 0; round < 200; round++)
        {
            byte[] noise = new byte[rng.Next(64)];

            rng.NextBytes(noise);

            MeatPackDecoder.Unpack(noise).Should().NotBeNull();
        }
    }

    private static string Unpack(byte[] packed)
    {
        return Encoding.UTF8.GetString(MeatPackDecoder.Unpack(packed));
    }
}
