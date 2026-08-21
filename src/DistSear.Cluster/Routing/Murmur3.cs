using System.Buffers.Binary;
using System.Text;

namespace DistSear.Cluster.Routing;

/// <summary>
/// MurmurHash3, 32-bit variant.
///
/// Implemented rather than taken from a hashing library because document routing must be
/// byte-for-byte reproducible: the shard a document lands in is baked into its Cosmos partition key
/// and cannot change between versions, machines or runtimes. This is also the hash Elasticsearch
/// routes with, so shard assignments are comparable with the conventional implementation.
/// </summary>
public static class Murmur3
{
    private const uint C1 = 0xcc9e2d51;
    private const uint C2 = 0x1b873593;

    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var hash = seed;
        var length = data.Length;
        var index = 0;

        // Body: consume four bytes at a time.
        while (length - index >= 4)
        {
            var block = BinaryPrimitives.ReadUInt32LittleEndian(data[index..]);
            index += 4;

            block *= C1;
            block = RotateLeft(block, 15);
            block *= C2;

            hash ^= block;
            hash = RotateLeft(hash, 13);
            hash = (hash * 5) + 0xe6546b64;
        }

        // Tail: the remaining one to three bytes.
        uint remainder = 0;

        switch (length - index)
        {
            case 3:
                remainder ^= (uint)data[index + 2] << 16;
                goto case 2;
            case 2:
                remainder ^= (uint)data[index + 1] << 8;
                goto case 1;
            case 1:
                remainder ^= data[index];
                remainder *= C1;
                remainder = RotateLeft(remainder, 15);
                remainder *= C2;
                hash ^= remainder;
                break;
        }

        // Finalisation mix, which spreads the influence of every input bit across the output.
        hash ^= (uint)length;
        hash ^= hash >> 16;
        hash *= 0x85ebca6b;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35;
        hash ^= hash >> 16;

        return hash;
    }

    public static uint Hash32(string text, uint seed = 0)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);

        if (maxBytes <= 256)
        {
            Span<byte> buffer = stackalloc byte[256];
            var written = Encoding.UTF8.GetBytes(text, buffer);
            return Hash32(buffer[..written], seed);
        }

        return Hash32(Encoding.UTF8.GetBytes(text), seed);
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
