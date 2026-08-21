using System.Collections.Concurrent;

namespace DistSear.Cluster.Allocation;

/// <summary>
/// Chooses which replica of a shard should serve a query.
///
/// Round-robin sends an equal share of traffic to a node that has become slow — a long garbage
/// collection, a noisy neighbour, a cold cache — and because a fan-out query is only as fast as its
/// slowest shard, one degraded replica sets the tail latency for the whole cluster. Ranking by
/// observed latency and current load routes around such a node automatically, and lets it back in
/// as soon as it recovers.
/// </summary>
public sealed class AdaptiveReplicaSelector
{
    /// <summary>
    /// Weight of each new observation. Low enough to ignore a single slow request, high enough to
    /// react within a few seconds of sustained degradation.
    /// </summary>
    private const double SmoothingFactor = 0.3;

    /// <summary>
    /// Latency assumed for a node with no history. Optimistic on purpose, so a newly-joined node
    /// receives traffic and gets the chance to prove itself rather than being starved.
    /// </summary>
    private const double UnknownLatencyMilliseconds = 1.0;

    private readonly ConcurrentDictionary<string, NodeStats> _stats = new(StringComparer.Ordinal);

    /// <summary>Picks the most promising node, or null when no candidate is available.</summary>
    public string? Select(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        string? best = null;
        var bestRank = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var rank = Rank(candidate);

            if (rank < bestRank)
            {
                bestRank = rank;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Expected cost of sending one more request to a node: its typical latency multiplied by the
    /// queue it would join. Queue depth matters as much as latency, because a fast node with
    /// requests already in flight will still make this one wait.
    /// </summary>
    public double Rank(string nodeId)
    {
        if (!_stats.TryGetValue(nodeId, out var stats))
        {
            return UnknownLatencyMilliseconds;
        }

        return stats.AverageLatencyMilliseconds * (1 + stats.Outstanding);
    }

    /// <summary>Call when a request is dispatched, and dispose the result when it completes.</summary>
    public IDisposable BeginRequest(string nodeId)
    {
        var stats = _stats.GetOrAdd(nodeId, _ => new NodeStats());
        return new RequestScope(stats);
    }

    /// <summary>Records an observation directly, for callers that time requests themselves.</summary>
    public void Record(string nodeId, TimeSpan elapsed)
    {
        var stats = _stats.GetOrAdd(nodeId, _ => new NodeStats());
        stats.Observe(elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Penalises a node that failed, so the next query prefers a different replica without waiting
    /// for a slow timeout to be observed as latency.
    /// </summary>
    public void RecordFailure(string nodeId)
    {
        var stats = _stats.GetOrAdd(nodeId, _ => new NodeStats());
        stats.Observe(stats.AverageLatencyMilliseconds * 4 + 100);
    }

    public void Forget(string nodeId) => _stats.TryRemove(nodeId, out _);

    private sealed class NodeStats
    {
        private readonly Lock _gate = new();
        private double _average = UnknownLatencyMilliseconds;
        private int _outstanding;

        public double AverageLatencyMilliseconds
        {
            get
            {
                lock (_gate)
                {
                    return _average;
                }
            }
        }

        public int Outstanding => Volatile.Read(ref _outstanding);

        public void Observe(double milliseconds)
        {
            lock (_gate)
            {
                _average = (SmoothingFactor * milliseconds) + ((1 - SmoothingFactor) * _average);
            }
        }

        public void Enter() => Interlocked.Increment(ref _outstanding);

        public void Exit() => Interlocked.Decrement(ref _outstanding);
    }

    private sealed class RequestScope : IDisposable
    {
        private readonly NodeStats _stats;
        private readonly long _startedAt;
        private bool _disposed;

        public RequestScope(NodeStats stats)
        {
            _stats = stats;
            _startedAt = TimeProvider.System.GetTimestamp();
            stats.Enter();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stats.Exit();
            _stats.Observe(TimeProvider.System.GetElapsedTime(_startedAt).TotalMilliseconds);
        }
    }
}
