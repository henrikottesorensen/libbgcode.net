# libbgcode.NET

[![CI](https://github.com/henrikottesorensen/libbgcode.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/henrikottesorensen/libbgcode.NET/actions/workflows/ci.yml)

A .NET reader and writer for Prusa's **binary G-code** (`bgcode`) container format: the file
header, lazy block enumeration, per-block compression (deflate, heatshrink), MeatPack G-code
encoding and decoding, and CRC-32 checksums.

Implemented from the format's [published specification](https://github.com/prusa3d/libbgcode/blob/main/doc/specifications.md).
The facts the specification does not state — that deflate payloads are zlib-wrapped, that the
CRC-32 covers each block from its header through its data, the reconstruction rules MeatPack's
lossy packing demands of a decoder, and the JSON metadata encoding PrusaSlicer 3 adds (it writes
its slicer metadata twice, a legacy INI block and a JSON block) — are established from real
PrusaSlicer output and pinned by interop tests against pybgcode, Prusa's own binding of the
reference implementation, at the exact commit PrusaSlicer 3.0.0-alpha11 pins.

The library targets `net10.0` and depends on
[HeatshrinkDotNet](https://github.com/henrikottesorensen/HeatshrinkDotNet) for the heatshrink
blocks.

## Untrusted input

The reader is written for files anybody may have uploaded. Every size on the wire is treated as
attacker-influenced: nothing is allocated from a declared size without a caller-configurable
bound, a payload must decompress to exactly its declared size, and the block walk refuses a file
whose offsets cannot be trusted. A malformed file yields `null` from whichever call discovered it,
never an exception — a contract held in place by a seeded mutation test and a coverage-guided
fuzzing harness (`libbgcode.NET.Fuzz`).

## Usage

```csharp
using libbgcode.NET;

using FileStream file = File.OpenRead("model.bgcode");

BgcodeReader? reader = BgcodeReader.Open(file);

if (reader is null)
{
    // Not a readable binary G-code file. Note that the name decides nothing:
    // PrusaSlicer routinely writes binary G-code to files called .gcode, so
    // dispatch on BgcodeReader.Magic, never on the extension.
    return;
}

while (reader.NextBlock() is { } block)
{
    switch (block.Type)
    {
        case BgcodeBlockType.PrinterMetadata:
            // "printer_model=COREONE\nnozzle_diameter=0.4\n..." - INI, one pair per line.
            string? ini = reader.ReadText(block);
            break;

        case BgcodeBlockType.Thumbnail:
            // block.Thumbnail carries format and pixel size; ReadData returns the image bytes.
            byte[]? image = reader.ReadData(block);
            break;

        case BgcodeBlockType.GCode:
            // Decompressed and MeatPack-decoded to plain G-code text.
            string? gcode = reader.ReadText(block);
            break;
    }
}
```

Blocks are descriptors: `NextBlock()` reads headers only and seeks past payloads, so walking to
the one block you want costs a few small reads regardless of file size. The specification orders
blocks (file metadata, printer metadata, thumbnails, print metadata, slicer metadata, G-code), so
a reader after early metadata can stop at the first later type.

`BgcodeReaderOptions` bounds what a payload may cost (`MaxDataBytes`, default 64 MiB) and turns on
per-block CRC-32 verification (`VerifyChecksum`, off by default).

### Writing

```csharp
using FileStream file = File.Create("model.bgcode");
using BgcodeWriter writer = new(file);          // CRC-32 trailers by default

writer.WritePrinterMetadata("printer_model=COREONE\nnozzle_diameter=0.4\n");
writer.WriteThumbnail(new BgcodeThumbnailParameters(BgcodeThumbnailFormat.Png, 16, 16), pngBytes);
writer.WritePrintMetadata("estimated printing time (normal mode)=34s\n");
writer.WriteSlicerMetadata("layer_height=0.2\n");
writer.WriteGCode(gcodeText);                   // MeatPack with comments, heatshrink 12/4 - the slicers' defaults
```

The writer enforces the specification's block order and the reference reader's mandatory chain
(printer, print and slicer metadata before any G-code), so it cannot produce a file the reference
implementation refuses; a call out of order throws before it writes. G-code is cut into blocks at
64 KiB of source on line boundaries, each with fresh MeatPack state, exactly as the reference
binarizer cuts it. Every compression and encoding the format allows is available per block; the
defaults are what PrusaSlicer writes.

MeatPack lives in its own package, [MeatPack.NET](MeatPack.NET/README.md), developed in this
repository — `MeatPackDecoder.Unpack` and `MeatPackEncoder.Pack` work on payloads from anywhere,
serial hosts included; `libbgcode.NET` depends on it for the G-code blocks.

## What this is not

It does not parse the G-code itself — it hands you the text — and it does not convert whole
ASCII G-code files to and from the container the way the reference `bgcode` tool does; it reads
and writes blocks.

## Licenses

- **libbgcode.NET** is licensed under the [LGPL-3.0-only](LICENSE).
- The MeatPack scheme is [Scott Mudge's](https://github.com/scottmudge/OctoPrint-MeatPack)
  (BSD-3-Clause, [LICENSE.meatpack](LICENSE.meatpack)); the decoder here is an independent
  implementation of the documented scheme, including the reconstruction behaviour the bgcode
  variant expects.
- The binary G-code format and its specification are [Prusa's](https://github.com/prusa3d/libbgcode);
  this library is an independent implementation of that published format and shares no code with
  `libbgcode`.
