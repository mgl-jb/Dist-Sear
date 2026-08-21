using System.Net.Http.Json;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;

namespace DistSear.Client;

/// <summary>A document being sent for indexing.</summary>
public sealed record SearchDocument
{
    public required string Id { get; init; }

    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary>Principals permitted to see this document. Empty means unrestricted.</summary>
    public IReadOnlyList<string> Acl { get; init; } = [];

    /// <summary>Co-locates related documents on one shard, so queries scoped to the key avoid a fan-out.</summary>
    public string? RoutingKey { get; init; }
}

public sealed record BulkOutcome(int Indexed, int Deleted, IReadOnlyList<string> Errors);

public sealed record IndexDefinition(
    IReadOnlyList<FieldMapping> Fields,
    int NumberOfShards = 1,
    int NumberOfReplicas = 1,
    string? DefaultField = null);

/// <summary>
/// Typed client for the coordinator's public API.
///
/// Deliberately thin: it owns no retry or failover logic, because the coordinator already performs
/// per-shard retries against replicas and a second, uncoordinated retry layer here would multiply
/// load during exactly the outage it is meant to survive.
/// </summary>
public sealed class SearchClient
{
    private readonly HttpClient _http;

    public SearchClient(HttpClient http) => _http = http;

    /// <summary>Sets the API key sent with every request from this client.</summary>
    public void UseApiKey(string apiKey)
    {
        _http.DefaultRequestHeaders.Remove("X-Api-Key");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<SearchResponse> SearchAsync(
        string index,
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/indexes/{Uri.EscapeDataString(index)}/_search",
            request with { Index = index },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await ReadAsync<SearchResponse>(response, cancellationToken);
    }

    /// <summary>Convenience overload for the common "just search for this text" case.</summary>
    public Task<SearchResponse> SearchAsync(
        string index,
        string query,
        int size = 10,
        CancellationToken cancellationToken = default) =>
        SearchAsync(index, new SearchRequest { Index = index, Query = query, Size = size }, cancellationToken);

    public async Task<BulkOutcome> IndexAsync(
        string index,
        IReadOnlyList<SearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/indexes/{Uri.EscapeDataString(index)}/_bulk",
            documents,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await ReadAsync<BulkOutcome>(response, cancellationToken);
    }

    public async Task<BulkOutcome> DeleteAsync(
        string index,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/indexes/{Uri.EscapeDataString(index)}/_delete",
            ids,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await ReadAsync<BulkOutcome>(response, cancellationToken);
    }

    public async Task CreateIndexAsync(
        string index,
        IndexDefinition definition,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(
            $"/indexes/{Uri.EscapeDataString(index)}",
            definition,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Points an alias at an index. Atomic, so a reindex can be swapped in without downtime.</summary>
    public async Task SetAliasAsync(
        string alias,
        string index,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "/_aliases",
            new { alias, index },
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Pages through an entire result set using cursors rather than offsets, which is the only way
    /// to walk a large set: offset paging costs from+size on every shard and collapses with depth.
    /// </summary>
    public async IAsyncEnumerable<SearchHit> ScrollAsync(
        string index,
        SearchRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<object?>? cursor = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await SearchAsync(
                index,
                request with { From = 0, SearchAfter = cursor },
                cancellationToken);

            if (page.Hits.Count == 0)
            {
                yield break;
            }

            foreach (var hit in page.Hits)
            {
                yield return hit;
            }

            cursor = page.Hits[^1].SortValues;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(cancellationToken)
        ?? throw new InvalidOperationException($"The coordinator returned an empty {typeof(T).Name}.");
}
