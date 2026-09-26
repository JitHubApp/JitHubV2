using MarkdownRenderer.Extensions;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownVectorPaintRoleTests
{
    [Fact]
    public void LegacyStyleKeepsAuthoredRolesAndNullColorCompatibility()
    {
        var authored = new MarkdownVectorPaintStyle(
            fillArgb: 0xFF123456,
            strokeArgb: 0xFF654321,
            strokeWidth: 1);
        var semanticForeground = new MarkdownVectorPaintStyle();

        Assert.Equal(MarkdownVectorPaintRole.Authored, authored.FillRole);
        Assert.Equal(MarkdownVectorPaintRole.Authored, authored.StrokeRole);
        Assert.Null(authored.HighContrastFillRole);
        Assert.Null(authored.HighContrastStrokeRole);
        Assert.Equal(MarkdownVectorPaintRole.Authored, semanticForeground.FillRole);
        Assert.Null(semanticForeground.FillArgb);
    }

    [Fact]
    public void LegacyDefaultArgumentsRemainUnambiguous()
    {
        var style = new MarkdownVectorPaintStyle(default, default);

        Assert.Null(style.FillArgb);
        Assert.Null(style.StrokeArgb);
        Assert.Equal(MarkdownVectorPaintRole.Authored, style.FillRole);
        Assert.Equal(MarkdownVectorPaintRole.Authored, style.StrokeRole);
    }

    [Fact]
    public void ExplicitSemanticRolesAreImmutableSceneData()
    {
        var style = MarkdownVectorPaintStyle.CreateSemantic(
            MarkdownVectorPaintRole.Surface,
            MarkdownVectorPaintRole.Link,
            fillArgb: 0xFFEEEEEE,
            strokeArgb: 0xFF0000FF,
            strokeWidth: 2);

        Assert.Equal(MarkdownVectorPaintRole.Surface, style.FillRole);
        Assert.Equal(MarkdownVectorPaintRole.Link, style.StrokeRole);
        Assert.Equal(0xFFEEEEEEu, style.FillArgb);
        Assert.Equal(0xFF0000FFu, style.StrokeArgb);
    }

    [Fact]
    public void HighContrastRolesPreserveAuthoredNormalThemeColors()
    {
        var authored = new MarkdownVectorPaintStyle(
            fillArgb: 0xFFECECFF,
            strokeArgb: 0xFF9370DB,
            strokeWidth: 1);

        MarkdownVectorPaintStyle accessible = authored.WithHighContrastRoles(
            MarkdownVectorPaintRole.Surface,
            MarkdownVectorPaintRole.Foreground);

        Assert.NotSame(authored, accessible);
        Assert.Equal(MarkdownVectorPaintRole.Authored, accessible.FillRole);
        Assert.Equal(MarkdownVectorPaintRole.Authored, accessible.StrokeRole);
        Assert.Equal(0xFFECECFFu, accessible.FillArgb);
        Assert.Equal(0xFF9370DBu, accessible.StrokeArgb);
        Assert.Equal(MarkdownVectorPaintRole.Surface, accessible.HighContrastFillRole);
        Assert.Equal(MarkdownVectorPaintRole.Foreground, accessible.HighContrastStrokeRole);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void InvalidHighContrastRolesAreRejected(int value)
    {
        var style = new MarkdownVectorPaintStyle(fillArgb: 0xFFFFFFFF);

        Assert.Throws<ArgumentOutOfRangeException>(() => style.WithHighContrastRoles(
            (MarkdownVectorPaintRole)value,
            MarkdownVectorPaintRole.Foreground));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void UndefinedSemanticPaintRolesAreRejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownVectorPaintStyle.CreateSemantic(
            (MarkdownVectorPaintRole)value,
            MarkdownVectorPaintRole.Foreground));
    }
}
