using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class ExactSourceProjectionTests
{
    [Fact]
    public void SourceGapsUseDirectionalBoundariesWithoutCopyingContainerPrefixes()
    {
        const string source = "> alpha\r\n> beta";
        var projection = Create(10, source.Length,
        [
            new(0, 5, 2, 5),
            new(5, 1, 7, 2, IsAtomic: true),
            new(6, 4, 11, 4),
        ]);

        Assert.Equal(7, projection.ProjectStart(5));
        Assert.Equal(9, projection.ProjectEnd(6));
        Assert.Equal(11, projection.ProjectStart(6));
        Assert.Equal(7, projection.ProjectEnd(5));
        var map = new MarkdownSourceMap(source);
        map.Add(1, 0, 10, new SourceSpan(0, source.Length), projection);
        Assert.Equal("\r\n", map.Slice(Range(5, 6)));
        Assert.Equal("e", map.Slice(Range(7, 8)));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(1, 3)]
    public void EitherUtf16HalfOfAnAtomicEntityMapsToTheWholeAuthoredToken(int start, int end)
    {
        const string entity = "&#x1F642;";
        const string source = "S" + entity + "E";
        var projection = Create(4, source.Length,
        [
            new(0, 1, 0, 1),
            new(1, 2, 1, entity.Length, IsAtomic: true),
            new(3, 1, 1 + entity.Length, 1),
        ]);
        var map = new MarkdownSourceMap(source);
        map.Add(1, 0, 4, new SourceSpan(0, source.Length), projection);

        Assert.True(map.TryMapRange(Range(start, end), out SourceSpan span));
        Assert.Equal(new SourceSpan(1, entity.Length), span);
        Assert.Equal(entity, map.Slice(Range(start, end)));
    }

    [Theory]
    [InlineData(false, false, 2, 5)]
    [InlineData(true, false, 0, 5)]
    [InlineData(false, true, 2, 7)]
    [InlineData(true, true, 0, 7)]
    public void PrefixAndSuffixInclusionAreIndependent(
        bool includePrefix, bool includeSuffix, int expectedStart, int expectedEnd)
    {
        var projection = MarkdownSourceMap.SourceProjection.TryCreateExactSegments(
            3, 7, [new(0, 3, 2, 3)], includePrefix, includeSuffix);
        Assert.NotNull(projection);
        Assert.Equal(expectedStart, projection.ProjectStart(0));
        Assert.Equal(expectedEnd, projection.ProjectEnd(3));
    }

    [Fact]
    public void ExactLinearProjectionRetainsOffsetsBeyondTheHeuristicLimits()
    {
        const int bodyLength = 80 * 1024;
        string source = "```x\n" + new string('x', bodyLength) + "\n```";
        var projection = Create(bodyLength, source.Length, [new(0, bodyLength, 5, bodyLength)], true, true);
        var map = new MarkdownSourceMap(source);
        map.Add(1, 0, bodyLength, new SourceSpan(0, source.Length), projection);

        Assert.True(map.TryMapRange(Range(bodyLength / 2, bodyLength / 2 + 1), out SourceSpan span));
        Assert.Equal(new SourceSpan(5 + bodyLength / 2, 1), span);
        Assert.Equal(source, map.Slice(Range(0, bodyLength)));
    }

    [Fact]
    public void ProjectionOwnsAnImmutableCopyOfProducerSegments()
    {
        var segments = new[] { new MarkdownSourceMap.ProjectionSegment(0, 3, 2, 3) };
        var projection = Create(3, 7, segments);
        segments[0] = new(0, 3, 0, 3);

        Assert.Equal(3, projection.ProjectStart(1));
        Assert.Equal(4, projection.ProjectEnd(2));
    }

    [Fact]
    public void DenseAdmittedEntityMetadataDoesNotLoseMappingAtTheLegacyHeuristicLimit()
    {
        const int entityCount = 20_000;
        const string entity = "&gt;";
        string source = string.Concat(Enumerable.Repeat(entity, entityCount));
        var segments = new MarkdownSourceMap.ProjectionSegment[entityCount];
        for (int index = 0; index < segments.Length; index++)
            segments[index] = new(index, 1, index * entity.Length, entity.Length, IsAtomic: true);
        var projection = Create(entityCount, source.Length, segments);
        var map = new MarkdownSourceMap(source);
        map.Add(1, 0, entityCount, new SourceSpan(0, source.Length), projection);

        Assert.Equal(entity, map.Slice(Range(entityCount / 2, entityCount / 2 + 1)));
        Assert.True(map.TryMapRange(Range(entityCount / 2, entityCount / 2 + 1), out SourceSpan span));
        Assert.Equal(new SourceSpan(entityCount / 2 * entity.Length, entity.Length), span);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void IncompleteOverlappingOverflowingAndUnmarkedNonlinearSegmentsAreRejected(
        int invalidCase)
    {
        MarkdownSourceMap.ProjectionSegment[] segments = invalidCase switch
        {
            0 => [new(0, 2, 0, 3)],
            1 => [new(1, 2, 0, 2)],
            2 => [new(0, 1, 1, 1), new(1, 1, 1, 1)],
            3 => [new(0, 2, int.MaxValue, 2)],
            4 => [new(0, 0, 0, 1)],
            5 => [new(0, 2, -1, 2)],
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase)),
        };
        Assert.Null(MarkdownSourceMap.SourceProjection.TryCreateExactSegments(
            2, 3, segments, includeSourcePrefix: false, includeSourceSuffix: false));
    }

    [Fact]
    public void EntryRejectsAProjectionWithDifferentRenderedOrSourceLength()
    {
        var map = new MarkdownSourceMap("abc");
        var projection = Create(2, 3, [new(0, 2, 0, 2)]);
        Assert.Throws<ArgumentException>(() => map.Add(1, 0, 3, new SourceSpan(0, 3), projection));
        Assert.Throws<ArgumentException>(() => map.Add(1, 0, 2, new SourceSpan(0, 2), projection));
    }

    private static MarkdownSourceMap.SourceProjection Create(
        int renderedLength,
        int sourceLength,
        IReadOnlyList<MarkdownSourceMap.ProjectionSegment> segments,
        bool includePrefix = false,
        bool includeSuffix = false)
        => Assert.IsType<MarkdownSourceMap.SourceProjection>(
            MarkdownSourceMap.SourceProjection.TryCreateExactSegments(
                renderedLength, sourceLength, segments, includePrefix, includeSuffix));

    private static DocumentRange Range(int start, int end)
        => new(new DocumentPosition(1, 0, start), new DocumentPosition(1, 0, end));
}
