using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using AwesomeAssertions;

using CsCheck;

using Xunit;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// The writer, judged by the reader: everything written must walk, verify and decode back to what
/// went in, and every call the specification's order forbids must be refused before it writes.
/// </summary>
public class BgcodeWriterTests
{
    private const string PrinterIni = "printer_model=COREONE\nnozzle_diameter=0.4\nfilament_type=PLA\n";

    private const string PrintIni = "estimated printing time (normal mode)=34s\nfilament used [mm]=28.35\n";

    private const string SlicerIni = "layer_height=0.2\nfill_density=15%\n";

    private const string SlicerJson = "{\"layer_height\":0.2,\"fill_density\":\"15%\"}";

    private const string GCode = "; generated for a test\nM73 P0 R0\nG28\nG1 X10 Y20 E0.5\nM104 S210\n";

    private static readonly Gen<string> GenNumber =
        Gen.Select(Gen.Int[0, 999], Gen.Int[-1, 99],
                   (whole, fraction) => fraction < 0 ? $"{whole}" : $"{whole}.{fraction}");

    private static readonly Gen<string> GenLine =
        Gen.OneOf(
            Gen.Select(Gen.OneOfConst("G0", "G1", "G28"),
                       Gen.Char["XYZEF"].Select(GenNumber, (letter, number) => $" {letter}{number}").Array[1, 4],
                       (command, parameters) => command + string.Concat(parameters)),
            Gen.Select(Gen.Int[0, 999], Gen.Char["SPRT"].Select(GenNumber, (letter, number) => $" {letter}{number}").Array[0, 3],
                       (code, parameters) => $"M{code}" + string.Concat(parameters)),
            Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789 _"].Array[0, 24].Select(chars => ("; " + new string(chars)).TrimEnd()));

    private static readonly Gen<string> GenGCode =
        GenLine.Array[1, 60].Select(lines => string.Concat(lines.Select(line => line + "\n")));

    /// <summary>
    /// Every block type, written with the slicers' defaults, comes back through the reader with
    /// checksums verified and payloads intact.
    /// </summary>
    [Fact]
    public void WritesAWholeFileTheReaderVerifies()
    {
        byte[] thumbnail = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03];
        byte[] file = Write(writer =>
        {
            writer.WriteFileMetadata("producer=libbgcode.NET tests\n");
            writer.WritePrinterMetadata(PrinterIni);
            writer.WriteThumbnail(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 16, 16), thumbnail);
            writer.WriteThumbnail(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Qoi, 220, 124), thumbnail);
            writer.WritePrintMetadata(PrintIni);
            writer.WriteSlicerMetadata(SlicerIni);
            writer.WriteSlicerMetadata(SlicerJson, BgcodeMetadataEncoding.Json);
            writer.WriteGCode(GCode);
        });

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;
        List<BgcodeBlock> blocks = ReadAll(reader);

        reader.FileHeader.ChecksumType.Should().Be(BgcodeChecksumType.Crc32);
        reader.AtEnd.Should().BeTrue();
        blocks.Select(block => block.Type).Should().Equal(
            BgcodeBlockType.FileMetadata,
            BgcodeBlockType.PrinterMetadata,
            BgcodeBlockType.Thumbnail,
            BgcodeBlockType.Thumbnail,
            BgcodeBlockType.PrintMetadata,
            BgcodeBlockType.SlicerMetadata,
            BgcodeBlockType.SlicerMetadata,
            BgcodeBlockType.GCode);

        reader.ReadText(blocks[0]).Should().Be("producer=libbgcode.NET tests\n");
        reader.ReadText(blocks[1]).Should().Be(PrinterIni);
        blocks[2].Thumbnail.Should().Be(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 16, 16));
        blocks[3].Thumbnail.Should().Be(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Qoi, 220, 124));
        reader.ReadData(blocks[2]).Should().Equal(thumbnail);
        reader.ReadText(blocks[4]).Should().Be(PrintIni);
        blocks[5].MetadataEncoding.Should().Be(BgcodeMetadataEncoding.Ini);
        blocks[6].MetadataEncoding.Should().Be(BgcodeMetadataEncoding.Json);
        reader.ReadText(blocks[5]).Should().Be(SlicerIni);
        reader.ReadText(blocks[6]).Should().Be(SlicerJson);
        blocks[4].Compression.Should().Be(BgcodeCompression.Deflate);
        blocks[7].Compression.Should().Be(BgcodeCompression.Heatshrink12);
        blocks[7].GCodeEncoding.Should().Be(BgcodeGCodeEncoding.MeatPackWithComments);
        reader.ReadText(blocks[7]).Should().Be(GCode);
    }

    /// <summary>Each compression the format allows round-trips, and the reader reports it as written.</summary>
    [Theory]
    [InlineData(BgcodeCompression.None)]
    [InlineData(BgcodeCompression.Deflate)]
    [InlineData(BgcodeCompression.Heatshrink11)]
    [InlineData(BgcodeCompression.Heatshrink12)]
    public void RoundTripsEveryCompression(BgcodeCompression compression)
    {
        string text = string.Concat(Enumerable.Repeat(PrinterIni, 40));
        byte[] file = Write(writer => writer.WritePrinterMetadata(text, compression));

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;
        BgcodeBlock block = reader.NextBlock()!;

        block.Compression.Should().Be(compression);
        reader.ReadText(block).Should().Be(text);
    }

    /// <summary>Each G-code encoding round-trips to the text the encoding preserves.</summary>
    [Theory]
    [InlineData(BgcodeGCodeEncoding.None, GCode)]
    [InlineData(BgcodeGCodeEncoding.MeatPackWithComments, GCode)]
    [InlineData(BgcodeGCodeEncoding.MeatPack, "M73 P0 R0\nG28\nG1 X10 Y20 E0.5\nM104 S210\n")]
    public void RoundTripsEveryGCodeEncoding(BgcodeGCodeEncoding encoding, string expected)
    {
        byte[] file = Write(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteGCode(GCode, encoding);
        });

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;
        BgcodeBlock gcode = ReadAll(reader).Single(block => block.Type == BgcodeBlockType.GCode);

        gcode.GCodeEncoding.Should().Be(encoding);
        reader.ReadText(gcode).Should().Be(expected);
    }

    /// <summary>Without checksums, blocks abut and the reader says so.</summary>
    [Fact]
    public void WritesWithoutChecksumsWhenAsked()
    {
        byte[] file = Write(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteGCode(GCode);
        }, BgcodeChecksumType.None);

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;

        reader.FileHeader.ChecksumType.Should().Be(BgcodeChecksumType.None);
        ReadAll(reader).Should().HaveCount(4);
        reader.AtEnd.Should().BeTrue();
    }

    /// <summary>
    /// G-code is cut into blocks at 64 KiB of source on line boundaries: no block splits a line,
    /// every block decodes standalone, and the concatenation is the input.
    /// </summary>
    [Fact]
    public void ChunksGCodeOnLineBoundaries()
    {
        StringBuilder moves = new();

        for (int i = 0; i < 8000; i++)
        {
            moves.Append("G1 X").Append(10 + (i % 200)).Append('.').Append(i % 1000).Append(" Y").Append(i % 180).Append(" E0.").Append(i % 97).Append('\n');
        }

        string gcode = moves.ToString();
        byte[] file = Write(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteGCode(gcode);
        });

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;
        List<string> blocks = ReadAll(reader).Where(block => block.Type == BgcodeBlockType.GCode)
                                             .Select(block => reader.ReadText(block)!)
                                             .ToList();

        blocks.Count.Should().BeGreaterThan(2, "the source is several times the 64 KiB cut");
        blocks.Should().AllSatisfy(text => text.Should().EndWith("\n", "a block ends on a line boundary"));
        string.Concat(blocks).Should().Be(gcode);
    }

    /// <summary>A single line longer than the cut becomes a block of its own, as the reference does.</summary>
    [Fact]
    public void AnOverlongLineBecomesItsOwnBlock()
    {
        string longLine = "; " + new string('x', BgcodeWriter.GCodeBlockSourceBytes + 10) + "\n";

        BgcodeWriter.ChunkOnLines("G28\n" + longLine + "G1 X1\n").Should().Equal("G28\n", longLine, "G1 X1\n");
    }

    /// <summary>Random canonical G-code and metadata survive the round trip through both halves.</summary>
    [Fact]
    public void RoundTripsArbitraryCanonicalFiles()
    {
        Gen.Select(GenGCode, Gen.Int[0, 3], Gen.Bool, (gcode, compression, crc) => (gcode, compression, crc)).Sample(sample =>
        {
            (string gcode, int compressionIndex, bool crc) = sample;
            BgcodeCompression compression = (BgcodeCompression)compressionIndex;
            byte[] file = Write(writer =>
            {
                writer.WritePrinterMetadata(PrinterIni, compression);
                writer.WritePrintMetadata(PrintIni, compression);
                writer.WriteSlicerMetadata(SlicerIni, BgcodeMetadataEncoding.Ini, compression);
                writer.WriteGCode(gcode, BgcodeGCodeEncoding.MeatPackWithComments, compression);
            }, crc ? BgcodeChecksumType.Crc32 : BgcodeChecksumType.None);

            using MemoryStream stream = new(file, writable: false);

            BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;
            List<BgcodeBlock> blocks = ReadAll(reader);

            reader.AtEnd.Should().BeTrue();
            reader.ReadText(blocks[0]).Should().Be(PrinterIni);
            string.Concat(blocks.Where(block => block.Type == BgcodeBlockType.GCode).Select(block => reader.ReadText(block))).Should().Be(gcode);
        }, iter: 100);
    }

    /// <summary>The specification's order, and the reference reader's mandatory chain, are enforced before anything is written.</summary>
    [Fact]
    public void RefusesBlocksOutOfOrder()
    {
        Refuses(writer => writer.WritePrintMetadata(PrintIni), "print metadata before printer metadata");
        Refuses(writer => writer.WriteGCode(GCode), "G-code before the mandatory metadata");
        Refuses(writer => writer.WriteThumbnail(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 1, 1), [0x00]), "a thumbnail before printer metadata");
        Refuses(writer =>
        {
            writer.WritePrinterMetadata(PrinterIni);
            writer.WritePrinterMetadata(PrinterIni);
        }, "a second printer metadata block");
        Refuses(writer =>
        {
            writer.WritePrinterMetadata(PrinterIni);
            writer.WriteFileMetadata("late=1\n");
        }, "file metadata after printer metadata");
        Refuses(writer =>
        {
            writer.WritePrinterMetadata(PrinterIni);
            writer.WritePrintMetadata(PrintIni);
            writer.WriteThumbnail(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 1, 1), [0x00]);
        }, "a thumbnail after print metadata");
        Refuses(writer =>
        {
            writer.WritePrinterMetadata(PrinterIni);
            writer.WriteSlicerMetadata(SlicerIni);
        }, "slicer metadata without print metadata");
        Refuses(writer =>
        {
            writer.WritePrinterMetadata(PrinterIni);
            writer.WritePrintMetadata(PrintIni);
            writer.WriteGCode(GCode);
        }, "G-code without slicer metadata - each mandatory block has its own check, and each needs its own case");
        Refuses(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteSlicerMetadata(SlicerIni);
        }, "a second INI slicer metadata block");
        Refuses(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteSlicerMetadata(SlicerJson, BgcodeMetadataEncoding.Json);
            writer.WriteSlicerMetadata(SlicerJson, BgcodeMetadataEncoding.Json);
        }, "a third slicer metadata block");
        Refuses(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteGCode(GCode);
            writer.WritePrintMetadata(PrintIni);
        }, "metadata after G-code");
    }

    /// <summary>A refused call writes nothing: the file stays exactly as it was before it.</summary>
    [Fact]
    public void ARefusedCallWritesNothing()
    {
        using MemoryStream stream = new();
        using BgcodeWriter writer = new(stream, leaveOpen: true);

        writer.WritePrinterMetadata(PrinterIni);

        long before = stream.Length;

        Action refused = () => writer.WriteGCode(GCode);

        refused.Should().Throw<InvalidOperationException>();
        stream.Length.Should().Be(before);
    }

    /// <summary>After disposal the writer refuses; the stream is closed unless asked to stay open.</summary>
    [Fact]
    public void DisposalClosesUnlessAskedNotTo()
    {
        MemoryStream closed = new();
        BgcodeWriter closing = new(closed);

        closing.Dispose();
        closed.CanWrite.Should().BeFalse();

        MemoryStream open = new();
        BgcodeWriter leaving = new(open, leaveOpen: true);

        leaving.Dispose();
        open.CanWrite.Should().BeTrue();

        Action afterDispose = () => leaving.WritePrinterMetadata(PrinterIni);

        afterDispose.Should().Throw<ObjectDisposedException>();
        open.Dispose();
    }

    /// <summary>The writer's argument checks are exceptions, the reader's contract is not the writer's.</summary>
    [Fact]
    public void RefusesBadArguments()
    {
        Action unwritable = () => _ = new BgcodeWriter(new MemoryStream([], writable: false));
        Action unknownChecksum = () => _ = new BgcodeWriter(new MemoryStream(), (BgcodeChecksumType)7);

        unwritable.Should().Throw<ArgumentException>();
        unknownChecksum.Should().Throw<ArgumentOutOfRangeException>();

        Refuses(writer => writer.WritePrinterMetadata(PrinterIni, (BgcodeCompression)9), "an unknown compression", typeof(ArgumentOutOfRangeException));
        Refuses(writer =>
        {
            WriteMandatoryPreamble(writer);
            writer.WriteGCode(GCode, (BgcodeGCodeEncoding)9);
        }, "an unknown G-code encoding", typeof(ArgumentOutOfRangeException));
    }

    private static void WriteMandatoryPreamble(BgcodeWriter writer)
    {
        writer.WritePrinterMetadata(PrinterIni);
        writer.WritePrintMetadata(PrintIni);
        writer.WriteSlicerMetadata(SlicerIni);
    }

    private static byte[] Write(Action<BgcodeWriter> body, BgcodeChecksumType checksumType = BgcodeChecksumType.Crc32)
    {
        using MemoryStream stream = new();

        using (BgcodeWriter writer = new(stream, checksumType, leaveOpen: true))
        {
            body(writer);
        }

        return stream.ToArray();
    }

    private static void Refuses(Action<BgcodeWriter> body, string because, Type? exception = null)
    {
        using MemoryStream stream = new();
        using BgcodeWriter writer = new(stream, leaveOpen: true);

        Action attempt = () => body(writer);

        attempt.Should().Throw<Exception>(because).Which.Should().BeOfType(exception ?? typeof(InvalidOperationException));
    }

    private static List<BgcodeBlock> ReadAll(BgcodeReader reader)
    {
        List<BgcodeBlock> blocks = [];

        while (reader.NextBlock() is { } block)
        {
            blocks.Add(block);
        }

        return blocks;
    }
}
