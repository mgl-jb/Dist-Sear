using DistSear.Index.Postings;
using DistSear.Index.Scoring;
using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>Matches documents containing an exact indexed term in a field.</summary>
public sealed class TermQuery : Query
{
    public TermQuery(string field, string term)
    {
        Field = field;
        Term = term;
    }

    public string Field { get; }

    public string Term { get; }

    public override Weight CreateWeight(SearchContext context, double boost)
    {
        var mapping = context.Mapping.Get(Field);
        var fieldBoost = mapping?.Boost ?? 1.0;

        var documentFrequency = context.Statistics.DocumentFrequency(Field, Term);
        var idf = Bm25Similarity.InverseDocumentFrequency(documentFrequency, context.Statistics.DocumentCount);

        return new TermWeight(
            this,
            context.Similarity,
            idf,
            context.Statistics.AverageFieldLength(Field),
            boost * fieldBoost);
    }

    public override string Describe() => $"{Field}:{Term}";

    private sealed class TermWeight : Weight
    {
        private readonly Bm25Similarity _similarity;
        private readonly double _idf;
        private readonly double _averageFieldLength;
        private readonly double _boost;

        public TermWeight(
            TermQuery query,
            Bm25Similarity similarity,
            double idf,
            double averageFieldLength,
            double boost)
            : base(query)
        {
            _similarity = similarity;
            _idf = idf;
            _averageFieldLength = averageFieldLength;
            _boost = boost;
        }

        private new TermQuery Query => (TermQuery)base.Query;

        public override void CollectTerms(ISet<(string Field, string Term)> terms) =>
            terms.Add((Query.Field, Query.Term));

        public override Scorer? CreateScorer(Segment segment)
        {
            var field = segment.GetField(Query.Field);
            var postings = field?.GetPostings(Query.Term);

            if (field is null || postings is null)
            {
                return null;
            }

            // Prefer the corpus-wide average when one is available, so that scores stay comparable
            // across segments of very different sizes.
            var average = _averageFieldLength > 0 ? _averageFieldLength : field.AverageFieldLength;

            return new TermScorer(postings, field.Norms, _similarity, _idf, average, _boost);
        }
    }

    private sealed class TermScorer : Scorer
    {
        private readonly PostingsEnumerator _postings;
        private readonly int[] _norms;
        private readonly Bm25Similarity _similarity;
        private readonly double _idf;
        private readonly double _averageFieldLength;
        private readonly double _boost;

        public TermScorer(
            PostingsList postings,
            int[] norms,
            Bm25Similarity similarity,
            double idf,
            double averageFieldLength,
            double boost)
        {
            _postings = postings.GetEnumerator();
            _norms = norms;
            _similarity = similarity;
            _idf = idf;
            _averageFieldLength = averageFieldLength;
            _boost = boost;
            Cost = postings.DocumentFrequency;
            MaxScore = boost * similarity.MaxScore(idf);
        }

        public override int DocId => _postings.DocId;

        public override long Cost { get; }

        public override double MaxScore { get; }

        public override int NextDoc() => _postings.NextDoc();

        public override int Advance(int target) => _postings.Advance(target);

        public override double Score()
        {
            var docId = _postings.DocId;
            var length = (uint)docId < (uint)_norms.Length ? _norms[docId] : 0;

            return _boost * _similarity.Score(_idf, _postings.TermFrequency, length, _averageFieldLength);
        }

        /// <summary>Exposes positions so a phrase query can verify adjacency without re-reading postings.</summary>
        public void ReadPositions(List<int> destination) => _postings.ReadPositions(destination);
    }

    /// <summary>Builds a term scorer directly, for queries that expand into many terms.</summary>
    internal static Scorer? CreateTermScorer(
        Segment segment,
        string field,
        string term,
        Bm25Similarity similarity,
        double idf,
        double averageFieldLength,
        double boost)
    {
        var fieldTerms = segment.GetField(field);
        var postings = fieldTerms?.GetPostings(term);

        if (fieldTerms is null || postings is null)
        {
            return null;
        }

        var average = averageFieldLength > 0 ? averageFieldLength : fieldTerms.AverageFieldLength;
        return new TermScorer(postings, fieldTerms.Norms, similarity, idf, average, boost);
    }
}
