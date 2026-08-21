using DistSear.Index.Postings;
using DistSear.Index.Scoring;
using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>
/// Matches documents where the terms appear in order and close together. With <see cref="Slop"/>
/// zero the terms must be strictly adjacent; a larger slop permits that many positions of total
/// displacement, which also allows a limited amount of reordering.
/// </summary>
public sealed class PhraseQuery : Query
{
    public PhraseQuery(string field, IReadOnlyList<string> terms, int slop = 0)
    {
        if (terms.Count == 0)
        {
            throw new ArgumentException("A phrase needs at least one term.", nameof(terms));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(slop);

        Field = field;
        Terms = terms;
        Slop = slop;
    }

    public string Field { get; }

    public IReadOnlyList<string> Terms { get; }

    public int Slop { get; }

    public override Weight CreateWeight(SearchContext context, double boost)
    {
        var fieldBoost = context.Mapping.Get(Field)?.Boost ?? 1.0;

        // A phrase is rarer than any of its terms, so summing the term IDFs is a reasonable and
        // conventional stand-in for a phrase document frequency the index cannot cheaply supply.
        double idf = 0;

        foreach (var term in Terms)
        {
            idf += Bm25Similarity.InverseDocumentFrequency(
                context.Statistics.DocumentFrequency(Field, term),
                context.Statistics.DocumentCount);
        }

        return new PhraseWeight(
            this,
            context.Similarity,
            idf,
            context.Statistics.AverageFieldLength(Field),
            boost * fieldBoost);
    }

    public override string Describe()
    {
        var phrase = string.Join(" ", Terms);
        return Slop > 0 ? $"{Field}:\"{phrase}\"~{Slop}" : $"{Field}:\"{phrase}\"";
    }

    private sealed class PhraseWeight : Weight
    {
        private readonly Bm25Similarity _similarity;
        private readonly double _idf;
        private readonly double _averageFieldLength;
        private readonly double _boost;

        public PhraseWeight(
            PhraseQuery query,
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

        private new PhraseQuery Query => (PhraseQuery)base.Query;

        public override void CollectTerms(ISet<(string Field, string Term)> terms)
        {
            foreach (var term in Query.Terms)
            {
                terms.Add((Query.Field, term));
            }
        }

        public override Scorer? CreateScorer(Segment segment)
        {
            var field = segment.GetField(Query.Field);

            if (field is null)
            {
                return null;
            }

            var cursors = new PostingsEnumerator[Query.Terms.Count];

            for (var i = 0; i < Query.Terms.Count; i++)
            {
                var postings = field.GetPostings(Query.Terms[i]);

                // Every term of the phrase must be present, or no document can contain the phrase.
                if (postings is null)
                {
                    return null;
                }

                if (!postings.HasPositions)
                {
                    throw new InvalidOperationException(
                        $"Field '{Query.Field}' was indexed without positions, so it cannot answer phrase queries.");
                }

                cursors[i] = postings.GetEnumerator();
            }

            var average = _averageFieldLength > 0 ? _averageFieldLength : field.AverageFieldLength;

            return new PhraseScorer(
                cursors,
                field.Norms,
                Query.Slop,
                _similarity,
                _idf,
                average,
                _boost);
        }
    }

    private sealed class PhraseScorer : Scorer
    {
        private readonly PostingsEnumerator[] _cursors;
        private readonly List<int>[] _positions;
        private readonly int[] _pointers;
        private readonly int[] _norms;
        private readonly int _slop;
        private readonly Bm25Similarity _similarity;
        private readonly double _idf;
        private readonly double _averageFieldLength;
        private readonly double _boost;

        private int _docId = -1;
        private int _matchCount;

        public PhraseScorer(
            PostingsEnumerator[] cursors,
            int[] norms,
            int slop,
            Bm25Similarity similarity,
            double idf,
            double averageFieldLength,
            double boost)
        {
            _cursors = cursors;
            _norms = norms;
            _slop = slop;
            _similarity = similarity;
            _idf = idf;
            _averageFieldLength = averageFieldLength;
            _boost = boost;

            _positions = new List<int>[cursors.Length];
            for (var i = 0; i < cursors.Length; i++)
            {
                _positions[i] = [];
            }

            _pointers = new int[cursors.Length];
            Cost = cursors.Min(c => c.Cost);
            MaxScore = boost * similarity.MaxScore(idf);
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
                candidate = AlignDocuments(candidate);

                if (candidate == NoMoreDocs)
                {
                    return _docId = NoMoreDocs;
                }

                _matchCount = CountPhraseMatches();

                if (_matchCount > 0)
                {
                    return _docId = candidate;
                }

                // All terms are present but never close enough together; try the next document.
                candidate++;
            }
        }

        /// <summary>Intersects the per-term postings, the same leapfrog a conjunction performs.</summary>
        private int AlignDocuments(int candidate)
        {
            while (candidate != NoMoreDocs)
            {
                var aligned = true;

                foreach (var cursor in _cursors)
                {
                    var doc = cursor.DocId;

                    if (doc < candidate)
                    {
                        doc = cursor.Advance(candidate);
                    }

                    if (doc == PostingsEnumerator.NoMoreDocs)
                    {
                        return NoMoreDocs;
                    }

                    if (doc != candidate)
                    {
                        candidate = doc;
                        aligned = false;
                        break;
                    }
                }

                if (aligned)
                {
                    return candidate;
                }
            }

            return NoMoreDocs;
        }

        /// <summary>
        /// Counts phrase occurrences by sweeping the term position lists.
        ///
        /// Each term's positions are normalised by subtracting its offset within the phrase, so an
        /// exact phrase becomes a set of *equal* normalised positions. The terms then form a match
        /// whenever the spread between the smallest and largest normalised position is within the
        /// slop. Repeatedly advancing whichever term currently sits at the minimum walks every
        /// candidate window in linear time.
        /// </summary>
        private int CountPhraseMatches()
        {
            for (var i = 0; i < _cursors.Length; i++)
            {
                _cursors[i].ReadPositions(_positions[i]);
                _pointers[i] = 0;

                if (_positions[i].Count == 0)
                {
                    return 0;
                }
            }

            var matches = 0;

            while (true)
            {
                var minimum = int.MaxValue;
                var maximum = int.MinValue;
                var minimumTerm = -1;

                for (var i = 0; i < _cursors.Length; i++)
                {
                    var normalised = _positions[i][_pointers[i]] - i;

                    if (normalised < minimum)
                    {
                        minimum = normalised;
                        minimumTerm = i;
                    }

                    if (normalised > maximum)
                    {
                        maximum = normalised;
                    }
                }

                if (maximum - minimum <= _slop)
                {
                    matches++;
                }

                // Advancing the trailing term is the only move that can shrink the window.
                _pointers[minimumTerm]++;

                if (_pointers[minimumTerm] >= _positions[minimumTerm].Count)
                {
                    return matches;
                }
            }
        }

        public override double Score()
        {
            var length = (uint)_docId < (uint)_norms.Length ? _norms[_docId] : 0;

            return _boost * _similarity.Score(_idf, _matchCount, length, _averageFieldLength);
        }
    }
}
