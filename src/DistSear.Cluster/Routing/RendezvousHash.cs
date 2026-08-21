using System.IO.Hashing;
using System.Text;

namespace DistSear.Cluster.Routing;

/// <summary>
/// Rendezvous (highest random weight) hashing, used to place shards on nodes.
///
/// Every (shard, node) pair is scored, and the shard goes to the highest-scoring nodes. Chosen over
/// a consistent-hash ring because it needs no ring state or virtual nodes, is a pure function of
/// the live node set, and moves only the shards belonging to a departed node — nothing else is
/// disturbed. Two coordinators computing placement independently reach the same answer, which
/// matters during a leadership handover.
/// </summary>
public static class RendezvousHash
{
    /// <summary>
    /// Orders nodes by their weight for a shard, strongest first. Ties break on node id so the
    /// ordering is total and identical everywhere.
    /// </summary>
    public static IReadOnlyList<string> Rank(string shardKey, IReadOnlyCollection<string> nodeIds)
    {
        return
        [
            .. nodeIds
                .Select(nodeId => (NodeId: nodeId, Weight: Weight(shardKey, nodeId)))
                .OrderByDescending(x => x.Weight)
                .ThenBy(x => x.NodeId, StringComparer.Ordinal)
                .Select(x => x.NodeId)
        ];
    }

    /// <summary>Picks the top <paramref name="count"/> nodes, or every node when there are fewer.</summary>
    public static IReadOnlyList<string> Select(
        string shardKey,
        IReadOnlyCollection<string> nodeIds,
        int count)
    {
        if (nodeIds.Count == 0 || count <= 0)
        {
            return [];
        }

        return [.. Rank(shardKey, nodeIds).Take(Math.Min(count, nodeIds.Count))];
    }

    internal static ulong Weight(string shardKey, string nodeId)
    {
        // Both parts are length-delimited so that ("ab", "c") and ("a", "bc") cannot collide.
        var text = $"{shardKey.Length}:{shardKey}:{nodeId}";
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));
    }
}
