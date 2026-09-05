// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 Henrik O. Sørensen
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

using HeatshrinkDotNet;

namespace libbgcode.NET;

/// <summary>
/// Heatshrink for one block payload: decodes to exactly the declared size, and encodes as the raw
/// stream a block stores.
/// </summary>
internal static class HeatshrinkBlock
{
    /// <summary>The heatshrink stream for <paramref name="payload"/>, with no framing of any kind.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload, int windowBits, int lookaheadBits)
    {
        HeatshrinkEncoder encoder = new(windowBits, lookaheadBits);
        byte[] input = payload.ToArray();
        byte[] chunk = new byte[4096];

        using System.IO.MemoryStream output = new(input.Length + (input.Length / 8) + 16);

        int sunk = 0;
        bool finished = false;

        while (!finished)
        {
            if (sunk < input.Length)
            {
                encoder.Sink(input, sunk, input.Length - sunk, out int count);
                sunk += count;
            }

            EncoderPollResult poll;

            do
            {
                poll = encoder.Poll(chunk, out int polled);
                output.Write(chunk, 0, polled);
            }
            while (poll == EncoderPollResult.More);

            if (sunk == input.Length)
            {
                finished = encoder.Finish() == EncoderFinishResult.Done;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// The decompressed payload, or null if the stream does not decode to exactly
    /// <paramref name="uncompressedSize"/> bytes: producing fewer is truncation, and producing
    /// more means the declared size was a lie, which the bound turns into a refusal instead of an
    /// unbounded allocation.
    /// </summary>
    public static byte[]? Decode(byte[] data, uint uncompressedSize, int windowBits, int lookaheadBits)
    {
        HeatshrinkDecoder decoder = new(inputBufferSize: 4096, windowBits, lookaheadBits);
        byte[] output = new byte[uncompressedSize];
        Span<byte> probe = stackalloc byte[1];
        int produced = 0;
        int consumed = 0;

        while (true)
        {
            int producedBefore = produced;
            int consumedBefore = consumed;

            if (consumed < data.Length)
            {
                decoder.Sink(data.AsSpan(consumed), out int sunk);
                consumed += sunk;
            }

            DecoderPollResult poll;

            do
            {
                if (produced == output.Length)
                {
                    // The buffer is full. One more byte appearing means the declared size lied;
                    // poll into the one-byte probe to find out.
                    poll = decoder.Poll(probe, out int extra);

                    if (extra > 0)
                    {
                        return null;
                    }
                }
                else
                {
                    poll = decoder.Poll(output.AsSpan(produced), out int polled);
                    produced += polled;
                }
            }
            while (poll == DecoderPollResult.More && produced < output.Length);

            if (consumed == data.Length)
            {
                DecoderFinishResult finish = decoder.Finish();

                if (finish == DecoderFinishResult.Done)
                {
                    return produced == output.Length ? output : null;
                }

                if (finish != DecoderFinishResult.More)
                {
                    return null;
                }

                // The input is exhausted, more output is promised, and this pass neither consumed
                // nor produced anything: the decoder is stalled, which is a truncated stream.
                if (produced == producedBefore && consumed == consumedBefore)
                {
                    return null;
                }
            }
        }
    }
}
