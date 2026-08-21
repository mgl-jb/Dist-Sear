using DistSear.Index.Postings;
using Xunit;

namespace DistSear.Index.Tests;

public class VByteTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(16_383u)]
    [InlineData(16_384u)]
    [InlineData(uint.MaxValue)]
    public void RoundTripsExactly(uint value)
    {
        Span<byte> buffer = stackalloc byte[VByte.MaxBytes32];
        var written = VByte.Write(buffer, value);

        var offset = 0;
        var read = VByte.Read(buffer[..written], ref offset);

        Assert.Equal(value, read);
        Assert.Equal(written, offset);
        Assert.Equal(written, VByte.SizeOf(value));
    }

    [Fact]
    public void SmallValuesCostASingleByte()
    {
        // This is the property that makes delta-gap postings compact.
        for (uint value = 0; value < 128; value++)
        {
            Assert.Equal(1, VByte.SizeOf(value));
        }

        Assert.Equal(2, VByte.SizeOf(128));
    }

    [Fact]
    public void ConsecutiveValuesDecodeInSequence()
    {
        var values = new uint[] { 3, 300, 1, 70_000, 0, 42 };
        Span<byte> buffer = stackalloc byte[values.Length * VByte.MaxBytes32];

        var write = 0;
        foreach (var value in values)
        {
            write += VByte.Write(buffer[write..], value);
        }

        var read = 0;
        foreach (var expected in values)
        {
            Assert.Equal(expected, VByte.Read(buffer[..write], ref read));
        }

        Assert.Equal(write, read);
    }
}

public class PostingsListTests
{
    private static PostingsList Build(
        IEnumerable<(int DocId, int[] Positions)> postings,
        bool withPositions = true)
    {
        var builder = new PostingsListBuilder(withPositions);

        foreach (var (docId, positions) in postings)
        {
            builder.Add(docId, positions.Length, positions);
        }

        return builder.Build();
    }

    [Fact]
    public void RoundTripsDocumentsFrequenciesAndPositions()
    {
        var list = Build([(0, [1, 4, 9]), (5, [0]), (17, [2, 3])]);

        Assert.Equal(3, list.DocumentFrequency);
        Assert.Equal(6, list.TotalTermFrequency);

        var cursor = list.GetEnumerator();
        var positions = new List<int>();

        Assert.Equal(0, cursor.NextDoc());
        Assert.Equal(3, cursor.TermFrequency);
        cursor.ReadPositions(positions);
        Assert.Equal([1, 4, 9], positions);

        Assert.Equal(5, cursor.NextDoc());
        cursor.ReadPositions(positions);
        Assert.Equal([0], positions);

        Assert.Equal(17, cursor.NextDoc());
        cursor.ReadPositions(positions);
        Assert.Equal([2, 3], positions);

        Assert.Equal(PostingsEnumerator.NoMoreDocs, cursor.NextDoc());
    }

    [Fact]
    public void RejectsOutOfOrderDocuments()
    {
        var builder = new PostingsListBuilder();
        builder.Add(5, 1, [0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(3, 1, [0]));
    }

    [Fact]
    public void AdvanceLandsOnTheFirstDocumentAtOrAfterTheTarget()
    {
        var list = Build([(2, [0]), (4, [0]), (8, [0]), (16, [0])]);
        var cursor = list.GetEnumerator();

        Assert.Equal(4, cursor.Advance(3));
        Assert.Equal(8, cursor.Advance(8));
        Assert.Equal(16, cursor.Advance(9));
        Assert.Equal(PostingsEnumerator.NoMoreDocs, cursor.Advance(17));
    }

    [Fact]
    public void AdvanceAcrossManySkipBlocksMatchesLinearScan()
    {
        // Spans several skip intervals so the binary search over checkpoints is exercised.
        var docs = Enumerable.Range(0, PostingsList.SkipInterval * 5)
            .Select(i => (DocId: i * 3, Positions: new[] { i % 7 }))
            .ToArray();

        var list = Build(docs);

        foreach (var target in new[] { 0, 1, 500, 501, 1000, 1149, 1151, 1152 })
        {
            var cursor = list.GetEnumerator();
            var expected = docs.FirstOrDefault(d => d.DocId >= target, (PostingsEnumerator.NoMoreDocs, []));

            Assert.Equal(expected.DocId, cursor.Advance(target));
        }
    }

    [Fact]
    public void AdvanceThenReadPositionsStillDecodesTheCorrectBlock()
    {
        var docs = Enumerable.Range(0, PostingsList.SkipInterval * 3)
            .Select(i => (DocId: i * 2, Positions: new[] { i, i + 1 }))
            .ToArray();

        var list = Build(docs);
        var cursor = list.GetEnumerator();
        var positions = new List<int>();

        // Target sits inside the third skip block.
        Assert.Equal(600, cursor.Advance(600));
        cursor.ReadPositions(positions);
        Assert.Equal([300, 301], positions);
    }

    [Fact]
    public void RepeatedAdvanceIsMonotonicAndNeverRewinds()
    {
        var docs = Enumerable.Range(0, 400).Select(i => (DocId: i * 5, Positions: new[] { 0 })).ToArray();
        var list = Build(docs);
        var cursor = list.GetEnumerator();

        var previous = -1;
        foreach (var target in new[] { 10, 10, 200, 205, 1000, 1000, 1995 })
        {
            var doc = cursor.Advance(target);
            Assert.True(doc >= previous, $"Advance({target}) rewound from {previous} to {doc}.");
            previous = doc;
        }
    }

    [Fact]
    public void PositionsAreSkippedEntirelyWhenTheFieldDoesNotIndexThem()
    {
        var withPositions = Build([(0, [1, 2, 3]), (1, [4, 5, 6])]);
        var without = Build([(0, [1, 2, 3]), (1, [4, 5, 6])], withPositions: false);

        Assert.True(without.ByteLength < withPositions.ByteLength);

        var cursor = without.GetEnumerator();
        var positions = new List<int> { 99 };

        Assert.Equal(0, cursor.NextDoc());
        Assert.Equal(3, cursor.TermFrequency);
        cursor.ReadPositions(positions);
        Assert.Empty(positions);
    }

    [Fact]
    public void GapEncodingBeatsFixedWidthOnDenseLists()
    {
        // 1000 consecutive docs, one position each. Every doc gap is 1 and every position gap is
        // 0, so each posting encodes as four single-byte values: gap, frequency, position-block
        // length, position gap.
        const int documents = 1000;
        var docs = Enumerable.Range(0, documents).Select(i => (DocId: i, Positions: new[] { 0 })).ToArray();
        var list = Build(docs);

        Assert.Equal(4 * documents, list.ByteLength);

        // A naive fixed-width layout would spend 4 bytes each on doc id, frequency and position.
        const int fixedWidth = 12 * documents;
        Assert.True(
            list.ByteLength * 2 < fixedWidth,
            $"Expected better than half of fixed-width {fixedWidth}, got {list.ByteLength}.");
    }

    [Fact]
    public void GapEncodingStaysCompactWhenDocIdsAreSparse()
    {
        // Large but evenly spaced ids: the absolute ids need three bytes each, the gaps only one.
        var docs = Enumerable.Range(0, 500).Select(i => (DocId: 1_000_000 + (i * 4), Positions: new[] { 0 })).ToArray();
        var list = Build(docs);

        // Only the first posting carries a large absolute value; the other 499 encode in 4 bytes.
        Assert.True(
            list.ByteLength < 4 * 500 + 8,
            $"Sparse ids should still gap-encode to about 4 bytes per posting, got {list.ByteLength}.");
    }
}
