using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Abstractions.Storage;
using DistSear.Abstractions.Transport;
using DistSear.Analysis;
using DistSear.Cluster;
using DistSear.Cluster.Allocation;
using DistSear.Cluster.InMemory;
using DistSear.Storage.Azure;
using DistSear.Coordinator.Search;
using DistSear.Coordinator.Transport;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CoordinatorOptions>(
    builder.Configuration.GetSection(CoordinatorOptions.SectionName));

var storage = builder.Configuration["DistSear:Storage"] ?? "InMemory";

if (string.Equals(storage, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDocumentStore, InMemoryDocumentStore>();
    builder.Services.AddSingleton<IClusterStore, InMemoryClusterStore>();
    builder.Services.AddSingleton<ISegmentStore, InMemorySegmentStore>();
    builder.Services.AddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
    builder.Services.AddSingleton<ILeaderElector, InMemoryLeaderElector>();
}
else if (string.Equals(storage, "Azure", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAzureStorage(builder.Configuration);
}
else
{
    throw new InvalidOperationException(
        $"Unknown storage provider '{storage}'. Configure DistSear:Storage as 'InMemory' or 'Azure'.");
}

builder.Services.AddSingleton(new AnalyzerRegistry());
builder.Services.AddSingleton<ClusterController>();
builder.Services.AddSingleton<AdaptiveReplicaSelector>();
builder.Services.AddSingleton<SearchCoordinator>();
builder.Services.AddSingleton<IndexingService>();

// Resilience is applied at the transport, but the coordinator still runs its own per-shard deadline
// and replica failover: the handler cannot know that a different replica would do just as well.
builder.Services.AddHttpClient<INodeTransport, HttpNodeTransport>()
    .AddStandardResilienceHandler();

var app = builder.Build();

app.MapPost("/indexes/{index}/_search", async (
    string index,
    [FromBody] SearchRequest request,
    SearchCoordinator coordinator,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var caller = PrincipalOf(context);
    HttpNodeTransport.CurrentPrincipals.Value = caller.Principals;

    var response = await coordinator.SearchAsync(
        request with { Index = index },
        caller,
        cancellationToken);

    return Results.Ok(response);
});

app.MapPost("/indexes/{index}/_bulk", async (
    string index,
    [FromBody] IReadOnlyList<IncomingDocument> documents,
    IndexingService indexing,
    CancellationToken cancellationToken) =>
    Results.Ok(await indexing.IndexAsync(index, documents, cancellationToken)));

app.MapPost("/indexes/{index}/_delete", async (
    string index,
    [FromBody] IReadOnlyList<string> ids,
    IndexingService indexing,
    CancellationToken cancellationToken) =>
    Results.Ok(await indexing.DeleteAsync(index, ids, cancellationToken)));

app.MapPut("/indexes/{index}", async (
    string index,
    [FromBody] CreateIndexRequest request,
    ClusterController controller,
    CancellationToken cancellationToken) =>
{
    var mapping = new IndexMapping(
        index,
        request.Fields,
        request.NumberOfShards,
        request.NumberOfReplicas,
        request.DefaultField);

    await controller.CreateIndexAsync(mapping, cancellationToken);
    return Results.Created($"/indexes/{index}", new { index, request.NumberOfShards, request.NumberOfReplicas });
});

app.MapPost("/_aliases", async (
    [FromBody] AliasRequest request,
    ClusterController controller,
    CancellationToken cancellationToken) =>
{
    await controller.SetAliasAsync(request.Alias, request.Index, cancellationToken);
    return Results.Ok(new { request.Alias, request.Index });
});

app.MapGet("/_cluster/state", async (ClusterController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);

    return Results.Ok(new
    {
        indexes = state.Indexes.Keys,
        aliases = state.Aliases,
        nodes = state.Nodes.Select(n => new { n.NodeId, n.Address, n.LastHeartbeat }),
        shards = state.Shards.Select(s => new
        {
            s.Index,
            s.ShardId,
            copies = s.Copies.Select(c => new { c.NodeId, state = c.State.ToString(), c.IsSnapshotOwner })
        })
    });
});

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

app.MapGet("/health/ready", async (IClusterStore store, CancellationToken cancellationToken) =>
{
    var state = await store.GetAsync(cancellationToken);
    var unavailable = ShardAllocator.UnavailableShards(state.Shards).ToList();

    // The coordinator is ready when it can reach a copy of every shard. Serving while shards are
    // unreachable would silently return partial results to callers who did not ask for them.
    return unavailable.Count == 0
        ? Results.Ok(new { status = "ready", shards = state.Shards.Count })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();

/// <summary>
/// Extracts the caller's groups from the authenticated identity. Document-level filtering is driven
/// entirely by this, and an unauthenticated caller sees only unrestricted documents.
/// </summary>
static SearchPrincipal PrincipalOf(HttpContext context)
{
    var groups = context.User.Claims
        .Where(c => c.Type is "groups" or "roles")
        .Select(c => c.Value)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    return new SearchPrincipal(groups);
}

public sealed record CreateIndexRequest(
    IReadOnlyList<FieldMapping> Fields,
    int NumberOfShards = 1,
    int NumberOfReplicas = 1,
    string? DefaultField = null);

public sealed record AliasRequest(string Alias, string Index);

/// <summary>Exposed so integration tests can host the coordinator in process.</summary>
public partial class Program;
