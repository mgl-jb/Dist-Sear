using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>Matches every document. Useful as a filter carrier and for facet-only requests.</summary>
public sealed class MatchAllQuery : Query
{
    public static MatchAllQuery Instance { get; } = new();

    public override Weight CreateWeight(SearchContext context, double boost) =>
        new MatchAllWeight(this, boost);

    public override string Describe() => "*:*";

    private sealed class MatchAllWeight : Weight
    {
        private readonly double _boost;

        public MatchAllWeight(Query query, double boost)
            : base(query) => _boost = boost;

        public override Scorer? CreateScorer(Segment segment) =>
            segment.MaxDoc == 0 ? null : new MatchAllScorer(segment.MaxDoc, _boost);
    }
}

/// <summary>Multiplies the score of an inner query.</summary>
public sealed class BoostQuery : Query
{
    public BoostQuery(Query inner, double boost)
    {
        Inner = inner;
        Boost = boost;
    }

    public Query Inner { get; }

    public double Boost { get; }

    public override Weight CreateWeight(SearchContext context, double boost) =>
        Inner.CreateWeight(context, boost * Boost);

    public override string Describe() => $"{Inner.Describe()}^{Boost}";
}

/// <summary>
/// Replaces an inner query's score with a constant, so that matching contributes membership but not
/// ranking. This is what makes a filter clause's cost independent of relevance.
/// </summary>
public sealed class ConstantScoreQuery : Query
{
    public ConstantScoreQuery(Query inner, double score = 1.0)
    {
        Inner = inner;
        Score = score;
    }

    public Query Inner { get; }

    public double Score { get; }

    public override Weight CreateWeight(SearchContext context, double boost) =>
        new ConstantScoreWeight(this, Inner.CreateWeight(context, 1.0), Score * boost);

    public override string Describe() => $"ConstantScore({Inner.Describe()})";

    private sealed class ConstantScoreWeight : Weight
    {
        private readonly Weight _inner;
        private readonly double _score;

        public ConstantScoreWeight(Query query, Weight inner, double score)
            : base(query)
        {
            _inner = inner;
            _score = score;
        }

        public override void CollectTerms(ISet<(string Field, string Term)> terms) =>
            _inner.CollectTerms(terms);

        public override Scorer? CreateScorer(Segment segment)
        {
            var scorer = _inner.CreateScorer(segment);
            return scorer is null ? null : new ConstantScoreScorer(scorer, _score);
        }
    }
}
