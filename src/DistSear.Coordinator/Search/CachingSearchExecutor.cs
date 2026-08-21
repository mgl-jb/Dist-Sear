using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DistSear.Abstractions.Search;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DistSear.Coordinator.Search;

/// <summary>
/// Caches search responses in Redis.
///
/// Two properties make this safe rather than merely fast.
///
/// First, the cache key includes the caller's principals. A cache keyed only on the query would let
/// one user's results be served to another whose access differs — the entry would carry documents
/// the second user is not allowed to see. Identity is part of what was asked, so it is part of the key.
///
/// Second, entries are short-lived rather than invalidated. The index moves continuously as the
/// change feed is applied, and there is no single event meaning "this query's answer changed", so
/// the lifetime is a deliberate staleness bound: results may be up to the configured duration old.
/// Partial results are never cached, since caching a failure would keep serving it after the cluster
/// had recovered.
/// </summary>
public sealed class CachingSearchExecutor : ISearchExecutor
{
    /// <summary>
    /// Delimits key components. Every component is length-bounded and hashed afterwards, so a
    /// printable delimiter is enough to keep components from running together.
    /// </summary>
    private const char Separator = '|';

    private readonly ISearchExecutor _inner;
    private readonly IDistributedCache _cache;
    private readonly CoordinatorOptions _options;
    private readonly ILogger<CachingSearchExecutor>? _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public CachingSearchExecutor(
        ISearchExecutor inner,
        IDistributedCache cache,
        IOptions<CoordinatorOptions> options,
        ILogger<CachingSearchExecutor>? logger = null)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        SearchPrincipal caller,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CacheEnabled)
        {
            return await _inner.SearchAsync(request, caller, cancellationToken);
        }

        var key = BuildKey(request, caller);

        try
        {
            var cached = await _cache.GetAsync(key, cancellationToken);

            if (cached is not null
                && JsonSerializer.Deserialize<SearchResponse>(cached, SerializerOptions) is { } hit)
            {
                return hit with { TookMilliseconds = 0 };
            }
        }
        catch (Exception exception)
        {
            // A cache outage must degrade latency, never availability.
            _logger?.LogWarning(exception, "Search cache read failed; falling through to the cluster.");
        }

        var response = await _inner.SearchAsync(request, caller, cancellationToken);

        if (response.Shards.IsPartial || response.TimedOut)
        {
            return response;
        }

        try
        {
            await _cache.SetAsync(
                key,
                JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.CacheDuration },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Search cache write failed.");
        }

        return response;
    }

    /// <summary>
    /// Hashes everything that can change the answer, including who is asking. Hashing keeps keys
    /// short and bounded however large the query is.
    /// </summary>
    internal static string BuildKey(SearchRequest request, SearchPrincipal caller)
    {
        var builder = new StringBuilder();

        void Part(string? value) => builder.Append(value).Append(Separator);

        Part(request.Index);
        Part(request.Query);
        Part(request.Filter);
        Part(request.From.ToString());
        Part(request.Size.ToString());
        Part(request.SearchType.ToString());
        Part(string.Join(',', request.Sort.Select(s => $"{s.Field}:{(s.Descending ? "desc" : "asc")}")));
        Part(string.Join(',', request.Facets.Select(f => $"{f.Name}:{f.Field}:{f.Kind}:{f.Size}")));
        Part(request.Fields is null ? null : string.Join(',', request.Fields));
        Part(request.SearchAfter is null ? null : string.Join(',', request.SearchAfter));
        Part(request.Highlight is null ? null : string.Join(',', request.Highlight.Fields));

        // Identity, sorted so the same groups in a different order share one entry.
        Part(caller.EnforceAcl
            ? "acl:" + string.Join(',', caller.Groups.Order(StringComparer.Ordinal))
            : "unrestricted");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return "distsear:search:" + Convert.ToHexStringLower(hash);
    }
}
