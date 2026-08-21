using Azure.Storage.Blobs;
using DistSear.Storage.Azure;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;

namespace DistSear.IntegrationTests;

/// <summary>
/// Connects to the Cosmos and Azurite emulators when the environment supplies them.
///
/// These tests are trait-gated and skip when the emulators are absent, so the main suite stays
/// runnable on any machine with no Docker at all. What they add over the in-process tests is
/// narrow but essential: the in-process stores prove the cluster logic, and only these prove the
/// Azure SDK is being driven correctly — change-feed semantics, ETag concurrency and blob leases
/// are exactly the places an in-memory stand-in can quietly diverge.
/// </summary>
public sealed class AzureEmulatorFixture : IAsyncLifetime
{
    /// <summary>Well-known emulator key. Public by design and meaningless outside the emulator.</summary>
    private const string DefaultCosmosKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string DefaultBlobConnection =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;"
        + "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
        + "BlobEndpoint=http://localhost:10000/devstoreaccount1;";

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    /// <summary>
    /// Reports the test as skipped when the emulators are absent.
    ///
    /// Skipped, not passed: a vacuous pass would report the Azure implementations as verified when
    /// nothing had run against them. CI always supplies the emulators, so a skip locally is
    /// visible and a skip in CI would be a failure of the pipeline, not of the tests.
    /// </summary>
    public bool RequireEmulators()
    {
        Skip.IfNot(
            Available,
            $"Cosmos and Azurite emulators are not available. {SkipReason} "
            + "Start them with deploy/docker-compose.yml.");

        return false;
    }

    public CosmosClient Cosmos { get; private set; } = null!;

    public BlobServiceClient Blobs { get; private set; } = null!;

    public IOptions<AzureStorageOptions> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var cosmosEndpoint = Environment.GetEnvironmentVariable("DISTSEAR_TEST_COSMOS");
        var blobConnection = Environment.GetEnvironmentVariable("DISTSEAR_TEST_BLOB") ?? DefaultBlobConnection;

        if (string.IsNullOrWhiteSpace(cosmosEndpoint))
        {
            SkipReason = "Set DISTSEAR_TEST_COSMOS to run emulator-backed tests.";
            return;
        }

        // Each run gets its own database so repeated runs cannot see each other's state.
        var options = new AzureStorageOptions
        {
            CosmosEndpoint = cosmosEndpoint,
            CosmosKey = Environment.GetEnvironmentVariable("DISTSEAR_TEST_COSMOS_KEY") ?? DefaultCosmosKey,
            DatabaseName = "distsear-test-" + Guid.NewGuid().ToString("N")[..8],
            BlobEndpoint = blobConnection,
            SnapshotsContainer = "snapshots-" + Guid.NewGuid().ToString("N")[..8],
            LeasesContainer = "leases-" + Guid.NewGuid().ToString("N")[..8],
            AllowUntrustedCertificate = true,
            LeaseDuration = TimeSpan.FromSeconds(15),
            NodeTimeToLive = TimeSpan.FromSeconds(30)
        };

        Options = Microsoft.Extensions.Options.Options.Create(options);

        Cosmos = new CosmosClient(
            options.CosmosEndpoint,
            options.CosmosKey,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions(),
                ServerCertificateCustomValidationCallback = (_, _, _) => true
            });

        Blobs = new BlobServiceClient(options.BlobEndpoint);

        try
        {
            await ProvisionAsync(options);
            Available = true;
        }
        catch (Exception exception)
        {
            SkipReason = $"Emulator unreachable: {exception.Message}";
        }
    }

    private async Task ProvisionAsync(AzureStorageOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var database = await Cosmos.CreateDatabaseIfNotExistsAsync(
            options.DatabaseName,
            cancellationToken: timeout.Token);

        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(options.DocumentsContainer, "/shardKey") { DefaultTimeToLive = -1 },
            cancellationToken: timeout.Token);

        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(options.ClusterContainer, "/partition"),
            cancellationToken: timeout.Token);

        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(options.NodesContainer, "/nodeId") { DefaultTimeToLive = -1 },
            cancellationToken: timeout.Token);

        await Blobs.GetBlobContainerClient(options.SnapshotsContainer)
            .CreateIfNotExistsAsync(cancellationToken: timeout.Token);

        await Blobs.GetBlobContainerClient(options.LeasesContainer)
            .CreateIfNotExistsAsync(cancellationToken: timeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (!Available)
        {
            return;
        }

        try
        {
            await Cosmos.GetDatabase(Options.Value.DatabaseName).DeleteAsync();
            await Blobs.GetBlobContainerClient(Options.Value.SnapshotsContainer).DeleteIfExistsAsync();
            await Blobs.GetBlobContainerClient(Options.Value.LeasesContainer).DeleteIfExistsAsync();
        }
        catch
        {
            // Cleanup is best-effort; the fixture uses unique names so leftovers cannot collide.
        }

        Cosmos.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class AzureEmulatorCollection : ICollectionFixture<AzureEmulatorFixture>
{
    public const string Name = "azure-emulator";
}
