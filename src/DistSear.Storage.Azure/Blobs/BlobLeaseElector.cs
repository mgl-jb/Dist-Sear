using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using DistSear.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DistSear.Storage.Azure.Blobs;

/// <summary>
/// Leader election over an Azure Blob lease.
///
/// A blob lease is an exclusive lock that Azure itself expires if the holder stops renewing, so a
/// coordinator that crashes cannot block the election indefinitely — which is exactly the failure a
/// hand-rolled lock table struggles with. The holder renews on a timer well inside the lease
/// duration, and signals <see cref="ILeaderLease.Lost"/> the moment a renewal fails, so a deposed
/// leader stops acting as one rather than racing its successor.
///
/// The lease blob is used for nothing else, as the pattern requires: anything written to it would be
/// inaccessible to instances that do not hold the lease.
/// </summary>
public sealed class BlobLeaseElector : ILeaderElector
{
    private readonly BlobContainerClient _container;
    private readonly AzureStorageOptions _options;
    private readonly ILogger<BlobLeaseElector>? _logger;

    public BlobLeaseElector(
        BlobServiceClient client,
        IOptions<AzureStorageOptions> options,
        ILogger<BlobLeaseElector>? logger = null)
    {
        _options = options.Value;
        _container = client.GetBlobContainerClient(_options.LeasesContainer);
        _logger = logger;
    }

    public async Task<ILeaderLease?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(name + ".lease");

        await EnsureBlobExistsAsync(blob, cancellationToken);

        var leaseClient = blob.GetBlobLeaseClient();

        try
        {
            var lease = await leaseClient.AcquireAsync(_options.LeaseDuration, cancellationToken: cancellationToken);

            _logger?.LogDebug("Acquired leadership lease '{Name}'.", name);

            return new BlobLease(leaseClient, _options.LeaseDuration, lease.Value.LeaseId, _logger);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // 409 Conflict means somebody else holds it. Not an error: exactly one instance leads.
            return null;
        }
    }

    private static async Task EnsureBlobExistsAsync(BlobClient blob, CancellationToken cancellationToken)
    {
        try
        {
            // A lease needs something to lease. The content is irrelevant and never read.
            await blob.UploadAsync(
                new BinaryData(ReadOnlyMemory<byte>.Empty),
                overwrite: false,
                cancellationToken);
        }
        catch (RequestFailedException exception) when (
            exception.Status == 409 || exception.ErrorCode == "BlobAlreadyExists")
        {
            // Already there, possibly leased by the current holder. Nothing to do.
        }
    }

    /// <summary>Creates the lease container if it is missing. Safe to call on every start.</summary>
    public Task EnsureCreatedAsync(CancellationToken cancellationToken) =>
        _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

    private sealed class BlobLease : ILeaderLease
    {
        private readonly BlobLeaseClient _client;
        private readonly CancellationTokenSource _lost = new();
        private readonly Timer _renewal;
        private readonly ILogger? _logger;
        private int _disposed;

        /// <summary>
        /// Captured once at construction. Reading CancellationTokenSource.Token after the source is
        /// disposed throws, and callers legitimately check this token after releasing the lease to
        /// confirm they are no longer leader.
        /// </summary>
        private readonly CancellationToken _lostToken;

        public BlobLease(
            BlobLeaseClient client,
            TimeSpan duration,
            string leaseId,
            ILogger? logger)
        {
            _client = client;
            _logger = logger;
            LeaseId = leaseId;
            _lostToken = _lost.Token;

            // Renew at a third of the duration: two consecutive renewals can fail transiently and
            // leadership still survives.
            var interval = duration / 3;
            _renewal = new Timer(_ => RenewAsync(), null, interval, interval);
        }

        public string LeaseId { get; }

        public CancellationToken Lost => _lostToken;

        private async void RenewAsync()
        {
            try
            {
                await _client.RenewAsync();
            }
            catch (Exception exception)
            {
                // Losing the lease is the signal to stop coordinating immediately; continuing would
                // mean two instances believing they lead.
                _logger?.LogWarning(exception, "Lost leadership lease {LeaseId}.", LeaseId);
                Signal();
            }
        }

        private void Signal()
        {
            if (!_lost.IsCancellationRequested)
            {
                _lost.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _renewal.DisposeAsync();

            try
            {
                // Releasing explicitly lets a successor take over at once instead of waiting out
                // the remaining lease duration.
                await _client.ReleaseAsync();
            }
            catch (RequestFailedException exception)
            {
                _logger?.LogDebug(exception, "Lease {LeaseId} was already gone on release.", LeaseId);
            }

            Signal();
            _lost.Dispose();
        }
    }
}
