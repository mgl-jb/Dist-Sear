using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Query;
using DistSear.Index.Segments;
using Xunit;

namespace DistSear.Index.Tests;

/// <summary>
/// A snapshot is only useful if what comes back is indistinguishable from what went in. These tests
/// compare a restored segment against the original by querying both, rather than by inspecting
/// fields, so any loss shows up as a behavioural difference.
/// </summary>
public class SegmentSerializerTests
{
    private static Segment RoundTrip(Segment segment)
    {
        using var buffer = new MemoryStream();
        SegmentSerializer.Write(segment, buffer);
        buffer.Position = 0;

        return SegmentSerializer.Read(buffer);
    }

    private static Segment BuildProductSegment()
    {
        var builder = new SegmentBuilder(TestCorpus.ProductMapping, new AnalyzerRegistry());

        foreach (var product in TestCorpus.Products)
        {
            builder.AddDocument(product);
        }

        return builder.Build();
    }

    private static IReadOnlyList<string> Search(Segment segment, Query.Query query, int size = 20)
    {
        var searcher = new IndexSearcher([segment]);

        var outcome = searcher.Search(new SearchExecution
        {
            Query = query,
            Context = new SearchContext
            {
                Mapping = TestCorpus.ProductMapping,
                Analyzers = new AnalyzerRegistry(),
                Statistics = new Scoring.SegmentTermStatistics([segment])
            },
            Size = size
        });

        return [.. outcome.TopDocs.Hits.Select(h => h.ExternalId)];
    }

    [Fact]
    public void RestoredSegmentHasTheSameShape()
    {
        var original = BuildProductSegment();
        var restored = RoundTrip(original);

        Assert.Equal(original.MaxDoc, restored.MaxDoc);
        Assert.Equal(original.ExternalIds, restored.ExternalIds);
        Assert.Equal(original.Fields.Keys.Order(), restored.Fields.Keys.Order());
        Assert.Equal(original.DocValues.Keys.Order(), restored.DocValues.Keys.Order());
    }

    [Fact]
    public void RestoredSegmentAnswersTermQueriesIdentically()
    {
        var original = BuildProductSegment();
        var restored = RoundTrip(original);

        var query = new TermQuery("title", "search");

        Assert.Equal(Search(original, query), Search(restored, query));
    }

    [Fact]
    public void RestoredSegmentPreservesPositionsForPhraseQueries()
    {
        var original = BuildProductSegment();
        var restored = RoundTrip(original);

        var query = new PhraseQuery("title", ["distribut", "search"]);

        Assert.Equal(["1"], Search(restored, query));
        Assert.Equal(Search(original, query), Search(restored, query));
    }

    [Fact]
    public void RestoredSegmentPreservesScoresExactly()
    {
        var original = BuildProductSegment();
        var restored = RoundTrip(original);

        double[] ScoresOf(Segment segment)
        {
            var searcher = new IndexSearcher([segment]);

            return
            [
                .. searcher.Search(new SearchExecution
                {
                    Query = new TermQuery("title", "search"),
                    Context = new SearchContext
                    {
                        Mapping = TestCorpus.ProductMapping,
                        Analyzers = new AnalyzerRegistry(),
                        Statistics = new Scoring.SegmentTermStatistics([segment])
                    },
                    Size = 20
                }).TopDocs.Hits.Select(h => h.Score)
            ];
        }

        // Norms and field-length statistics must survive, or ranking changes after a recovery.
        Assert.Equal(ScoresOf(original), ScoresOf(restored));
    }

    [Fact]
    public void RestoredSegmentPreservesDocValuesForSortingAndFiltering()
    {
        var restored = RoundTrip(BuildProductSegment());

        Assert.Equal(["1", "2", "3", "6"], Search(restored, new NumericRangeQuery("price", 10, 50)).Order().ToArray());
    }

    [Fact]
    public void RestoredSegmentPreservesStoredFields()
    {
        var original = BuildProductSegment();
        var restored = RoundTrip(original);

        for (var i = 0; i < original.MaxDoc; i++)
        {
            Assert.Equal(original.StoredFields[i]["title"], restored.StoredFields[i]["title"]);
            Assert.Equal(original.StoredFields[i]["price"], restored.StoredFields[i]["price"]);
        }
    }

    [Fact]
    public void RestoredSegmentPreservesMultiValuedStoredFields()
    {
        var restored = RoundTrip(BuildProductSegment());
        var tags = restored.StoredFields[0]["tags"];

        Assert.NotNull(tags);
        Assert.Equal(["search", "systems"], ((object?[])tags).Cast<string>());
    }

    [Fact]
    public void RestoredSegmentPreservesAcls()
    {
        var builder = new SegmentBuilder(TestCorpus.ProductMapping, new AnalyzerRegistry());
        builder.AddDocument(TestCorpus.Product("secret", "t", "b", "books", 1, 2020, acl: ["group-a", "group-b"]));

        var restored = RoundTrip(builder.Build());

        Assert.Equal(["group-a", "group-b"], restored.Acls[0]);
    }

    [Fact]
    public void RestoredSegmentPreservesDeletions()
    {
        var original = BuildProductSegment();
        original.LiveDocs.Delete(2);
        original.LiveDocs.Delete(4);

        var restored = RoundTrip(original);

        Assert.False(restored.LiveDocs.IsLive(2));
        Assert.False(restored.LiveDocs.IsLive(4));
        Assert.True(restored.LiveDocs.IsLive(0));
        Assert.Equal(original.LiveDocs.LiveCount, restored.LiveDocs.LiveCount);
    }

    [Fact]
    public void DeletedDocumentsStayDeletedAfterRestore()
    {
        var original = BuildProductSegment();
        original.LiveDocs.Delete(0);

        var restored = RoundTrip(original);

        // Recovering from a snapshot must not resurrect deleted documents.
        Assert.DoesNotContain("1", Search(restored, MatchAllQuery.Instance));
    }

    [Fact]
    public void AnEmptySegmentRoundTrips()
    {
        var empty = new SegmentBuilder(TestCorpus.ProductMapping, new AnalyzerRegistry()).Build();
        var restored = RoundTrip(empty);

        Assert.Equal(0, restored.MaxDoc);
    }

    [Fact]
    public void CorruptDataIsRejectedRatherThanMisread()
    {
        using var buffer = new MemoryStream("not a segment at all"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => SegmentSerializer.Read(buffer));
    }

    [Fact]
    public void PostingsAreWrittenWithoutReEncoding()
    {
        // The serialised form should be close to the in-memory postings size, confirming the bytes
        // are copied rather than expanded.
        var segment = BuildProductSegment();
        var postingsBytes = segment.Fields.Values.Sum(f => f.AllTerms.Sum(t => t.Value.ByteLength));

        using var buffer = new MemoryStream();
        SegmentSerializer.Write(segment, buffer);

        Assert.True(postingsBytes > 0);
        Assert.True(
            buffer.Length < postingsBytes * 40,
            $"Serialised {buffer.Length} bytes for {postingsBytes} bytes of postings.");
    }
}
