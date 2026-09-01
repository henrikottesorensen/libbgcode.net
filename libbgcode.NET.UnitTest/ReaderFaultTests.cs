using System;
using System.IO;
using System.Text;

using AwesomeAssertions;

using Xunit;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// The reader against streams that misbehave rather than files that lie: lengths that overstate
/// what can be read, reads that throw, and the walk's behaviour after it has already answered.
/// </summary>
/// <remarks>
/// A <see cref="FileStream"/> can genuinely do all of this - a file truncated by another process
/// after opening has exactly the lying-length shape - so these paths are contract, not paranoia.
/// </remarks>
public class ReaderFaultTests
{
    /// <summary>After the clean end, the answer stays the end, however often it is asked.</summary>
    [Fact]
    public void NextBlockAfterTheEndKeepsAnswering()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model=MK4S\n");
        byte[] file = TestBgcode.MetadataBlockFile(compression: 0, ini, (uint)ini.Length);

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.NextBlock().Should().NotBeNull();
        reader.NextBlock().Should().BeNull();
        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeTrue();
    }

    /// <summary>After a refusal, the walk stays refused - every later offset would be a guess.</summary>
    [Fact]
    public void NextBlockAfterARefusalStaysRefused()
    {
        byte[] file =
        [
            .. TestBgcode.FileHeader(),
            0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.NextBlock().Should().BeNull();
        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>
    /// A stream whose length overstates what it can deliver - the shape of a file truncated
    /// behind an open handle. The header read runs out of bytes, and the open refuses.
    /// </summary>
    [Fact]
    public void OpenRefusesAStreamShorterThanItsLength()
    {
        using LyingLengthStream stream = new("GCDE"u8.ToArray(), claimedExtra: 20);

        BgcodeReader.Open(stream).Should().BeNull();
    }

    /// <summary>A stream that fails with an I/O error on the first read refuses cleanly.</summary>
    [Fact]
    public void OpenRefusesAStreamThatThrowsOnRead()
    {
        using ThrowingStream stream = new();

        BgcodeReader.Open(stream).Should().BeNull();
    }

    /// <summary>The same truncation one step later: the header is intact, the first block is not.</summary>
    [Fact]
    public void NextBlockRefusesAStreamShorterThanItsLength()
    {
        using LyingLengthStream stream = new(TestBgcode.FileHeader(), claimedExtra: 20);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>And one step later again: the walk is fine, the payload bytes are gone.</summary>
    [Fact]
    public void ReadDataRefusesAStreamShorterThanItsLength()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model=MK4S\n");
        byte[] whole = TestBgcode.MetadataBlockFile(compression: 0, ini, (uint)ini.Length);

        // Everything up to half the payload exists; the length claims all of it.
        using LyingLengthStream stream = new(whole[..(whole.Length - 10)], claimedExtra: 10);

        BgcodeReader reader = BgcodeReader.Open(stream)!;
        BgcodeBlock block = reader.NextBlock()!;

        reader.ReadData(block).Should().BeNull();
    }

    /// <summary>
    /// With verification on, the checksum re-read is what hits the missing bytes first, and it
    /// must answer mismatch rather than escape.
    /// </summary>
    [Fact]
    public void VerificationRefusesAStreamShorterThanItsLength()
    {
        byte[] ini = Encoding.UTF8.GetBytes("printer_model=MK4S\n");
        byte[] whole = TestBgcode.MetadataBlockFile(compression: 0, ini, (uint)ini.Length, checksumType: 1);

        using LyingLengthStream stream = new(whole[..(whole.Length - 10)], claimedExtra: 10);

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })!;
        BgcodeBlock block = reader.NextBlock()!;

        reader.ReadData(block).Should().BeNull();
    }

    /// <summary>
    /// A payload with a genuine zlib header wrapping garbage: the inflater's corrupt-data
    /// exception is the third exception shape on this path, and it too must become null.
    /// </summary>
    [Fact]
    public void ReadDataRefusesACorruptDeflateBody()
    {
        byte[] corrupt = [0x78, 0x9C, 0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF];
        byte[] file =
        [
            .. TestBgcode.FileHeader(),
            .. TestBgcode.BlockHeader(type: 3, compression: 1, uncompressedSize: 64, compressedSize: (uint)corrupt.Length),
            0x00, 0x00,
            .. corrupt,
        ];

        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.ReadData(reader.NextBlock()!).Should().BeNull();
    }

    /// <summary>A read fault after the header: the open succeeds, the walk refuses.</summary>
    [Fact]
    public void NextBlockRefusesAStreamThatStartsThrowing()
    {
        byte[] file =
        [
            .. TestBgcode.FileHeader(),
            .. TestBgcode.BlockHeader(type: 3, compression: 0, uncompressedSize: 4, compressedSize: null),
            0x00, 0x00, 0x01, 0x02, 0x03, 0x04,
        ];

        using ThrowingTailStream stream = new(file, faultFrom: 10);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        reader.NextBlock().Should().BeNull();
        reader.AtEnd.Should().BeFalse();
    }

    /// <summary>A memory stream that claims more length than it can deliver.</summary>
    private sealed class LyingLengthStream(byte[] content, int claimedExtra) : MemoryStream(content, writable: false)
    {
        public override long Length => base.Length + claimedExtra;
    }

    /// <summary>Reads faithfully up to a position, then fails with an I/O error.</summary>
    private sealed class ThrowingTailStream(byte[] content, long faultFrom) : MemoryStream(content, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            return Position >= faultFrom ? throw new IOException("The disk went away.") : base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            return Position >= faultFrom ? throw new IOException("The disk went away.") : base.Read(buffer);
        }
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => 100;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("The disk went away.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return Position;
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
