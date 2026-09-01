using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using AwesomeAssertions;

using Xunit;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// Reading the container: the header, the block walk, and each payload on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixtures are real slicer output</b>, not hand-assembled bytes, which is the whole point
/// of having them: a hand-written binary blob tests this parser against a reading of the
/// specification, and a sliced file tests it against what PrusaSlicer actually emits. Regenerate
/// them with a 3 mm cube and PrusaSlicer 2.9.6's CLI:
/// <code>
/// PrusaSlicer --export-gcode --printer-profile "Prusa CORE One HF0.4 nozzle" \
///   --print-profile "0.20mm SPEED @COREONE HF0.4" \
///   --material-profile "Esun PLA @COREONE HF0.4" -o metadata-coreone-hf04-pla.bgcode cube.stl
/// </code>
/// with <c>3D-Fuel Pro PCTG Matte Black @COREONE HF0.4</c> for the abrasive one.
/// </para>
/// <para>
/// <b>The malformed cases are the ones with something to prove.</b> Every size in a binary file is
/// attacker-influenced, and this parser is written for whatever anybody uploads, so the tests that
/// matter are the ones where a declared length is a lie.
/// </para>
/// </remarks>
public class BgcodeReaderTests
{
    /// <summary>The header of a real file: version 1, CRC-32 trailers.</summary>
    [Fact]
    public void ReadsARealFileHeader()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader? reader = BgcodeReader.Open(file);

