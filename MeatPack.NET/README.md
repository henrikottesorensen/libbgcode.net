# MeatPack.NET

A .NET encoder and decoder for **MeatPack**, [Scott Mudge's](https://github.com/scottmudge/OctoPrint-MeatPack)
G-code packing scheme (BSD-3-Clause; see `LICENSE.meatpack`), used by Marlin hosts over serial and
by Prusa's binary G-code container for its G-code blocks.

The scheme packs the fifteen most common G-code characters two to a byte through a 4-bit table;
`0b1111` in a nibble announces a full-width character, `0xFF` announces two, and a doubled `0xFF`
introduces a command byte toggling packing or the no-spaces table variant (in which `E` takes the
space's slot).

The packing is deliberately lossy — writers strip spaces from `G`-command lines and pad odd-length
lines with a newline — so the decoder also owes the reconstruction the reference implementations
perform: a space re-inserted before each parameter letter on a `G` line, the padding after a
newline dropped, and consecutive newlines collapsed. The output is equivalent G-code, not the
packer's original bytes. This behaviour is pinned against real PrusaSlicer output in the
[libbgcode.NET](https://github.com/henrikottesorensen/libbgcode.NET) repository this package is
developed in.

There is no malformed MeatPack — unknown commands are ignored, every other byte is packed data or
passthrough — so decoding never throws, and the output is bounded by a small constant multiple of
the input.

## Usage

```csharp
using MeatPack.NET;

byte[] gcodeText = MeatPackDecoder.Unpack(packedBytes);

// And the other direction - the reference packer's line treatment exactly:
// comments kept or dropped, inline comments cut, G-lines space-stripped with
// serial checksums recomputed, the no-spaces table variant on by default.
byte[] packed = MeatPackEncoder.Pack("G1 X10 Y20\nM104 S210\n", keepComments: true);
```

## License

LGPL-3.0-only. The MeatPack scheme itself is Scott Mudge's, BSD-3-Clause, carried in
`LICENSE.meatpack`.
