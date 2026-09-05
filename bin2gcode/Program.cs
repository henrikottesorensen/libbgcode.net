using System;
using System.IO;
using System.Text;

using libbgcode.NET;

if (args.Length is < 1 or > 2 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: bin2gcode <input.bgcode> [output.gcode]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Converts binary G-code to ASCII G-code in PrusaSlicer's layout, verifying every");
    Console.Error.WriteLine("block's checksum on the way. The output defaults to the input's name with a .gcode");
    Console.Error.WriteLine("extension.");

    return 2;
}

string input = args[0];
string output = args.Length == 2 ? args[1] : Path.ChangeExtension(input, ".gcode");

if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
{
    Console.Error.WriteLine("bin2gcode: the output would overwrite the input; name a different output file.");

    return 2;
}

try
{
    using FileStream binary = new(input, FileMode.Open, FileAccess.Read, FileShare.Read);
    using StreamWriter ascii = new(output, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    BgcodeConverter.ToAscii(binary, ascii);

    return 0;
}
catch (InvalidDataException failure)
{
    Console.Error.WriteLine($"bin2gcode: {input}: {failure.Message}");
    DiscardPartialOutput(output);

    return 1;
}
catch (IOException failure)
{
    Console.Error.WriteLine($"bin2gcode: {failure.Message}");
    DiscardPartialOutput(output);

    return 1;
}
catch (UnauthorizedAccessException failure)
{
    Console.Error.WriteLine($"bin2gcode: {failure.Message}");

    return 1;
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
