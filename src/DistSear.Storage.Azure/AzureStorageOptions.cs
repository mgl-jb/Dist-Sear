namespace DistSear.Storage.Azure;

public sealed class AzureStorageOptions
{
    public const string SectionName = "DistSear:Azure";

    /// <summary>Cosmos account endpoint. Authentication is by managed identity, so no key is needed.</summary>
    public string CosmosEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Emulator key. Set only for local development; production authenticates with
    /// <c>DefaultAzureCredential</c> and this stays empty.
    /// </summary>
    public string? CosmosKey { get; set; }

    public string DatabaseName { get; set; } = "distsear";

    public string DocumentsContainer { get; set; } = "documents";

    public string ClusterContainer { get; set; } = "cluster";

    public string NodesContainer { get; set; } = "nodes";

    /// <summary>Blob service endpoint holding segment snapshots, checkpoints and the lease blob.</summary>
    public string BlobEndpoint { get; set; } = string.Empty;

    public string SnapshotsContainer { get; set; } = "snapshots";

    public string LeasesContainer { get; set; } = "leases";

    /// <summary>
    /// How long a deleted document's tombstone is retained before Cosmos reclaims it. Must comfortably
    /// exceed the longest plausible gap between a replica's snapshots, or a replica recovering from
    /// an old snapshot could miss the deletion entirely and resurrect the document.
    /// </summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Documents requested per change-feed page.</summary>
    public int ChangeFeedPageSize { get; set; } = 100;

    /// <summary>
    /// Blob lease duration. Azure permits 15 to 60 seconds for a fixed lease; the holder renews at
    /// roughly a third of this, so a brief hiccup does not cost leadership.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Heartbeat TTL for node registration documents.</summary>
    public TimeSpan NodeTimeToLive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Trusts the emulator's self-signed certificate. Development only.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    public bool UsesEmulator => !string.IsNullOrEmpty(CosmosKey);
}
