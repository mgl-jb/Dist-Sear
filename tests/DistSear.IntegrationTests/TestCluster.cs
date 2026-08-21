using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Cluster;
using DistSear.Cluster.Allocation;
using DistSear.Cluster.InMemory;
using DistSear.Coordinator.Search;
using DistSear.Node.Shards;
using Microsoft.Extensions.Options;

namespace DistSear.IntegrationTests;

/// <summary>
/// A complete search cluster running in one process: shared stores, several index nodes, and a
/// coordinator fanning out across them.
///
/// Everything below the transport is the production code path, so these tests exercise routing,
/// recovery, merging and failover for real — they just skip the network and the emulators.
/// </summary>
public sealed class TestCluster : IAsyncDisposable
{
    private readonly Dictionary<string, ShardHost> _hosts = new(StringComparer.Ordinal);
    private readonly AnalyzerRegistry _analyzers = new();

    private TestCluster(CoordinatorOptions? options = null)
    {
        Documents = new InMemoryDocumentStore();
        ClusterStore = new InMemoryClusterStore();
        Segments = new InMemorySegmentStore();
        Checkpoints = new InMemoryCheckpointStore();
        Elector = new InMemoryLeaderElector();

        Controller = new ClusterController(ClusterStore, Elector);
        Transport = new InProcessNodeTransport();
        Replicas = new AdaptiveReplicaSelector();
        Options = options ?? new CoordinatorOptions();

        Coordinator = new SearchCoordinator(
            ClusterStore,
            Transport,
            Replicas,
            _analyzers,
            Microsoft.Extensions.Options.Options.Create(Options));

        Indexing = new IndexingService(Documents, ClusterStore);
    }

    public InMemoryDocumentStore Documents { get; }

    public InMemoryClusterStore ClusterStore { get; }

    public InMemorySegmentStore Segments { get; }

    public InMemoryCheckpointStore Checkpoints { get; }

    public InMemoryLeaderElector Elector { get; }

    public ClusterController Controller { get; }

    public InProcessNodeTransport Transport { get; }

    public AdaptiveReplicaSelector Replicas { get; }

    public CoordinatorOptions Options { get; }

    public SearchCoordinator Coordinator { get; }

    public IndexingService Indexing { get; }

    public IReadOnlyCollection<string> NodeIds => _hosts.Keys;

    /// <summary>Builds a cluster with the given number of nodes.</summary>
    public static async Task<TestCluster> StartAsync(int nodeCount, CoordinatorOptions? options = null)
    {
        var cluster = new TestCluster(options);

        for (var i = 0; i < nodeCount; i++)
        {
            await cluster.AddNodeAsync($"node-{(char)('a' + i)}");
        }

        return cluster;
    }

    public async Task AddNodeAsync(string nodeId)
    {
        var host = new ShardHost(
            nodeId,
            _analyzers,
            Documents,
            Checkpoints,
            Segments,
            Controller);

        _hosts[nodeId] = host;
        Transport.Register(nodeId, new ShardQueryService(host, _analyzers));

        await ClusterStore.HeartbeatAsync(
            new NodeInfo { NodeId = nodeId, Address = $"inproc://{nodeId}" },
            default);
    }

    /// <summary>
    /// Removes a node abruptly, as a crash would. Its heartbeat stops and its shards become
    /// unreachable until the coordinator reallocates them.
    /// </summary>
    public void KillNode(string nodeId)
    {
        _hosts.Remove(nodeId);
        Transport.Remove(nodeId);
        ClusterStore.Kill(nodeId);
    }

    public ShardHost Host(string nodeId) => _hosts[nodeId];

    public async Task CreateIndexAsync(IndexMapping mapping)
    {
        await Controller.CreateIndexAsync(mapping, default);
        await StabiliseAsync();
    }

    public Task<BulkResult> IndexAsync(string index, IReadOnlyList<IncomingDocument> documents) =>
        Indexing.IndexAsync(index, documents, default);

    public Task<BulkResult> DeleteAsync(string index, IReadOnlyList<string> ids) =>
        Indexing.DeleteAsync(index, ids, default);

    /// <summary>
    /// Drives the cluster to a steady state: reconcile placement, let every node converge on the
    /// allocation table, and drain the change feed. Replaces waiting on background timers, so tests
    /// are deterministic rather than timing-dependent.
    /// </summary>
    public async Task StabiliseAsync(int rounds = 3)
    {
        for (var round = 0; round < rounds; round++)
        {
            await Controller.TryReconcileAsync(default);

            var state = await Controller.GetStateAsync(default);

            foreach (var host in _hosts.Values.ToList())
            {
                await host.ReconcileAsync(state, default);
                await host.CatchUpAllAsync(default);
            }
        }
    }

    public Task<SearchResponse> SearchAsync(SearchRequest request, SearchPrincipal? caller = null)
    {
        var principal = caller ?? SearchPrincipal.Unrestricted;

        // The HTTP transport carries principals in the request envelope; in process they flow
        // through an async-local, so set it the same way the coordinator endpoint would.
        InProcessNodeTransport.CurrentPrincipals = principal.Principals;

        return Coordinator.SearchAsync(request, principal, default);
    }

    public Task<SearchResponse> SearchAsync(string index, string query, int size = 10) =>
        SearchAsync(new SearchRequest { Index = index, Query = query, Size = size });

    public async Task SnapshotAllAsync()
    {
        foreach (var host in _hosts.Values)
        {
            await host.SnapshotAllAsync(default);
        }
    }

    public async Task<IReadOnlyList<ShardAllocation>> AllocationsAsync(string index)
    {
        var state = await Controller.GetStateAsync(default);
        return [.. state.ShardsOf(index).OrderBy(s => s.ShardId)];
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Mapping used by most tests: several shards so fan-out is genuinely exercised.</summary>
    public static IndexMapping CatalogMapping(int shards = 4, int replicas = 2) => new(
        "catalog",
        [
            new FieldMapping
            {
                Name = "title",
                Type = FieldType.Text,
                Analyzer = AnalyzerRegistry.English,
                Boost = 2.0
            },
            new FieldMapping { Name = "body", Type = FieldType.Text, Analyzer = AnalyzerRegistry.English },
            new FieldMapping { Name = "category", Type = FieldType.Keyword, DocValues = true },
            new FieldMapping { Name = "price", Type = FieldType.Double, DocValues = true },
            new FieldMapping { Name = "year", Type = FieldType.Long, DocValues = true }
        ],
        numberOfShards: shards,
        numberOfReplicas: replicas,
        defaultField: "title");

    public static IncomingDocument Document(
        string id,
        string title,
        string body = "",
        string category = "general",
        double price = 10,
        long year = 2020,
        string[]? acl = null) =>
        new()
        {
            Id = id,
            Acl = acl ?? [],
            Fields = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["body"] = body,
                ["category"] = category,
                ["price"] = price,
                ["year"] = year
            }
        };
}
