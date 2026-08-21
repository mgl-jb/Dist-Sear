using System.Text.Json;
using System.Text.Json.Serialization;
using DistSear.Abstractions.Documents;

namespace DistSear.Storage.Azure.Cosmos;

/// <summary>
/// A document as Cosmos stores it. Kept separate from <see cref="IndexedDocument"/> so the storage
/// shape (partition key naming, TTL, system properties) can change without touching the index.
/// </summary>
internal sealed class CosmosDocumentEntity
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key. Also the shard the document belongs to.</summary>
    [JsonPropertyName("shardKey")]
    public string ShardKey { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public Dictionary<string, JsonElement> Fields { get; set; } = [];

    [JsonPropertyName("acl")]
    public List<string> Acl { get; set; } = [];

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    /// <summary>
    /// Cosmos time-to-live in seconds. Only set on tombstones: a deleted document has to linger
    /// long enough for every replica to observe the deletion on the change feed.
    /// </summary>
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLive { get; set; }

    /// <summary>Server-assigned timestamp, used to order concurrent updates of the same document.</summary>
    [JsonPropertyName("_ts")]
    public long Timestamp { get; set; }

    public static CosmosDocumentEntity From(IndexedDocument document, TimeSpan tombstoneRetention) => new()
    {
        Id = document.Id,
        ShardKey = document.ShardKey,
        Fields = document.Fields.ToDictionary(
            kv => kv.Key,
            kv => JsonSerializer.SerializeToElement(kv.Value)),
        Acl = [.. document.Acl],
        Deleted = document.Deleted,
        TimeToLive = document.Deleted ? (int)tombstoneRetention.TotalSeconds : null
    };

    public IndexedDocument ToDocument() => new()
    {
        Id = Id,
        ShardKey = ShardKey,
        Fields = Fields.ToDictionary(kv => kv.Key, kv => JsonValueConverter.ToClr(kv.Value)),
        Acl = Acl,
        Deleted = Deleted,
        Version = Timestamp
    };
}

/// <summary>
/// Converts JSON back into the plain CLR values the index expects.
///
/// The index works with loosely-typed scalars and coerces them per the field mapping, so the job
/// here is only to undo JSON's own typing — not to guess what a field means.
/// </summary>
internal static class JsonValueConverter
{
    public static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,

        // Integers are kept as longs so that identifiers and counts do not pick up floating-point
        // representation error on the way through.
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),

        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => element.ToString()
    };
}
