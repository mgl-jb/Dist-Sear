namespace DistSear.Abstractions.Search;

/// <summary>
/// How the fan-out is executed.
/// </summary>
public enum SearchType
{
    /// <summary>
    /// Two phases. Each shard scores using only its own term statistics and returns ids plus
    /// scores; the coordinator merges, then fetches stored fields only from the shards that own
    /// the winning documents. One round trip per phase, and full documents are never shipped from
    /// shards that lost.
    /// </summary>
    QueryThenFetch,

    /// <summary>
    /// Adds a pre-pass collecting global document frequencies before scoring, so BM25 IDF is
    /// computed over the whole index rather than per shard. Exact, at the cost of one extra
    /// round trip. Matters most when shards are small or unevenly distributed.
    /// </summary>
    DfsQueryThenFetch
}

public sealed record SearchRequest
{
    public required string Index { get; init; }

    /// <summary>Query-string syntax, e.g. <c>title:(fast AND search) -tag:draft "exact phrase"~2</c>.</summary>
    public string? Query { get; init; }

    /// <summary>
    /// Additional query-string clause applied as a pure filter: it restricts matches but
    /// contributes no score, so it is cacheable independently of relevance.
    /// </summary>
    public string? Filter { get; init; }

    public int From { get; init; }

    public int Size { get; init; } = 10;

    public IReadOnlyList<SortSpec> Sort { get; init; } = [];

    public IReadOnlyList<FacetSpec> Facets { get; init; } = [];

    public HighlightSpec? Highlight { get; init; }

    /// <summary>
    /// Cursor for deep paging: the sort values of the last hit on the previous page. Offset
    /// paging costs <c>O(from + size)</c> per shard and degrades badly past a few thousand
    /// documents, so this is the supported way to walk a large result set.
    /// </summary>
    public IReadOnlyList<object?>? SearchAfter { get; init; }

    public SearchType SearchType { get; init; } = SearchType.QueryThenFetch;

    /// <summary>Fields to return per hit. Null returns every stored field.</summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// Per-shard deadline. Shards that miss it are reported as failed and their results omitted,
    /// rather than failing the whole query.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    public bool Explain { get; init; }

    public int TopDocsNeeded => From + Size;
}
