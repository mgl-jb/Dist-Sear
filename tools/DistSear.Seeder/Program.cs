using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Client;

// Creates the sample index and loads a corpus into it, so a freshly started cluster has something
// to search. Safe to re-run: indexing is an upsert keyed by document id.

var coordinator = args.FirstOrDefault(a => a.StartsWith("--url=", StringComparison.Ordinal))?["--url=".Length..]
    ?? Environment.GetEnvironmentVariable("DISTSEAR_URL")
    ?? "http://localhost:8080";

var indexName = args.FirstOrDefault(a => a.StartsWith("--index=", StringComparison.Ordinal))?["--index=".Length..]
    ?? "catalog";

var count = int.TryParse(
    args.FirstOrDefault(a => a.StartsWith("--count=", StringComparison.Ordinal))?["--count=".Length..],
    out var parsed)
    ? parsed
    : 500;

Console.WriteLine($"Seeding '{indexName}' with {count} documents via {coordinator}.");

using var http = new HttpClient { BaseAddress = new Uri(coordinator), Timeout = TimeSpan.FromMinutes(2) };
var client = new SearchClient(http);

if (Environment.GetEnvironmentVariable("DISTSEAR_API_KEY") is { Length: > 0 } apiKey)
{
    client.UseApiKey(apiKey);
}

await WaitForCoordinatorAsync(http);

var definition = new IndexDefinition(
    [
        // Boosted: a match in the title says more about relevance than one in the body.
        new FieldMapping { Name = "title", Type = FieldType.Text, Analyzer = "english", Boost = 3.0 },
        new FieldMapping { Name = "body", Type = FieldType.Text, Analyzer = "english" },

        // Suggest fields are indexed through the edge-n-gram chain but searched with a plain
        // analyzer, or the query prefix would itself be expanded into prefixes.
        new FieldMapping
        {
            Name = "name",
            Type = FieldType.Text,
            Analyzer = "suggest",
            SearchAnalyzer = "simple",
            Suggest = true
        },

        // Doc values are what make these sortable and facetable.
        new FieldMapping { Name = "category", Type = FieldType.Keyword, DocValues = true },
        new FieldMapping { Name = "tags", Type = FieldType.Keyword, DocValues = true },
        new FieldMapping { Name = "price", Type = FieldType.Double, DocValues = true },
        new FieldMapping { Name = "year", Type = FieldType.Long, DocValues = true }
    ],
    NumberOfShards: 4,
    NumberOfReplicas: 2,
    DefaultField: "title");

try
{
    await client.CreateIndexAsync(indexName, definition);
    Console.WriteLine($"Created index '{indexName}'.");
}
catch (HttpRequestException)
{
    // Already present from an earlier run, which is fine.
    Console.WriteLine($"Index '{indexName}' already exists.");
}

var documents = Corpus.Generate(count).ToList();
var indexed = 0;

// Batched rather than sent as one request: a bulk body of unbounded size is a memory risk on both
// ends, and smaller batches surface errors sooner.
foreach (var batch in documents.Chunk(200))
{
    var result = await client.IndexAsync(indexName, batch);
    indexed += result.Indexed;

    foreach (var error in result.Errors)
    {
        Console.Error.WriteLine($"  rejected: {error}");
    }
}

Console.WriteLine($"Indexed {indexed} documents.");

// Indexing is asynchronous by design: writes land in Cosmos and nodes pick them up from the change
// feed, so a moment passes before they are searchable.
Console.WriteLine("Waiting for the cluster to catch up...");

// Every tenth document is ACL-restricted and this seeder authenticates as nobody, so the expected
// visible count is lower than the indexed count. Comparing against the wrong number would look like
// a replication lag that is not there.
var restricted = documents.Count(d => d.Acl.Count > 0);
var expectedVisible = indexed - restricted;

for (var attempt = 0; attempt < 30; attempt++)
{
    await Task.Delay(TimeSpan.FromSeconds(1));

    var response = await client.SearchAsync(indexName, new SearchRequest { Size = 0 });

    if (response.TotalHits >= expectedVisible)
    {
        Console.WriteLine(
            $"Searchable: {response.TotalHits} documents across {response.Shards.Total} shards "
            + $"({restricted} more are ACL-restricted and hidden from an anonymous caller).");

        break;
    }

    if (attempt == 29)
    {
        Console.WriteLine($"Only {response.TotalHits} of an expected {expectedVisible} documents visible.");
    }
}

var sample = await client.SearchAsync(indexName, "distributed search", size: 3);

Console.WriteLine();
Console.WriteLine($"Sample query 'distributed search' returned {sample.TotalHits} hits:");

foreach (var hit in sample.Hits)
{
    Console.WriteLine($"  {hit.Score:F3}  {hit.Fields.GetValueOrDefault("title")}");
}

static async Task WaitForCoordinatorAsync(HttpClient http)
{
    for (var attempt = 0; attempt < 60; attempt++)
    {
        try
        {
            using var response = await http.GetAsync("/health/live");

            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Still starting.
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    throw new InvalidOperationException("The coordinator did not become reachable.");
}

/// <summary>
/// Generates a deterministic corpus. Fixed seed on purpose: a demo whose results change between
/// runs is impossible to write documentation against.
/// </summary>
internal static class Corpus
{
    private static readonly string[] Topics =
    [
        "distributed search", "inverted index", "relevance ranking", "shard allocation",
        "change feed", "query planning", "text analysis", "fault tolerance",
        "coffee grinder", "espresso machine", "pour over kettle", "burr mill"
    ];

    private static readonly string[] Adjectives =
    [
        "fast", "resilient", "compact", "scalable", "precise", "durable", "elegant", "practical"
    ];

    private static readonly string[] Categories = ["books", "kitchen", "outdoors", "software"];

    private static readonly string[] Tags =
    [
        "search", "systems", "coffee", "reference", "beginner", "advanced"
    ];

    public static IEnumerable<SearchDocument> Generate(int count)
    {
        var random = new Random(20260821);

        for (var i = 0; i < count; i++)
        {
            var topic = Topics[random.Next(Topics.Length)];
            var adjective = Adjectives[random.Next(Adjectives.Length)];
            var title = $"{char.ToUpperInvariant(adjective[0])}{adjective[1..]} {topic}";

            yield return new SearchDocument
            {
                Id = $"doc-{i:D5}",
                Fields = new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["body"] = $"A {adjective} guide to {topic}, covering {Topics[random.Next(Topics.Length)]} "
                        + $"and {Topics[random.Next(Topics.Length)]} in practice.",
                    ["name"] = title,
                    ["category"] = Categories[random.Next(Categories.Length)],
                    ["tags"] = new[] { Tags[random.Next(Tags.Length)], Tags[random.Next(Tags.Length)] }.Distinct().ToArray(),
                    ["price"] = Math.Round(5 + (random.NextDouble() * 495), 2),
                    ["year"] = 2015L + random.Next(11)
                },

                // Every tenth document is restricted, so document-level security is demonstrable
                // rather than merely implemented.
                Acl = i % 10 == 0 ? ["restricted"] : []
            };
        }
    }
}
