using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class TouchSelectionEndpointPolicyTests
{
    [Fact]
    public void CrossingStartHandleExchangesLogicalRole()
    {
        var boundaries = new TextElementBoundaryIndex("alpha beta");

        var result = TouchSelectionEndpointPolicy.Resolve(
            boundaries,
            fixedOffset: 5,
            rawCandidateOffset: 8,
            startEndpoint: true,
            allowCrossing: true);

        Assert.False(result.IsStartEndpoint);
        Assert.Equal(9, result.Offset);
    }

    [Fact]
    public void CrossingEndHandleExchangesLogicalRole()
    {
        var boundaries = new TextElementBoundaryIndex("alpha beta");

        var result = TouchSelectionEndpointPolicy.Resolve(
            boundaries,
            fixedOffset: 6,
            rawCandidateOffset: 2,
            startEndpoint: false,
            allowCrossing: true);

        Assert.True(result.IsStartEndpoint);
        Assert.Equal(2, result.Offset);
    }

    [Fact]
    public void EndpointNeverSplitsEmojiOrCombiningSequence()
    {
        const string text = "A👩🏽‍💻e\u0301Z";
        var boundaries = new TextElementBoundaryIndex(text);

        var emojiEnd = TouchSelectionEndpointPolicy.Resolve(
            boundaries, 0, 3, startEndpoint: false, allowCrossing: true);
        var combiningStart = TouchSelectionEndpointPolicy.Resolve(
            boundaries, text.Length, text.Length - 2, startEndpoint: true, allowCrossing: true);

        Assert.Equal(boundaries.FindBoundaries(3).End, emojiEnd.Offset);
        Assert.Equal(boundaries.FindBoundaries(text.Length - 2).Start, combiningStart.Offset);
    }

    [Fact]
    public void LogicalOffsetsRemainOrderedForMixedRtlText()
    {
        const string text = "abc אבג def";
        var boundaries = new TextElementBoundaryIndex(text);

        var result = TouchSelectionEndpointPolicy.Resolve(
            boundaries, fixedOffset: 4, rawCandidateOffset: 7,
            startEndpoint: true, allowCrossing: true);

        Assert.False(result.IsStartEndpoint);
        Assert.True(result.Offset > 4);
    }

    [Fact]
    public void KeyboardEndHandleCanMoveToPreviousBoundary()
    {
        var boundaries = new TextElementBoundaryIndex("abcde");

        var result = TouchSelectionEndpointPolicy.Resolve(
            boundaries,
            fixedOffset: 0,
            rawCandidateOffset: 4,
            startEndpoint: false,
            allowCrossing: false,
            candidateIsBoundary: true);

        Assert.Equal(4, result.Offset);
        Assert.False(result.IsStartEndpoint);
    }

    [Theory]
    [InlineData(4, 1, true, 1, false)]
    [InlineData(4, 4, true, 3, true)]
    [InlineData(4, 3, false, 2, true)]
    [InlineData(4, 0, false, 0, false)]
    public void CaretQueryUsesTheCharacterSideOwnedByTheSelectedRange(
        int textLength,
        int offset,
        bool rangeStart,
        int expectedCharacterIndex,
        bool expectedTrailingSide)
    {
        TouchSelectionCaretQuery query = TouchSelectionCaretPolicy.Resolve(
            textLength,
            offset,
            rangeStart);

        Assert.Equal(expectedCharacterIndex, query.CharacterIndex);
        Assert.Equal(expectedTrailingSide, query.TrailingSideOfCharacter);
    }

    [Theory]
    [InlineData(24, 200, false, 24)]
    [InlineData(24, 200, true, 176)]
    [InlineData(0, 200, true, 200)]
    [InlineData(200, 200, true, 0)]
    public void VisualXMirrorsOnlyForRightToLeftParagraphs(
        double caretX,
        double layoutWidth,
        bool rightToLeft,
        double expected)
    {
        Assert.Equal(
            expected,
            TouchSelectionCaretPolicy.ResolveVisualX(caretX, layoutWidth, rightToLeft));
    }

    [Theory]
    [InlineData(double.NaN, 200)]
    [InlineData(24, double.PositiveInfinity)]
    [InlineData(24, -1)]
    public void VisualXRejectsInvalidGeometry(double caretX, double layoutWidth)
    {
        Assert.True(double.IsNaN(
            TouchSelectionCaretPolicy.ResolveVisualX(
                caretX,
                layoutWidth,
                paragraphRightToLeft: true)));
    }
}
