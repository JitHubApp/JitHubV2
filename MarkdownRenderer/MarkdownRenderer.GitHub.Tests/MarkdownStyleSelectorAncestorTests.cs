using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class MarkdownStyleSelectorAncestorTests
{
    private static readonly Color BaseForeground = Color.FromArgb(0xFF, 0x1F, 0x23, 0x28);
    private static readonly Color QuoteForeground = Color.FromArgb(0xFF, 0x59, 0x63, 0x6E);
    private static readonly Color LaterForeground = Color.FromArgb(0xFF, 0x77, 0x22, 0x99);
    private static readonly Color LinkForeground = Color.FromArgb(0xFF, 0x09, 0x69, 0xDA);
    private static readonly Color HeadingForeground = Color.FromArgb(0xFF, 0x82, 0x2A, 0x6B);
    private static readonly Color HighContrastForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
    private static readonly Color HighContrastBackground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    private static readonly MethodInfo GetRunStyleMethod = typeof(InlineContainerBox).GetMethod(
        "GetRunStyle",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetContainerStyleMethod = typeof(InlineContainerBox).GetMethod(
        "GetContainerStyle",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetLinkInlineSpanStylesMethod = typeof(InlineContainerBox).GetMethod(
        "GetLinkInlineSpanStyles",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void ExistingPublicConstructorSignaturesRemainAvailable()
    {
        Assert.NotNull(typeof(MarkdownStyleContext).GetConstructor(
            [typeof(MarkdownStyleRole), typeof(int), typeof(string), typeof(string), typeof(IEnumerable<string>)]));
        Assert.NotNull(typeof(MarkdownStyleSelector).GetConstructor(
            [typeof(MarkdownStyleRole), typeof(int), typeof(int), typeof(string), typeof(string), typeof(string)]));
    }

    [Fact]
    public void AncestorRoleIsTypedOrdinalAndDefensivelySnapshotted()
    {
        var ancestors = new[] { MarkdownStyleRole.Quote, MarkdownStyleRole.List };
        var context = new MarkdownStyleContext(
            MarkdownStyleRole.Body,
            ancestors,
            nestingDepth: 2,
            language: " CSharp ",
            state: " Active ",
            classNames: [".note"]);
        var selector = new MarkdownStyleSelector(
            MarkdownStyleRole.Body,
            MarkdownStyleRole.Quote,
            minimumNestingDepth: 2,
            maximumNestingDepth: 3,
            language: "csharp",
            state: "active",
            className: "note");

        ancestors[0] = MarkdownStyleRole.Link;

        Assert.Equal(new[] { MarkdownStyleRole.Quote, MarkdownStyleRole.List }, context.AncestorRoles);
        Assert.Equal(MarkdownStyleRole.Quote, selector.AncestorRole);
        Assert.True(selector.Matches(context));
        Assert.False(new MarkdownStyleSelector(MarkdownStyleRole.Body, MarkdownStyleRole.FromElementKey("quote"))
            .Matches(context));
        Assert.False(new MarkdownStyleSelector(MarkdownStyleRole.Body, MarkdownStyleRole.Quote, className: "other")
            .Matches(context));
        Assert.False(new MarkdownStyleSelector(MarkdownStyleRole.Body, MarkdownStyleRole.Quote, minimumNestingDepth: 3)
            .Matches(context));
    }

    [Fact]
    public void EmptyAncestorRolesAreRejectedAndLegacyContextsRemainUnqualified()
    {
        Assert.Throws<ArgumentException>(() => new MarkdownStyleContext(
            MarkdownStyleRole.Body,
            [default(MarkdownStyleRole)]));
        Assert.Throws<ArgumentException>(() => new MarkdownStyleSelector(
            MarkdownStyleRole.Body,
            default(MarkdownStyleRole)));

        var legacyContext = new MarkdownStyleContext(MarkdownStyleRole.Body);
        Assert.Empty(legacyContext.AncestorRoles);
        Assert.Null(new MarkdownStyleSelector(MarkdownStyleRole.Body).AncestorRole);
    }

    [Fact]
    public void CompiledSnapshotMatchesAncestorStackWithoutTreatingAliasesAsAncestors()
    {
        MarkdownStyleSheet sheet = CreateQuoteSheet();
        ThemeSnapshot snapshot = CreateSnapshot(sheet);
        string[] quoteAndListContext = [MarkdownStyleRole.Quote.Name, "ListDepth1"];
        string[] listContext = ["ListDepth1"];
        string[] quoteAlias = ["Quote"];

        Assert.Equal(QuoteForeground, snapshot.GetStyle(MarkdownElementKeys.Body, quoteAndListContext, null).Foreground);
        Assert.Equal(BaseForeground, snapshot.GetStyle(MarkdownElementKeys.Body, listContext, null).Foreground);
        Assert.Equal(BaseForeground, snapshot.GetStyle(MarkdownElementKeys.Body, listContext, quoteAlias).Foreground);

        MarkdownStyleSheet laterOverride = sheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Body),
            new ElementStyleOverride { Foreground = LaterForeground }));
        ThemeSnapshot laterSnapshot = CreateSnapshot(laterOverride);
        Assert.Equal(LaterForeground,
            laterSnapshot.GetStyle(MarkdownElementKeys.Body, quoteAndListContext, null).Foreground);
    }

    [Fact]
    public void CompiledAncestorMatchingExcludesListDepthMetadataButKeepsCustomRoles()
    {
        MarkdownStyleRole listDepthRole = MarkdownStyleRole.FromElementKey("ListDepth1");
        MarkdownStyleRole zeroPaddedListDepthRole = MarkdownStyleRole.FromElementKey("ListDepth01");
        MarkdownStyleRole malformedListDepthRole = MarkdownStyleRole.FromElementKey("ListDepthOne");
        MarkdownStyleSheet sheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body),
                new ElementStyleOverride { Foreground = BaseForeground }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.Body,
                    minimumNestingDepth: 1,
                    maximumNestingDepth: 1),
                new ElementStyleOverride { Foreground = QuoteForeground }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body, listDepthRole),
                new ElementStyleOverride { Foreground = LaterForeground }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body, zeroPaddedListDepthRole),
                new ElementStyleOverride { Foreground = HeadingForeground }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body, malformedListDepthRole),
                new ElementStyleOverride { Foreground = LinkForeground }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(sheet);

        Assert.Equal(
            QuoteForeground,
            snapshot.GetStyle(MarkdownElementKeys.Body, ["ListDepth1"], null).Foreground);
        Assert.Equal(
            QuoteForeground,
            snapshot.GetStyle(MarkdownElementKeys.Body, ["ListDepth01"], null).Foreground);
        Assert.Equal(
            LinkForeground,
            snapshot.GetStyle(MarkdownElementKeys.Body, ["ListDepthOne"], null).Foreground);
    }

    [Fact]
    public void WarmCompiledAncestorSelectorCacheHitAllocatesNoPerHitObjects()
    {
        ThemeSnapshot snapshot = CreateSnapshot(CreateQuoteSheet());
        string[] context = [MarkdownStyleRole.Quote.Name];
        Assert.Equal(QuoteForeground, snapshot.GetStyle(MarkdownElementKeys.Body, context, null).Foreground);

        // Warm the call path and JIT before reading the per-thread allocation counter.
        for (int i = 0; i < 8; i++)
            _ = snapshot.GetStyle(MarkdownElementKeys.Body, context, null);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            _ = snapshot.GetStyle(MarkdownElementKeys.Body, context, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void HighContrastStillOverridesInheritedForegroundAfterCompiledMatching()
    {
        ThemeSnapshot snapshot = CreateSnapshot(CreateQuoteSheet(), highContrast: true);
        ElementStyle resolved = snapshot.GetStyle(
            MarkdownElementKeys.Body,
            [MarkdownStyleRole.Quote.Name],
            null);

        Assert.Equal(HighContrastForeground, resolved.Foreground);
        Assert.Equal(HighContrastBackground, resolved.Background);
    }

    [Theory]
    [InlineData("> [**bold** *italic* `code`](https://example.test/link)")]
    [InlineData("> ## **bold** *italic* `code`")]
    public void QuoteForegroundSelectorsPreserveCloserLinkAndHeadingForegrounds(string source)
    {
        using LayoutSnapshot snapshot = BuildSnapshot(source);
        StackBox quote = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        InlineContainerBox inline = Assert.Single(quote.Children.OfType<InlineContainerBox>());
        if (source.Contains("](", StringComparison.Ordinal))
        {
            LinkRun link = Assert.Single(inline.Runs.OfType<LinkRun>());
            Assert.Equal(LinkForeground, ResolveRunStyle(inline, link).Foreground);
            LinkInlineStyleMap map = Assert.IsType<LinkInlineStyleMap>(link.InlineStyleMap);
            ElementStyle[] spanStyles = Assert.IsType<ElementStyle[]>(
                GetLinkInlineSpanStylesMethod.Invoke(inline, [link]));

            Assert.Equal(LinkForeground, GetNestedSpanStyle(map, spanStyles, MarkdownElementKeys.Strong).Foreground);
            Assert.Equal(LinkForeground, GetNestedSpanStyle(map, spanStyles, MarkdownElementKeys.Emphasis).Foreground);
            Assert.Equal(LinkForeground, GetNestedSpanStyle(map, spanStyles, MarkdownElementKeys.CodeInline).Foreground);
        }
        else
        {
            Assert.Equal(MarkdownElementKeys.Heading2, inline.ElementKey);
            InlineRun boldRun = Assert.Single(inline.Runs, static run => run.Text == "bold");
            InlineRun italicRun = Assert.Single(inline.Runs, static run => run.Text == "italic");
            InlineRun codeRun = Assert.Single(inline.Runs, static run => run.Text == "code");

            Assert.True(boldRun.HasStyleModifier(MarkdownElementKeys.Strong));
            Assert.True(italicRun.HasStyleModifier(MarkdownElementKeys.Emphasis));
            Assert.Equal(MarkdownElementKeys.CodeInline, codeRun.ElementKey);
            Color headingForeground = ResolveContainerStyle(inline).Foreground;
            Assert.Equal(headingForeground, ResolveRunStyle(inline, boldRun).Foreground);
            Assert.Equal(headingForeground, ResolveRunStyle(inline, italicRun).Foreground);
            Assert.Equal(headingForeground, ResolveRunStyle(inline, codeRun).Foreground);
        }
    }

    private static MarkdownStyleSheet CreateQuoteSheet()
        => new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body),
                new ElementStyleOverride { Foreground = BaseForeground }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body, MarkdownStyleRole.Quote),
                new ElementStyleOverride { Foreground = QuoteForeground }),
        ]);

    private static LayoutSnapshot BuildSnapshot(string source)
    {
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new() { FontFamily = "Segoe UI", FontSize = 16, Foreground = BaseForeground },
            [MarkdownElementKeys.Quote] = new() { FontFamily = "Segoe UI", FontSize = 16, Foreground = BaseForeground },
            [MarkdownElementKeys.Strong] = new() { FontFamily = "Segoe UI", FontSize = 16, Foreground = BaseForeground },
            [MarkdownElementKeys.Emphasis] = new() { FontFamily = "Segoe UI", FontSize = 16, Foreground = BaseForeground },
            [MarkdownElementKeys.CodeInline] = new() { FontFamily = "Cascadia Mono", FontSize = 13, Foreground = BaseForeground },
            [MarkdownElementKeys.Link] = new() { FontFamily = "Segoe UI", FontSize = 16, Foreground = LinkForeground },
            [MarkdownElementKeys.Heading2] = new() { FontFamily = "Segoe UI", FontSize = 24, Foreground = HeadingForeground },
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xFF, 0, 0, 0),
            Color.FromArgb(0xFF, 0, 0, 0),
            Color.FromArgb(0xFF, 0, 0, 0),
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: GitHubReadmeVisualStyleProfiles.PrimerLightV6.StyleSheet);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context).Build(parsed, availableWidth: 640);
    }

    private static ElementStyle ResolveRunStyle(InlineContainerBox inline, InlineRun run)
        => Assert.IsType<ElementStyle>(GetRunStyleMethod.Invoke(inline, [run]));

    private static ElementStyle ResolveContainerStyle(InlineContainerBox inline)
        => Assert.IsType<ElementStyle>(GetContainerStyleMethod.Invoke(inline, null));

    private static ElementStyle GetNestedSpanStyle(
        LinkInlineStyleMap map,
        ElementStyle[] styles,
        string expectedStyleKey)
    {
        for (int index = 0; index < map.Count; index++)
        {
            LinkInlineStyleSpan span = map.GetSpan(index);
            for (LinkInlineStylePath? path = span.Path; path is not null; path = path.Parent)
            {
                if (string.Equals(path.StyleKey, expectedStyleKey, StringComparison.Ordinal))
                    return styles[index];
            }
        }

        throw new Xunit.Sdk.XunitException($"No link span carried style key '{expectedStyleKey}'.");
    }

    private static ThemeSnapshot CreateSnapshot(MarkdownStyleSheet sheet, bool highContrast = false)
    {
        Color baseBackground = highContrast ? HighContrastBackground : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color baseForeground = highContrast ? HighContrastForeground : BaseForeground;
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new()
            {
                FontFamily = "Segoe UI",
                FontSize = 16,
                Foreground = baseForeground,
                Background = baseBackground,
            },
        };

        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            baseBackground,
            HighContrastForeground,
            baseBackground,
            HighContrastForeground,
            isDark: false,
            isHighContrast: highContrast,
            textScaleFactor: 1,
            styleSheet: sheet);
    }
}
