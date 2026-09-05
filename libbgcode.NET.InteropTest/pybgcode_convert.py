# SPDX-License-Identifier: MPL-2.0
# Copyright (c) 2026 Henrik O. Sørensen
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
# the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Conversion shim over pybgcode, Prusa's own binding of libbgcode.

Used by the interop tests as the reference implementation: convert a file in
either direction, exit 0 on success. ``to_binary`` optionally takes a G-code
encoding, a compression (applied to every block type), and a checksum choice,
so the tests can cover the format's whole encoding space. Kept to plumbing -
anything worth asserting lives in the C# tests.
"""

import sys

import pybgcode
from pybgcode import _bgcode

GCODE_ENCODINGS = {
    "none": _bgcode.GCodeEncodingType.none,
    "meatpack": _bgcode.GCodeEncodingType.MeatPack,
    "meatpack_comments": _bgcode.GCodeEncodingType.MeatPackComments,
}

COMPRESSIONS = {
    "none": _bgcode.CompressionType.none,
    "deflate": _bgcode.CompressionType.Deflate,
    "hs11": _bgcode.CompressionType.Heatshrink_11_4,
    "hs12": _bgcode.CompressionType.Heatshrink_12_4,
}

CHECKSUMS = {
    "none": _bgcode.ChecksumType.none,
    "crc32": _bgcode.ChecksumType.CRC32,
}


def main() -> int:
    mode, source, destination = sys.argv[1:4]

    infile = pybgcode.open(source, "rb")
    outfile = pybgcode.open(destination, "wb")

    if mode == "to_ascii":
        result = pybgcode.from_binary_to_ascii(infile, outfile, True)
    elif mode == "to_binary":
        config = pybgcode.get_config()
        if len(sys.argv) > 4:
            config.gcode_encoding = GCODE_ENCODINGS[sys.argv[4]]
        if len(sys.argv) > 5:
            compression = COMPRESSIONS[sys.argv[5]]
            config.compression.file_metadata = compression
            config.compression.printer_metadata = compression
            config.compression.print_metadata = compression
            config.compression.slicer_metadata = compression
            config.compression.gcode = compression
        if len(sys.argv) > 6:
            config.checksum = CHECKSUMS[sys.argv[6]]
        result = pybgcode.from_ascii_to_binary(infile, outfile, config)
    else:
        print(f"unknown mode: {mode}", file=sys.stderr)
        return 2

    pybgcode.close(outfile)
    pybgcode.close(infile)

    return 0 if result == pybgcode.EResult.Success else 1


if __name__ == "__main__":
    sys.exit(main())
