using System.Collections.Concurrent;
using DistSear.Abstractions.Storage;

namespace DistSear.Cluster.InMemory;

/// <summary>
/// In-process leader elector with the same semantics as the Blob-lease implementation: exclusive
/// acquisition, expiry if the holder stops renewing, and a cancellation signal when leadership is
/// lost. Tests can therefore cover the handover path without Azure Storage.
/// </summary>
public sealed class InMemoryLeaderElector : ILeaderElector
{
    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Task<ILeaderLease?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();

        while (true)
        {
            if (_leases.TryGetValue(name, out var existing))
            {
                if (existing.ExpiresAt > now && !existing.Released)
                {
                    return Task.FromResult<ILeaderLease?>(null);
                }

                // Expired or released: take it over, but only if nobody else got there first.
                var replacement = new Lease(this, name, now + LeaseDuration);

                if (_leases.TryUpdate(name, replacement, existing))
                {
                    return Task.FromResult<ILeaderLease?>(replacement);
                }

                continue;
            }

            var fresh = new Lease(this, name, now + LeaseDuration);

            if (_leases.TryAdd(name, fresh))
            {
                return Task.FromResult<ILeaderLease?>(fresh);
            }
        }
    }

    /// <summary>Expires a lease immediately, simulating a leader that stopped renewing.</summary>
    public void Expire(string name)
    {
        if (_leases.TryGetValue(name, out var lease))
        {
            lease.Expire();
        }
    }

    internal void Release(string name, Lease lease) => _leases.TryRemove(new KeyValuePair<string, Lease>(name, lease));

    internal sealed class Lease : ILeaderLease
    {
        private readonly InMemoryLeaderElector _owner;
        private readonly string _name;
        private readonly CancellationTokenSource _lost = new();

        public Lease(InMemoryLeaderElector owner, string name, DateTimeOffset expiresAt)
        {
            _owner = owner;
            _name = name;
            ExpiresAt = expiresAt;
        }

        public DateTimeOffset ExpiresAt { get; private set; }

        public bool Released { get; private set; }

        public CancellationToken Lost => _lost.Token;

        public void Renew() => ExpiresAt = _owner.TimeProvider.GetUtcNow() + _owner.LeaseDuration;

        public void Expire()
        {
            ExpiresAt = _owner.TimeProvider.GetUtcNow() - TimeSpan.FromSeconds(1);
            _lost.Cancel();
        }

        public ValueTask DisposeAsync()
        {
            if (Released)
            {
                return ValueTask.CompletedTask;
            }

            Released = true;
            _owner.Release(_name, this);

            if (!_lost.IsCancellationRequested)
            {
                _lost.Cancel();
            }

            _lost.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
