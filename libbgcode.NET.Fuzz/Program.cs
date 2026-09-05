using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

using MeatPack.NET;

using SharpFuzz;

namespace libbgcode.NET.Fuzz;

/// <summary>
/// Coverage-guided fuzzing harnesses for SharpFuzz/libFuzzer.
/// </summary>
/// <remarks>
/// Three harnesses: <c>reader</c> feeds arbitrary bytes through the whole container path - open,
/// walk, read and verify every block; <c>meatpack</c> feeds them to the MeatPack codec alone; and
/// <c>writer</c> treats them as G-code and options, writes a file, and demands the reader take it
/// back whole. The property the first two enforce is the library's contract: whatever the bytes,
/// answer or refuse, never throw - and never spend unbounded memory doing it, which the reader's
/// own options cap. The third enforces the writer's: whatever the text, a verifiable file the
/// reader decodes to the same blocks. Run via <c>fuzz.sh</c>; see that script for the full
/// pipeline.
/// </remarks>
public static class Program
{
    /// <summary>Entry point: harness selection, seed-corpus generation, crash replay.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();

            return 2;
        }

        switch (args[0])
        {
            case "reader":
                Fuzzer.LibFuzzer.Run(ReaderHarness);

                return 0;

            case "meatpack":
                Fuzzer.LibFuzzer.Run(MeatPackHarness);

                return 0;

            case "writer":
                Fuzzer.LibFuzzer.Run(WriterHarness);

                return 0;

            case "seed":
                if (args.Length != 2)
                {
                    PrintUsage();

                    return 2;
                }

                WriteSeedCorpus(args[1]);

                return 0;

            case "replay":
                if (args.Length != 3)
                {
                    PrintUsage();

                    return 2;
                }

                return Replay(args[1], args[2]);

            default:
                PrintUsage();

                return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  libbgcode.NET.Fuzz reader|meatpack|writer      (run under libfuzzer-dotnet)");
        Console.Error.WriteLine("  libbgcode.NET.Fuzz seed <corpus-root>          (write seed corpora)");
        Console.Error.WriteLine("  libbgcode.NET.Fuzz replay <harness> <path>     (re-run corpus/crash inputs)");
    }

    /// <summary>
    /// The whole container path against arbitrary bytes: any exception escaping is a crash.
    /// </summary>
    private static void ReaderHarness(ReadOnlySpan<byte> data)
    {
        using MemoryStream stream = new(data.ToArray(), writable: false);

        BgcodeReaderOptions options = new() { MaxDataBytes = 4 * 1024 * 1024, VerifyChecksum = true };
        BgcodeReader? reader = BgcodeReader.Open(stream, options);

        if (reader is null)
        {
            return;
        }

        while (reader.NextBlock() is { } block)
        {
            byte[]? payload = reader.ReadData(block);

            if (payload is not null && payload.Length != block.UncompressedSize)
            {
                throw new InvalidOperationException("a payload came back at other than its declared size");
            }

            reader.ReadText(block);
        }
    }

    /// <summary>
    /// The MeatPack codec against arbitrary bytes: the decoder never throws and its output stays
    /// within the structural bound of two characters per input byte plus one re-inserted space
    /// each; the same bytes read as text then encode and decode again without an escape, whatever
    /// they contain.
    /// </summary>
    private static void MeatPackHarness(ReadOnlySpan<byte> data)
    {
        byte[] text = MeatPackDecoder.Unpack(data);

        if (text.Length > (4L * data.Length) + 4)
        {
            throw new InvalidOperationException("the decoder produced implausibly much output");
        }

        string asText = Encoding.UTF8.GetString(data);
        byte[] packed = MeatPackEncoder.Pack(asText, keepComments: (data.Length & 1) == 0, omitSpaces: (data.Length & 2) == 0);

        if (packed.Length > (4L * Encoding.UTF8.GetByteCount(asText)) + 16)
        {
            throw new InvalidOperationException("the encoder produced implausibly much output");
        }

        MeatPackDecoder.Unpack(packed);
    }

    /// <summary>
    /// The writer against arbitrary input: the first byte picks compression, encoding and
    /// checksum, the rest is G-code text. Writing must never throw, and the reader must walk the
    /// result to a clean end, verify every block, and give the G-code back as the encoder's
    /// reconstruction - the same text a second write-and-read yields, which is the fixed point
    /// the lossy encoding converges on.
    /// </summary>
    private static void WriterHarness(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        BgcodeCompression compression = (BgcodeCompression)(data[0] & 0x03);
        BgcodeGCodeEncoding encoding = (BgcodeGCodeEncoding)((data[0] >> 2) % 3);
        BgcodeChecksumType checksum = (data[0] & 0x10) == 0 ? BgcodeChecksumType.Crc32 : BgcodeChecksumType.None;
        string gcode = Encoding.UTF8.GetString(data[1..]);

        string first = WriteAndReadBack(gcode, compression, encoding, checksum);
        string second = WriteAndReadBack(first, compression, encoding, checksum);

        if (second != first)
        {
            throw new InvalidOperationException("a second write-and-read did not reproduce the first: the encoding is not converging");
        }
    }

    private static string WriteAndReadBack(string gcode, BgcodeCompression compression, BgcodeGCodeEncoding encoding, BgcodeChecksumType checksum)
    {
        using MemoryStream stream = new();

        using (BgcodeWriter writer = new(stream, checksum, leaveOpen: true))
        {
            writer.WritePrinterMetadata("printer_model=FUZZ\n", compression);
            writer.WritePrintMetadata("estimated printing time (normal mode)=0s\n", compression);
            writer.WriteSlicerMetadata("layer_height=0.2\n", BgcodeMetadataEncoding.Ini, compression);
            writer.WriteGCode(gcode, encoding, compression);
        }

        stream.Position = 0;

        BgcodeReader reader = BgcodeReader.Open(stream, new BgcodeReaderOptions { VerifyChecksum = true })
                              ?? throw new InvalidOperationException("the reader refused a file the writer produced");
        StringBuilder text = new();

        while (reader.NextBlock() is { } block)
        {
            byte[] payload = reader.ReadData(block)
                             ?? throw new InvalidOperationException($"the reader refused a {block.Type} block the writer produced");

            if (payload.Length != block.UncompressedSize)
            {
                throw new InvalidOperationException("a written payload came back at other than its declared size");
            }

            if (block.Type == BgcodeBlockType.GCode)
            {
                text.Append(reader.ReadText(block) ?? throw new InvalidOperationException("the reader refused the G-code text the writer produced"));
            }
        }

        if (!reader.AtEnd)
        {
            throw new InvalidOperationException("the reader found the writer's file malformed");
        }

        return text.ToString();
    }

    private static int Replay(string harness, string path)
    {
        byte[] data = File.ReadAllBytes(path);

        switch (harness)
        {
            case "reader":
                ReaderHarness(data);

                return 0;

            case "meatpack":
                MeatPackHarness(data);

                return 0;

            case "writer":
                WriterHarness(data);

                return 0;

            default:
                PrintUsage();

                return 2;
        }
    }

    private static void WriteSeedCorpus(string root)
    {
        string readerDirectory = Path.Combine(root, "reader");
        string meatpackDirectory = Path.Combine(root, "meatpack");
        string writerDirectory = Path.Combine(root, "writer");

        Directory.CreateDirectory(readerDirectory);
        Directory.CreateDirectory(meatpackDirectory);
        Directory.CreateDirectory(writerDirectory);

        // One option byte, then G-code: the slicers' defaults, and the plain-text corner.
        File.WriteAllBytes(Path.Combine(writerDirectory, "meatpack-heatshrink.txt"),
                           [0x0B, .. "; seed\nM73 P0 R0\nG28\nG1 X10 Y20 E0.5\nM104 S210\n"u8]);
        File.WriteAllBytes(Path.Combine(writerDirectory, "plain-uncompressed.txt"),
                           [0x10, .. "G1 X1\nG1 X2\n"u8]);

        // A real sliced file, when the build carried one along.
        string fixture = Path.Combine(AppContext.BaseDirectory, "metadata-coreone-hf04-pla.bgcode");

        if (File.Exists(fixture))
        {
            File.Copy(fixture, Path.Combine(readerDirectory, "real-slicer-output.bgcode"), overwrite: true);
        }

        File.WriteAllBytes(Path.Combine(readerDirectory, "uncompressed-metadata.bgcode"),
                           SyntheticFile(compressPrinterMetadata: false));
        File.WriteAllBytes(Path.Combine(readerDirectory, "deflate-metadata.bgcode"),
                           SyntheticFile(compressPrinterMetadata: true));

        // Every emission shape of the packing grammar, plus the command words.
        File.WriteAllBytes(Path.Combine(meatpackDirectory, "packed-shapes.bin"),
                           [
                               0xFF, 0xFF, 251,
                               0xFF, 0xFF, 247,
                               0x1D, 0x1E, 0xF5, (byte)'Y', 0xC2,
                               0x4F, (byte)'M', 0xF4, (byte)'M', 0xFF, (byte)'M', (byte)'k', 0xCC,
                               0xFF, 0xFF, 250,
                               (byte)';', (byte)'c', (byte)'\n',
                               0xFF, 0xFF, 246,
                               0xFF, 0xFF, 251,
                               0xB1, 0xCC,
                           ]);
    }

    /// <summary>A tiny well-formed file: header, one printer metadata block, CRC-32 trailers.</summary>
    private static byte[] SyntheticFile(bool compressPrinterMetadata)
    {
        byte[] ini = "printer_model=MK4S\nnozzle_diameter=0.4\n"u8.ToArray();
        byte[] payload = compressPrinterMetadata ? ZLibCompress(ini) : ini;
        int headerSize = compressPrinterMetadata ? 12 : 8;
        byte[] block = new byte[headerSize + 2 + payload.Length];

        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2, 2), compressPrinterMetadata ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), (uint)ini.Length);

        if (compressPrinterMetadata)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8, 4), (uint)payload.Length);
        }

        payload.CopyTo(block.AsSpan(headerSize + 2));

        byte[] crc = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.HashToUInt32(block));

        byte[] fileHeader = new byte[10];

        "GCDE"u8.CopyTo(fileHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fileHeader.AsSpan(8, 2), 1);

        return [.. fileHeader, .. block, .. crc];
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
}
