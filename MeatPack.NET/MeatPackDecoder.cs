using System;
using System.IO;

namespace MeatPack.NET;

/// <summary>
/// Decodes MeatPack-packed G-code, the packing scheme Marlin hosts use over serial and binary G-code uses for its G-code blocks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scheme is Scott Mudge's MeatPack</b> (BSD-3-Clause; see <c>LICENSE.meatpack</c>): the
/// fifteen most common G-code characters pack two to a byte through a 4-bit table, <c>0b1111</c>
/// in a nibble announces a full-width character following the pair, <c>0xFF</c> announces two, and
/// a doubled <c>0xFF</c> introduces a command byte that toggles packing or the no-spaces table
/// variant, in which <c>E</c> takes the table slot of the space.
/// </para>
/// <para>
/// <b>The packing is deliberately lossy, and the decoder owes the reconstruction.</b> Writers
/// strip the spaces from <c>G</c>-command lines and pad odd-length lines with a newline, so this
/// decoder - matching the wire behaviour of the reference implementation, pinned by the interop
/// tests - re-inserts a space before each parameter letter on a line starting with <c>G</c>,
/// drops the second character of a pair whose first is a newline, and collapses consecutive
/// newlines. The output is equivalent G-code, not the packer's original input bytes.
/// </para>
/// <para>
/// <b>Any byte sequence decodes to something.</b> There is no malformed MeatPack - unknown
/// commands are ignored and every other byte is either packed data or passthrough - so this never
/// refuses. The output is at most a small constant multiple of the input (two characters per
/// packed byte, at most one re-inserted space each), so the caller's bound on the input bounds
/// the output.
/// </para>
/// </remarks>
public static class MeatPackDecoder
{
    private const byte SignalByte = 0xFF;

    private const byte CommandEnablePacking = 251;

    private const byte CommandDisablePacking = 250;

    private const byte CommandResetAll = 249;

    private const byte CommandEnableNoSpaces = 247;

    private const byte CommandDisableNoSpaces = 246;

    /// <summary>The 4-bit table, indexed by packed value; slot 11 is the space the no-spaces variant reassigns.</summary>
    private static ReadOnlySpan<byte> Table => "0123456789. \nGX"u8;

    /// <summary>
    /// The parameter letters that get a space re-inserted before them on a <c>G</c> line, exactly
    /// the set the reference implementation restores: the packer stripped those spaces, and
    /// G-code parsers need them back.
    /// </summary>
    private static ReadOnlySpan<byte> GLineParameters => "XYZEFIJRSGPWHCA"u8;

    /// <summary>The G-code text a packed payload carries.</summary>
    /// <param name="packed">The payload, as stored in a G-code block after decompression.</param>
    public static byte[] Unpack(ReadOnlySpan<byte> packed)
    {
        MemoryStream output = new(packed.Length * 2);

        bool unpacking = false;
        bool noSpaces = false;
        bool commandPending = false;
        int signalCount = 0;
        int fullCharsQueued = 0;
        byte bufferedSecond = 0;
        bool lineStartsWithG = false;

        void Emit(byte c)
        {
            bool newLineG = false;

            if (c == (byte)'G' && (output.Length == 0 || LastByte(output) == (byte)'\n'))
            {
                lineStartsWithG = true;
                newLineG = true;
            }
            else if (c == (byte)'\n')
            {
                lineStartsWithG = false;
            }

            if (!newLineG
                && lineStartsWithG
                && (output.Length == 0 || LastByte(output) != (byte)' ')
                && GLineParameters.IndexOf(c) >= 0)
            {
                output.WriteByte((byte)' ');
            }

            if (c != (byte)'\n' || output.Length == 0 || LastByte(output) != (byte)'\n')
            {
                output.WriteByte(c);
            }
        }

        byte TableChar(int value)
        {
            return value == 11 && noSpaces ? (byte)'E' : Table[value];
        }

        void Receive(byte c)
        {
            if (!unpacking)
            {
                Emit(c);

                return;
            }

            if (fullCharsQueued > 0)
            {
                Emit(c);

                if (bufferedSecond != 0)
                {
                    Emit(bufferedSecond);
                    bufferedSecond = 0;
                }

                fullCharsQueued--;

                return;
            }

            int low = c & 0xF;
            int high = (c >> 4) & 0xF;

            if (low == 0xF)
            {
                fullCharsQueued++;

                if (high == 0xF)
                {
                    fullCharsQueued++;
                }
                else
                {
                    bufferedSecond = TableChar(high);
                }

                return;
            }

            byte first = TableChar(low);

            Emit(first);

            // A pair whose first character is the newline ends the line; the second slot is the
            // writer's padding and is dropped, full-width announcement included.
            if (first == (byte)'\n')
            {
                return;
            }

            if (high == 0xF)
            {
                fullCharsQueued++;
            }
            else
            {
                Emit(TableChar(high));
            }
        }

        void Command(byte c)
        {
            switch (c)
            {
                case CommandEnablePacking:
                    unpacking = true;

                    break;

                case CommandDisablePacking:
                case CommandResetAll:
                    unpacking = false;

                    break;

                case CommandEnableNoSpaces:
                    noSpaces = true;

                    break;

                case CommandDisableNoSpaces:
                    noSpaces = false;

                    break;

                default:
                    // Unknown commands are ignored, matching the reference implementation.
                    break;
            }
        }

        foreach (byte b in packed)
        {
            if (b == SignalByte)
            {
                if (signalCount > 0)
                {
                    commandPending = true;
                    signalCount = 0;
                }
                else
                {
                    signalCount++;
                }
            }
            else
            {
                if (commandPending)
                {
                    Command(b);
                    commandPending = false;
                }
                else
                {
                    if (signalCount > 0)
                    {
                        Receive(SignalByte);
                        signalCount = 0;
                    }

                    Receive(b);
                }
            }
        }

        return output.ToArray();
    }

    private static byte LastByte(MemoryStream output)
    {
        return output.GetBuffer()[output.Length - 1];
    }
}
