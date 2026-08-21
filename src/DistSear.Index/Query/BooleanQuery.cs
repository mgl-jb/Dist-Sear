using DistSear.Index.Segments;

namespace DistSear.Index.Query;

public enum Occur
{
    /// <summary>The clause must match, and contributes to the score.</summary>
    Must,

    /// <summary>The clause may match; matching improves the score.</summary>
    Should,

    /// <summary>The clause must not match.</summary>
    MustNot,

    /// <summary>The clause must match but contributes no score. Cacheable independently of ranking.</summary>
    Filter
}

public sealed record BooleanClause(Query Query, Occur Occur);

/// <summary>
/// Combines clauses with boolean logic. Scoring clauses sum their contributions, so a document
/// matching more of the query ranks above one matching less of it.
/// </summary>
public sealed class BooleanQuery : Query
{
    public BooleanQuery(IReadOnlyList<BooleanClause> clauses, int minimumShouldMatch = 0)
    {
        Clauses = clauses;
        MinimumShouldMatch = minimumShouldMatch;
    }

    public IReadOnlyList<BooleanClause> Clauses { get; }

    /// <summary>
    /// How many <see cref="Occur.Should"/> clauses must match. Zero means the default: at least one
    /// when there are no required clauses, otherwise none.
    /// </summary>
    public int MinimumShouldMatch { get; }

    public static BooleanQuery Of(params BooleanClause[] clauses) => new(clauses);

    public override Weight CreateWeight(SearchContext context, double boost) =>
        new BooleanWeight(this, context, boost);

    public override string Describe()
    {
        var parts = Clauses.Select(c => c.Occur switch
        {
            Occur.Must => "+" + c.Query.Describe(),
            Occur.MustNot => "-" + c.Query.Describe(),
            Occur.Filter => "#" + c.Query.Describe(),
            _ => c.Query.Describe()
        });

        return "(" + string.Join(" ", parts) + ")";
    }

    private sealed class BooleanWeight : Weight
    {
        private readonly List<(Weight Weight, Occur Occur)> _weights = [];
        private readonly int _minimumShouldMatch;
        private readonly bool _hasRequired;
        private readonly bool _enablePruning;

        public BooleanWeight(BooleanQuery query, SearchContext context, double boost)
            : base(query)
        {
            _enablePruning = context.EnableTopKPruning;

            foreach (var clause in query.Clauses)
            {
                // Filter and must-not clauses never influence ranking, so their boost is irrelevant.
                var clauseBoost = clause.Occur is Occur.Filter or Occur.MustNot ? 1.0 : boost;
                _weights.Add((clause.Query.CreateWeight(context, clauseBoost), clause.Occur));
            }

            _hasRequired = _weights.Any(w => w.Occur is Occur.Must or Occur.Filter);

            // With no required clause at least one optional clause has to match, otherwise the
            // query would match every document in the index.
            _minimumShouldMatch = query.MinimumShouldMatch > 0
                ? query.MinimumShouldMatch
                : _hasRequired ? 0 : 1;
        }

        public override void CollectTerms(ISet<(string Field, string Term)> terms)
        {
            foreach (var (weight, _) in _weights)
            {
                weight.CollectTerms(terms);
            }
        }

        public override Scorer? CreateScorer(Segment segment)
        {
            List<Scorer> required = [];
            List<Scorer> optional = [];
            List<Scorer> prohibited = [];

            foreach (var (weight, occur) in _weights)
            {
                var scorer = weight.CreateScorer(segment);

                switch (occur)
                {
                    case Occur.Must when scorer is null:
                    case Occur.Filter when scorer is null:
                        // A required clause with no postings in this segment means no match at all.
                        return null;

                    case Occur.Must:
                        required.Add(scorer!);
                        break;

                    case Occur.Filter:
                        required.Add(new ConstantScoreScorer(scorer!, 0));
                        break;

                    case Occur.Should when scorer is not null:
                        optional.Add(scorer);
                        break;

                    case Occur.MustNot when scorer is not null:
                        prohibited.Add(scorer);
                        break;
                }
            }

            var positive = BuildPositive(required, optional);

            if (positive is null)
            {
                return null;
            }

            if (prohibited.Count == 0)
            {
                return positive;
            }

            var excluded = prohibited.Count == 1 ? prohibited[0] : new DisjunctionScorer(prohibited);
            return new ReqExclScorer(positive, excluded);
        }

        private Scorer? BuildPositive(List<Scorer> required, List<Scorer> optional)
        {
            if (required.Count == 0)
            {
                if (optional.Count < _minimumShouldMatch)
                {
                    return null;
                }

                if (optional.Count == 0)
                {
                    return null;
                }

                // A pure disjunction is the case top-k pruning pays off in, so use WAND unless the
                // minimum-should-match constraint means clauses cannot be skipped independently.
                return _minimumShouldMatch <= 1 && _enablePruning
                    ? new WandScorer(optional)
                    : new DisjunctionScorer(optional, _minimumShouldMatch);
            }

            var requiredScorer = required.Count == 1 ? required[0] : new ConjunctionScorer(required);

            if (optional.Count == 0)
            {
                return _minimumShouldMatch > 0 ? null : requiredScorer;
            }

            if (_minimumShouldMatch > 0)
            {
                // Optional clauses are constrained, so they become part of the intersection.
                var constrained = new DisjunctionScorer(optional, _minimumShouldMatch);
                return new ConjunctionScorer([requiredScorer, constrained]);
            }

            var optionalScorer = optional.Count == 1 ? optional[0] : new DisjunctionScorer(optional);
            return new RequiredOptionalScorer(requiredScorer, optionalScorer);
        }
    }
}
