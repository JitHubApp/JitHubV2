using Xunit;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Tests;

public class DocumentPositionTests
{
    [Fact]
    public void Zero_IsDefaultValue()
    {
        var z = DocumentPosition.Zero;
        Assert.Equal(0, z.BlockIndex);
        Assert.Equal(0, z.InlineIndex);
        Assert.Equal(0, z.CharacterOffset);
    }

    [Fact]
    public void CompareTo_SamePosition_ReturnsZero()
    {
        var a = new DocumentPosition(1, 2, 3);
        Assert.Equal(0, a.CompareTo(a));
    }

    [Fact]
    public void CompareTo_HigherBlockIndex_ReturnsPositive()
    {
        var a = new DocumentPosition(2, 0, 0);
        var b = new DocumentPosition(1, 0, 0);
        Assert.True(a.CompareTo(b) > 0);
    }

    [Fact]
    public void CompareTo_SameBlockHigherInline_ReturnsPositive()
    {
        var a = new DocumentPosition(1, 3, 0);
        var b = new DocumentPosition(1, 2, 0);
        Assert.True(a.CompareTo(b) > 0);
    }

    [Fact]
    public void CompareTo_SameBlockInlineHigherChar_ReturnsPositive()
    {
        var a = new DocumentPosition(1, 2, 5);
        var b = new DocumentPosition(1, 2, 4);
        Assert.True(a.CompareTo(b) > 0);
    }

    [Fact]
    public void LessThan_Operator_Works()
    {
        var earlier = new DocumentPosition(0, 0, 0);
        var later = new DocumentPosition(1, 0, 0);
        Assert.True(earlier < later);
        Assert.False(later < earlier);
    }

    [Fact]
    public void GreaterThan_Operator_Works()
    {
        var a = new DocumentPosition(2, 0, 0);
        var b = new DocumentPosition(1, 0, 0);
        Assert.True(a > b);
    }

    [Fact]
    public void LessThanOrEqual_EqualPositions_ReturnsTrue()
    {
        var a = new DocumentPosition(1, 2, 3);
        var b = new DocumentPosition(1, 2, 3);
        Assert.True(a <= b);
        Assert.True(b <= a);
    }

    [Fact]
    public void GreaterThanOrEqual_EqualPositions_ReturnsTrue()
    {
        var a = new DocumentPosition(1, 2, 3);
        Assert.True(a >= a);
    }

    [Fact]
    public void Equality_SameValues_Equal()
    {
        var a = new DocumentPosition(3, 4, 5);
        var b = new DocumentPosition(3, 4, 5);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentCharOffset_NotEqual()
    {
        Assert.NotEqual(new DocumentPosition(0, 0, 1), new DocumentPosition(0, 0, 2));
    }
}

public class DocumentRangeTests
{
    [Fact]
    public void Empty_HasZeroStartAndEnd()
    {
        var r = DocumentRange.Empty;
        Assert.Equal(DocumentPosition.Zero, r.Start);
        Assert.Equal(DocumentPosition.Zero, r.End);
    }

    [Fact]
    public void IsEmpty_WhenStartEqualsEnd_ReturnsTrue()
    {
        var r = new DocumentRange(DocumentPosition.Zero, DocumentPosition.Zero);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void IsEmpty_WhenStartAfterEnd_ReturnsFalse()
    {
        // A backwards range (Start > End) is not empty — it has content.
        // Use Normalized() before checking IsEmpty when ordering matters.
        var start = new DocumentPosition(2, 0, 0);
        var end = new DocumentPosition(1, 0, 0);
        var r = new DocumentRange(start, end);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void IsEmpty_WhenEndAfterStart_ReturnsFalse()
    {
        var start = new DocumentPosition(0, 0, 0);
        var end = new DocumentPosition(1, 0, 0);
        var r = new DocumentRange(start, end);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void Normalized_AlreadyOrdered_ReturnsSame()
    {
        var start = new DocumentPosition(0, 0, 0);
        var end = new DocumentPosition(1, 2, 3);
        var r = new DocumentRange(start, end);
        var n = r.Normalized();
        Assert.Equal(start, n.Start);
        Assert.Equal(end, n.End);
    }

    [Fact]
    public void Normalized_Reversed_SwapsStartEnd()
    {
        var a = new DocumentPosition(0, 0, 0);
        var b = new DocumentPosition(1, 0, 0);
        var reversed = new DocumentRange(b, a);
        var n = reversed.Normalized();
        Assert.Equal(a, n.Start);
        Assert.Equal(b, n.End);
    }

    [Fact]
    public void Normalized_Empty_RemainsEmpty()
    {
        var n = DocumentRange.Empty.Normalized();
        Assert.True(n.IsEmpty);
    }
}
