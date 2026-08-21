using DistSear.Abstractions.Search;
using DistSear.Abstractions.Storage;
using DistSear.Abstractions.Transport;
using DistSear.Analysis;
using DistSear.Cluster;
using DistSear.Cluster.InMemory;
using DistSear.Node.Hosting;
using DistSear.Node.Shards;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(NodeOptions.SectionName));

// Storage is chosen by configuration so the same node binary runs against Azure in production and
// against in-process stores locally, with no conditional code on the request path.
var storage = builder.Configuration["DistSear:Storage"] ?? "InMemory";

if (string.Equals(storage, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<InMemoryDocumentStore>();
    builder.Services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
    builder.Services.AddSingleton<IClusterStore, InMemoryClusterStore>();
    builder.Services.AddSingleton<ISegmentStore, InMemorySegmentStore>();
    builder.Services.AddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
    builder.Services.AddSingleton<ILeaderElector, InMemoryLeaderElector>();
}
else
{
    throw new InvalidOperationException(
        $"Unknown storage provider '{storage}'. Configure DistSear:Storage as 'InMemory' or 'Azure'.");
}

builder.Services.AddSingleton(new AnalyzerRegistry());
builder.Services.AddSingleton<ClusterController>();

builder.Services.AddSingleton(sp => new ShardHost(
    sp.GetRequiredService<IOptions<NodeOptions>>().Value.NodeId,
    sp.GetRequiredService<AnalyzerRegistry>(),
    sp.GetRequiredService<IDocumentStore>(),
    sp.GetRequiredService<ICheckpointStore>(),
    sp.GetRequiredService<ISegmentStore>(),
    sp.GetRequiredService<ClusterController>(),
    sp.GetRequiredService<ILogger<ShardHost>>()));

builder.Services.AddSingleton<ShardQueryService>();
builder.Services.AddHostedService<NodeWorker>();

var app = builder.Build();

// Internal endpoints, reachable only over Container Apps internal ingress. The coordinator is the
// only intended caller; document-level security is applied by passing the caller's principals
// through rather than by trusting the node's own view of identity.
var shards = app.MapGroup("/_internal/shards");

shards.MapPost("/query", (
    [FromBody] ShardQueryEnvelope envelope,
    ShardQueryService service,
    CancellationToken cancellationToken) =>
    Results.Ok(service.Query(envelope.Request, envelope.Principals, cancellationToken)));

shards.MapPost("/fetch", (
    [FromBody] ShardFetchRequest request,
    ShardQueryService service,
    CancellationToken cancellationToken) =>
    Results.Ok(service.Fetch(request, cancellationToken)));

shards.MapPost("/stats", ([FromBody] ShardStatsRequest request, ShardQueryService service) =>
    Results.Ok(service.Statistics(request)));

shards.MapPost("/suggest", (
    [FromBody] ShardSuggestEnvelope envelope,
    ShardQueryService service,
    CancellationToken cancellationToken) =>
    Results.Ok(service.Suggest(envelope.ShardId, envelope.Request, envelope.Principals, cancellationToken)));

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

// Readiness is not liveness: a node that is still recovering a shard is alive but must not receive
// traffic, or queries would be answered from a half-built index.
app.MapGet("/health/ready", (ShardHost host) => host.IsReady
    ? Results.Ok(new { status = "ready", shards = host.Shards.Count })
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/_internal/shards", (ShardHost host) => Results.Ok(
    host.Shards.Select(s => new
    {
        index = s.IndexName,
        shard = s.ShardId,
        state = s.State.ToString(),
        documents = s.Index.DocumentCount,
        segments = s.Index.SegmentCount,
        snapshotOwner = s.IsSnapshotOwner,
        token = s.Indexer.ContinuationToken
    })));

app.Run();

/// <summary>Carries the caller's principals alongside the query so the node can filter by ACL.</summary>
public sealed record ShardQueryEnvelope(ShardQueryRequest Request, IReadOnlyList<string>? Principals);

public sealed record ShardSuggestEnvelope(
    int ShardId,
    SuggestRequest Request,
    IReadOnlyList<string>? Principals);

/// <summary>Exposed so integration tests can drive the node through WebApplicationFactory.</summary>
public partial class Program;
