namespace DistSear.Index.Postings;

/// <summary>
/// Forward-only cursor over an encoded postings list. Positions are decoded only when
/// <see cref="ReadPositions"/> is called, so boolean and term queries never pay for them.
/// </summary>
public sealed class PostingsEnumerator
{
    /// <summary>Sentinel returned once the cursor is exhausted. Ordering above every real doc id.</summary>
    public const int NoMoreDocs = int.MaxValue;

    private readonly byte[] _data;
    private readonly SkipEntry[] _skips;
    private readonly bool _hasPositions;
    private readonly int _documentFrequency;

    private int _offset;
    private int _consumed;
    private int _positionsOffset;
    private int _positionsLength;

    internal PostingsEnumerator(byte[] data, SkipEntry[] skips, int documentFrequency, bool hasPositions)
    {
        _data = data;
        _skips = skips;
        _documentFrequency = documentFrequency;
        _hasPositions = hasPositions;
        DocId = -1;
    }

    /// <summary>Current document id, or <see cref="NoMoreDocs"/> when exhausted. -1 before the first move.</summary>
    public int DocId { get; private set; }

    /// <summary>Occurrences of the term in <see cref="DocId"/>.</summary>
    public int TermFrequency { get; private set; }

    /// <summary>Number of documents in the list; an upper bound on remaining work.</summary>
    public long Cost => _documentFrequency;

    public int NextDoc()
    {
        if (_consumed >= _documentFrequency)
        {
            return DocId = NoMoreDocs;
        }

        var previous = DocId < 0 ? 0 : DocId;
        DocId = previous + (int)VByte.Read(_data, ref _offset);
        TermFrequency = (int)VByte.Read(_data, ref _offset);

        if (_hasPositions)
        {
            _positionsLength = (int)VByte.Read(_data, ref _offset);
            _positionsOffset = _offset;
            _offset += _positionsLength;
        }

        _consumed++;
        return DocId;
    }

    /// <summary>
    /// Moves to the first document at or after <paramref name="target"/>. Uses the skip index to
    /// jump to the last checkpoint at or before the target, then scans, so a highly selective
    /// clause does not force a full decode of a long postings list.
    /// </summary>
    public int Advance(int target)
    {
        if (DocId >= target && DocId >= 0)
        {
            return DocId;
        }

        SeekToSkipPoint(target);

        int doc;
        do
        {
            doc = NextDoc();
        }
        while (doc < target);

        return doc;
    }

    private void SeekToSkipPoint(int target)
    {
        // Binary search for the last checkpoint whose doc id does not exceed the target.
        var low = 0;
        var high = _skips.Length - 1;
        var found = -1;

        while (low <= high)
        {
            var mid = (low + high) >> 1;

            if (_skips[mid].DocId <= target)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0)
        {
            return;
        }

        var entry = _skips[found];
        var postingIndex = found * PostingsList.SkipInterval;

        // Only jump forwards; the cursor may already be past this checkpoint.
        if (postingIndex <= _consumed)
        {
            return;
        }

        _offset = entry.Offset;
        _consumed = postingIndex;

        // The checkpoint stores an absolute doc id, but NextDoc reads a gap from the previous
        // document, so rewind DocId to the predecessor the gap is relative to.
        DocId = entry.DocId;
        var rewind = _offset;
        var gap = (int)VByte.Read(_data, ref rewind);
        DocId = entry.DocId - gap;
    }

    /// <summary>
    /// Decodes the term positions within the current document. Only valid when the field was
    /// indexed with positions and after a successful move.
    /// </summary>
    public void ReadPositions(List<int> destination)
    {
        destination.Clear();

        if (!_hasPositions || _positionsLength == 0)
        {
            return;
        }

        var offset = _positionsOffset;
        var end = _positionsOffset + _positionsLength;
        var position = 0;

        while (offset < end)
        {
            position += (int)VByte.Read(_data, ref offset);
            destination.Add(position);
        }
    }
}