        reader.Should().NotBeNull();
        reader!.FileHeader.Version.Should().Be(1u);
        reader.FileHeader.ChecksumType.Should().Be(BgcodeChecksumType.Crc32);
        reader.FileHeader.ChecksumSize.Should().Be(4);
    }

    /// <summary>
    /// The block sequence of a real file follows the specification's ordering, and the walk ends
    /// cleanly with <see cref="BgcodeReader.AtEnd"/> set.
    /// </summary>
    [Fact]
    public void WalksARealFilesBlocks()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file)!;
        List<BgcodeBlock> blocks = ReadAllBlocks(reader);

        blocks.Should().HaveCount(5);
        blocks[0].Type.Should().Be(BgcodeBlockType.FileMetadata);
        blocks[1].Type.Should().Be(BgcodeBlockType.PrinterMetadata);
        blocks[2].Type.Should().Be(BgcodeBlockType.PrintMetadata);
        blocks[3].Type.Should().Be(BgcodeBlockType.SlicerMetadata);
        blocks[4].Type.Should().Be(BgcodeBlockType.GCode);

        blocks[0].Compression.Should().Be(BgcodeCompression.None);
        blocks[2].Compression.Should().Be(BgcodeCompression.Deflate);
        blocks[4].Compression.Should().Be(BgcodeCompression.Heatshrink12);
        blocks[4].GCodeEncoding.Should().Be(BgcodeGCodeEncoding.MeatPackWithComments);

        reader.AtEnd.Should().BeTrue();
    }

    /// <summary>The printer metadata block reads as the INI text the slicer wrote.</summary>
    [Fact]
    public void ReadsPrinterMetadataText()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file)!;
        BgcodeBlock block = FindBlock(reader, BgcodeBlockType.PrinterMetadata);

        string? text = reader.ReadText(block);

        text.Should().NotBeNull();
        text.Should().Contain("printer_model=COREONE");
        text.Should().Contain("nozzle_diameter=0.4");
        text.Should().Contain("nozzle_high_flow=1");
    }

    /// <summary>
    /// The second fixture differs in one fact - an abrasive filament - proving the values read
    /// really come from the file at hand.
    /// </summary>
    [Fact]
    public void ReadsTheAbrasiveFixturesMetadata()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-abrasive.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file)!;
        string? text = reader.ReadText(FindBlock(reader, BgcodeBlockType.PrinterMetadata));

        text.Should().NotBeNull();
        text.Should().Contain("filament_type=PCTG");
        text.Should().Contain("filament_abrasive=1");
    }

    /// <summary>
    /// The slicer metadata block is deflate-compressed in real output, so reading it exercises
    /// the zlib-wrapped path against a payload this suite did not fabricate.
    /// </summary>
    [Fact]
    public void ReadsADeflateCompressedBlockFromARealFile()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file)!;
        BgcodeBlock block = FindBlock(reader, BgcodeBlockType.SlicerMetadata);

        string? text = reader.ReadText(block);

        text.Should().NotBeNull();
        text.Should().Contain("filament_type=PLA");
        reader.ReadData(block)!.Length.Should().Be((int)block.UncompressedSize);
    }

    /// <summary>
    /// The G-code block - heatshrink 12/4 wrapping MeatPack - decodes to the expected text,
    /// checked byte for byte against a fixture cross-checked against Prusa's own pybgcode.
    /// </summary>
    /// <remarks>
    /// The comparison is bytes, not strings, and the fixture is read as bytes: a byte-exact
    /// expectation must not pass through anything entitled to normalise line endings. The first
    /// CI run on Windows proved the point - the runner's machine-wide <c>autocrlf</c> rewrote
    /// the fixture at checkout, which <c>.gitattributes</c> now forbids.
    /// </remarks>
    [Fact]
    public void DecodesTheGCodeBlockOfARealFile()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file)!;
        BgcodeBlock block = FindBlock(reader, BgcodeBlockType.GCode);

        string? text = reader.ReadText(block);

        text.Should().NotBeNull();
        Encoding.UTF8.GetBytes(text!).Should().Equal(File.ReadAllBytes(FixturePath("gcode-block-coreone-hf04-pla.txt")));
    }

    /// <summary>Verification on: every block of a genuine file passes its stored CRC-32.</summary>
    [Fact]
    public void ChecksumsOfARealFileVerify()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { VerifyChecksum = true })!;

        foreach (BgcodeBlock block in ReadAllBlocks(reader))
        {
            reader.ReadData(block).Should().NotBeNull($"block {block.Type} should verify");
        }
    }

    /// <summary>One flipped payload byte fails verification, and only verification.</summary>
    [Fact]
    public void ACorruptedPayloadFailsVerification()
    {
        byte[] whole = File.ReadAllBytes(FixturePath("metadata-coreone-hf04-pla.bgcode"));

        using MemoryStream corrupted = new(whole, writable: true);

        BgcodeReader probe = BgcodeReader.Open(corrupted)!;
        BgcodeBlock block = FindBlock(probe, BgcodeBlockType.PrinterMetadata);

        whole[block.DataPosition + 5] ^= 0x01;

        BgcodeReader trusting = BgcodeReader.Open(new MemoryStream(whole))!;
        BgcodeReader verifying = BgcodeReader.Open(new MemoryStream(whole), new BgcodeReaderOptions { VerifyChecksum = true })!;

        trusting.ReadData(FindBlock(trusting, BgcodeBlockType.PrinterMetadata)).Should().NotBeNull();
        verifying.ReadData(FindBlock(verifying, BgcodeBlockType.PrinterMetadata)).Should().BeNull();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("GC", "shorter than the magic")]
    [InlineData("GCDE", "the magic and nothing else")]
    public void RefusesWhatItCannotRead(string content, string reason)
    {
        BgcodeReader.Open(Stream(Encoding.UTF8.GetBytes(content))).Should().BeNull(reason);
    }

    /// <summary>
    /// A version this does not know is refused rather than read optimistically: an unreadable
    /// file makes no claims, where a misread one makes wrong ones.
    /// </summary>
    [Fact]
    public void RefusesAnUnknownVersion()
    {
        BgcodeReader.Open(Stream(FileHeader(version: 2, checksumType: 1))).Should().BeNull();
    }

    /// <summary>
    /// The checksum algorithm decides how many bytes sit between one block and the next, so an
    /// unknown one is not a skippable curiosity - every later offset would be wrong.
    /// </summary>
    [Fact]
    public void RefusesAnUnknownChecksumType()
    {
        BgcodeReader.Open(Stream(FileHeader(version: 1, checksumType: 7))).Should().BeNull();
    }

    /// <summary>
    /// A block header promising more bytes than the file holds - the shape an interrupted upload
    /// takes, and the shape a hostile file takes.
    /// </summary>
    [Fact]
    public void RefusesABlockLongerThanTheFile()
    {
        byte[] file =
        [
            .. FileHeader(version: 1, checksumType: 0),

            // A printer metadata block declaring 4 GB of uncompressed INI, with nothing behind it.
            0x03, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00,
        ];

        BgcodeReader reader = BgcodeReader.Open(Stream(file))!;

        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse("a lie about a size is malformed, not the end of the file");
    }

    /// <summary>
    /// A block the walk merely steps over lies about its size: the declared length must be
    /// refused before it becomes a seek target, which throws on an array-backed stream where a
    /// file would tolerate it.
    /// </summary>
    [Fact]
    public void RefusesASkippedBlockLongerThanTheFile()
    {
        byte[] file =
        [
            .. FileHeader(version: 1, checksumType: 0),

            // A thumbnail block declaring ~3.3 GB of compressed payload, with nothing behind it.
            0x05, 0x00, 0x01, 0x00, 0x10, 0x00, 0x00, 0x00, 0xB1, 0xC1, 0x2A, 0xC6,
        ];

        BgcodeReader reader = BgcodeReader.Open(Stream(file))!;

        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>
    /// The walk refuses a block type it does not know: its parameter size is unknown, and the
    /// parameter size is part of where the next block starts.
    /// </summary>
    [Fact]
    public void RefusesAnUnknownBlockType()
    {
        byte[] file =
        [
            .. FileHeader(version: 1, checksumType: 0),
            0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        BgcodeReader reader = BgcodeReader.Open(Stream(file))!;

        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>
    /// An unknown compression does not stop the walk - the header still says how much to skip -
    /// and only refuses the payload.
    /// </summary>
    [Fact]
    public void StepsOverACompressionItCannotDecode()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] file =
        [
            .. FileHeader(version: 1, checksumType: 0),
            .. BlockHeader(type: 3, compression: 9, uncompressedSize: 16, compressedSize: (uint)payload.Length),
            0x00, 0x00,
            .. payload,
            .. BlockHeader(type: 1, compression: 0, uncompressedSize: 0, compressedSize: null),
            0x00, 0x00,
        ];

        BgcodeReader reader = BgcodeReader.Open(Stream(file))!;
        BgcodeBlock first = reader.NextBlock()!;

        first.Type.Should().Be(BgcodeBlockType.PrinterMetadata);
        reader.ReadData(first).Should().BeNull("the compression is unknown");
        reader.NextBlock()!.Type.Should().Be(BgcodeBlockType.GCode, "the walk continues past it");
    }

    /// <summary>
    /// The decompression bomb: a small declared size that passes any cap, wrapping a payload that
    /// inflates far past it. The declared size must bound the read, or a kilobyte of upload
    /// becomes a gigabyte of allocation.
    /// </summary>
    [Fact]
    public void RefusesADeflateBlockThatExpandsPastItsDeclaredSize()
    {
        byte[] bomb = ZLibCompress(new byte[256 * 1024]);

        ReadSingleDeflateBlock(bomb, declaredUncompressedSize: 100).Should().BeNull();
    }

    /// <summary>The other direction of the same lie: a payload that stops short of its declaration.</summary>
    [Fact]
    public void RefusesADeflateBlockShorterThanItDeclares()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model = MK4S\n");

        ReadSingleDeflateBlock(ZLibCompress(ini), declaredUncompressedSize: (uint)ini.Length + 10).Should().BeNull();
    }

    /// <summary>
    /// A zlib header demanding a preset dictionary: the inflater refuses it with an exception
    /// type whose only public ancestor is <see cref="IOException"/>, a second exception shape on
    /// the same corrupt-payload path. Found by fuzzing the predecessor of this reader.
    /// </summary>
    [Fact]
    public void RefusesADeflatePayloadDemandingAPresetDictionary()
    {
        byte[] payload = [0x78, 0x20, 0x01, 0x02, 0x03, 0x04, 0x00, 0x00];

        ReadSingleDeflateBlock(payload, declaredUncompressedSize: 16).Should().BeNull();
    }

    /// <summary>A well-formed hand-built deflate block reads back exactly.</summary>
    [Fact]
    public void ReadsAHandBuiltDeflateBlock()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model = MK4S\nnozzle_diameter = 0.4\n");

        byte[]? data = ReadSingleDeflateBlock(ZLibCompress(ini), declaredUncompressedSize: (uint)ini.Length);

        data.Should().NotBeNull();
        Encoding.UTF8.GetString(data!).Should().Contain("printer_model = MK4S");
    }

    /// <summary>Truncated part-way through a real file, which is what a failed upload leaves.</summary>
    [Fact]
    public void RefusesATruncatedRealFile()
    {
        byte[] whole = File.ReadAllBytes(FixturePath("metadata-coreone-hf04-pla.bgcode"));

        BgcodeReader reader = BgcodeReader.Open(Stream(whole[..40]))!;

        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>A payload larger than the configured bound is refused before it is allocated.</summary>
    [Fact]
    public void RefusesAPayloadOverTheConfiguredBound()
    {
        using FileStream file = OpenFixture("metadata-coreone-hf04-pla.bgcode");

        BgcodeReader reader = BgcodeReader.Open(file, new BgcodeReaderOptions { MaxDataBytes = 64 })!;
        BgcodeBlock block = FindBlock(reader, BgcodeBlockType.PrinterMetadata);

        block.DataSize.Should().BeGreaterThan(64);
        reader.ReadData(block).Should().BeNull();
    }

    /// <summary>A thumbnail block's parameters parse into format and pixel size.</summary>
    [Fact]
    public void ReadsThumbnailParameters()
    {
        byte[] image = [0x89, 0x50, 0x4E, 0x47];
        byte[] file =
        [
            .. FileHeader(version: 1, checksumType: 0),
            .. BlockHeader(type: 5, compression: 0, uncompressedSize: (uint)image.Length, compressedSize: null),
            0x01, 0x00, 0x40, 0x01, 0xF0, 0x00,
            .. image,
        ];

        BgcodeReader reader = BgcodeReader.Open(Stream(file))!;
        BgcodeBlock block = reader.NextBlock()!;

        block.Type.Should().Be(BgcodeBlockType.Thumbnail);
        block.Thumbnail.Should().Be(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Jpg, 320, 240));
        block.MetadataEncoding.Should().BeNull();
        reader.ReadText(block).Should().BeNull("a thumbnail is not text");
        reader.ReadData(block).Should().Equal(image);
    }

    private static FileStream OpenFixture(string name)
    {
        return new FileStream(FixturePath(name), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    private static MemoryStream Stream(byte[] content)
    {
        return new MemoryStream(content, writable: false);
    }

    private static List<BgcodeBlock> ReadAllBlocks(BgcodeReader reader)
    {
        List<BgcodeBlock> blocks = [];

        while (reader.NextBlock() is { } block)
        {
            blocks.Add(block);
        }

        return blocks;
    }

    private static BgcodeBlock FindBlock(BgcodeReader reader, BgcodeBlockType type)
    {
        while (reader.NextBlock() is { } block)
        {
            if (block.Type == type)
            {
                return block;
            }
        }

        throw new InvalidOperationException($"No {type} block in the fixture.");
    }

    private static byte[]? ReadSingleDeflateBlock(byte[] compressed, uint declaredUncompressedSize)
    {
        byte[] file =
        [
            .. FileHeader(version: 1, checksumType: 0),
            .. BlockHeader(type: 3, compression: 1, uncompressedSize: declaredUncompressedSize, compressedSize: (uint)compressed.Length),
            0x00, 0x00,
            .. compressed,
        ];

        BgcodeReader reader = BgcodeReader.Open(Stream(file))!;

        return reader.ReadData(reader.NextBlock()!);
    }

    private static byte[] ZLibCompress(byte[] plain)
    {
        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionMode.Compress))
        {
            compressor.Write(plain);
        }

        return output.ToArray();
    }

    /// <summary>The ten-byte file header: magic, version, checksum type. Little-endian throughout.</summary>
    private static byte[] FileHeader(uint version, ushort checksumType)
    {
        byte[] header = new byte[10];

        "GCDE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), checksumType);

        return header;
    }

    /// <summary>A block header: 8 bytes uncompressed, 12 with a compressed size.</summary>
    private static byte[] BlockHeader(ushort type, ushort compression, uint uncompressedSize, uint? compressedSize)
    {
        byte[] header = new byte[compressedSize is null ? 8 : 12];

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), uncompressedSize);

        if (compressedSize is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), compressedSize.Value);
        }

        return header;
    }
}
