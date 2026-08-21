namespace DistSear.Index.Scoring;

/// <summary>
/// Okapi BM25, in its textbook form:
/// <code>
///   score(q, d) = IDF(q) * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * |d| / avgdl))
///   IDF(q)      = ln(1 + (N - n + 0.5) / (n + 0.5))
/// </code>
/// <c>k1</c> controls how quickly term frequency saturates; <c>b</c> how strongly a long field is
/// penalised. The defaults are the values almost every engine ships.
/// </summary>
public sealed class Bm25Similarity
{
    public Bm25Similarity(double k1 = 1.2, double b = 0.75)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k1);
        ArgumentOutOfRangeException.ThrowIfNegative(b);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(b, 1.0);

        K1 = k1;
        B = b;
    }

    public static Bm25Similarity Default { get; } = new();

    public double K1 { get; }

    public double B { get; }

    /// <summary>
    /// Inverse document frequency. The <c>+0.5</c> smoothing and the enclosing <c>1 +</c> keep the
    /// result positive even for a term present in every document, which the unsmoothed form does not.
    /// </summary>
    public static double InverseDocumentFrequency(long documentFrequency, long documentCount)
    {
        // A term the shard has never seen still gets a finite, maximal IDF rather than infinity.
        var df = Math.Max(0, documentFrequency);
        var n = Math.Max(df, documentCount);

        return Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
    }

    /// <summary>
    /// Term-frequency component, scaled by the length normalisation. Kept separate from IDF so a
    /// scorer can hoist the IDF out of its inner loop.
    /// </summary>
    public double TermFrequencyFactor(int termFrequency, int fieldLength, double averageFieldLength)
    {
        if (termFrequency <= 0)
        {
            return 0;
        }

        // A field absent from the corpus average would divide by zero; treat it as average length.
        var normalisedLength = averageFieldLength > 0 ? fieldLength / averageFieldLength : 1.0;
        var denominator = termFrequency + (K1 * (1 - B + (B * normalisedLength)));

        return termFrequency * (K1 + 1) / denominator;
    }

    public double Score(double idf, int termFrequency, int fieldLength, double averageFieldLength) =>
        idf * TermFrequencyFactor(termFrequency, fieldLength, averageFieldLength);

    /// <summary>
    /// Greatest score this term can contribute to any document, used by WAND to prune candidates
    /// that cannot reach the current top-k threshold.
    ///
    /// The frequency factor <c>tf * (k1 + 1) / (tf + L)</c> increases monotonically in <c>tf</c>
    /// for any positive length penalty <c>L</c>, and converges to <c>k1 + 1</c>. So
    /// <c>idf * (k1 + 1)</c> is a valid upper bound for every document, whatever its length.
    /// The bound is loose, which costs some pruning efficiency but can never prune a document that
    /// belonged in the results — which is the property that matters.
    /// </summary>
    public double MaxScore(double idf) => idf * (K1 + 1);
}
