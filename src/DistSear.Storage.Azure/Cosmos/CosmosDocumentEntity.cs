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
        JsonValueKind.Number => ToNumber(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => element.ToString()
    };

    /// <summary>
    /// Chooses a CLR type for a JSON number.
    ///
    /// Cosmos has a single numeric type, an IEEE-754 double, so a value written as the integer 7
    /// can come back as 7.0 — the distinction simply does not survive storage. Rather than let that
    /// leak out as an inconsistency, whole numbers within long range are normalised back to long.
    ///
    /// The convention is safe because the index coerces every value by its declared field type
    /// anyway: a field mapped as <c>Double</c> is read as a double whatever this returns. What it
    /// buys is that identifiers, counts and years behave as integers when handed back to callers in
    /// stored fields, instead of arriving as 2021.0.
    /// </summary>
    private static object ToNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            return integer;
        }

        var value = element.GetDouble();

        return double.IsInteger(value) && value is >= long.MinValue and <= long.MaxValue
            ? (long)value
            : value;
    }
}
