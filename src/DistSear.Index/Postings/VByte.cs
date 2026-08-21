using System.Buffers;

namespace DistSear.Index.Postings;

/// <summary>
/// Variable-byte integer coding: seven payload bits per byte, the high bit marking the final byte.
/// Small values cost one byte, which is what makes delta-gap encoded postings compact — after
/// gapping, most doc-id deltas in a dense postings list fit in a single byte.
/// </summary>
public static class VByte
{
    /// <summary>Maximum bytes a 32-bit value can occupy (ceil(32 / 7)).</summary>
    public const int MaxBytes32 = 5;

    public static void Write(IBufferWriter<byte> writer, uint value)
    {
        Span<byte> scratch = stackalloc byte[MaxBytes32];
        var written = Write(scratch, value);
        writer.Write(scratch[..written]);
    }

    /// <summary>Writes <paramref name="value"/> and returns the number of bytes used.</summary>
    public static int Write(Span<byte> destination, uint value)
    {
        var index = 0;

        while (value >= 0x80)
        {
            destination[index++] = (byte)(value & 0x7F);
            value >>= 7;
        }

        // High bit set marks the terminating byte.
        destination[index++] = (byte)(value | 0x80);
        return index;
    }

    /// <summary>Reads one value starting at <paramref name="offset"/>, advancing it past the value.</summary>
    public static uint Read(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = 0;
        var shift = 0;

        while (true)
        {
            var b = source[offset++];

            if ((b & 0x80) != 0)
            {
                return value | ((uint)(b & 0x7F) << shift);
            }

            value |= (uint)b << shift;
            shift += 7;
        }
    }

    /// <summary>Bytes required to encode <paramref name="value"/>.</summary>
    public static int SizeOf(uint value)
    {
        var size = 1;

        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }
}
