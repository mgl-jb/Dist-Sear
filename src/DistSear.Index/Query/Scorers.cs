namespace DistSear.Index.Query;

/// <summary>Matches every live document in the segment with a constant score.</summary>
internal sealed class MatchAllScorer : Scorer
{
    private readonly int _maxDoc;
    private readonly double _score;
    private int _docId = -1;

    public MatchAllScorer(int maxDoc, double score)
    {
        _maxDoc = maxDoc;
        _score = score;
    }

    public override int DocId => _docId;

    public override long Cost => _maxDoc;

    public override double MaxScore => _score;

    public override int NextDoc() => Advance(_docId + 1);

    public override int Advance(int target)
    {
        _docId = target >= _maxDoc ? NoMoreDocs : Math.Max(target, 0);
        return _docId;
    }

    public override double Score() => _score;
}

/// <summary>Replaces a sub-scorer's score with a constant. Used for filter clauses, which restrict
/// the result set but must not influence ranking.</summary>
internal sealed class ConstantScoreScorer : Scorer
{
    private readonly Scorer _inner;
    private readonly double _score;

    public ConstantScoreScorer(Scorer inner, double score)
    {
        _inner = inner;
        _score = score;
    }

    public override int DocId => _inner.DocId;

    public override long Cost => _inner.Cost;

    public override double MaxScore => _score;

    public override int NextDoc() => _inner.NextDoc();

    public override int Advance(int target) => _inner.Advance(target);

    public override double Score() => _score;
}

/// <summary>
/// Intersection. Iteration is led by the cheapest clause and the others leapfrog to it, so the cost
/// is driven by the most selective clause rather than by the largest postings list.
/// </summary>
internal sealed class ConjunctionScorer : Scorer
{
    private readonly Scorer[] _scorers;
    private readonly Scorer _lead;
    private int _docId = -1;

    public ConjunctionScorer(IReadOnlyList<Scorer> scorers)
    {
        if (scorers.Count == 0)
        {
            throw new ArgumentException("A conjunction needs at least one clause.", nameof(scorers));
        }

        _scorers = [.. scorers.OrderBy(s => s.Cost)];
        _lead = _scorers[0];
        MaxScore = scorers.Sum(s => s.MaxScore);
        Cost = _lead.Cost;
    }

    public override int DocId => _docId;

    public override long Cost { get; }

    public override double MaxScore { get; }

    public override int NextDoc() => AlignFrom(_lead.NextDoc());

    public override int Advance(int target) => AlignFrom(_lead.Advance(target));

    /// <summary>
    /// Drives every clause to a common document. Whenever a clause overshoots, that clause's
    /// document becomes the new candidate and the scan restarts, which is what makes the loop
    /// converge on the next intersection rather than stepping one document at a time.
    /// </summary>
    private int AlignFrom(int candidate)
    {
        while (candidate != NoMoreDocs)
        {
            var aligned = true;

            foreach (var scorer in _scorers)
            {
                if (scorer.DocId == candidate)
                {
                    continue;
                }

                var doc = scorer.Advance(candidate);

                if (doc != candidate)
                {
                    candidate = doc;
                    aligned = false;
                    break;
                }
            }

            if (aligned)
            {
                return _docId = candidate;
            }
        }

        return _docId = NoMoreDocs;
    }

    public override double Score()
    {
        double total = 0;

        foreach (var scorer in _scorers)
        {
            total += scorer.Score();
        }

        return total;
    }
}

/// <summary>
/// Union, optionally requiring several clauses to agree. Scores sum over the clauses that matched.
/// </summary>
internal sealed class DisjunctionScorer : Scorer
{
    private readonly Scorer[] _scorers;
    private readonly int _minimumShouldMatch;
    private int _docId = -1;

    public DisjunctionScorer(IReadOnlyList<Scorer> scorers, int minimumShouldMatch = 1)
    {
        if (scorers.Count == 0)
        {
            throw new ArgumentException("A disjunction needs at least one clause.", nameof(scorers));
        }

        _scorers = [.. scorers];
        _minimumShouldMatch = Math.Clamp(minimumShouldMatch, 1, scorers.Count);
        MaxScore = scorers.Sum(s => s.MaxScore);
        Cost = scorers.Sum(s => s.Cost);
    }

    public override int DocId => _docId;

    public override long Cost { get; }

    public override double MaxScore { get; }

    public override int NextDoc() => Advance(_docId + 1);

    public override int Advance(int target)
    {
        var candidate = target;

        while (true)
        {
            var smallest = NoMoreDocs;
            var matching = 0;

            foreach (var scorer in _scorers)
            {
                var doc = scorer.DocId;

                if (doc < candidate)
                {
                    doc = scorer.Advance(candidate);
                }

                if (doc == NoMoreDocs)
                {
                    continue;
                }

                if (doc < smallest)
                {
                    smallest = doc;
                    matching = 1;
                }
                else if (doc == smallest)
                {
                    matching++;
                }
            }

            if (smallest == NoMoreDocs)
            {
                return _docId = NoMoreDocs;
            }

            if (matching >= _minimumShouldMatch)
            {
                return _docId = smallest;
            }

            // Too few clauses agreed here; the next candidate is the document after this one.
            candidate = smallest + 1;
        }
    }

    public override double Score()
    {
        double total = 0;

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

/// <summary>Required clause minus the documents matched by a prohibited one.</summary>
internal sealed class ReqExclScorer : Scorer
{
    private readonly Scorer _required;
    private readonly Scorer _excluded;
    private int _docId = -1;

    public ReqExclScorer(Scorer required, Scorer excluded)
    {
        _required = required;
        _excluded = excluded;
    }

    public override int DocId => _docId;

    public override long Cost => _required.Cost;

    public override double MaxScore => _required.MaxScore;

    public override int NextDoc() => FilterFrom(_required.NextDoc());

    public override int Advance(int target) => FilterFrom(_required.Advance(target));

    private int FilterFrom(int candidate)
    {
        while (candidate != NoMoreDocs)
        {
            var excluded = _excluded.DocId;

            if (excluded < candidate)
            {
                excluded = _excluded.Advance(candidate);
            }

            if (excluded != candidate)
            {
                return _docId = candidate;
            }

            candidate = _required.NextDoc();
        }

        return _docId = NoMoreDocs;
    }

    public override double Score() => _required.Score();
}

/// <summary>
/// Required clauses drive iteration; optional clauses only contribute score where they happen to
/// align. This is what makes <c>+must should</c> behave as "must match, ranked better if it also
/// matches the optional part".
/// </summary>
internal sealed class RequiredOptionalScorer : Scorer
{
    private readonly Scorer _required;
    private readonly Scorer _optional;

    public RequiredOptionalScorer(Scorer required, Scorer optional)
    {
        _required = required;
        _optional = optional;
    }

    public override int DocId => _required.DocId;

    public override long Cost => _required.Cost;

    public override double MaxScore => _required.MaxScore + _optional.MaxScore;

    public override int NextDoc() => _required.NextDoc();

    public override int Advance(int target) => _required.Advance(target);

    public override double Score()
    {
        var score = _required.Score();
        var docId = _required.DocId;
        var optional = _optional.DocId;

        if (optional < docId)
        {
            optional = _optional.Advance(docId);
        }

        if (optional == docId)
        {
            score += _optional.Score();
        }

        return score;
    }
}
