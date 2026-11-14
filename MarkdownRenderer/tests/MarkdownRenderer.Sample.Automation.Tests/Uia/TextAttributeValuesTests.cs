using MarkdownRenderer.Sample.Automation.Uia;
using Xunit;

namespace MarkdownRenderer.Sample.Automation.Tests.Uia;

public sealed class TextAttributeValuesTests
{
    private enum Decoration
    {
        None = 0,
        Single = 1,
    }

    [Fact]
    public void ColorRefPacksRedInTheLowByte()
        => Assert.Equal(0x00E22B8A, TextAttributeValues.ColorRef(0x8A, 0x2B, 0xE2));

    [Theory]
    [InlineData(0x00FFFF00)]
    [InlineData(0x00FFFF00u)]
    [InlineData(16776960d)]
    public void ToColorRefValueAcceptsIntegerAndNumericValues(object value)
        => Assert.Equal(0x00FFFF00, TextAttributeValues.ToColorRefValue(value));

    [Fact]
    public void ToColorRefValueRejectsNullAndText()
    {
        Assert.Throws<InvalidOperationException>(() => TextAttributeValues.ToColorRefValue(null));
        var error = Assert.Throws<InvalidOperationException>(() => TextAttributeValues.ToColorRefValue("red"));
        Assert.Contains("red (System.String)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAttributeNumberReadsEnumsAndNumbersButNotText()
    {
        Assert.True(TextAttributeValues.TryAttributeNumber(Decoration.Single, out double enumValue));
        Assert.Equal(1d, enumValue);
        Assert.True(TextAttributeValues.TryAttributeNumber(14.5f, out double floatValue));
        Assert.Equal(14.5d, floatValue);
        Assert.False(TextAttributeValues.TryAttributeNumber("14", out double textValue));
        Assert.Equal(0d, textValue);
    }

    [Fact]
    public void NonNoneTextDecorationIgnoresNullNoneAndZero()
    {
        Assert.False(TextAttributeValues.IsNonNoneTextDecoration(null));
        Assert.False(TextAttributeValues.IsNonNoneTextDecoration(Decoration.None));
        Assert.False(TextAttributeValues.IsNonNoneTextDecoration(0));
        Assert.True(TextAttributeValues.IsNonNoneTextDecoration(Decoration.Single));
        Assert.True(TextAttributeValues.IsNonNoneTextDecoration("Mixed"));
    }

    [Fact]
    public void TrueAttributeAcceptsBooleansAndBooleanText()
    {
        Assert.True(TextAttributeValues.IsTrueAttribute(true));
        Assert.True(TextAttributeValues.IsTrueAttribute("True"));
        Assert.False(TextAttributeValues.IsTrueAttribute(false));
        Assert.False(TextAttributeValues.IsTrueAttribute(null));
        Assert.False(TextAttributeValues.IsTrueAttribute(1));
    }

    [Fact]
    public void DescribeAttributeValueNamesTheRuntimeType()
    {
        Assert.Equal("<null>", TextAttributeValues.DescribeAttributeValue(null));
        Assert.Equal("3 (System.Int32)", TextAttributeValues.DescribeAttributeValue(3));
    }
}
