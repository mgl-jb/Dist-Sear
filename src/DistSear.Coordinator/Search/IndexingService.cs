using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Storage;
using DistSear.Cluster.Routing;

namespace DistSear.Coordinator.Search;

/// <summary>A document as supplied by a caller, before routing has been decided.</summary>
public sealed record IncomingDocument
{
    public required string Id { get; init; }

    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary>Principals allowed to see this document. Empty means unrestricted.</summary>
    public IReadOnlyList<string> Acl { get; init; } = [];

    /// <summary>
    /// Overrides id-based routing, co-locating related documents on one shard so that queries
    /// scoped to the same key can be answered without a fan-out.
    /// </summary>
    public string? RoutingKey { get; init; }
}

public sealed record BulkResult(int Indexed, int Deleted, IReadOnlyList<string> Errors);

/// <summary>
/// The write path.
///
/// Writes go to Cosmos DB, never directly to an index node. Nodes discover them by reading the
/// change feed, which is what makes every replica of a shard converge on the same content without a
/// replication protocol, and what allows a lost replica to be rebuilt from the durable copy.
/// </summary>
public sealed class IndexingService
{
    private readonly IDocumentStore _documents;
    private readonly IClusterStore _clusterStore;

    public IndexingService(IDocumentStore documents, IClusterStore clusterStore)
    {
        _documents = documents;
        _clusterStore = clusterStore;
    }

    public async Task<BulkResult> IndexAsync(
        string indexOrAlias,
        IReadOnlyList<IncomingDocument> documents,
        CancellationToken cancellationToken)
    {
        var mapping = await ResolveMappingAsync(indexOrAlias, cancellationToken);
        var errors = new List<string>();
        var prepared = new List<IndexedDocument>(documents.Count);

        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Id))
            {
                errors.Add("A document was rejected because it has no id.");
                continue;
            }

            var unknown = document.Fields.Keys.Where(f => !mapping.Has(f)).ToList();

            if (unknown.Count > 0)
            {
                // Rejecting unmapped fields early beats silently discarding them at index time,
                // where the caller would have no way to tell their data was dropped.
                errors.Add($"Document '{document.Id}' has unmapped fields: {string.Join(", ", unknown)}.");
                continue;
            }

            prepared.Add(new IndexedDocument
            {
                Id = document.Id,
                ShardKey = DocumentRouter.ShardKeyFor(mapping, document.Id, document.RoutingKey),
                Fields = document.Fields,
                Acl = document.Acl
            });
        }

        if (prepared.Count > 0)
        {
            await _documents.UpsertAsync(prepared, cancellationToken);
        }

        return new BulkResult(prepared.Count, 0, errors);
    }

    /// <summary>
    /// Deletes by writing a tombstone rather than removing the row.
    ///
    /// The latest-version change feed does not carry deletions, so a hard delete would simply
    /// vanish and replicas would never learn the document is gone. The tombstone travels the feed
    /// like any other change; Cosmos TTL reclaims the row later.
    /// </summary>
    public async Task<BulkResult> DeleteAsync(
        string indexOrAlias,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var mapping = await ResolveMappingAsync(indexOrAlias, cancellationToken);

        var tombstones = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new IndexedDocument
            {
                Id = id,
                ShardKey = DocumentRouter.ShardKeyFor(mapping, id, null),
                Fields = new Dictionary<string, object?>(),
                Deleted = true
            })
            .ToList();

        if (tombstones.Count > 0)
        {
            await _documents.UpsertAsync(tombstones, cancellationToken);
        }

        return new BulkResult(0, tombstones.Count, []);
    }

    private async Task<IndexMapping> ResolveMappingAsync(string indexOrAlias, CancellationToken cancellationToken)
    {
        var state = await _clusterStore.GetAsync(cancellationToken);
        var name = state.ResolveIndex(indexOrAlias);

        return state.Indexes.TryGetValue(name, out var metadata)
            ? metadata.Mapping
            : throw new IndexNotFoundException(indexOrAlias);
    }
}
