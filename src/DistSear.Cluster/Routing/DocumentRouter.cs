using DistSear.Abstractions.Mapping;

namespace DistSear.Cluster.Routing;

/// <summary>
/// Decides which shard a document belongs to.
///
/// The shard count is fixed at index creation, so this is a pure function with no lookup table.
/// The computed shard key doubles as the Cosmos partition key, which is what allows an index node
/// to read the change feed for exactly the shards it owns and nothing else.
/// </summary>
public static class DocumentRouter
{
    public const string ShardKeyPrefix = "shard-";

    public static int ShardFor(string routingKey, int numberOfShards)
    {
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfShards, 1);

        return (int)(Murmur3.Hash32(routingKey) % (uint)numberOfShards);
    }

    /// <summary>
    /// Routes by an explicit routing key when one is supplied, otherwise by document id. Supplying
    /// a routing key deliberately co-locates related documents (all of a tenant's rows, say) so
    /// that queries scoped to that key can be answered by a single shard.
    /// </summary>
    public static int ShardFor(string id, string? routingKey, int numberOfShards) =>
        ShardFor(routingKey ?? id, numberOfShards);

    public static string ShardKey(int shardId) => ShardKeyPrefix + shardId.ToString();

    public static string ShardKeyFor(string id, string? routingKey, int numberOfShards) =>
        ShardKey(ShardFor(id, routingKey, numberOfShards));

    public static string ShardKeyFor(IndexMapping mapping, string id, string? routingKey = null) =>
        ShardKey(ShardFor(id, routingKey, mapping.NumberOfShards));

    /// <summary>Parses a shard key back to its ordinal. Returns false for anything malformed.</summary>
    public static bool TryParseShardKey(string shardKey, out int shardId)
    {
        shardId = -1;

        return shardKey.StartsWith(ShardKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(shardKey.AsSpan(ShardKeyPrefix.Length), out shardId)
            && shardId >= 0;
    }

    public static IEnumerable<string> AllShardKeys(int numberOfShards) =>
        Enumerable.Range(0, numberOfShards).Select(ShardKey);
}
