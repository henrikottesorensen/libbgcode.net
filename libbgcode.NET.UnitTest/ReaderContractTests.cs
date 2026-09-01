using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;

using AwesomeAssertions;

using Xunit;

namespace libbgcode.NET.UnitTest;

/// <summary>
/// The exception contract under mutation: whatever the bytes, the reader answers with data or
/// null and never throws.
/// </summary>
public class ReaderContractTests
{
    /// <summary>
    /// Seeded mutations of a real file, so a failure reproduces exactly. Distilled from the
    /// fuzzing passes over this reader's predecessor, which found a seek throwing on an
    /// array-backed stream and an exception type slipping past the deflate handling; the fuzz
    /// harness in <c>libbgcode.NET.Fuzz</c> continues the open-ended search.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
                     Justification = "The randomness generates hostile test inputs; a fixed seed making failures reproducible is the point.")]
    public void NoMutationOfARealFileEscapesTheContract()
    {
        Random rng = new(20260831);
        byte[] seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "metadata-coreone-hf04-pla.bgcode"));
        uint[] interestingSizes = [0, 1, 1024, int.MaxValue, uint.MaxValue];

        for (int variant = 0; variant < 2000; variant++)
        {
            byte[] mutated = (byte[])seed.Clone();

            for (int edits = 1 + rng.Next(16); edits > 0 && mutated.Length > 0; edits--)
            {
                switch (rng.Next(4))
                {
                    case 0:
                        mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);

                        break;

                    case 1:
                        mutated[rng.Next(mutated.Length)] ^= (byte)(1 << rng.Next(8));

                        break;

                    case 2 when mutated.Length >= 4:
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            mutated.AsSpan(rng.Next(mutated.Length - 3), 4),
                            interestingSizes[rng.Next(interestingSizes.Length)]);

                        break;

                    default:
                        mutated = mutated[..rng.Next(mutated.Length + 1)];

                        break;
                }
            }

            // The assertion is that this returns at all: any escape fails the test.
            Exercise(mutated);
        }
    }

    private static void Exercise(byte[] file)
    {
        using MemoryStream stream = new(file, writable: false);

        BgcodeReaderOptions options = new() { MaxDataBytes = 4 * 1024 * 1024, VerifyChecksum = true };
        BgcodeReader? reader = BgcodeReader.Open(stream, options);

        if (reader is null)
        {
            return;
        }

        reader.FileHeader.ChecksumSize.Should().BeInRange(0, 4);

        while (reader.NextBlock() is { } block)
        {
            reader.ReadData(block);
            reader.ReadText(block);
        }
    }
}
