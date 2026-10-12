using System.Reflection;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
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

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RelativeScriptFontSizeTests
{
    private const string ScriptClass = "script-scale";
    private readonly ITestOutputHelper _output;
    private static readonly MethodInfo GetRunStyleMethod = typeof(InlineContainerBox).GetMethod(
        "GetRunStyle",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetInnermostScriptStyleKeyMethod = typeof(InlineContainerBox).GetMethod(
        "GetInnermostScriptStyleKey",
        BindingFlags.Static | BindingFlags.NonPublic)!;

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
    }

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
        Assert.Equal(
            MarkdownElementKeys.Subscript,
            GetInnermostScriptStyleKeyMethod.Invoke(null, [link]));
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
        Assert.Equal(
            MarkdownElementKeys.Superscript,
            GetInnermostScriptStyleKeyMethod.Invoke(null, [link]));
        ElementStyle resolved = ResolveRunStyle(MarkdownElementKeys.Heading2, link, sheet);
        Assert.Equal(11f, resolved.FontSize);
        Assert.True(resolved.Underline);
        Assert.Equal(Color.FromArgb(255, 9, 105, 218), resolved.Foreground);
    }

    [Fact]
    public void RelativeProfilePreservesUnlinkedNestedScriptsAndRestoresSiblingScope()
    {
        const string source = "before ^<sub>x</sub>^ after";

        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun nested = Assert.Single(box.Runs, static run => run.Text == "x");

        Assert.Equal(
            new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript },
            nested.ScriptStyleAncestryKeys);
        Assert.Equal(9f, ResolveRunStyle(MarkdownElementKeys.Body, nested, RelativeSheet()).FontSize);
        Assert.All(
            box.Runs.Where(run => !ReferenceEquals(run, nested)),
            static run => Assert.Empty(run.ScriptStyleAncestryKeys));
    }

    [Fact]
    public void RelativeProfilePreservesBoldAroundUnlinkedSafeHtmlSuperscript()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot("**<sup>x</sup>**", RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun nested = Assert.Single(box.Runs, static run => run.Text == "x");

        Assert.Equal([MarkdownElementKeys.Superscript], nested.ScriptStyleAncestryKeys);
        Assert.Contains(MarkdownElementKeys.Strong, nested.StyleModifierKeys);
        ElementStyle resolved = ResolveRunStyle(MarkdownElementKeys.Body, nested, RelativeSheet());
        Assert.Equal(12f, resolved.FontSize);
        Assert.Equal((ushort)700, resolved.FontWeight.Weight);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void RelativeProfilePreservesUnlinkedNestedScriptsInGfmChildBuilder(string container)
    {
        string source = container switch
        {
            "table" => "| cell |\n| --- |\n| ^<sub>x</sub>^ |",
            "task-list" => "- [x] **<sup>x</sup>**",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        InlineRun nested = Assert.Single(content.Runs, static run => run.Text == "x");

        Assert.Equal(
            container == "table"
                ? new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript }
                : new[] { MarkdownElementKeys.Superscript },
            nested.ScriptStyleAncestryKeys);
        if (container == "table")
        {
            Assert.Equal(9f, ResolveRunStyle(MarkdownElementKeys.TableCell, nested, RelativeSheet()).FontSize);
        }
        else
        {
            Assert.Contains(MarkdownElementKeys.Strong, nested.StyleModifierKeys);
            Assert.Equal(12f, ResolveRunStyle(MarkdownElementKeys.Body, nested, RelativeSheet()).FontSize);
        }
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void GfmContainingLinkPreservesScriptMetadataWhenWrappingRuns(string container)
    {
        const string linkedContent = "[^x^ ![icon](https://example.test/icon.png)](https://example.test/outer)";
        string source = container switch
        {
            "table" => $"| header |\n| --- |\n| {linkedContent} |",
            "task-list" => $"- [x] {linkedContent}",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        LinkRun script = Assert.Single(content.Runs.OfType<LinkRun>(), static run => run.Text == "x");

        Assert.Equal("https://example.test/outer", script.Url);
        Assert.Contains(content.Runs, static run => run is InlineImageRun);
        Assert.Equal([MarkdownElementKeys.Superscript], script.ScriptStyleAncestryKeys);
        Assert.False(script.SafeHtmlScriptRoleIsRunElement);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void GfmContainingLinkPreservesMixedScriptOrderWhenWrappingRuns(string container)
    {
        const string linkedContent = "[^<sub>x</sub>^ ![icon](https://example.test/icon.png)](https://example.test/outer)";
        string source = container switch
        {
            "table" => $"| header |\n| --- |\n| {linkedContent} |",
            "task-list" => $"- [x] {linkedContent}",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        LinkRun script = Assert.Single(content.Runs.OfType<LinkRun>(), static run => run.Text == "x");

        Assert.Equal("https://example.test/outer", script.Url);
        Assert.Contains(content.Runs, static run => run is InlineImageRun);
        Assert.Equal(
            new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript },
            script.ScriptStyleAncestryKeys);
        Assert.True(script.SafeHtmlScriptRoleIsRunElement);
        string containerRole = container == "table"
            ? MarkdownElementKeys.TableCell
            : MarkdownElementKeys.Body;
        Assert.Equal(9f, ResolveRunStyle(containerRole, script, RelativeSheet()).FontSize);
    }

    [Fact]
    public void LegacyProfileKeepsNoLinkEmphasisFlatteningUnchanged()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "before ^<sub>x</sub>^ after",
            MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        SuperscriptRun flattened = Assert.Single(box.Runs.OfType<SuperscriptRun>());

        Assert.Equal("x", flattened.Text);
        Assert.Equal([MarkdownElementKeys.Superscript], flattened.ScriptStyleAncestryKeys);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RelativeRecursionHonorsSafeHtmlLinkPolicy(
        bool enableSafeHtmlLinks,
        bool expectedLink)
    {
        const string source = "^<sub><a href=\"https://example.test/\">x</a></sub>^";
        SafeHtmlOptions? safeHtmlOptions = enableSafeHtmlLinks
            ? null
            : new SafeHtmlOptions(enableLinks: false, enableImages: false);

        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            safeHtmlOptions);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());

        Assert.Equal(expectedLink, box.Runs.OfType<LinkRun>().Any());
    }

    [Fact]
    public void RelativeRecursionDoesNotPromoteUnsafeSafeHtmlLinkSchemes()
    {
        const string source = "^<sub><a href=\"javascript:alert(1)\">x</a></sub>^";

        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());

        Assert.Empty(box.Runs.OfType<LinkRun>());
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
    [InlineData(true)]
    [InlineData(false)]
    public void InnermostScriptBaselineFollowsRunElementAfterInheritedModifier(bool runIsSuperscript)
    {
        InlineRun run = runIsSuperscript
            ? new SuperscriptRun("x") { StyleModifierKeys = [MarkdownElementKeys.Subscript] }
            : new SubscriptRun("x") { StyleModifierKeys = [MarkdownElementKeys.Superscript] };
        string expected = runIsSuperscript
            ? MarkdownElementKeys.Superscript
            : MarkdownElementKeys.Subscript;

        Assert.Equal(expected, GetInnermostScriptStyleKeyMethod.Invoke(null, [run]));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-0.25f)]
    [InlineData(1.0001f)]
    public void RelativeScaleRejectsUnsupportedValues(float scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownStyleSheet.Empty.WithRelativeScriptFontSize(scale));

    private static MarkdownStyleSheet RelativeSheet()
        => MarkdownStyleSheet.Empty.WithRelativeScriptFontSize(0.75f);

    private static string Format(Windows.Foundation.Rect bounds)
        => $"({bounds.X:R},{bounds.Y:R},{bounds.Width:R},{bounds.Height:R})";

    private static ElementStyle ResolveRunStyle(
        string containerRole,
        InlineRun run,
        MarkdownStyleSheet sheet,
        double textScale = 1,
        bool highContrast = false,
        Color? mandatoryForeground = null)
        => ResolveRunStyle(
            CreateContext(CreateTheme(sheet, textScale, highContrast, mandatoryForeground), FlowDirection.LeftToRight),
            containerRole,
            run);

    private static ElementStyle ResolveRunStyle(
        string containerRole,
        InlineRun run,
        ThemeSnapshot theme)
        => ResolveRunStyle(CreateContext(theme, FlowDirection.LeftToRight), containerRole, run);

    private static ElementStyle ResolveRunStyle(
        MarkdownLayoutContext context,
        string containerRole,
        InlineRun run)
    {
        var box = new InlineContainerBox(context, containerRole)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            box.Add(run);
            object? result = GetRunStyleMethod.Invoke(box, [run]);
            return Assert.IsType<ElementStyle>(result);
        }
        finally
        {
            box.Dispose();
        }
    }

    private static ThemeSnapshot CreateTheme(
        MarkdownStyleSheet sheet,
        double textScale = 1,
        bool highContrast = false,
        Color? mandatoryForeground = null)
    {
        Color black = Color.FromArgb(255, 0, 0, 0);
        Color foreground = mandatoryForeground ?? black;
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new() { FontFamily = "Segoe UI", FontSize = 16f, Foreground = black },
            [MarkdownElementKeys.Heading1] = new()
            {
                FontFamily = "Segoe UI", FontSize = 32f, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = black,
            },
            [MarkdownElementKeys.Heading2] = new()
            {
                FontFamily = "Segoe UI", FontSize = 24f, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }, Foreground = black,
            },
            [MarkdownElementKeys.Quote] = new() { FontFamily = "Segoe UI", FontSize = 20f, Foreground = black },
            [MarkdownElementKeys.TableCell] = new() { FontFamily = "Segoe UI", FontSize = 16f, Foreground = black },
            [MarkdownElementKeys.Strong] = new()
            {
                FontFamily = "Segoe UI", FontSize = 16f, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = black,
            },
            [MarkdownElementKeys.Link] = new()
            {
                FontFamily = "Segoe UI", FontSize = 16f,
                Foreground = highContrast ? foreground : Color.FromArgb(255, 9, 105, 218),
                Underline = true,
            },
            [MarkdownElementKeys.Subscript] = new() { FontFamily = "Segoe UI", FontSize = 11f, Foreground = foreground },
            [MarkdownElementKeys.Superscript] = new() { FontFamily = "Segoe UI", FontSize = 11f, Foreground = foreground },
        };
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 180, 180, 180),
            foreground,
            foreground,
            isDark: false,
            isHighContrast: highContrast,
            textScaleFactor: textScale,
            styleSheet: sheet);
    }

    private static MarkdownLayoutContext CreateContext(ThemeSnapshot theme, FlowDirection flowDirection)
        => new(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap("script"),
            new MarkdownExtensionRegistry(),
            flowDirection);

    private static LayoutSnapshot BuildParsedSnapshot(
        string source,
        MarkdownStyleSheet sheet,
        SafeHtmlOptions? safeHtmlOptions = null,
        bool useGfmContainerExtensions = false)
    {
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry()
            .ConfigureGitHubReadmeRegistry(safeHtmlOptions);
        // Strict GitHub GFM intentionally enables only strikethrough from
        // EmphasisExtras. These focused source-order cases opt into sub/sup
        // parsing without changing the shipping GitHub profile.
        var pipelineBuilder = new MarkdownPipelineBuilder()
            .UseEmphasisExtras(EmphasisExtraOptions.Subscript | EmphasisExtraOptions.Superscript);
        if (useGfmContainerExtensions)
            pipelineBuilder.UsePipeTables().UseTaskLists();
        var pipeline = pipelineBuilder.Build();
        var document = Markdown.Parse(source, pipeline);
        MarkdownLayoutContext context = new(
            CanvasDevice.GetSharedDevice(),
            CreateTheme(sheet),
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context).Build(document, availableWidth: 640f);
    }

    private static InlineContainerBox GetBodyCell(TableBox table, int row, int column)
    {
        InlineContainerBox[][] bodyCells = Assert.IsType<InlineContainerBox[][]>(
            typeof(TableBox)
                .GetField("_bodyCells", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(table));
        return bodyCells[row][column];
    }

    private static IEnumerable<ListItemBox> EnumerateListItems(IEnumerable<BlockBox> boxes)
    {
        foreach (BlockBox box in boxes)
        {
            if (box is ListItemBox item)
            {
                yield return item;
                foreach (ListItemBox nested in EnumerateListItems(item.Content.Children))
                    yield return nested;
            }
            else if (box is StackBox stack)
            {
                foreach (ListItemBox nested in EnumerateListItems(stack.Children))
                    yield return nested;
            }
        }
    }

}
