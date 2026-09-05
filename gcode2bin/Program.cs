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

        case "--stdin":
            fromStdin = true;

            break;

        case "--stdout":
            toStdout = true;

            break;

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
    output = Path.ChangeExtension(input!, ".bgcode");
}

if (input is not null && output is not null && !toStdout
    && string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
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
    using StreamReader ascii = fromStdin
        ? new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
        : new StreamReader(input!, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    using Stream binary = toStdout
        ? Console.OpenStandardOutput()
        : new FileStream(output!, FileMode.Create, FileAccess.Write, FileShare.None);

    BgcodeConverter.ToBinary(ascii, binary, options);

    return 0;
}
catch (IOException failure)
{
    Console.Error.WriteLine($"gcode2bin: {failure.Message}");
    DiscardPartialOutput(toStdout ? null : output);

    return 1;
}
catch (UnauthorizedAccessException failure)
{
    Console.Error.WriteLine($"gcode2bin: {failure.Message}");

    return 1;
}
catch (FormatException failure)
{
    Console.Error.WriteLine($"gcode2bin: {(fromStdin ? "stdin" : input)}: a thumbnail section is not valid base64 ({failure.Message})");
    DiscardPartialOutput(toStdout ? null : output);

    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: gcode2bin <input.gcode> [output.bgcode] [--stdout] [options]");
    Console.Error.WriteLine("       gcode2bin --stdin [output.bgcode] [options]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Converts ASCII G-code in PrusaSlicer's layout to binary G-code. The defaults are");
    Console.Error.WriteLine("what PrusaSlicer writes: MeatPack with comments kept, heatshrink 12/4 on the");
    Console.Error.WriteLine("G-code, deflate on the print and slicer metadata, CRC-32 on every block. The");
    Console.Error.WriteLine("output defaults to the input's name with a .bgcode extension, or to stdout when");
    Console.Error.WriteLine("the input is stdin. Diagnostics go to stderr.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --drop-comments   pack the G-code without its comment lines");
    Console.Error.WriteLine("  --plain           store the G-code as plain text rather than MeatPack");
    Console.Error.WriteLine("  --no-compression  store every block uncompressed");
    Console.Error.WriteLine("  --no-checksum     write no CRC-32 trailers");

    return 2;
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
