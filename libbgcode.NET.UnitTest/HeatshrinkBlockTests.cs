// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

using AwesomeAssertions;

using Xunit;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// The heatshrink block path, driven with streams built by the real heatshrink encoder - the one
/// compression whose refusal branches a sliced fixture cannot reach, because a slicer only ever
/// writes valid streams at window 12.
/// </summary>
public class HeatshrinkBlockTests
{
    /// <summary>
    /// Both windows the specification allows round-trip; the fixture files only ever exercise 12.
    /// </summary>
    [Theory]
    [InlineData(2, 11)]
    [InlineData(3, 12)]
    public void RoundTripsBothWindows(ushort compression, int windowBits)
    {
        byte[] payload = RepetitivePayload(5000);
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits, lookaheadBits: 4);
        byte[] file = TestBgcode.MetadataBlockFile(compression, compressed, (uint)payload.Length);

        ReadSingleBlock(file).Should().Equal(payload);
    }

    /// <summary>An incompressible payload - the encoder expands it - still round-trips.</summary>
    [Fact]
    public void RoundTripsAnIncompressiblePayload()
    {
        byte[] payload = RandomPayload(2048, seed: 1);
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits: 12, lookaheadBits: 4);
        byte[] file = TestBgcode.MetadataBlockFile(3, compressed, (uint)payload.Length);

        compressed.Length.Should().BeGreaterThan(payload.Length, "random bytes do not compress");
        ReadSingleBlock(file).Should().Equal(payload);
    }

    /// <summary>
    /// The declared size understates the stream: one byte too many appears, and the reader must
    /// refuse rather than trust either number. This is the probe branch of the decode loop.
    /// </summary>
    [Fact]
    public void RefusesAStreamProducingMoreThanDeclared()
    {
        byte[] payload = RepetitivePayload(5000);
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits: 12, lookaheadBits: 4);
        byte[] file = TestBgcode.MetadataBlockFile(3, compressed, (uint)payload.Length - 1);

        ReadSingleBlock(file).Should().BeNull();
    }

    /// <summary>
    /// The gross version of the same lie - half the real size declared - so the overflow is
    /// discovered mid-stream rather than at the final byte.
    /// </summary>
    [Fact]
    public void RefusesAStreamProducingFarMoreThanDeclared()
    {
        byte[] payload = RepetitivePayload(5000);
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits: 12, lookaheadBits: 4);
        byte[] file = TestBgcode.MetadataBlockFile(3, compressed, (uint)payload.Length / 2);

        ReadSingleBlock(file).Should().BeNull();
    }

    /// <summary>The other lie: the stream ends before producing what the header declared.</summary>
    [Fact]
    public void RefusesAStreamProducingLessThanDeclared()
    {
        byte[] payload = RepetitivePayload(5000);
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits: 12, lookaheadBits: 4);
        byte[] file = TestBgcode.MetadataBlockFile(3, compressed, (uint)payload.Length + 1);

        ReadSingleBlock(file).Should().BeNull();
    }

    /// <summary>
    /// A truncated stream - the shape an interrupted write leaves - stalls the decoder mid-symbol
    /// and must come back as a refusal, not a hang or a short answer.
    /// </summary>
    [Fact]
    public void RefusesATruncatedStream()
    {
        byte[] payload = RepetitivePayload(5000);
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits: 12, lookaheadBits: 4);
        byte[] file = TestBgcode.MetadataBlockFile(3, compressed[..(compressed.Length / 2)], (uint)payload.Length);

        ReadSingleBlock(file).Should().BeNull();
    }

    /// <summary>Decoding with the wrong window parameters yields wrong bytes, never an escape.</summary>
    [Fact]
    public void TheWrongWindowIsWrongDataNotAnException()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('a', 600) + new string('b', 600));
        byte[] compressed = TestBgcode.HeatshrinkCompress(payload, windowBits: 11, lookaheadBits: 4);

        // Declared as window 12 although encoded at 11.
        byte[] file = TestBgcode.MetadataBlockFile(3, compressed, (uint)payload.Length);

        byte[]? read = ReadSingleBlock(file);

        if (read is not null)
        {
            read.Length.Should().Be(payload.Length, "a produced payload is always exactly the declared size");
            read.Should().NotEqual(payload, "the window mismatch must corrupt, or this test proves nothing");
        }
    }

    /// <summary>
    /// Arbitrary bytes are usually a decodable heatshrink stream (literals and backrefs into the
    /// zeroed window), so the contract is not refusal - it is: exactly the declared size, or null,
    /// and never an exception.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
                     Justification = "The randomness generates hostile test inputs; a fixed seed making failures reproducible is the point.")]
    public void GarbageStreamsHoldTheContract()
    {
        Random rng = new(20260901);

        for (int round = 0; round < 200; round++)
        {
            byte[] garbage = new byte[1 + rng.Next(512)];

            rng.NextBytes(garbage);

            uint declared = (uint)rng.Next(1, 4096);
            byte[] file = TestBgcode.MetadataBlockFile(3, garbage, declared);

            byte[]? read = ReadSingleBlock(file);

            if (read is not null)
            {
                read.Length.Should().Be((int)declared);
            }
        }
    }

    private static byte[]? ReadSingleBlock(byte[] file)
    {
        using MemoryStream stream = new(file, writable: false);

        BgcodeReader reader = BgcodeReader.Open(stream)!;

        return reader.ReadData(reader.NextBlock()!);
    }

    private static byte[] RepetitivePayload(int size)
    {
        byte[] payload = new byte[size];

        for (int i = 0; i < size; i++)
        {
            payload[i] = (byte)("abcabcdabcde"[i % 12]);
        }

        return payload;
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
                     Justification = "Fixed-seed data generation for reproducible tests.")]
    private static byte[] RandomPayload(int size, int seed)
    {
        byte[] payload = new byte[size];

        new Random(seed).NextBytes(payload);

        return payload;
    }
}
