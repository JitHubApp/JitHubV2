using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class HighContrastDefaultsTests
{
    [Fact]
    public void LinkUsesSystemHotlightAndUnderline()
    {
        var roles = MarkdownHighContrastDefaults.Resolve("Link");

        Assert.Equal(MarkdownHighContrastColorRole.Hotlight, roles.Foreground);
        Assert.True(roles.Underline);
        Assert.Null(roles.Background);
    }

    [Fact]
    public void BodyAndCodeUseWindowForegroundAndBackground()
    {
        var body = MarkdownHighContrastDefaults.Resolve("Body");
        var code = MarkdownHighContrastDefaults.Resolve("CodeBlock");
        var codeHeader = MarkdownHighContrastDefaults.Resolve("CodeBlockHeader");
        var codeLanguage = MarkdownHighContrastDefaults.Resolve("CodeBlockLanguage");
        var codeGutter = MarkdownHighContrastDefaults.Resolve("CodeBlockGutter");
        var codeLineNumber = MarkdownHighContrastDefaults.Resolve("CodeBlockLineNumber");
        var inlineCode = MarkdownHighContrastDefaults.Resolve("CodeInline");

        Assert.Equal(MarkdownHighContrastColorRole.WindowText, body.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, body.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, code.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, code.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, code.AccentBar);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, codeHeader.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, codeHeader.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, codeHeader.AccentBar);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, codeLanguage.Foreground);
        Assert.Null(codeLanguage.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, codeGutter.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, codeGutter.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, codeGutter.AccentBar);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, codeLineNumber.Foreground);
        Assert.Null(codeLineNumber.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, inlineCode.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, inlineCode.Background);
    }

    [Fact]
    public void TableHeaderUsesHighlightPair()
    {
        var table = MarkdownHighContrastDefaults.Resolve("Table");
        var roles = MarkdownHighContrastDefaults.Resolve("TableHeader");

        Assert.Equal(MarkdownHighContrastColorRole.WindowText, table.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, table.Background);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, table.AccentBar);
        Assert.Equal(MarkdownHighContrastColorRole.HighlightText, roles.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Highlight, roles.Background);
    }

    [Fact]
    public void AlertsUseHotlightAccentBar()
    {
        var roles = MarkdownHighContrastDefaults.Resolve("AlertWarning");

        Assert.Equal(MarkdownHighContrastColorRole.WindowText, roles.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Hotlight, roles.AccentBar);
    }

    [Fact]
    public void AbbreviationUsesVisibleSystemUnderline()
    {
        var roles = MarkdownHighContrastDefaults.Resolve("Abbreviation");

        Assert.Equal(MarkdownHighContrastColorRole.WindowText, roles.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.WindowText, roles.AccentBar);
        Assert.True(roles.Underline);
    }
}
