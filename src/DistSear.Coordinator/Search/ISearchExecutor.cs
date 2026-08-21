using DistSear.Abstractions.Search;

namespace DistSear.Coordinator.Search;

/// <summary>
/// Executes a distributed search. Exists so caching can decorate the coordinator rather than being
/// threaded through it — the fan-out logic has no reason to know whether its answer was cached.
/// </summary>
public interface ISearchExecutor
{
    Task<SearchResponse> SearchAsync(
        SearchRequest request,
        SearchPrincipal caller,
        CancellationToken cancellationToken = default);
}
