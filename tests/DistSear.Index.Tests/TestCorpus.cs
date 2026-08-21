using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Analysis;
using DistSear.Index;

namespace DistSear.Index.Tests;

/// <summary>Shared fixtures for index-level tests.</summary>
internal static class TestCorpus
{
    public static IndexMapping ProductMapping { get; } = new(
        "products",
        [
            new FieldMapping { Name = "title", Type = FieldType.Text, Analyzer = AnalyzerRegistry.English, Boost = 2.0 },
            new FieldMapping { Name = "body", Type = FieldType.Text, Analyzer = AnalyzerRegistry.English },
            new FieldMapping { Name = "category", Type = FieldType.Keyword, DocValues = true },
            new FieldMapping { Name = "tags", Type = FieldType.Keyword, DocValues = true },
            new FieldMapping { Name = "price", Type = FieldType.Double, DocValues = true },
            new FieldMapping { Name = "year", Type = FieldType.Long, DocValues = true }
        ],
        numberOfShards: 1,
        defaultField: "title");

    public static IndexedDocument Product(
        string id,
        string title,
        string body,
        string category,
        double price,
        long year,
        string[]? tags = null,
        string[]? acl = null) =>
        new()
        {
            Id = id,
            ShardKey = "shard-0",
            Acl = acl ?? [],
            Fields = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["body"] = body,
                ["category"] = category,
                ["tags"] = tags ?? Array.Empty<string>(),
                ["price"] = price,
                ["year"] = year
            }
        };

    /// <summary>A small, hand-checkable catalogue.</summary>
    public static IReadOnlyList<IndexedDocument> Products { get; } =
    [
        Product("1", "Fast distributed search", "A guide to searching large corpora quickly", "books", 29.99, 2021, ["search", "systems"]),
        Product("2", "Distributed systems design", "Designing systems that scale across machines", "books", 49.50, 2019, ["systems"]),
        Product("3", "Search engine internals", "How a search engine stores and ranks documents", "books", 39.00, 2023, ["search"]),
        Product("4", "Coffee grinder", "A burr grinder for consistent coffee", "kitchen", 89.00, 2022, ["coffee"]),
        Product("5", "Espresso machine", "Makes espresso and steams milk", "kitchen", 499.00, 2023, ["coffee"]),
        Product("6", "Search lighting", "Bright torch for searching dark places", "outdoors", 19.99, 2020, ["light"])
    ];

    public static ShardIndex BuildProductIndex()
    {
        var index = new ShardIndex(ProductMapping, new AnalyzerRegistry());

        foreach (var product in Products)
        {
            index.AddOrUpdate(product);
        }

        index.Refresh();
        return index;
    }

    /// <summary>Minimal mapping using the non-stemming analyzer, for hand-computed scoring checks.</summary>
    public static IndexMapping SimpleMapping { get; } = new(
        "simple",
        [new FieldMapping { Name = "body", Type = FieldType.Text, Analyzer = AnalyzerRegistry.Simple }],
        defaultField: "body");

    public static IndexedDocument Simple(string id, string body) => new()
    {
        Id = id,
        ShardKey = "shard-0",
        Fields = new Dictionary<string, object?> { ["body"] = body }
    };
}
