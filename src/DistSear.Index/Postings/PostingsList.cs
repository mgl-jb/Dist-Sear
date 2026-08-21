using System.Buffers;

namespace DistSear.Index.Postings;

/// <summary>A jump point into the encoded postings, letting <c>Advance</c> skip whole blocks.</summary>
public readonly record struct SkipEntry(int DocId, int Offset);

/// <summary>
/// The encoded postings for one term in one field.
///
/// Layout per posting, all variable-byte:
/// <code>
///   [docId gap] [term frequency] ( [positions byte length] [position gap]* )?
/// </code>
/// Doc ids are stored as gaps from the previous posting, which keeps them small and therefore
/// mostly single-byte. The position block is length-prefixed so a query that does not need
/// positions can step over it without decoding it.
/// </summary>
public sealed class PostingsList
{
    /// <summary>Postings between skip entries. A power of two, sized to trade index size against seek cost.</summary>
    public const int SkipInterval = 128;

    private readonly byte[] _data;
    private readonly SkipEntry[] _skips;

    internal PostingsList(
        byte[] data,
        SkipEntry[] skips,
        int documentFrequency,
        long totalTermFrequency,
        bool hasPositions)
    {
        _data = data;
        _skips = skips;
        DocumentFrequency = documentFrequency;
        TotalTermFrequency = totalTermFrequency;
        HasPositions = hasPositions;
    }

    /// <summary>Number of documents containing the term. The <c>n</c> in BM25's IDF.</summary>
    public int DocumentFrequency { get; }

    /// <summary>Sum of the term's frequency across all documents.</summary>
    public long TotalTermFrequency { get; }

    public bool HasPositions { get; }

    public int ByteLength => _data.Length;

    /// <summary>
    /// Rebuilds a postings list from bytes previously written by the segment serialiser. The
    /// encoding is unchanged on the way to disk and back, so restoring costs a copy rather than a
    /// decode.
    /// </summary>
    public static PostingsList FromEncoded(
        byte[] data,
        SkipEntry[] skips,
        int documentFrequency,
        long totalTermFrequency,
        bool hasPositions) =>
        new(data, skips, documentFrequency, totalTermFrequency, hasPositions);

    public PostingsEnumerator GetEnumerator() => new(_data, _skips, DocumentFrequency, HasPositions);

    /// <summary>Serialised form, written verbatim into a segment file.</summary>
    public ReadOnlySpan<byte> Data => _data;

    public IReadOnlyList<SkipEntry> Skips => _skips;
}

/// <summary>
/// Accumulates postings for a single term. Documents must be added in increasing doc-id order,
/// which the segment builder guarantees because it assigns ids sequentially.
/// </summary>
public sealed class PostingsListBuilder
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly List<SkipEntry> _skips = [];
    private readonly bool _hasPositions;

    private int _lastDocId = -1;
    private int _documentFrequency;
    private long _totalTermFrequency;

    public PostingsListBuilder(bool hasPositions = true) => _hasPositions = hasPositions;

    public int DocumentFrequency => _documentFrequency;

    public void Add(int docId, int termFrequency, IReadOnlyList<int>? positions)
    {
        if (docId <= _lastDocId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(docId),
                $"Postings must be appended in increasing document order (got {docId} after {_lastDocId}).");
        }

        if (_documentFrequency % PostingsList.SkipInterval == 0)
        {
            _skips.Add(new SkipEntry(docId, _buffer.WrittenCount));
        }

        // The decoder starts from origin 0, so the very first gap is absolute. Using _lastDocId
        // (-1) here would shift every doc id by one.
        var previous = _documentFrequency == 0 ? 0 : _lastDocId;

        VByte.Write(_buffer, (uint)(docId - previous));
        VByte.Write(_buffer, (uint)termFrequency);

        if (_hasPositions)
        {
            WritePositions(positions);
        }

        _lastDocId = docId;
        _documentFrequency++;
        _totalTermFrequency += termFrequency;
    }

    private void WritePositions(IReadOnlyList<int>? positions)
    {
        if (positions is null || positions.Count == 0)
        {
            VByte.Write(_buffer, 0);
            return;
        }

        // Encode into scratch first: the block length has to precede the block, and it is not
        // known until the gaps have been encoded.
        var scratch = new ArrayBufferWriter<byte>(positions.Count * 2);
        var previous = 0;

        foreach (var position in positions)
        {
            VByte.Write(scratch, (uint)(position - previous));
            previous = position;
        }

        VByte.Write(_buffer, (uint)scratch.WrittenCount);
        _buffer.Write(scratch.WrittenSpan);
    }

    public PostingsList Build() => new(
        _buffer.WrittenSpan.ToArray(),
        _skips.ToArray(),
        _documentFrequency,
        _totalTermFrequency,
        _hasPositions);
}
