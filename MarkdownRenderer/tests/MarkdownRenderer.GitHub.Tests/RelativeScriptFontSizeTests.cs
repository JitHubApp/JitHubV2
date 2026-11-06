using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;
using static MarkdownRenderer.GitHub.Tests.RelativeScriptFontSizeFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks relative superscript and subscript font sizing: the containing
/// role it scales, nested and malformed HTML script scopes, absolute rule
/// precedence, text scale, High Contrast, fingerprints and validation.
/// </summary>
public sealed class RelativeScriptFontSizeTests
{
    private const string ScriptClass = "script-scale";

    private readonly ITestOutputHelper _output;

    public RelativeScriptFontSizeTests(ITestOutputHelper output)
        => _output = output;

    [Theory]
    [InlineData(MarkdownElementKeys.Body, 16f, 12f)]
    [InlineData(MarkdownElementKeys.Heading1, 32f, 24f)]
    [InlineData(MarkdownElementKeys.Heading2, 24f, 18f)]
    [InlineData(MarkdownElementKeys.Quote, 20f, 15f)]
    [InlineData(MarkdownElementKeys.TableCell, 16f, 12f)]
    public void RelativeScriptSizeUsesEffectiveContainingRole(
        string containerRole,
        float containingSize,
        float expectedSize)
    {
        ElementStyle actual = ResolveRunStyle(
            containerRole,
            new SuperscriptRun("x"),
            RelativeSheet());

        Assert.Equal(containingSize * 0.75f, expectedSize);
        Assert.Equal(expectedSize, actual.FontSize);
    }

    [Fact]
    public void ScriptModifierUsesSizeBeforeItsNominalAbsoluteRoleSize()
    {
        var run = new StrongRun("x")
        {
            StyleModifierKeys = [MarkdownElementKeys.Superscript],
        };

        ElementStyle actual = ResolveRunStyle(
            MarkdownElementKeys.Heading2,
            run,
            RelativeSheet());

        Assert.Equal(18f, actual.FontSize);
        Assert.Equal((ushort)700, actual.FontWeight.Weight);
    }

