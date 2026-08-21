using System.Collections.Concurrent;
using System.Net.Http.Json;
using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Search;
using DistSear.Abstractions.Storage;
using DistSear.Abstractions.Transport;

namespace DistSear.Coordinator.Transport;

/// <summary>Envelope carrying the caller's principals to the node alongside the query.</summary>
public sealed record ShardQueryEnvelope(ShardQueryRequest Request, IReadOnlyList<string>? Principals);

public sealed record ShardSuggestEnvelope(
    int ShardId,
    SuggestRequest Request,
    IReadOnlyList<string>? Principals);

/// <summary>
/// Talks to index nodes over Container Apps internal ingress.
///
/// Node addresses come from the cluster's own node registry rather than from DNS guessing, so a
/// replica that has been replaced is reached at its new address as soon as it heartbeats. Addresses
/// are cached per call batch and refreshed from the store, keeping the hot path free of lookups
/// while still converging on membership changes.
/// </summary>
public sealed class HttpNodeTransport : INodeTransport
{
    private readonly HttpClient _client;
    private readonly IClusterStore _clusterStore;
    private readonly ConcurrentDictionary<string, string> _addresses = new(StringComparer.Ordinal);

    public HttpNodeTransport(HttpClient client, IClusterStore clusterStore)
    {
        _client = client;
        _clusterStore = clusterStore;
    }

    public async Task<ShardQueryResult> QueryAsync(
        string nodeId,
        ShardQueryRequest request,
        CancellationToken cancellationToken) =>
        await PostAsync<ShardQueryEnvelope, ShardQueryResult>(
            nodeId,
            "/_internal/shards/query",
            new ShardQueryEnvelope(request, CurrentPrincipals.Value),
            cancellationToken);

    public async Task<ShardFetchResult> FetchAsync(
        string nodeId,
        ShardFetchRequest request,
        CancellationToken cancellationToken) =>
        await PostAsync<ShardFetchRequest, ShardFetchResult>(
            nodeId,
            "/_internal/shards/fetch",
            request,
            cancellationToken);

    public async Task<CollectionStatistics> StatisticsAsync(
        string nodeId,
        ShardStatsRequest request,
        CancellationToken cancellationToken) =>
        await PostAsync<ShardStatsRequest, CollectionStatistics>(
            nodeId,
            "/_internal/shards/stats",
            request,
            cancellationToken);

    public async Task<SuggestResponse> SuggestAsync(
        string nodeId,
        int shardId,
        SuggestRequest request,
        CancellationToken cancellationToken) =>
        await PostAsync<ShardSuggestEnvelope, SuggestResponse>(
            nodeId,
            "/_internal/shards/suggest",
            new ShardSuggestEnvelope(shardId, request, CurrentPrincipals.Value),
            cancellationToken);

    /// <summary>Principals for the in-flight request, flowed to nodes so they can filter by ACL.</summary>
    public static readonly AsyncLocal<IReadOnlyList<string>?> CurrentPrincipals = new();

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string nodeId,
        string path,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        var address = await ResolveAddressAsync(nodeId, cancellationToken);

        using var response = await _client.PostAsJsonAsync(
            address.TrimEnd('/') + path,
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
            ?? throw new InvalidOperationException($"Node '{nodeId}' returned an empty response from {path}.");
    }

    private async Task<string> ResolveAddressAsync(string nodeId, CancellationToken cancellationToken)
    {
        if (_addresses.TryGetValue(nodeId, out var cached))
        {
            return cached;
        }

        foreach (var node in await _clusterStore.GetNodesAsync(cancellationToken))
        {
            _addresses[node.NodeId] = node.Address;
        }

        return _addresses.TryGetValue(nodeId, out var address)
            ? address
            : throw new InvalidOperationException($"Node '{nodeId}' has no registered address.");
    }

    /// <summary>Drops a cached address so the next call re-reads it from the registry.</summary>
    public void Forget(string nodeId) => _addresses.TryRemove(nodeId, out _);
}
