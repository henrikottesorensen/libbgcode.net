using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

using AwesomeAssertions;

using Xunit;

namespace libbgcode.NET.InteropTest;

/// <summary>
/// Cross-checks against <c>pybgcode</c>, Prusa's own binding of the reference implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests pin the facts the specification does not state.</b> The zlib wrapping of the
/// deflate blocks, what the CRC-32 covers, and the MeatPack reconstruction rules were all
/// established from real files; agreeing with the reference implementation on files it wrote -
/// and on files it can read back - is what keeps them pinned.
/// </para>
/// <para>
/// <b>The oracle is optional tooling.</b> The tests skip when no Python with
/// <c>pybgcode</c> importable is found; point <c>LIBBGCODE_PYTHON</c> at one to run them
/// (<c>pip install pybgcode</c>, or build it from Prusa's libbgcode checkout).
/// </para>
/// </remarks>
public class PybgcodeInteropTests
{
    private static readonly Lazy<string?> Python = new(FindPython);

    /// <summary>
    /// Our decode of a real file agrees with the reference conversion: every metadata pair we
    /// read appears in pybgcode's ASCII output, and our decoded G-code is a verbatim segment of
    /// it once comment-only lines - which the converter drops as cosmetics - are filtered the
    /// way it filters them.
    /// </summary>
    [Fact]
    public void AgreesWithTheReferenceConversionOfARealFile()
    {
        string python = RequirePython();
        string bgcode = FixturePath("metadata-coreone-hf04-pla.bgcode");
        string ascii = Path.Combine(WorkDirectory(), "reference.gcode");

        Convert(python, "to_ascii", bgcode, ascii);

        string reference = File.ReadAllText(ascii);

        using FileStream file = new(bgcode, FileMode.Open, FileAccess.Read, FileShare.Read);

        BgcodeReader reader = BgcodeReader.Open(file)!;
        string? printerMetadata = null;
        string? gcode = null;

        while (reader.NextBlock() is { } block)
        {
            if (block.Type == BgcodeBlockType.PrinterMetadata)
            {
                printerMetadata = reader.ReadText(block);
            }

            if (block.Type == BgcodeBlockType.GCode)
            {
                gcode = reader.ReadText(block);
            }
        }

        printerMetadata.Should().NotBeNull();
        gcode.Should().NotBeNull();

        foreach (string line in printerMetadata!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = line.Split('=', 2);

            reference.Should().Contain($"; {pair[0]} = {pair[1]}",
                                       "the converter re-serialises every metadata pair as a comment");
        }

        reference.Should().Contain(DropCommentOnlyLines(gcode!),
                                   "the decoded G-code block should be a verbatim segment of the reference output");
    }

    /// <summary>
    /// The other direction: a file the reference implementation writes - fresh compression
    /// choices and checksums, not the slicer's - walks, verifies and decodes end to end.
    /// </summary>
    [Fact]
    public void ReadsAFileTheReferenceImplementationWrote()
    {
        string python = RequirePython();
        string bgcode = FixturePath("metadata-coreone-hf04-pla.bgcode");
        string ascii = Path.Combine(WorkDirectory(), "roundtrip.gcode");
        string rewritten = Path.Combine(WorkDirectory(), "rewritten.bgcode");

        Convert(python, "to_ascii", bgcode, ascii);
        Convert(python, "to_binary", ascii, rewritten);

        using FileStream file = new(rewritten, FileMode.Open, FileAccess.Read, FileShare.Read);

        BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { VerifyChecksum = true })!;
        List<BgcodeBlockType> seen = [];

        while (reader.NextBlock() is { } block)
        {
            seen.Add(block.Type);
            reader.ReadData(block).Should().NotBeNull($"block {block.Type} should decompress and verify");

            if (block.Type != BgcodeBlockType.Thumbnail)
            {
                reader.ReadText(block).Should().NotBeNull();
            }
        }