    [Fact]
    public void NestedSafeHtmlScriptsCompoundAndKeepAliasesLinkStyleAndRtlContext()
    {
        Color linkColor = Color.FromArgb(255, 13, 91, 173);
        MarkdownStyleSheet sheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Link, className: ScriptClass),
                new ElementStyleOverride { Foreground = linkColor, Underline = true }),
        ]).WithRelativeScriptFontSize(0.75f);
        ThemeSnapshot theme = CreateTheme(sheet);
        MarkdownLayoutContext context = CreateContext(theme, FlowDirection.RightToLeft);
        var state = new SafeHtmlInlineState(new SafeHtmlRenderPolicy(
            enableLinks: true,
            enableImages: false,
            renderUnknownElementsLiterally: false,
            allowedStyleClasses: [ScriptClass]));
        Assert.Null(state.Process(new HtmlInline($"<sup class=\"{ScriptClass}\">"), context));
        Assert.Null(state.Process(new HtmlInline("<sup>"), context));

        var sourceLink = new LinkRun("citation", "https://example.test/citation");
        InlineRun linkedScript = Assert.IsType<LinkRun>(state.Apply(sourceLink));

        ElementStyle actual = ResolveRunStyle(context, MarkdownElementKeys.Heading2, linkedScript);
        Assert.Equal(13.5f, actual.FontSize);
        Assert.Equal((ushort)600, actual.FontWeight.Weight);
        Assert.Equal(linkColor, actual.Foreground);
        Assert.True(actual.Underline);
        Assert.Equal(2, linkedScript.ScriptStyleAncestryKeys.Count);
        Assert.Contains(MarkdownElementKeys.Class(ScriptClass), linkedScript.StyleAliases);
    }

    [Fact]
    public void MalformedHtmlCrossingAndMarkdownScopeDisposalPreserveOnlyLiveScriptTokens()
    {
        MarkdownLayoutContext context = CreateContext(
            CreateTheme(RelativeSheet()),
            FlowDirection.LeftToRight);
        var state = new SafeHtmlInlineState(new SafeHtmlRenderPolicy(
            enableLinks: true,
            enableImages: false,
            renderUnknownElementsLiterally: false,
            allowedStyleClasses: null));

        Assert.Null(state.Process(new HtmlInline("<sub>"), context));
        SafeHtmlInlineState.ScriptStyleScope markdownSup =
            state.PushMarkdownScript(MarkdownElementKeys.Superscript);
        Assert.Null(state.Process(new HtmlInline("<sup>"), context));
        Assert.Null(state.Process(new HtmlInline("</sup>"), context));

        LinkRun afterInnerHtmlClose = Assert.IsType<LinkRun>(
            state.Apply(new LinkRun("x", "https://example.test/")));
        Assert.Equal(
            new[] { MarkdownElementKeys.Subscript, MarkdownElementKeys.Superscript },
            afterInnerHtmlClose.ScriptStyleAncestryKeys);

        markdownSup.Dispose();
        LinkRun afterMarkdownDispose = Assert.IsType<LinkRun>(
            state.Apply(new LinkRun("y", "https://example.test/")));
        Assert.Equal(
            new[] { MarkdownElementKeys.Subscript },
            afterMarkdownDispose.ScriptStyleAncestryKeys);

        // A misnested closing tag removes the HTML scopes it crosses, but it
        // must not erase a live Markdown token that was interleaved between them.
        Assert.Null(state.Process(new HtmlInline("<sup>"), context));
        SafeHtmlInlineState.ScriptStyleScope markdownSub =
            state.PushMarkdownScript(MarkdownElementKeys.Subscript);
        Assert.Null(state.Process(new HtmlInline("<span>"), context));
        Assert.Null(state.Process(new HtmlInline("</sub>"), context));
        LinkRun afterMalformedCrossing = Assert.IsType<LinkRun>(
            state.Apply(new LinkRun("z", "https://example.test/")));
        Assert.Equal(
            new[] { MarkdownElementKeys.Subscript },
            afterMalformedCrossing.ScriptStyleAncestryKeys);

        markdownSub.Dispose();
        LinkRun followingSibling = Assert.IsType<LinkRun>(
            state.Apply(new LinkRun("sibling", "https://example.test/")));
        Assert.Empty(followingSibling.ScriptStyleAncestryKeys);
        Assert.Empty(GetScriptAncestry(state));
    }

    [Fact]
    public void PlainAndNonScriptHtmlDoNotAllocateAncestryAndSiblingScopesReuseIt()
    {
        MarkdownLayoutContext context = CreateContext(
            CreateTheme(RelativeSheet()),
            FlowDirection.LeftToRight);
        var state = new SafeHtmlInlineState(new SafeHtmlRenderPolicy(
            enableLinks: true,
            enableImages: false,
            renderUnknownElementsLiterally: false,
            allowedStyleClasses: null));

        Assert.Null(state.ScriptStyleAncestry);
        Assert.Null(state.Process(new HtmlInline("<strong>"), context));
        Assert.IsType<StrongRun>(state.Apply(new TextRun("ordinary text")));
        Assert.Null(state.Process(new HtmlInline("</strong>"), context));
        Assert.Null(state.ScriptStyleAncestry);

        SafeHtmlInlineState.ScriptStyleScope first =
            state.PushMarkdownScript(MarkdownElementKeys.Superscript);
        IReadOnlyList<SafeHtmlInlineState.ScriptStyleAncestryToken> ancestry = GetScriptAncestry(state);
        Assert.Single(ancestry);
        first.Dispose();
        Assert.Same(ancestry, state.ScriptStyleAncestry);
        Assert.Empty(ancestry);

        SafeHtmlInlineState.ScriptStyleScope sibling =
            state.PushMarkdownScript(MarkdownElementKeys.Subscript);
        Assert.Same(ancestry, state.ScriptStyleAncestry);
        Assert.Equal([MarkdownElementKeys.Subscript],
            Assert.IsType<TextRun>(state.Apply(new TextRun("script sibling")))
                .ScriptStyleAncestryKeys);
        sibling.Dispose();
        Assert.Same(ancestry, state.ScriptStyleAncestry);
        Assert.Empty(ancestry);
    }

    [Fact]
    public void StandaloneTypedScriptRunsDoNotRequireAncestryList()
    {
        var state = new SafeHtmlInlineState(policy: null);

        SuperscriptRun superscript = Assert.IsType<SuperscriptRun>(
            state.Apply(new SuperscriptRun("1")));
        LinkRun linkedSuperscript = Assert.IsType<LinkRun>(
            state.Apply(new LinkRun("2", "https://example.test/") { IsSuperscript = true }));

        Assert.Equal([MarkdownElementKeys.Superscript], superscript.ScriptStyleAncestryKeys);
        Assert.Equal([MarkdownElementKeys.Superscript], linkedSuperscript.ScriptStyleAncestryKeys);
        Assert.Null(state.ScriptStyleAncestry);
    }

    private static IReadOnlyList<SafeHtmlInlineState.ScriptStyleAncestryToken> GetScriptAncestry(
        SafeHtmlInlineState state)
        => Assert.IsAssignableFrom<IReadOnlyList<SafeHtmlInlineState.ScriptStyleAncestryToken>>(
            state.ScriptStyleAncestry);

    [Fact]
    public void MarkdownScriptOutsideSafeHtmlScriptUsesSourceOrderForLinkedRun()
    {
        const string source = "^<sub><a href=\"https://example.test/\">x</a></sub>^";
        MarkdownStyleSheet sheet = RelativeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
            new ElementStyleOverride { FontSize = 11f }));

        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, sheet);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        LinkRun link = Assert.Single(box.Runs.OfType<LinkRun>());

        Assert.Equal(
            new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript },
            link.ScriptStyleAncestryKeys);
        ElementStyle resolved = ResolveRunStyle(MarkdownElementKeys.Heading2, link, sheet);
        Assert.Equal(8.25f, resolved.FontSize);
        Assert.True(resolved.Underline);
        Assert.Equal(Color.FromArgb(255, 9, 105, 218), resolved.Foreground);
    }

    [Fact]
    public void SafeHtmlScriptOutsideMarkdownScriptUsesSourceOrderForLinkedRun()
    {
        const string source = "<sub>^[x](https://example.test/)^</sub>";
        MarkdownStyleSheet sheet = RelativeSheet().WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
            new ElementStyleOverride { FontSize = 11f }));

        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, sheet);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        LinkRun link = Assert.Single(box.Runs.OfType<LinkRun>());

        Assert.Equal(
            new[] { MarkdownElementKeys.Subscript, MarkdownElementKeys.Superscript },
            link.ScriptStyleAncestryKeys);
        ElementStyle resolved = ResolveRunStyle(MarkdownElementKeys.Heading2, link, sheet);
        Assert.Equal(11f, resolved.FontSize);
        Assert.True(resolved.Underline);
        Assert.Equal(Color.FromArgb(255, 9, 105, 218), resolved.Foreground);
    }

    [Fact]
    public void ExplicitAbsoluteScriptRuleAfterRelativeRuleWinsAndClearsEffectiveMarker()
    {
        MarkdownStyleSheet sheet = RelativeSheet()
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Subscript)),
                new ElementStyleOverride { FontSize = 9f }))
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
                new ElementStyleOverride { FontSize = 11f }));
        ThemeSnapshot theme = CreateTheme(sheet);

        ElementStyle resolvedSuperscript = theme.GetStyle(MarkdownElementKeys.Superscript);
        ElementStyle resolvedSubscript = theme.GetStyle(MarkdownElementKeys.Subscript);
        ElementStyle actual = ResolveRunStyle(
            MarkdownElementKeys.Heading2,
            new SuperscriptRun("x"),
            sheet);

        Assert.Equal(11f, actual.FontSize);
        Assert.Equal(11f, resolvedSuperscript.FontSize);
        Assert.Null(resolvedSuperscript.RelativeFontSizeScale);
        Assert.Null(resolvedSubscript.RelativeFontSizeScale);
        Assert.True(theme.HasRelativeScriptFontSizeRules);
    }

    [Fact]
    public void TextScaleAndHighContrastPreserveRelativeSizingAndMandatoryColors()
    {
        ThemeSnapshot scaledTheme = CreateTheme(RelativeSheet(), textScale: 1.5);
        ElementStyle scaled = ResolveRunStyle(
            MarkdownElementKeys.Heading2,
            new SuperscriptRun("x"),
            RelativeSheet(),
            textScale: 1.5);

        Assert.Equal(27f, scaled.FontSize);
        Assert.Equal(36f, scaledTheme.GetStyle(MarkdownElementKeys.Heading2).FontSize);

        Color mandatoryForeground = Color.FromArgb(255, 255, 255, 0);
        MarkdownStyleSheet highContrastSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Link),
                new ElementStyleOverride { Foreground = Color.FromArgb(255, 0, 80, 255), Underline = false }),
        ]).WithRelativeScriptFontSize(0.75f);
        ThemeSnapshot highContrastTheme = CreateTheme(
            highContrastSheet,
            highContrast: true,
            mandatoryForeground: mandatoryForeground);
        var linkRun = new LinkRun("x", "https://example.test") { IsSuperscript = true };
        ElementStyle highContrast = ResolveRunStyle(
            MarkdownElementKeys.Heading2,
            linkRun,
            highContrastTheme);

        Assert.Equal(18f, highContrast.FontSize);
        Assert.Equal(mandatoryForeground, highContrast.Foreground);
        Assert.True(highContrast.Underline);
    }

    [Fact]
    public void FingerprintIncludesRelativePolicyEvenWhenAbsoluteRulesMaskItsMarker()
    {
        MarkdownStyleSheet absoluteSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Subscript)),
                new ElementStyleOverride { FontSize = 11f }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
                new ElementStyleOverride { FontSize = 11f }),
        ]);
        MarkdownStyleSheet relativeThenAbsoluteSheet = RelativeSheet()
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Subscript)),
                new ElementStyleOverride { FontSize = 11f }))
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
                new ElementStyleOverride { FontSize = 11f }));
        ThemeSnapshot baseline = CreateTheme(absoluteSheet);
        ThemeSnapshot overridden = CreateTheme(relativeThenAbsoluteSheet);
        ThemeSnapshot relative = CreateTheme(RelativeSheet());
        ThemeSnapshot legacy = CreateTheme(MarkdownStyleSheet.Empty);

        Assert.NotEqual(baseline.LayoutFingerprint, overridden.LayoutFingerprint);
        Assert.NotEqual(legacy.LayoutFingerprint, overridden.LayoutFingerprint);
        Assert.Equal(legacy.LayoutFingerprint, baseline.LayoutFingerprint);
        Assert.NotEqual(baseline.LayoutFingerprint, relative.LayoutFingerprint);
        Assert.True(overridden.HasRelativeScriptFontSizeRules);
        Assert.False(CreateTheme(MarkdownStyleSheet.Empty).HasRelativeScriptFontSizeRules);
        Assert.Equal(
            11f,
            ResolveRunStyle(MarkdownElementKeys.Heading2, new SuperscriptRun("x"), MarkdownStyleSheet.Empty).FontSize);
    }

    [Fact]
    public void DeepRelativeNestingHasPositiveFloorAndDoesNotMutateLegacyStyles()
    {
        string[] nestedScripts = Enumerable.Repeat(MarkdownElementKeys.Superscript, 64).ToArray();
        var run = new SuperscriptRun("x")
        {
            ScriptStyleAncestryKeys = nestedScripts,
            SafeHtmlScriptRoleIsRunElement = true,
        };
        ElementStyle relative = ResolveRunStyle(MarkdownElementKeys.Heading2, run, RelativeSheet());
        ElementStyle smallestScale = ResolveRunStyle(
            MarkdownElementKeys.Heading2,
            new SuperscriptRun("x")
            {
                ScriptStyleAncestryKeys = nestedScripts,
                SafeHtmlScriptRoleIsRunElement = true,
            },
            MarkdownStyleSheet.Empty.WithRelativeScriptFontSize(float.Epsilon));
        ElementStyle legacy = ResolveRunStyle(
            MarkdownElementKeys.Heading2,
            new SuperscriptRun("x"),
            MarkdownStyleSheet.Empty);

        Assert.Equal(ThemeSnapshot.MinimumRelativeScriptFontSize, relative.FontSize);
        Assert.Equal(ThemeSnapshot.MinimumRelativeScriptFontSize, smallestScale.FontSize);
        Assert.True(relative.FontSize > 0f);
        Assert.Equal(11f, legacy.FontSize);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("2")]
    public void SegoeUiSuperscriptFeaturePreservesNominalLayoutAndReportsInkGeometry(string glyph)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 18f,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        using var plainSameSize = new CanvasTextLayout(device, glyph, format, 128f, float.MaxValue);
        using var nativeFeature = new CanvasTextLayout(device, glyph, format, 128f, float.MaxValue);
        var typography = new CanvasTypography();
        typography.AddFeature(new CanvasTypographyFeature
        {
            Name = CanvasTypographyFeatureName.Superscript,
            Parameter = 1,
        });
        nativeFeature.SetTypography(0, glyph.Length, typography);

        CanvasTextLayoutRegion plainRegion = Assert.Single(plainSameSize.GetCharacterRegions(0, glyph.Length));
        CanvasTextLayoutRegion nativeRegion = Assert.Single(nativeFeature.GetCharacterRegions(0, glyph.Length));
        CanvasLineMetrics plainLine = Assert.Single(plainSameSize.LineMetrics);
        CanvasLineMetrics nativeLine = Assert.Single(nativeFeature.LineMetrics);
        _output.WriteLine(
            $"glyph={glyph}; nominalSize=18; regular-glyph-layout region={Format(plainRegion.LayoutBounds)} " +
            $"ink={Format(plainSameSize.DrawBounds)} line={plainLine.Height:R}; " +
            $"same-size-sups region={Format(nativeRegion.LayoutBounds)} " +
            $"ink={Format(nativeFeature.DrawBounds)} line={nativeLine.Height:R}");

        // This is a nominal-size/font-feature diagnostic, not a simulation of
        // CSS font-size: smaller or vertical-align: super. CSS smaller is a
        // keyword; browser computed-size and baseline behavior are not measured here.
        Assert.Equal(18f, format.FontSize);
        Assert.Equal(plainLine.Height, nativeLine.Height);
        Assert.Equal(plainRegion.LayoutBounds.Height, nativeRegion.LayoutBounds.Height);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-0.25f)]
    [InlineData(1.0001f)]
    public void RelativeScaleRejectsUnsupportedValues(float scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownStyleSheet.Empty.WithRelativeScriptFontSize(scale));

    private static string Format(Windows.Foundation.Rect bounds)
        => $"({bounds.X:R},{bounds.Y:R},{bounds.Width:R},{bounds.Height:R})";
}
