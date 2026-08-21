using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace DistSear.Storage.Azure.Cosmos;

/// <summary>
/// Cluster topology stored in Cosmos.
///
/// The whole allocation table lives in one document, updated under <c>IfMatchEtag</c>. Keeping it in
/// a single document is what makes an update atomic: reallocating a shard touches several entries at
/// once, and a partial write would leave shards double-assigned or stranded.
/// </summary>
public sealed class CosmosClusterStore : IClusterStore
{
    private const string StateId = "cluster-state";
    private const string StatePartition = "cluster";

    private readonly Container _state;
    private readonly Container _nodes;
    private readonly AzureStorageOptions _options;
    private readonly TimeProvider _time;

    public CosmosClusterStore(
        CosmosClient client,
        IOptions<AzureStorageOptions> options,
        TimeProvider? time = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
        _state = client.GetContainer(_options.DatabaseName, _options.ClusterContainer);
        _nodes = client.GetContainer(_options.DatabaseName, _options.NodesContainer);
    }

    public async Task<ClusterState> GetAsync(CancellationToken cancellationToken)
    {
        var nodes = await GetNodesAsync(cancellationToken);

        try
        {
            var response = await _state.ReadItemAsync<ClusterStateEntity>(
                StateId,
                new PartitionKey(StatePartition),
                cancellationToken: cancellationToken);

            return response.Resource.ToState(response.ETag) with { Nodes = nodes };
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // A cluster that has never been configured is empty, not broken.
            return new ClusterState { ETag = null, Nodes = nodes };
        }
    }

    public async Task<ClusterState?> TryUpdateAsync(ClusterState next, CancellationToken cancellationToken)
    {
        var entity = ClusterStateEntity.From(next);

        var requestOptions = new ItemRequestOptions
        {
            // A null ETag means "this state was never persisted", so the write must only succeed if
            // the document still does not exist.
            IfMatchEtag = next.ETag
        };

        try
        {
            var response = next.ETag is null
                ? await _state.CreateItemAsync(
                    entity,
                    new PartitionKey(StatePartition),
                    cancellationToken: cancellationToken)
                : await _state.ReplaceItemAsync(
                    entity,
                    StateId,
                    new PartitionKey(StatePartition),
                    requestOptions,
                    cancellationToken);

            return response.Resource.ToState(response.ETag) with { Nodes = next.Nodes };
        }
        catch (CosmosException exception) when (
            exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            // Another writer won the race. Returning null tells the caller to re-read and re-plan
            // rather than retry a decision made against a version that no longer exists.
            return null;
        }
    }

    public async Task HeartbeatAsync(NodeInfo node, CancellationToken cancellationToken)
    {
        var entity = new NodeEntity
        {
            Id = node.NodeId,
            NodeId = node.NodeId,
            Address = node.Address,
            LastHeartbeat = _time.GetUtcNow(),

            // TTL is what makes liveness self-cleaning: a node that stops heartbeating disappears
            // without anyone having to notice and delete it.
            TimeToLive = (int)_options.NodeTimeToLive.TotalSeconds
        };

        await _nodes.UpsertItemAsync(
            entity,
            new PartitionKey(node.NodeId),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Live nodes, filtered by heartbeat age rather than trusting TTL alone.
    ///
    /// Cosmos deletes expired items as a background task using spare throughput, so a document can
    /// outlive its TTL by an unbounded margin under load. Treating a stale registration as live
    /// would allocate shards to a node that is gone, and those shards would sit unavailable until
    /// the record eventually vanished. TTL still does the cleanup; this decides liveness.
    /// </summary>
    public async Task<IReadOnlyList<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c");
        using var iterator = _nodes.GetItemQueryIterator<NodeEntity>(query);

        var cutoff = _time.GetUtcNow() - _options.NodeTimeToLive;
        var nodes = new List<NodeInfo>();

        while (iterator.HasMoreResults)
        {
            foreach (var entity in await iterator.ReadNextAsync(cancellationToken))
            {
                if (entity.LastHeartbeat < cutoff)
                {
                    continue;
                }

                nodes.Add(new NodeInfo
                {
                    NodeId = entity.NodeId,
                    Address = entity.Address,
                    LastHeartbeat = entity.LastHeartbeat,
                    Status = NodeStatus.Alive
                });
            }
        }

        // Ordered so that placement decisions are reproducible across coordinators.
        return [.. nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal)];
    }

