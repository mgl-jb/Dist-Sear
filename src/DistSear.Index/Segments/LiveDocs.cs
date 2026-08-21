using System.Numerics;

namespace DistSear.Index.Segments;

/// <summary>
/// Tombstone bitset marking which documents in a segment are still live. Segments are immutable, so
/// a delete or an update cannot rewrite the postings: it clears the bit here, and the document is
/// filtered out at collection time. The space is reclaimed when the segment is merged.
/// </summary>
public sealed class LiveDocs
{
    private readonly ulong[] _bits;

    public LiveDocs(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        Capacity = capacity;
        _bits = new ulong[(capacity + 63) / 64];
        _bits.AsSpan().Fill(ulong.MaxValue);
        LiveCount = capacity;

        // Clear the padding bits above Capacity so PopCount stays honest.
        var remainder = capacity % 64;
        if (remainder != 0 && _bits.Length > 0)
        {
            _bits[^1] = (1UL << remainder) - 1;
        }
    }

    private LiveDocs(ulong[] bits, int capacity, int liveCount)
    {
        _bits = bits;
        Capacity = capacity;
        LiveCount = liveCount;
    }

    public int Capacity { get; }

    public int LiveCount { get; private set; }

    public bool HasDeletions => LiveCount < Capacity;

    public bool IsLive(int docId) =>
        (uint)docId < (uint)Capacity && (_bits[docId >> 6] & (1UL << (docId & 63))) != 0;

    /// <summary>Marks a document dead. Returns false when it was already dead.</summary>
    public bool Delete(int docId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Capacity - docId);

        var word = docId >> 6;
        var mask = 1UL << (docId & 63);

        if ((_bits[word] & mask) == 0)
        {
            return false;
        }

        _bits[word] &= ~mask;
        LiveCount--;
        return true;
    }

    /// <summary>
    /// Point-in-time copy. Readers take a snapshot so that deletions applied after a search began
    /// cannot change the result set underneath it.
    /// </summary>
    public LiveDocs Snapshot() => new((ulong[])_bits.Clone(), Capacity, LiveCount);

    public int CountLive()
    {
        var total = 0;

        foreach (var word in _bits)
        {
            total += BitOperations.PopCount(word);
        }

        return total;
    }
}
