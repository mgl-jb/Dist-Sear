using System.Net;
using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace DistSear.Storage.Azure.Cosmos;

/// <summary>
/// The durable document store, backed by Cosmos DB.
///
/// The container is partitioned by <c>/shardKey</c>, which is the shard the document routes to. That
/// single decision is what lets an index node read the change feed for exactly the shards it owns
/// and nothing else, and it is why the shard count is fixed at index creation.
/// </summary>
public sealed class CosmosDocumentStore : IDocumentStore
{
    private readonly Container _container;
    private readonly AzureStorageOptions _options;

    public CosmosDocumentStore(CosmosClient client, IOptions<AzureStorageOptions> options)
    {
        _options = options.Value;
        _container = client.GetContainer(_options.DatabaseName, _options.DocumentsContainer);
    }

    /// <summary>
    /// Writes documents, batching by partition key. A <see cref="TransactionalBatch"/> is scoped to
    /// one partition, so documents are grouped by shard first; that also means a bulk write spanning
    /// shards is atomic per shard rather than globally, which is the correct granularity here since
    /// each shard is indexed independently anyway.
    /// </summary>
    public async Task UpsertAsync(IReadOnlyList<IndexedDocument> documents, CancellationToken cancellationToken)
    {
        foreach (var group in documents.GroupBy(d => d.ShardKey, StringComparer.Ordinal))
        {
            var partitionKey = new PartitionKey(group.Key);
            var pending = group.ToList();

            // Cosmos caps a transactional batch at 100 operations.
            foreach (var chunk in pending.Chunk(100))
            {
                if (chunk.Length == 1)
                {
                    var entity = CosmosDocumentEntity.From(chunk[0], _options.TombstoneRetention);

                    await _container.UpsertItemAsync(
                        entity,
                        partitionKey,
                        cancellationToken: cancellationToken);

                    continue;
                }

                var batch = _container.CreateTransactionalBatch(partitionKey);

                foreach (var document in chunk)
                {
                    batch.UpsertItem(CosmosDocumentEntity.From(document, _options.TombstoneRetention));
                }

                using var response = await batch.ExecuteAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new CosmosException(
                        $"Bulk write to shard '{group.Key}' failed: {response.ErrorMessage}",
                        response.StatusCode,
                        subStatusCode: 0,
                        activityId: response.ActivityId,
                        requestCharge: response.RequestCharge);
                }
            }
        }
    }

    public async Task<IReadOnlyList<IndexedDocument>> GetAsync(
        string shardKey,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        // A point-read batch is far cheaper than a query: every read is by id within one partition.
        var reads = ids
            .Select(id => (id, new PartitionKey(shardKey)))
            .ToList();

        var response = await _container.ReadManyItemsAsync<CosmosDocumentEntity>(
            reads,
            cancellationToken: cancellationToken);

        return
        [
            .. response
                .Where(entity => !entity.Deleted)
                .Select(entity => entity.ToDocument())
        ];
    }

    public IChangeFeedCursor OpenChangeFeed(string shardKey, string? continuationToken) =>
        new CosmosChangeFeedCursor(_container, shardKey, continuationToken, _options.ChangeFeedPageSize);

    /// <summary>
    /// Reads one shard's slice of the change feed.
    ///
    /// Scoped to a single partition key, which the pull model supports and the change feed processor
    /// does not — the processor distributes ranges across consumers, whereas every replica of a shard
    /// needs that shard's complete feed.
    /// </summary>
    private sealed class CosmosChangeFeedCursor : IChangeFeedCursor
    {
        private readonly FeedIterator<CosmosDocumentEntity> _iterator;
        private string? _continuationToken;

        public CosmosChangeFeedCursor(
            Container container,
            string shardKey,
            string? continuationToken,
            int pageSize)
        {
            _continuationToken = continuationToken;

            // A continuation token already encodes its feed range, so it must not be combined with a
            // start position.
            var startFrom = continuationToken is null
                ? ChangeFeedStartFrom.Beginning(FeedRange.FromPartitionKey(new PartitionKey(shardKey)))
                : ChangeFeedStartFrom.ContinuationToken(continuationToken);

            _iterator = container.GetChangeFeedIterator<CosmosDocumentEntity>(
                startFrom,

                // Latest-version mode does not emit deletions, which is why deletes are modelled as
                // tombstone updates that travel the feed like any other change.
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = pageSize });
        }

        public async Task<ChangeFeedBatch> ReadNextAsync(CancellationToken cancellationToken)
        {
            // HasMoreResults is always true for a change feed: it is an unbounded stream, so being
            // caught up is signalled by NotModified rather than by the iterator ending.
            var response = await _iterator.ReadNextAsync(cancellationToken);

            _continuationToken = response.ContinuationToken ?? _continuationToken;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new ChangeFeedBatch
                {
                    Documents = [],
                    ContinuationToken = _continuationToken,
                    HasMore = false
                };
            }

            var documents = response.Select(entity => entity.ToDocument()).ToList();

            return new ChangeFeedBatch
            {
                Documents = documents,

                // An empty page with status OK does not mean the feed is drained; only NotModified
                // does. Reporting more work keeps the reader polling until it genuinely catches up.
                ContinuationToken = _continuationToken,
                HasMore = true
            };
        }

        public ValueTask DisposeAsync()
        {
            _iterator.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
