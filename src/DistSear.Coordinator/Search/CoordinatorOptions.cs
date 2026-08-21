namespace DistSear.Coordinator.Search;

public sealed class CoordinatorOptions
{
    public const string SectionName = "DistSear:Coordinator";

    /// <summary>
    /// Deadline for a single shard. A fan-out is only as fast as its slowest shard, so a bound here
    /// is what stops one degraded replica from holding up every query.
    /// </summary>
    public TimeSpan ShardTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Replicas attempted per shard before giving up. One retry covers the common case of a node
    /// that has just been reallocated or restarted, without multiplying load during an outage.
    /// </summary>
    public int MaxReplicaAttempts { get; set; } = 2;

    /// <summary>Largest page a caller may request, bounding coordinator memory during a merge.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>Cache lifetime for identical queries. Short, because the index moves underneath it.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(10);

    public bool CacheEnabled { get; set; } = true;
}
