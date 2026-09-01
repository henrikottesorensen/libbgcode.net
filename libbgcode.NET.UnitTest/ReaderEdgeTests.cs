using System;
using System.IO;
using System.Text;

using AwesomeAssertions;

using Xunit;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// The reader's edges: empty payloads, trailing bytes, descriptor independence, and the argument
/// contract - the shapes between a well-formed file and a hostile one.
/// </summary>
public class ReaderEdgeTests
{
    /// <summary>A zero-length metadata payload is an ordinary answer: empty text, not a refusal.</summary>
    [Fact]
    public void ReadsAZeroLengthMetadataBlock()
    {
        byte[] file = TestBgcode.MetadataBlockFile(compression: 0, storedPayload: [], declaredUncompressedSize: 0);

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;
        BgcodeBlock block = reader.NextBlock()!;

        reader.ReadData(block).Should().BeEmpty();
        reader.ReadText(block).Should().BeEmpty();
        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeTrue();
    }

    /// <summary>A zero-length plain G-code block reads as empty text.</summary>
    [Fact]
    public void ReadsAZeroLengthGCodeBlock()
    {
        byte[] file =
        [
            .. TestBgcode.FileHeader(),
            .. TestBgcode.BlockHeader(type: 1, compression: 0, uncompressedSize: 0, compressedSize: null),
            0x00, 0x00,
        ];

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;
        BgcodeBlock block = reader.NextBlock()!;

        block.GCodeEncoding.Should().Be(BgcodeGCodeEncoding.None);
        reader.ReadText(block).Should().BeEmpty();
    }

    /// <summary>
    /// Bytes after the last block that cannot be a block header are malformedness, not the end of
    /// the file - an appended tail must never read as a clean walk.
    /// </summary>
    [Fact]
    public void TrailingGarbageIsMalformedNotTheEnd()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model=MK4S\n");
        byte[] file =
        [
            .. TestBgcode.MetadataBlockFile(compression: 0, ini, (uint)ini.Length),
            0x0B, 0xAD, 0x00,
        ];

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.NextBlock().Should().NotBeNull("the real block is intact");
        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse("three stray bytes are not a block");
    }

    /// <summary>
    /// A block overhanging the end of the file by less than its own header's width: the
    /// truncation window a bound computed against the wrong base would wave through. Every other
    /// truncation test overshoots by kilobytes, which several wrong bounds also refuse.
    /// </summary>
    [Fact]
    public void RefusesABlockOverhangingByLessThanItsHeader()
    {
        byte[] file =
        [
            .. TestBgcode.FileHeader(),
            .. TestBgcode.BlockHeader(type: 3, compression: 0, uncompressedSize: 32, compressedSize: null),
            0x00, 0x00,
            .. new byte[27],
        ];

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.NextBlock().Should().BeNull("the payload runs five bytes past the end of the file");
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>
    /// Verification on a file that carries no checksums verifies nothing and refuses nothing -
    /// absence of a trailer is a file-level choice, not corruption.
    /// </summary>
    [Fact]
    public void VerificationOnAChecksumlessFileReadsNormally()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model=MK4S\n");
        byte[] file = TestBgcode.MetadataBlockFile(compression: 0, ini, (uint)ini.Length);

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;

        reader.FileHeader.ChecksumType.Should().Be(BgcodeChecksumType.None);
        reader.ReadData(reader.NextBlock()!).Should().Equal(ini);
    }

    /// <summary>And the mirror: a correct CRC-32 built by hand verifies.</summary>
    [Fact]
    public void AHandBuiltChecksummedFileVerifies()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model=MK4S\n");
        byte[] file = TestBgcode.MetadataBlockFile(compression: 0, ini, (uint)ini.Length, checksumType: 1);

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;

        reader.ReadData(reader.NextBlock()!).Should().Equal(ini);
    }

    /// <summary>The reader walks by seeking, and says so instead of failing strangely later.</summary>
    [Fact]
    public void RefusesANonSeekableStream()
    {
        using NonSeekableStream stream = new();

        Action open = () => BgcodeReader.Open(stream);

        open.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Blocks are descriptors with positions, so payloads can be read in any order, and read
    /// again - walking and reading do not share a cursor.
    /// </summary>
    [Fact]
    public void ReadsDescriptorsOutOfOrderAndRepeatedly()
    {
        using FileStream file = new(Path.Combine(AppContext.BaseDirectory, "metadata-coreone-hf04-pla.bgcode"),
                                    FileMode.Open, FileAccess.Read, FileShare.Read);

        BgcodeReader reader = BgcodeReader.Open(file)!;
        BgcodeBlock first = reader.NextBlock()!;
        BgcodeBlock second = reader.NextBlock()!;
        BgcodeBlock third = reader.NextBlock()!;

        byte[] thirdData = reader.ReadData(third)!;
        byte[] firstData = reader.ReadData(first)!;

        reader.ReadData(third).Should().Equal(thirdData, "a second read answers the same bytes");
        reader.ReadData(second)!.Length.Should().Be((int)second.UncompressedSize);
        reader.ReadData(first).Should().Equal(firstData);

        reader.NextBlock()!.Type.Should().Be(BgcodeBlockType.SlicerMetadata,
                                             "reading payloads must not move the walk");
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