    private sealed class NodeEntity
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("lastHeartbeat")]
        public DateTimeOffset LastHeartbeat { get; set; }

        [JsonPropertyName("ttl")]
        public int TimeToLive { get; set; }
    }

    /// <summary>
    /// Persisted shape of the cluster state. Mappings are stored in their constructor-ready form
    /// rather than as <see cref="IndexMapping"/>, which has no parameterless constructor.
    /// </summary>
    private sealed class ClusterStateEntity
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = StateId;

        [JsonPropertyName("partition")]
        public string Partition { get; set; } = StatePartition;

        [JsonPropertyName("indexes")]
        public Dictionary<string, IndexEntity> Indexes { get; set; } = [];

        [JsonPropertyName("aliases")]
        public Dictionary<string, string> Aliases { get; set; } = [];

        [JsonPropertyName("shards")]
        public List<ShardEntity> Shards { get; set; } = [];

        public static ClusterStateEntity From(ClusterState state) => new()
        {
            Indexes = state.Indexes.ToDictionary(
                kv => kv.Key,
                kv => new IndexEntity
                {
                    Fields = [.. kv.Value.Mapping.Fields],
                    NumberOfShards = kv.Value.Mapping.NumberOfShards,
                    NumberOfReplicas = kv.Value.Mapping.NumberOfReplicas,
                    DefaultField = kv.Value.Mapping.DefaultField,
                    CreatedAt = kv.Value.CreatedAt,
                    Generation = kv.Value.Generation
                }),
            Aliases = new Dictionary<string, string>(state.Aliases, StringComparer.Ordinal),
            Shards =
            [
                .. state.Shards.Select(s => new ShardEntity
                {
                    Index = s.Index,
                    ShardId = s.ShardId,
                    Copies =
                    [
                        .. s.Copies.Select(c => new CopyEntity
                        {
                            NodeId = c.NodeId,
                            State = c.State.ToString(),
                            IsSnapshotOwner = c.IsSnapshotOwner
                        })
                    ]
                })
            ]
        };

        public ClusterState ToState(string etag) => new()
        {
            ETag = etag,
            Indexes = Indexes.ToDictionary(
                kv => kv.Key,
                kv => new IndexMetadata
                {
                    Mapping = new IndexMapping(
                        kv.Key,
                        kv.Value.Fields,
                        kv.Value.NumberOfShards,
                        kv.Value.NumberOfReplicas,
                        kv.Value.DefaultField),
                    CreatedAt = kv.Value.CreatedAt,
                    Generation = kv.Value.Generation
                },
                StringComparer.Ordinal),
            Aliases = new Dictionary<string, string>(Aliases, StringComparer.Ordinal),
            Shards =
            [
                .. Shards.Select(s => new ShardAllocation
                {
                    Index = s.Index,
                    ShardId = s.ShardId,
                    Copies =
                    [
                        .. s.Copies.Select(c => new ShardCopy
                        {
                            NodeId = c.NodeId,
                            State = Enum.TryParse<ShardState>(c.State, out var parsed)
                                ? parsed
                                : ShardState.Unassigned,
                            IsSnapshotOwner = c.IsSnapshotOwner
                        })
                    ]
                })
            ]
        };
    }

    private sealed class IndexEntity
    {
        [JsonPropertyName("fields")]
        public List<FieldMapping> Fields { get; set; } = [];

        [JsonPropertyName("numberOfShards")]
        public int NumberOfShards { get; set; } = 1;

        [JsonPropertyName("numberOfReplicas")]
        public int NumberOfReplicas { get; set; } = 1;

        [JsonPropertyName("defaultField")]
        public string? DefaultField { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("generation")]
        public long Generation { get; set; }
    }

    private sealed class ShardEntity
    {
        [JsonPropertyName("index")]
        public string Index { get; set; } = string.Empty;

        [JsonPropertyName("shardId")]
        public int ShardId { get; set; }

        [JsonPropertyName("copies")]
        public List<CopyEntity> Copies { get; set; } = [];
    }

    private sealed class CopyEntity
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = nameof(ShardState.Unassigned);

        [JsonPropertyName("isSnapshotOwner")]
        public bool IsSnapshotOwner { get; set; }
    }
}
