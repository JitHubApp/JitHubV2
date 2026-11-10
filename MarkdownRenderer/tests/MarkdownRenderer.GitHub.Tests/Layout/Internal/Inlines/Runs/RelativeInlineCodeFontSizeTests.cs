using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.LinkInlineStyleMapAssertions;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.RelativeScriptFontSizeFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Runs;

/// <summary>
/// Checks the opt-in relative inline code font size: its containing and
/// heading basis, how it composes with script and link scales, absolute
/// rule precedence and the native font size floor.
/// </summary>
public sealed class RelativeInlineCodeFontSizeTests
{
    [Theory]
    [InlineData("Body `code`", 13.6f)]
    [InlineData("# Heading `code`", 27.2f)]
    [InlineData("## Heading `code`", 20.4f)]
    public void OptInRelativeInlineCodeSizeUsesContainingTypography(string source, float expectedSize)
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, RelativeInlineCodeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CodeInlineRun codeRun = Assert.Single(box.Runs.OfType<CodeInlineRun>());
        ElementStyle resolved = box.GetRunStyle(codeRun);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        int codeStart = GetRunStart(box, codeRun);

        Assert.Equal(expectedSize, resolved.FontSize, precision: 2);
        Assert.Equal(expectedSize, ReadUniformFontSize(layout, codeStart, codeRun.TextLength), precision: 2);
        Assert.Equal("Cascadia Mono", resolved.FontFamily);
        Assert.Equal(Color.FromArgb(255, 230, 240, 250), resolved.Background);
    }

    [Fact]
    public void RelativeInlineCodeSizePreservesHeadingLinkTypographyAndTextScaling()
    {
        Color linkForeground = Color.FromArgb(255, 12, 94, 184);
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Link),
            new ElementStyleOverride { Foreground = linkForeground, Underline = true }));

        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "# [`code`](https://example.test/code)",
            sheet,
            textScale: 1.5);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        LinkRun link = Assert.Single(box.Runs.OfType<LinkRun>());
        LinkInlineStyleMap map = Assert.IsType<LinkInlineStyleMap>(link.InlineStyleMap);
        LinkInlineStyleSpan codeSpan = GetSpan(map, MarkdownElementKeys.CodeInline);
        ElementStyle[] spanStyles = box.GetLinkInlineSpanStyles(link);
        ElementStyle resolved = spanStyles[FindSpanIndex(map, codeSpan)];
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);

        Assert.Equal(40.8f, resolved.FontSize, precision: 2);
        Assert.Equal(40.8f, ReadUniformFontSize(layout, codeSpan.Start, codeSpan.Length), precision: 2);
        Assert.Equal("Cascadia Mono", resolved.FontFamily);
        Assert.Equal((ushort)700, resolved.FontWeight.Weight);
        Assert.Equal(linkForeground, resolved.Foreground);
        Assert.True(resolved.Underline);
        Assert.Equal(Color.FromArgb(255, 230, 240, 250), resolved.Background);
    }

    [Fact]
    public void LaterAbsoluteInlineCodeSizeWinsOverRelativeHeadingPolicy()
    {
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
            new ElementStyleOverride { FontSize = 11f }));

        using LayoutSnapshot snapshot = BuildParsedSnapshot("# Heading `code`", sheet);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CodeInlineRun codeRun = Assert.Single(box.Runs.OfType<CodeInlineRun>());
        ElementStyle resolved = box.GetRunStyle(codeRun);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        int codeStart = GetRunStart(box, codeRun);

        Assert.Equal(11f, resolved.FontSize);
        Assert.Equal(11f, ReadUniformFontSize(layout, codeStart, codeRun.TextLength));
        Assert.Equal("Cascadia Mono", resolved.FontFamily);
    }

    [Fact]
    public void SafeHtmlScriptParentIsResolvedBeforeRelativeInlineCodeAndRunCacheIsStable()
    {
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
            new ElementStyleOverride { FontSize = 11f }));
        using LayoutSnapshot snapshot = BuildParsedSnapshot("<sup><code>x</code></sup>", sheet);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CodeInlineRun codeRun = Assert.Single(box.Runs.OfType<CodeInlineRun>());

        Assert.Equal([MarkdownElementKeys.Superscript], codeRun.ScriptStyleAncestryKeys);
        ElementStyle first = box.GetRunStyle(codeRun);
        ElementStyle cached = box.GetRunStyle(codeRun);

        Assert.Same(first, cached);
        Assert.Equal(9.35f, first.FontSize, precision: 2);
    }

    [Fact]
    public void AbsoluteInlineCodeRuleWinsAfterRelativeScriptAncestor()
    {
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet()
            .WithRelativeScriptFontSize(0.75f)
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
                new ElementStyleOverride { FontSize = 11f }));
        using LayoutSnapshot snapshot = BuildParsedSnapshot("<sup><code>x</code></sup>", sheet);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CodeInlineRun codeRun = Assert.Single(box.Runs.OfType<CodeInlineRun>());
        ElementStyle resolved = box.GetRunStyle(codeRun);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);

        Assert.Equal([MarkdownElementKeys.Superscript], codeRun.ScriptStyleAncestryKeys);
        Assert.Equal(11f, resolved.FontSize);
        Assert.Equal(11f, ReadUniformFontSize(layout, GetRunStart(box, codeRun), codeRun.TextLength));
    }

    [Fact]
    public void RelativeScriptAndInlineCodeScalesComposeOnceForDirectCodeRun()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "<sup><code>x</code></sup>",
            RelativeInlineCodeSheet().WithRelativeScriptFontSize(0.75f));
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CodeInlineRun codeRun = Assert.Single(box.Runs.OfType<CodeInlineRun>());
        ElementStyle resolved = box.GetRunStyle(codeRun);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);

        Assert.Equal(10.2f, resolved.FontSize, precision: 2);
        Assert.Equal(10.2f, ReadUniformFontSize(layout, GetRunStart(box, codeRun), codeRun.TextLength), precision: 2);
    }

    [Fact]
    public void RelativeInlineCodeUsesSemanticHeadingWhenContainerIsNotHeading()
    {
        var codeRun = new CodeInlineRun("x")
        {
            SemanticHeadingKey = MarkdownElementKeys.Heading1,
        };

        ElementStyle resolved = ResolveRunStyle(
            MarkdownElementKeys.Body,
            codeRun,
            RelativeInlineCodeSheet());

        Assert.Equal(27.2f, resolved.FontSize, precision: 2);
        Assert.Equal("Cascadia Mono", resolved.FontFamily);
    }

    [Theory]
    [InlineData(MarkdownElementKeys.Body, true)]
    [InlineData(MarkdownElementKeys.Heading1, false)]
    public void RelativeInlineCodeHeadingBasisDoesNotUseDescendantClassAliases(
        string containerRole,
        bool semanticHeading)
    {
        const string childOnlyClass = "inline-code-only";
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(
                MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Heading1),
                className: childOnlyClass),
            new ElementStyleOverride
            {
                FontSize = 80f,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 900 },
                Foreground = Color.FromArgb(255, 210, 20, 170),
            }));
        var codeRun = new CodeInlineRun("x")
        {
            SemanticHeadingKey = semanticHeading ? MarkdownElementKeys.Heading1 : string.Empty,
        };
        codeRun.SetStyleAliases([MarkdownElementKeys.Class(childOnlyClass)]);

        ThemeSnapshot theme = CreateTheme(sheet);
        ElementStyle expectedHeadingStyle = theme.GetStyle(MarkdownElementKeys.Heading1);
        ElementStyle resolved = ResolveRunStyle(containerRole, codeRun, theme);

        // The code run's class matches this deliberately hostile Heading1
        // selector, but it is not an alias on the containing heading.
        Assert.Equal(27.2f, resolved.FontSize, precision: 2);
        Assert.Equal(expectedHeadingStyle.FontWeight, resolved.FontWeight);
        Assert.Equal(expectedHeadingStyle.Foreground, resolved.Foreground);
    }

    [Theory]
    [InlineData(MarkdownElementKeys.Body)]
    [InlineData(MarkdownElementKeys.Heading1)]
    public void RelativeInlineCodeIsAppliedAfterDirectScriptModifiers(string containerRole)
    {
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Subscript)),
            new ElementStyleOverride { FontSize = 11f }));
        var codeRun = new CodeInlineRun("x")
        {
            StyleModifierKeys = [MarkdownElementKeys.Subscript],
        };

        ElementStyle resolved = ResolveRunStyle(containerRole, codeRun, sheet);

        Assert.Equal(9.35f, resolved.FontSize, precision: 2);
    }

    [Fact]
    public void RelativeInlineCodeLinkSpanComposesItsScriptAndCodeScalesOnce()
    {
        MarkdownStyleSheet sheet = RelativeInlineCodeSheet().WithRelativeScriptFontSize(0.75f);
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "^[`code`](https://example.test/code)^",
            sheet);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        LinkRun link = Assert.Single(box.Runs.OfType<LinkRun>());
        LinkInlineStyleMap map = Assert.IsType<LinkInlineStyleMap>(link.InlineStyleMap);
        LinkInlineStyleSpan codeSpan = GetSpan(map, MarkdownElementKeys.CodeInline);
        ElementStyle[] first = box.GetLinkInlineSpanStyles(link);
        ElementStyle[] cached = box.GetLinkInlineSpanStyles(link);
        ElementStyle codeStyle = first[FindSpanIndex(map, codeSpan)];
        ElementStyle linkStyle = box.GetRunStyle(link);

        Assert.Equal([MarkdownElementKeys.Superscript], link.ScriptStyleAncestryKeys);
        Assert.Same(first, cached);
        Assert.Equal(12f, linkStyle.FontSize, precision: 2);
        Assert.Equal(10.2f, codeStyle.FontSize, precision: 2);
    }

    [Fact]
    public void RelativeInlineCodePolicyIsDistinctFromScriptsAndSurvivesAnAbsoluteOverride()
    {
        MarkdownStyleSheet absoluteOnly = MarkdownStyleSheet.Empty.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
            new ElementStyleOverride { FontSize = 11f }));
        MarkdownStyleSheet relativeThenAbsolute = RelativeInlineCodeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
            new ElementStyleOverride { FontSize = 11f }));
        ThemeSnapshot absoluteTheme = CreateTheme(absoluteOnly);
        ThemeSnapshot maskedPolicyTheme = CreateTheme(relativeThenAbsolute);

        Assert.False(absoluteTheme.HasRelativeInlineCodeFontSizeRules);
        Assert.False(absoluteTheme.HasRelativeScriptFontSizeRules);
        Assert.True(maskedPolicyTheme.HasRelativeInlineCodeFontSizeRules);
        Assert.False(maskedPolicyTheme.HasRelativeScriptFontSizeRules);
        Assert.Null(maskedPolicyTheme.GetStyle(MarkdownElementKeys.CodeInline).RelativeFontSizeScale);
        Assert.Equal(11f, maskedPolicyTheme.GetStyle(MarkdownElementKeys.CodeInline).FontSize);
        Assert.NotEqual(absoluteTheme.LayoutFingerprint, maskedPolicyTheme.LayoutFingerprint);
    }

    [Fact]
    public void TinyRelativeInlineCodeScaleClampsToTheNativeSafeFontFloor()
    {
        ElementStyle resolved = ResolveRunStyle(
            MarkdownElementKeys.Heading1,
            new CodeInlineRun("code"),
            MarkdownStyleSheet.Empty.WithRelativeInlineCodeFontSize(float.Epsilon));

        Assert.Equal(0.1f, resolved.FontSize);
        Assert.True(float.IsFinite(resolved.FontSize));
        Assert.True(resolved.FontSize > 0f);
    }

    private static MarkdownStyleSheet RelativeInlineCodeSheet()
        => MarkdownStyleSheet.Empty
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
                new ElementStyleOverride
                {
                    FontFamily = "Cascadia Mono",
                    Background = Color.FromArgb(255, 230, 240, 250),
                    Padding = new Thickness(2),
                }))
            .WithRelativeInlineCodeFontSize(0.85f);
}
