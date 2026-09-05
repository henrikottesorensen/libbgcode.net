// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using libbgcode.NET;

List<string> positional = [];
bool fromStdin = false;
bool toStdout = false;

foreach (string argument in args)
{
    switch (argument)
    {
        case "-h" or "--help":
            return Usage();

        case "--stdin":
            fromStdin = true;

            break;

        case "--stdout":
            toStdout = true;

            break;

        default:
            if (argument.StartsWith('-'))
            {
                Console.Error.WriteLine($"bin2gcode: unknown option {argument}");

                return Usage();
            }

            positional.Add(argument);

            break;
    }
}

// Without --stdin the first name is the input; the next name, if any, is the output. Reading
// stdin leaves no name to derive an output from, so the output then defaults to stdout.
string? input = fromStdin ? null : positional.Count > 0 ? positional[0] : null;
int outputIndex = fromStdin ? 0 : 1;
string? output = positional.Count > outputIndex ? positional[outputIndex] : null;

if ((!fromStdin && input is null) || positional.Count > outputIndex + 1)
{
    return Usage();
}

if (output is null && (fromStdin || toStdout))
{
    toStdout = true;
}
else if (output is null)
{
    output = Path.ChangeExtension(input!, ".gcode");
}

if (input is not null && output is not null && !toStdout
    && string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
{
    Console.Error.WriteLine("bin2gcode: the output would overwrite the input; name a different output file.");

    return 2;
}

try
{
    using Stream binary = fromStdin ? BufferStdin() : new FileStream(input!, FileMode.Open, FileAccess.Read, FileShare.Read);
    using StreamWriter ascii = toStdout
        ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        : new StreamWriter(output!, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    BgcodeConverter.ToAscii(binary, ascii);

    return 0;
}
catch (InvalidDataException failure)
{
    Console.Error.WriteLine($"bin2gcode: {(fromStdin ? "stdin" : input)}: {failure.Message}");
    DiscardPartialOutput(toStdout ? null : output);

    return 1;
}
catch (IOException failure)
{
    Console.Error.WriteLine($"bin2gcode: {failure.Message}");
    DiscardPartialOutput(toStdout ? null : output);

    return 1;
}
catch (UnauthorizedAccessException failure)
{
    Console.Error.WriteLine($"bin2gcode: {failure.Message}");

    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: bin2gcode <input.bgcode> [output.gcode] [--stdout]");
    Console.Error.WriteLine("       bin2gcode --stdin [output.gcode]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Converts binary G-code to ASCII G-code in PrusaSlicer's layout, verifying every");
    Console.Error.WriteLine("block's checksum on the way. The output defaults to the input's name with a .gcode");
    Console.Error.WriteLine("extension, or to stdout when the input is stdin. Diagnostics go to stderr.");

    return 2;
}

// The reader walks the container by seeking, and stdin cannot seek; the conversion reads its
// whole input anyway, so buffering it costs nothing extra.
static MemoryStream BufferStdin()
{
    using Stream stdin = Console.OpenStandardInput();

    MemoryStream buffer = new();

    stdin.CopyTo(buffer);
    buffer.Position = 0;

    return buffer;
}

static void DiscardPartialOutput(string? path)
{
    if (path is null)
    {
        return;
    }

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
