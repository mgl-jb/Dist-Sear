using DistSear.Abstractions.Mapping;
using DistSear.Analysis;
using DistSear.Index.Scoring;
using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>
/// Cursor over matching documents that can also score them. Iteration is forward-only and
/// monotonic, which is what lets conjunctions leapfrog rather than intersect materialised sets.
/// </summary>
public abstract class Scorer
{
    /// <summary>Returned once exhausted. Chosen so it sorts above every real document id.</summary>
    public const int NoMoreDocs = int.MaxValue;

    /// <summary>Current document, -1 before the first move, <see cref="NoMoreDocs"/> when spent.</summary>
    public abstract int DocId { get; }

    public abstract int NextDoc();

    /// <summary>Moves to the first document at or after <paramref name="target"/>.</summary>
    public abstract int Advance(int target);

    public abstract double Score();

    /// <summary>Upper bound on any score this scorer can produce. Must never under-estimate.</summary>
    public abstract double MaxScore { get; }

    /// <summary>Estimated number of matches; used to pick the cheapest clause to lead a conjunction.</summary>
    public abstract long Cost { get; }

    /// <summary>
    /// Tells the scorer the lowest score still able to enter the top-k. Implementations that can
    /// prune use it; the rest ignore it and simply return every match.
    /// </summary>
    public virtual void SetMinCompetitiveScore(double minScore)
    {
    }
}

/// <summary>
/// A query bound to corpus-level statistics. Created once per query, then asked for a scorer per
/// segment, so IDF is resolved a single time rather than per segment.
/// </summary>
public abstract class Weight
{
    protected Weight(Query query) => Query = query;

    public Query Query { get; }

    /// <summary>Returns null when the segment cannot possibly match, letting callers skip it.</summary>
    public abstract Scorer? CreateScorer(Segment segment);

    /// <summary>Reports the terms this weight will look up, for the DFS statistics pre-pass.</summary>
    public virtual void CollectTerms(ISet<(string Field, string Term)> terms)
    {
    }
}

/// <summary>Everything a query needs to turn itself into scorers.</summary>
public sealed class SearchContext
{
    public required IndexMapping Mapping { get; init; }

    public required AnalyzerRegistry Analyzers { get; init; }

    public required ITermStatisticsProvider Statistics { get; init; }

    public Bm25Similarity Similarity { get; init; } = Bm25Similarity.Default;

    /// <summary>
    /// Caps how many terms a wildcard, prefix or fuzzy query may expand to. Without a ceiling a
    /// query like <c>a*</c> can rewrite into hundreds of thousands of clauses and exhaust the node.
    /// </summary>
    public int MaxExpansions { get; init; } = 1024;

    /// <summary>
    /// When false, disjunctions score every match instead of pruning with WAND. Pruning must not
    /// change results, only work done — the property tests assert exactly that by running the same
    /// query both ways.
    /// </summary>
    public bool EnableTopKPruning { get; init; } = true;
}

public abstract class Query
{
    public abstract Weight CreateWeight(SearchContext context, double boost);

    /// <summary>
    /// Sub-queries, for callers that need to walk the tree rather than execute it — highlighting
    /// needs to know which terms to mark up, for instance.
    /// </summary>
    public virtual IEnumerable<Query> Children => [];

    /// <summary>Human-readable form, used in explanations and error messages.</summary>
    public abstract string Describe();

    public override string ToString() => Describe();
}
