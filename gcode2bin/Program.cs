using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using libbgcode.NET;

List<string> positional = [];
bool noChecksum = false;
bool dropComments = false;
bool plain = false;
bool noCompression = false;

foreach (string argument in args)
{
    switch (argument)
    {
        case "-h" or "--help":
            return Usage();

        case "--no-checksum":
            noChecksum = true;

            break;

        case "--drop-comments":
            dropComments = true;

            break;

        case "--plain":
            plain = true;

            break;

        case "--no-compression":
            noCompression = true;

            break;

        default:
            if (argument.StartsWith('-'))
            {
                Console.Error.WriteLine($"gcode2bin: unknown option {argument}");

                return Usage();
            }

            positional.Add(argument);

            break;
    }
}

if (positional.Count is < 1 or > 2)
{
    return Usage();
}

string input = positional[0];
string output = positional.Count == 2 ? positional[1] : Path.ChangeExtension(input, ".bgcode");

if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
{
    Console.Error.WriteLine("gcode2bin: the output would overwrite the input; name a different output file.");

    return 2;
}

BgcodeConverterOptions options = new()
{
    ChecksumType = noChecksum ? BgcodeChecksumType.None : BgcodeChecksumType.Crc32,
    GCodeEncoding = plain ? BgcodeGCodeEncoding.None
                  : dropComments ? BgcodeGCodeEncoding.MeatPack
                  : BgcodeGCodeEncoding.MeatPackWithComments,
    PrintMetadataCompression = noCompression ? BgcodeCompression.None : BgcodeCompression.Deflate,
    SlicerMetadataCompression = noCompression ? BgcodeCompression.None : BgcodeCompression.Deflate,
    GCodeCompression = noCompression ? BgcodeCompression.None : BgcodeCompression.Heatshrink12,
};

try
{
    using StreamReader ascii = new(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    using FileStream binary = new(output, FileMode.Create, FileAccess.Write, FileShare.None);

    BgcodeConverter.ToBinary(ascii, binary, options);

    return 0;
}
catch (IOException failure)
{
    Console.Error.WriteLine($"gcode2bin: {failure.Message}");
    DiscardPartialOutput(output);

    return 1;
}
catch (UnauthorizedAccessException failure)
{
    Console.Error.WriteLine($"gcode2bin: {failure.Message}");

    return 1;
}
catch (FormatException failure)
{
    Console.Error.WriteLine($"gcode2bin: {input}: a thumbnail section is not valid base64 ({failure.Message})");
    DiscardPartialOutput(output);

    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: gcode2bin <input.gcode> [output.bgcode] [options]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Converts ASCII G-code in PrusaSlicer's layout to binary G-code. The defaults are");
    Console.Error.WriteLine("what PrusaSlicer writes: MeatPack with comments kept, heatshrink 12/4 on the");
    Console.Error.WriteLine("G-code, deflate on the print and slicer metadata, CRC-32 on every block. The");
    Console.Error.WriteLine("output defaults to the input's name with a .bgcode extension.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --drop-comments   pack the G-code without its comment lines");
    Console.Error.WriteLine("  --plain           store the G-code as plain text rather than MeatPack");
    Console.Error.WriteLine("  --no-compression  store every block uncompressed");
    Console.Error.WriteLine("  --no-checksum     write no CRC-32 trailers");

    return 2;
}

static void DiscardPartialOutput(string path)
{
    try
    {
        File.Delete(path);
    }
    catch (IOException)
    {
        // The half-written file is the lesser problem; the error already reported is the real one.
    }
    catch (UnauthorizedAccessException)
    {
        // Likewise.
    }
}
