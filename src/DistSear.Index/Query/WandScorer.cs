namespace DistSear.Index.Query;

/// <summary>
/// Weak AND: a disjunction that skips documents which provably cannot enter the top-k.
///
/// Each clause reports an upper bound on the score it can contribute. Clauses are kept ordered by
/// current document; walking them in that order and accumulating bounds finds the *pivot* — the
/// first document whose accumulated bound could beat the collector's current threshold. Every
/// document before the pivot is guaranteed to score at or below the threshold, so it can be skipped
/// without being scored at all.
///
/// Correctness rests entirely on the bounds never being under-estimates. With an exact threshold
/// the results are identical to a full disjunction, which the property tests assert directly.
/// </summary>
internal sealed class WandScorer : Scorer
{
    /// <summary>Clauses in their original order. Scoring always sums in this order.</summary>
    private readonly Scorer[] _scorers;

    /// <summary>The same clauses, re-sorted by current document to locate the pivot.</summary>
    private readonly Scorer[] _ordered;

    private double _minCompetitiveScore;
    private int _docId = -1;

    public WandScorer(IReadOnlyList<Scorer> scorers)
    {
        if (scorers.Count == 0)
        {
            throw new ArgumentException("A disjunction needs at least one clause.", nameof(scorers));
        }

        _scorers = [.. scorers];
        _ordered = [.. scorers];
        MaxScore = scorers.Sum(s => s.MaxScore);
        Cost = scorers.Sum(s => s.Cost);
    }

    public override int DocId => _docId;

    public override long Cost { get; }

    public override double MaxScore { get; }

    public override void SetMinCompetitiveScore(double minScore)
    {
        // Thresholds only ever rise as the top-k fills up.
        if (minScore > _minCompetitiveScore)
        {
            _minCompetitiveScore = minScore;
        }
    }

    public override int NextDoc() => Advance(_docId + 1);

    public override int Advance(int target)
    {
        var candidate = target;

        while (true)
        {
            // Bring every lagging clause up to the candidate, then order by current document.
            foreach (var scorer in _scorers)
            {
                if (scorer.DocId < candidate)
                {
                    scorer.Advance(candidate);
                }
            }

            Array.Sort(_ordered, static (a, b) => a.DocId.CompareTo(b.DocId));

            if (_ordered[0].DocId == NoMoreDocs)
            {
                return _docId = NoMoreDocs;
            }

            var pivot = FindPivot();

            if (pivot < 0)
            {
                // No suffix of clauses can reach the threshold, so nothing further can compete.
                return _docId = NoMoreDocs;
            }

            var pivotDoc = _ordered[pivot].DocId;

            // Clauses are sorted, so when the first one already sits on the pivot document every
            // clause up to the pivot does too, and the document is a genuine candidate.
            if (_ordered[0].DocId == pivotDoc)
            {
                return _docId = pivotDoc;
            }

            // Otherwise pull the trailing clauses forward and re-evaluate.
            candidate = pivotDoc;
        }
    }

    /// <summary>
    /// Index of the first clause at which the accumulated score bound exceeds the threshold, or -1
    /// when even every clause together cannot beat it.
    /// </summary>
    private int FindPivot()
    {
        // With no threshold yet (an unfilled top-k) every document is competitive. Returning early
        // also avoids mistaking a legitimate zero-bound clause for "nothing left to find".
        if (_minCompetitiveScore <= 0)
        {
            return 0;
        }

        double upperBound = 0;

        for (var i = 0; i < _ordered.Length; i++)
        {
            if (_ordered[i].DocId == NoMoreDocs)
            {
                break;
            }

            upperBound += _ordered[i].MaxScore;

            if (upperBound > _minCompetitiveScore)
            {
                return i;
            }
        }

        return -1;
    }

    public override double Score()
    {
        double total = 0;

        // Summed in declaration order, not pivot order. Floating-point addition is not associative,
        // so summing in the sorted order would make a document's score depend on how the clauses
        // happened to be arranged at that moment, and tied documents would rank unstably.
        foreach (var scorer in _scorers)
        {
            if (scorer.DocId == _docId)
            {
                total += scorer.Score();
            }
        }

        return total;
    }
}
