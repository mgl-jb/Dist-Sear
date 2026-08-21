using System.Collections.Frozen;

namespace DistSear.Abstractions.Mapping;

/// <summary>
/// The full schema of an index, plus its shard topology. Shard count is fixed at creation:
/// documents route by <c>hash(id) % NumberOfShards</c>, so changing it would invalidate all
/// existing routing. Changing shard count therefore requires a reindex into a new index and
/// an alias swap.
/// </summary>
public sealed class IndexMapping
{
    private readonly FrozenDictionary<string, FieldMapping> _fields;

    public IndexMapping(
        string name,
        IEnumerable<FieldMapping> fields,
        int numberOfShards = 1,
        int numberOfReplicas = 1,
        string? defaultField = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfShards, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfReplicas, 1);

        Name = name;
        NumberOfShards = numberOfShards;
        NumberOfReplicas = numberOfReplicas;
        _fields = fields.ToFrozenDictionary(f => f.Name, StringComparer.Ordinal);

        if (_fields.Count == 0)
        {
            throw new ArgumentException("An index mapping must declare at least one field.", nameof(fields));
        }

        DefaultField = defaultField
            ?? _fields.Values.FirstOrDefault(f => f.Type == FieldType.Text)?.Name
            ?? _fields.Keys.First();
    }

    public string Name { get; }

    public int NumberOfShards { get; }

    public int NumberOfReplicas { get; }

    /// <summary>Field targeted by bare query terms that carry no <c>field:</c> prefix.</summary>
    public string DefaultField { get; }

    public IReadOnlyCollection<FieldMapping> Fields => _fields.Values;

    public FieldMapping? Get(string field) => _fields.TryGetValue(field, out var m) ? m : null;

    public FieldMapping Require(string field) =>
        Get(field) ?? throw new KeyNotFoundException($"Field '{field}' is not mapped in index '{Name}'.");

    public bool Has(string field) => _fields.ContainsKey(field);
}
