using Azure.Identity;
using Azure.Storage.Blobs;
using DistSear.Abstractions.Storage;
using DistSear.Storage.Azure.Blobs;
using DistSear.Storage.Azure.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DistSear.Storage.Azure;

public static class AzureStorageRegistration
{
    /// <summary>
    /// Registers the Azure-backed stores.
    ///
    /// Nothing above this line knows which implementation it is using: the node and coordinator
    /// depend only on the storage interfaces, so switching between Azure and the in-process stores
    /// is a configuration change rather than a code path.
    /// </summary>
    public static IServiceCollection AddAzureStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AzureStorageOptions>(configuration.GetSection(AzureStorageOptions.SectionName));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureStorageOptions>>().Value;

            var clientOptions = new CosmosClientOptions
            {
                // The pull-model change feed and point reads are all gateway-friendly, and gateway
                // mode is the only mode the Linux emulator supports.
                ConnectionMode = ConnectionMode.Gateway,
                UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions()
            };

            if (options.AllowUntrustedCertificate)
            {
                // Development only: the emulator presents a self-signed certificate. Guarded by an
                // explicit setting so it can never be switched on by accident in a deployment.
                clientOptions.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            return options.UsesEmulator
                ? new CosmosClient(options.CosmosEndpoint, options.CosmosKey, clientOptions)

                // Managed identity in Azure: no keys in configuration, nothing to rotate or leak.
                : new CosmosClient(options.CosmosEndpoint, new DefaultAzureCredential(), clientOptions);
        });

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureStorageOptions>>().Value;

            return options.UsesEmulator
                ? new BlobServiceClient(options.BlobEndpoint)
                : new BlobServiceClient(new Uri(options.BlobEndpoint), new DefaultAzureCredential());
        });

        services.AddSingleton<IDocumentStore, CosmosDocumentStore>();
        services.AddSingleton<IClusterStore, CosmosClusterStore>();
        services.AddSingleton<BlobSegmentStore>();
        services.AddSingleton<ISegmentStore>(sp => sp.GetRequiredService<BlobSegmentStore>());
        services.AddSingleton<ICheckpointStore, BlobCheckpointStore>();
        services.AddSingleton<BlobLeaseElector>();
        services.AddSingleton<ILeaderElector>(sp => sp.GetRequiredService<BlobLeaseElector>());

        services.AddHostedService<AzureStorageProvisioner>();

        return services;
    }
}

/// <summary>
/// Creates the database, containers and blob containers on start if they are missing.
///
/// Runs before anything else so a fresh deployment — or a developer's first <c>docker compose up</c> —
/// works without a manual provisioning step. Every operation is create-if-not-exists, so it is safe
/// to run on every replica at every start.
/// </summary>
internal sealed class AzureStorageProvisioner : IHostedService
{
    private readonly CosmosClient _cosmos;
    private readonly BlobSegmentStore _segments;
    private readonly BlobLeaseElector _leases;
    private readonly AzureStorageOptions _options;
    private readonly ILogger<AzureStorageProvisioner> _logger;

    public AzureStorageProvisioner(
        CosmosClient cosmos,
        BlobSegmentStore segments,
        BlobLeaseElector leases,
        IOptions<AzureStorageOptions> options,
        ILogger<AzureStorageProvisioner> logger)
    {
        _cosmos = cosmos;
        _segments = segments;
        _leases = leases;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var database = await _cosmos.CreateDatabaseIfNotExistsAsync(
            _options.DatabaseName,
            cancellationToken: cancellationToken);

        // Partitioned by the shard the document routes to, which is what lets a node read the change
        // feed for only its own shards.
        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.DocumentsContainer, "/shardKey")
            {
                // Off by default; only tombstones set a per-item TTL.
                DefaultTimeToLive = -1
            },
            cancellationToken: cancellationToken);

        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.ClusterContainer, "/partition"),
            cancellationToken: cancellationToken);

        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.NodesContainer, "/nodeId")
            {
                // Node registrations expire on their own, so liveness needs no reaper.
                DefaultTimeToLive = -1
            },
            cancellationToken: cancellationToken);

        await _segments.EnsureCreatedAsync(cancellationToken);
        await _leases.EnsureCreatedAsync(cancellationToken);

        _logger.LogInformation("Azure storage provisioned for database '{Database}'.", _options.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
