using Xunit;

namespace MarkdownRenderer.Tests;

public class SourceSpanTests
{
    [Fact]
    public void Constructor_SetsStartAndLength()
    {
        var s = new SourceSpan(10, 5);
        Assert.Equal(10, s.Start);
        Assert.Equal(5, s.Length);
    }

    [Fact]
    public void End_ReturnsStartPlusLength()
    {
        var s = new SourceSpan(10, 5);
        Assert.Equal(15, s.End);
    }

    [Fact]
    public void End_AtOrigin()
    {
        var s = new SourceSpan(0, 3);
        Assert.Equal(3, s.End);
    }

    [Fact]
    public void Empty_HasZeroLengthAndStart()
    {
        Assert.Equal(0, SourceSpan.Empty.Length);
        Assert.Equal(0, SourceSpan.Empty.Start);
    }

    [Fact]
    public void IsEmpty_TrueWhenLengthZero()
    {
        Assert.True(new SourceSpan(5, 0).IsEmpty);
    }

    [Fact]
    public void IsEmpty_FalseWhenLengthPositive()
    {
        Assert.False(new SourceSpan(5, 1).IsEmpty);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]  // End is exclusive
    [InlineData(9, false)]
    public void Contains_Position(int position, bool expected)
    {
        var s = new SourceSpan(10, 5); // [10..15)
        Assert.Equal(expected, s.Contains(position));
    }

    [Fact]
    public void Union_WithEmpty_ReturnsSelf()
    {
        var s = new SourceSpan(5, 10);
        Assert.Equal(s, s.Union(SourceSpan.Empty));
    }

    [Fact]
    public void Union_EmptyWithSpan_ReturnsOther()
    {
        var s = new SourceSpan(5, 10);
        Assert.Equal(s, SourceSpan.Empty.Union(s));
    }

    [Fact]
    public void Union_AdjacentSpans_CoversBoth()
    {
        var a = new SourceSpan(0, 5);
        var b = new SourceSpan(5, 5);
        var u = a.Union(b);
        Assert.Equal(0, u.Start);
        Assert.Equal(10, u.Length);
    }

    [Fact]
    public void Union_OverlappingSpans_TakesMinStartMaxEnd()
    {
        var a = new SourceSpan(3, 10); // [3..13)
        var b = new SourceSpan(7, 8);  // [7..15)
        var u = a.Union(b);
        Assert.Equal(3, u.Start);
        Assert.Equal(12, u.Length); // [3..15)
    }

    [Fact]
    public void Union_Commutative()
    {
        var a = new SourceSpan(1, 4);
        var b = new SourceSpan(3, 6);
        Assert.Equal(a.Union(b), b.Union(a));
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new SourceSpan(2, 8);
        var b = new SourceSpan(2, 8);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentValues_NotEqual()
    {
        Assert.NotEqual(new SourceSpan(1, 5), new SourceSpan(1, 6));
    }
}
