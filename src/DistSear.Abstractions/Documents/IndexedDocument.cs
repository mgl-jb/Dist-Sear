namespace DistSear.Abstractions.Documents;

/// <summary>
/// A document as it lives in the source of truth (Cosmos DB) and as it arrives on the
/// change feed. Field values are plain CLR scalars (<see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/>, <see cref="DateTimeOffset"/>) or an
/// <see cref="IReadOnlyList{T}"/> of them; the index mapping decides how each is coerced.
/// </summary>
public sealed record IndexedDocument
{
    public required string Id { get; init; }

    /// <summary>
    /// Cosmos partition key, of the form <c>shard-{n}</c>. Computed on the write path so that a
    /// node can read the change feed for exactly the shards it owns and nothing else.
    /// </summary>
    public required string ShardKey { get; init; }

    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary>
    /// Principals (user or group object IDs) permitted to see this document. The coordinator
    /// injects a mandatory filter over this field; an empty list means the document is public.
    /// </summary>
    public IReadOnlyList<string> Acl { get; init; } = [];

    /// <summary>
    /// Soft-delete tombstone. The latest-version change feed does not emit deletions, so deletes
    /// are modelled as an update setting this flag; Cosmos TTL reaps the row later.
    /// </summary>
    public bool Deleted { get; init; }

    /// <summary>
    /// Monotonic version used to resolve out-of-order application of the same document.
    /// Populated from the Cosmos <c>_ts</c>/etag on the read path.
    /// </summary>
    public long Version { get; init; }
}