        reader.AtEnd.Should().BeTrue();
        seen.Should().Contain(BgcodeBlockType.PrinterMetadata);
        seen.Should().Contain(BgcodeBlockType.GCode);
    }

    /// <summary>
    /// The whole encoding space the format allows, not just the corner PrusaSlicer ships: every
    /// compression on every block type, all three G-code encodings, with and without checksums -
    /// 24 files the reference implementation writes, each walked, verified and decoded end to end.
    /// </summary>
    /// <remarks>
    /// The load-bearing assertion is the equality group: compression and checksums are transport,
    /// so within one G-code encoding all eight variants must decode to byte-identical text. That
    /// pins heatshrink-11, heatshrink-12, deflate and plain against each other with no tolerance.
    /// </remarks>
    [Fact]
    public void TheWholeEncodingSpaceDecodesConsistently()
    {
        string python = RequirePython();
        string ascii = Path.Combine(WorkDirectory(), "matrix-source.gcode");

        Convert(python, "to_ascii", FixturePath("metadata-coreone-hf04-pla.bgcode"), ascii);

        Dictionary<string, string> gcodeByEncoding = [];

        foreach (string encoding in (string[])["none", "meatpack", "meatpack_comments"])
        {
            foreach (string compression in (string[])["none", "deflate", "hs11", "hs12"])
            {
                foreach (string checksum in (string[])["none", "crc32"])
                {
                    string variant = $"{encoding}-{compression}-{checksum}";
                    string bgcode = Path.Combine(WorkDirectory(), $"matrix-{variant}.bgcode");

                    Convert(python, "to_binary", ascii, bgcode, encoding, compression, checksum);

                    using FileStream file = new(bgcode, FileMode.Open, FileAccess.Read, FileShare.Read);

                    BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { VerifyChecksum = checksum == "crc32" })!;
                    StringBuilder gcode = new();
                    bool sawPrinterMetadata = false;

                    while (reader.NextBlock() is { } block)
                    {
                        reader.ReadData(block).Should().NotBeNull($"block {block.Type} of {variant} should decompress and verify");

                        if (block.Type == BgcodeBlockType.PrinterMetadata)
                        {
                            sawPrinterMetadata = true;
                            reader.ReadText(block).Should().Contain("printer_model=COREONE", $"variant {variant}");
                        }

                        if (block.Type == BgcodeBlockType.GCode)
                        {
                            gcode.Append(reader.ReadText(block));
                        }
                    }

                    reader.AtEnd.Should().BeTrue($"variant {variant} should walk to a clean end");
                    sawPrinterMetadata.Should().BeTrue($"variant {variant}");

                    string text = gcode.ToString();

                    text.Should().Contain("M73 P0 R0\n", $"variant {variant}");
                    text.Should().Contain("G1 ", $"variant {variant} should carry space-separated G moves");

                    if (encoding == "meatpack")
                    {
                        text.Should().NotContain("; MBL", "the plain MeatPack encoding drops comment lines");
                    }
                    else
                    {
                        text.Should().Contain("; MBL", $"variant {variant} keeps comment lines");
                    }

                    if (gcodeByEncoding.TryGetValue(encoding, out string? previous))
                    {
                        text.Should().Be(previous, $"compression and checksums are transport: {variant} must decode identically to its encoding group");
                    }
                    else
                    {
                        gcodeByEncoding[encoding] = text;
                    }
                }
            }
        }
    }

    /// <summary>
    /// A file big enough that the writer splits the G-code across several blocks (it cuts at
    /// 64 KiB of source, on line boundaries): every block must decode standalone - each carries
    /// its own MeatPack state - and the concatenation must restore the inserted G-code verbatim,
    /// across the block boundaries.
    /// </summary>
    [Fact]
    public void DecodesAMultiBlockGCodeFile()
    {
        string python = RequirePython();
        string ascii = Path.Combine(WorkDirectory(), "multiblock-source.gcode");
        string bgcode = Path.Combine(WorkDirectory(), "multiblock.bgcode");

        Convert(python, "to_ascii", FixturePath("metadata-coreone-hf04-pla.bgcode"), ascii);

        // ~190 KiB of canonical G-moves spliced in after a known line: enough for three blocks.
        StringBuilder moves = new();

        for (int i = 0; i < 7000; i++)
        {
            moves.Append("G1 X").Append(10 + (i % 200)).Append('.').Append(i % 1000)
                 .Append(" Y").Append(20 + (i % 180)).Append(" E0.").Append(i % 97).Append('\n');
        }

        string inserted = moves.ToString();
        string source = File.ReadAllText(ascii).Replace("M73 P0 R0\n", "M73 P0 R0\n" + inserted);

        File.WriteAllText(Path.Combine(WorkDirectory(), "multiblock-inflated.gcode"), source);
        Convert(python, "to_binary", Path.Combine(WorkDirectory(), "multiblock-inflated.gcode"), bgcode,
                "meatpack_comments", "hs12", "crc32");

        using FileStream file = new(bgcode, FileMode.Open, FileAccess.Read, FileShare.Read);

        BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { VerifyChecksum = true })!;
        List<string> gcodeBlocks = [];

        while (reader.NextBlock() is { } block)
        {
            if (block.Type == BgcodeBlockType.GCode)
            {
                string? text = reader.ReadText(block);

                text.Should().NotBeNull($"gcode block {gcodeBlocks.Count} must decode standalone");
                gcodeBlocks.Add(text!);
            }
        }

        gcodeBlocks.Count.Should().BeGreaterThan(2, "the source is several times the writer's 64 KiB block cut");
        string.Concat(gcodeBlocks).Should().Contain(inserted,
            "concatenating per-block decodes must restore the moves verbatim across block boundaries");
    }

    /// <summary>
    /// A real thumbnail block, written by the reference implementation from an ASCII thumbnail
    /// section: the parameters carry the declared format and pixel size, and the payload is the
    /// image bytes exactly - no fixture profile embeds one, so the block type had only
    /// hand-assembled coverage before this.
    /// </summary>
    [Fact]
    public void RoundTripsAThumbnailBlock()
    {
        string python = RequirePython();
        string ascii = Path.Combine(WorkDirectory(), "thumb-source-plain.gcode");
        string withThumbnail = Path.Combine(WorkDirectory(), "thumb-source.gcode");
        string bgcode = Path.Combine(WorkDirectory(), "thumb.bgcode");

        Convert(python, "to_ascii", FixturePath("metadata-coreone-hf04-pla.bgcode"), ascii);

        byte[] png = TinyPng();
        string encoded = System.Convert.ToBase64String(png);
        StringBuilder section = new();

        section.Append("\n;\n; thumbnail begin 1x1 ").Append(encoded.Length).Append('\n');

        for (int i = 0; i < encoded.Length; i += 78)
        {
            section.Append("; ").Append(encoded, i, Math.Min(78, encoded.Length - i)).Append('\n');
        }

        section.Append("; thumbnail end\n;\n");

        string source = File.ReadAllText(ascii);

        File.WriteAllText(withThumbnail, source.Replace("M73 P0 R0", section + "M73 P0 R0"));
        Convert(python, "to_binary", withThumbnail, bgcode);

        using FileStream file = new(bgcode, FileMode.Open, FileAccess.Read, FileShare.Read);

        BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { VerifyChecksum = true })!;
        BgcodeBlock? thumbnail = null;

        while (reader.NextBlock() is { } block)
        {
            if (block.Type == BgcodeBlockType.Thumbnail)
            {
                thumbnail = block;
            }
        }

        thumbnail.Should().NotBeNull("the writer should reconstruct the thumbnail section as a block");
        thumbnail!.Thumbnail.Should().Be(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 1, 1));
        reader.ReadData(thumbnail).Should().Equal(png, "the payload is the image bytes exactly");
        reader.ReadText(thumbnail).Should().BeNull("a thumbnail is not text");
    }

    /// <summary>A genuine 1x1 PNG, built chunk by chunk so the test owns every byte of it.</summary>
    private static byte[] TinyPng()
    {
        static byte[] Chunk(string tag, byte[] data)
        {
            byte[] tagged = [.. Encoding.ASCII.GetBytes(tag), .. data];
            byte[] length = new byte[4];
            byte[] crc = new byte[4];

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(tagged));

            return [.. length, .. tagged, .. crc];
        }

        byte[] header = [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 8, 2, 0, 0, 0];
        byte[] pixel;

        using (MemoryStream compressed = new())
        {
            using (System.IO.Compression.ZLibStream compressor = new(compressed, System.IO.Compression.CompressionMode.Compress))
            {
                compressor.Write([0x00, 0xFF, 0x00, 0x00]);
            }

            pixel = compressed.ToArray();
        }

        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", header),
            .. Chunk("IDAT", pixel),
            .. Chunk("IEND", []),
        ];
    }

    /// <summary>
    /// The PrusaSlicer 3 shape: an ASCII file carrying a <c>prusaslicer_json_config</c> section
    /// converts - through the exact libbgcode commit PrusaSlicer 3.0.0-alpha11 pins - into a
    /// second slicer metadata block, JSON-encoded, alongside the legacy INI one. Both must walk,
    /// verify and read as text.
    /// </summary>
    [Fact]
    public void ReadsThePrusaSlicer3JsonMetadataBlock()
    {
        string python = RequirePython();
        string ascii = Path.Combine(WorkDirectory(), "ps3-source-plain.gcode");
        string withJson = Path.Combine(WorkDirectory(), "ps3-source.gcode");
        string bgcode = Path.Combine(WorkDirectory(), "ps3.bgcode");

        Convert(python, "to_ascii", FixturePath("metadata-coreone-hf04-pla.bgcode"), ascii);

        string section = "; prusaslicer_json_config = begin\n"
                         + "; {\"ps3_marker\":\"json_metadata\",\"nozzle_diameter\":[0.4]}\n"
                         + "; prusaslicer_json_config = end\n";

        File.WriteAllText(withJson, File.ReadAllText(ascii) + section);
        Convert(python, "to_binary", withJson, bgcode);

        using FileStream file = new(bgcode, FileMode.Open, FileAccess.Read, FileShare.Read);

        BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { VerifyChecksum = true })!;
        List<(BgcodeMetadataEncoding? encoding, string? text)> slicerBlocks = [];

        while (reader.NextBlock() is { } block)
        {
            reader.ReadData(block).Should().NotBeNull($"block {block.Type} should decompress and verify");

            if (block.Type == BgcodeBlockType.SlicerMetadata)
            {
                slicerBlocks.Add((block.MetadataEncoding, reader.ReadText(block)));
            }
        }

        reader.AtEnd.Should().BeTrue();
        slicerBlocks.Should().HaveCount(2, "PrusaSlicer 3 writes the slicer metadata twice, legacy INI then JSON");
        slicerBlocks[0].encoding.Should().Be(BgcodeMetadataEncoding.Ini);
        slicerBlocks[1].encoding.Should().Be(BgcodeMetadataEncoding.Json);
        slicerBlocks[0].text.Should().NotBeNull();
        slicerBlocks[1].text.Should().Contain("ps3_marker");
    }

    /// <summary>
    /// The writer, judged by the reference implementation: a file we write, with every block
    /// type and the slicers' default compressions, must convert to ASCII through pybgcode with
    /// checksum verification on - and the conversion must carry our metadata and G-code.
    /// </summary>
    [Fact]
    public void TheReferenceImplementationReadsWhatWeWrite()
    {
        string python = RequirePython();
        string bgcode = Path.Combine(WorkDirectory(), "ours.bgcode");
        string ascii = Path.Combine(WorkDirectory(), "ours.gcode");

        using (FileStream file = new(bgcode, FileMode.Create, FileAccess.Write))
        using (BgcodeWriter writer = new(file))
        {
            writer.WriteFileMetadata("Producer=libbgcode.NET interop test\n");
            writer.WritePrinterMetadata("printer_model=COREONE\nnozzle_diameter=0.4\nfilament_type=PLA\n");
            writer.WriteThumbnail(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 1, 1), TinyPng());
            writer.WritePrintMetadata("estimated printing time (normal mode)=34s\n");
            writer.WriteSlicerMetadata("layer_height=0.2\n");
            writer.WriteSlicerMetadata("{\"layer_height\":0.2}", BgcodeMetadataEncoding.Json);
            writer.WriteGCode("; written by libbgcode.NET\nM73 P0 R0\nG28\nG1 X10.5 Y20 E0.5\nM104 S210\n");
        }

        Convert(python, "to_ascii", bgcode, ascii);

        string converted = File.ReadAllText(ascii);

        converted.Should().Contain("; printer_model = COREONE");
        converted.Should().Contain("; nozzle_diameter = 0.4");
        converted.Should().Contain("; layer_height = 0.2");
        converted.Should().Contain("thumbnail begin 1x1");
        converted.Should().Contain("G1 X10.5 Y20 E0.5\n");
        converted.Should().Contain("M104 S210\n");
    }

    /// <summary>
    /// Every compression the writer offers, with and without checksums, produces a file the
    /// reference implementation verifies and converts - heatshrink at both windows included,
    /// which a slicer never writes on metadata and so nothing else would prove.
    /// </summary>
    [Theory]
    [InlineData(BgcodeCompression.None, BgcodeChecksumType.Crc32)]
    [InlineData(BgcodeCompression.Deflate, BgcodeChecksumType.Crc32)]
    [InlineData(BgcodeCompression.Heatshrink11, BgcodeChecksumType.Crc32)]
    [InlineData(BgcodeCompression.Heatshrink12, BgcodeChecksumType.Crc32)]
    [InlineData(BgcodeCompression.Heatshrink12, BgcodeChecksumType.None)]
    public void TheReferenceImplementationReadsEveryCompressionWeWrite(BgcodeCompression compression, BgcodeChecksumType checksum)
    {
        string python = RequirePython();
        string bgcode = Path.Combine(WorkDirectory(), $"ours-{compression}-{checksum}.bgcode");
        string ascii = Path.Combine(WorkDirectory(), $"ours-{compression}-{checksum}.gcode");
        StringBuilder moves = new();

        for (int i = 0; i < 3000; i++)
        {
            moves.Append("G1 X").Append(i % 200).Append(" Y").Append(i % 180).Append(" E0.").Append(i % 97).Append('\n');
        }

        using (FileStream file = new(bgcode, FileMode.Create, FileAccess.Write))
        using (BgcodeWriter writer = new(file, checksum))
        {
            writer.WritePrinterMetadata("printer_model=COREONE\n", compression);
            writer.WritePrintMetadata("estimated printing time (normal mode)=34s\n", compression);
            writer.WriteSlicerMetadata("layer_height=0.2\n", BgcodeMetadataEncoding.Ini, compression);
            writer.WriteGCode(moves.ToString(), BgcodeGCodeEncoding.MeatPackWithComments, compression);
        }

        Convert(python, "to_ascii", bgcode, ascii);

        string converted = File.ReadAllText(ascii);

        converted.Should().Contain("; printer_model = COREONE");
        converted.Should().Contain("G1 X199 Y119 E0.");
    }

    private static string RequirePython()
    {
        if (Python.Value is null)
        {
            // A runner that promises the oracle must fail loudly when it is missing - a skip
            // there would quietly retire the whole interop suite. Everywhere else, skipping is
            // the designed shape of a machine without pybgcode.
            if (Environment.GetEnvironmentVariable("LIBBGCODE_REQUIRE_ORACLE") is not null)
            {
                Assert.Fail("LIBBGCODE_REQUIRE_ORACLE is set, but no Python can import pybgcode.");
            }

            Assert.Skip("No Python with pybgcode importable; set LIBBGCODE_PYTHON to run the interop tests.");
        }

        return Python.Value;
    }

    private static string? FindPython()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("LIBBGCODE_PYTHON"), "python3", "python"];

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            try
            {
                if (Run(candidate, ["-c", "import pybgcode"]) == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on the path; try the next candidate.
            }
        }

        return null;
    }

    private static void Convert(string python, string mode, string source, string destination, params string[] options)
    {
        Run(python, [Path.Combine(AppContext.BaseDirectory, "pybgcode_convert.py"), mode, source, destination, .. options])
            .Should().Be(0, $"pybgcode should convert {mode} {string.Join(' ', options)}");
    }

    private static int Run(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;

        process.WaitForExit(TimeSpan.FromSeconds(60)).Should().BeTrue("the oracle should not hang");

        return process.ExitCode;
    }

    private static string WorkDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "libbgcode.NET.InteropTest");

        Directory.CreateDirectory(path);

        return path;
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>
    /// The reference converter's own cosmetic filter: a line that is empty once trimmed and
    /// uncommented does not survive into its ASCII output.
    /// </summary>
    private static string DropCommentOnlyLines(string gcode)
    {
        List<string> kept = [];

        foreach (string line in gcode.Split('\n'))
        {
            string reduced = line.Trim();

            if (reduced.StartsWith(';'))
            {
                reduced = reduced[1..].Trim();
            }

            if (reduced.Length > 0)
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept) + "\n";
    }
}
