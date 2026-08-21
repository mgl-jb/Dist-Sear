using System.Collections.Concurrent;
using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Search;
using DistSear.Abstractions.Transport;
using DistSear.Node.Shards;

namespace DistSear.IntegrationTests;

/// <summary>
/// Routes coordinator calls straight to a node's query service, with no network in between.
///
/// This is what makes the distributed behaviour — routing, merging, replica failover, partial
/// results — testable deterministically. The production transport differs only in serialisation, so
/// everything above it is exercised exactly as it runs in the cluster.
/// </summary>
public sealed class InProcessNodeTransport : INodeTransport
{
    private readonly ConcurrentDictionary<string, ShardQueryService> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeBehaviour> _behaviours = new(StringComparer.Ordinal);

    public void Register(string nodeId, ShardQueryService service) => _nodes[nodeId] = service;

    public void Remove(string nodeId)
    {
        _nodes.TryRemove(nodeId, out _);
        _behaviours.TryRemove(nodeId, out _);
    }

    /// <summary>Makes a node fail every call, simulating a crashed or unreachable replica.</summary>
    public void Fail(string nodeId) => _behaviours[nodeId] = NodeBehaviour.Fail;

    /// <summary>Makes a node hang, so the coordinator's per-shard deadline is what ends the call.</summary>
    public void Hang(string nodeId) => _behaviours[nodeId] = NodeBehaviour.Hang;

    public void Heal(string nodeId) => _behaviours.TryRemove(nodeId, out _);

    /// <summary>Calls received per node, for asserting which shards were actually contacted.</summary>
    public ConcurrentDictionary<string, int> QueryCalls { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, int> FetchCalls { get; } = new(StringComparer.Ordinal);

    public async Task<ShardQueryResult> QueryAsync(
        string nodeId,
        ShardQueryRequest request,
        CancellationToken cancellationToken)
    {
        QueryCalls.AddOrUpdate(nodeId, 1, (_, count) => count + 1);
        await ApplyBehaviourAsync(nodeId, cancellationToken);

        return Resolve(nodeId).Query(request, CurrentPrincipals, cancellationToken);
    }

    public async Task<ShardFetchResult> FetchAsync(
        string nodeId,
        ShardFetchRequest request,
        CancellationToken cancellationToken)
    {
        FetchCalls.AddOrUpdate(nodeId, 1, (_, count) => count + 1);
        await ApplyBehaviourAsync(nodeId, cancellationToken);

        return Resolve(nodeId).Fetch(request, cancellationToken);
    }

    public async Task<CollectionStatistics> StatisticsAsync(
        string nodeId,
        ShardStatsRequest request,
        CancellationToken cancellationToken)
    {
        await ApplyBehaviourAsync(nodeId, cancellationToken);
        return Resolve(nodeId).Statistics(request);
    }

    public async Task<SuggestResponse> SuggestAsync(
        string nodeId,
        int shardId,
        SuggestRequest request,
        CancellationToken cancellationToken)
    {
        await ApplyBehaviourAsync(nodeId, cancellationToken);
        return Resolve(nodeId).Suggest(shardId, request, CurrentPrincipals, cancellationToken);
    }

    /// <summary>
    /// Principals for the current call. The real transport carries these in the request envelope;
    /// in process they are passed through an async-local so the harness stays simple.
    /// </summary>
    public static IReadOnlyList<string>? CurrentPrincipals
    {
        get => _principals.Value;
        set => _principals.Value = value;
    }

    private static readonly AsyncLocal<IReadOnlyList<string>?> _principals = new();

    private ShardQueryService Resolve(string nodeId) =>
        _nodes.TryGetValue(nodeId, out var service)
            ? service
            : throw new InvalidOperationException($"Node '{nodeId}' is not reachable.");

    private async Task ApplyBehaviourAsync(string nodeId, CancellationToken cancellationToken)
    {
        if (!_behaviours.TryGetValue(nodeId, out var behaviour))
        {
            return;
        }

        switch (behaviour)
        {
            case NodeBehaviour.Fail:
                throw new HttpRequestException($"Node '{nodeId}' is unreachable.");

            case NodeBehaviour.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
        }
    }

    private enum NodeBehaviour
    {
        Fail,
        Hang
    }
}
