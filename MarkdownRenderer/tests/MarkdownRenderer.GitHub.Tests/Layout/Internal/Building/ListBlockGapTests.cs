using System.Collections.Generic;
using System.Linq;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using MarkdownRenderer.Theming.Internal.Environment;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Building;

/// <summary>
/// Checks that a Markdown list ends with the same block gap as a paragraph in
/// the built-in theme, so the block after a list is not drawn flush against
/// its last item, and that nested, loose, task and quoted lists neither lose
/// nor double that gap.
/// </summary>
[Collection("Shared Canvas text format cache")]
public sealed class ListBlockGapTests
{
    private const float LayoutWidth = 600;

    /// <summary>Where a customized paragraph and list gap comes from.</summary>
    public enum Customization
    {
        /// <summary>The built-in Fluent role styles.</summary>
        None,

        /// <summary>
        /// Host resources such as <c>MarkdownRenderer.List.Margin</c>, which
        /// the theme resolver folds into each role's base style.
        /// </summary>
        HostResources,

        /// <summary><c>MarkdownTheme.Overrides</c> entries for the roles.</summary>
        ThemeOverrides,
    }

    public static TheoryData<Customization> Themes => new()
    {
        Customization.None,
        Customization.HostResources,
        Customization.ThemeOverrides,
    };

    public static TheoryData<Customization, string, string> ListShapes
    {
        get
        {
            var data = new TheoryData<Customization, string, string>();
            foreach (Customization customization in Enum.GetValues<Customization>())
            {
                data.Add(customization, "- First item\n- Second item", "Second item");
                data.Add(customization, "1. First item\n2. Second item", "Second item");
                data.Add(customization, "- [ ] Open task\n- [x] Done task", "Done task");
                data.Add(customization, "- First item\n\n- Loose item", "Loose item");
                data.Add(customization, "- Outer item\n  - Nested item\n  - Nested last", "Nested last");
                data.Add(customization, "- Outer item\n\n  - Nested loose\n\n  - Nested loose last", "Nested loose last");
                data.Add(customization, "- Outer item\n  1. Nested ordered\n  2. Nested ordered last", "Nested ordered last");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ParagraphAndHeadingAfterAListUseTheirNormalBlockGap(Customization customization)
    {
        const string source =
            "Paragraph one.\n\n" +
            "Paragraph two.\n\n" +
            "- First item\n" +
            "- Second item\n\n" +
            "A paragraph after the list.\n\n" +
            "- Another list\n" +
            "- End\n\n" +
            "## Heading after a list\n\n" +
            "Paragraph three.\n\n" +
            "## Heading after a paragraph";

        using LayoutSnapshot snapshot = Build(source, customization);

        double paragraphGap = Gap(snapshot, "Paragraph one.", "Paragraph two.");
        double headingGap = Gap(snapshot, "Paragraph three.", "Heading after a paragraph");
        Assert.True(paragraphGap > 0);

        AssertClose(paragraphGap, Gap(snapshot, "Second item", "A paragraph after the list."));
        AssertClose(headingGap, Gap(snapshot, "End", "Heading after a list"));
    }

    [Theory]
    [MemberData(nameof(ListShapes))]
    public void EveryListShapeEndsWithExactlyOneParagraphGap(
        Customization customization,
        string list,
        string lastItemText)
    {
        string source = "Paragraph one.\n\nParagraph two.\n\n" + list + "\n\nAfter the list.";

        using LayoutSnapshot snapshot = Build(source, customization);

        AssertClose(
            Gap(snapshot, "Paragraph one.", "Paragraph two."),
            Gap(snapshot, lastItemText, "After the list."));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void NestedTightListsAddNoGapInsideTheirParentList(Customization customization)
    {
        const string source =
            "- Outer one\n" +
            "  - Inner one\n" +
            "  - Inner two\n" +
            "- Outer two";

        using LayoutSnapshot snapshot = Build(source, customization);
        StackBox outer = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        StackBox inner = Assert.Single(
            Assert.IsType<ListItemBox>(outer.Children[0]).Content.Children.OfType<StackBox>());

        Assert.Equal(0, inner.Margin.Top);
        Assert.Equal(0, inner.Margin.Bottom);
        AssertClose(outer.InterChildSpacing, Gap(snapshot, "Inner two", "Outer two"));
        AssertClose(Gap(snapshot, "Inner one", "Inner two"), Gap(snapshot, "Inner two", "Outer two"));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void NestedListStyleSheetRuleCanStillGiveNestedListsAGap(Customization customization)
    {
        const string source =
            "- Outer one\n" +
            "  - Inner one\n" +
            "  - Inner two\n" +
            "- Outer two";
        var nestedListGap = new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.List, minimumNestingDepth: 2),
            new ElementStyleOverride { Margin = new Thickness(0, 4, 0, 6) });

        using LayoutSnapshot snapshot = Build(source, customization, nestedListGap);
        StackBox outer = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        StackBox inner = Assert.Single(
            Assert.IsType<ListItemBox>(outer.Children[0]).Content.Children.OfType<StackBox>());

        Assert.Equal(4, inner.Margin.Top);
        Assert.Equal(6, inner.Margin.Bottom);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ListInsideABlockQuoteUsesTheQuoteParagraphGap(Customization customization)
    {
        const string source =
            "> Quote one.\n" +
            ">\n" +
            "> Quote two.\n\n" +
            "> - Quoted item\n" +
            "> - Quoted last\n" +
            ">\n" +
            "> After the quoted list.";

        using LayoutSnapshot snapshot = Build(source, customization);

        AssertClose(
            Gap(snapshot, "Quote one.", "Quote two."),
            Gap(snapshot, "Quoted last", "After the quoted list."));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ListEndingABlockQuoteLeavesTheSameTrailingSpaceAsAParagraph(Customization customization)
    {
        using LayoutSnapshot paragraphQuote = Build("> Quoted paragraph.", customization);
        using LayoutSnapshot listQuote = Build("> - Quoted item\n> - Quoted last", customization);

        StackBox paragraphStack = Assert.IsType<StackBox>(Assert.Single(paragraphQuote.Blocks));
        StackBox listStack = Assert.IsType<StackBox>(Assert.Single(listQuote.Blocks));

        AssertClose(
            paragraphStack.Bounds.Bottom - TextBottom(paragraphQuote, "Quoted paragraph."),
            listStack.Bounds.Bottom - TextBottom(listQuote, "Quoted last"));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ListEndingTheDocumentLeavesTheSameTrailingSpaceAsAParagraph(Customization customization)
    {
        using LayoutSnapshot paragraphDocument = Build("Last paragraph.", customization);
        using LayoutSnapshot tightDocument = Build("- Item\n- Last item", customization);
        using LayoutSnapshot looseDocument = Build("- Item\n\n- Last loose item", customization);

        double paragraphTrailing = paragraphDocument.Size.Height -
            TextBottom(paragraphDocument, "Last paragraph.");

        AssertClose(
            paragraphTrailing,
            tightDocument.Size.Height - TextBottom(tightDocument, "Last item"));
        AssertClose(
            paragraphTrailing,
            looseDocument.Size.Height - TextBottom(looseDocument, "Last loose item"));
    }

    private static double Gap(LayoutSnapshot snapshot, string previousText, string followingText)
        => ContentTop(Find(snapshot, followingText)) - TextBottom(snapshot, previousText);

    // The bottom of a paragraph's text, or of its list item's marker when the
    // marker (such as a task check box) reaches lower than the text.
    private static double TextBottom(LayoutSnapshot snapshot, string text)
    {
        InlineContainerBox paragraph = Find(snapshot, text);
        ListItemBox? owner = Flatten(snapshot.Blocks)
            .OfType<ListItemBox>()
            .SingleOrDefault(item => item.Content.Children.Contains(paragraph));
        double bottom = ContentBottom(paragraph);
        return owner is null ? bottom : System.Math.Max(bottom, ContentBottom(owner.Marker));
    }

    private static double ContentTop(BlockBox box) => box.Bounds.Top + box.Margin.Top;

    private static double ContentBottom(BlockBox box) => box.Bounds.Bottom - box.Margin.Bottom;

    private static InlineContainerBox Find(LayoutSnapshot snapshot, string text)
        => Assert.Single(
            Flatten(snapshot.Blocks).OfType<InlineContainerBox>(),
            box => string.Concat(box.Runs.Select(static run => run.Text)).Trim() == text);

    private static IEnumerable<BlockBox> Flatten(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            yield return block;
            IEnumerable<BlockBox> children = block switch
            {
                ListItemBox item => Flatten(item.Content.Children),
                StackBox stack => Flatten(stack.Children),
                _ => [],
            };
            foreach (BlockBox child in children)
                yield return child;
        }
    }

    /// <summary>
    /// Lays out <paramref name="source"/> with the built-in Fluent role styles.
    /// A customized build gives paragraphs and lists a 16-DIP paragraph gap,
    /// from host resources or theme overrides, and gives paragraphs inside
    /// lists a compact 8-DIP style-sheet margin, the way an app customizes them.
    /// </summary>
    private static LayoutSnapshot Build(
        string source,
        Customization customization,
        params MarkdownStyleRule[] extraRules)
    {
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry().ConfigureGfmRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        MarkdownDocument document = MarkdownDocument.FromParsed(source, parsed);
        PlatformThemeColors platformColors = PlatformThemeColors.Resolve(ElementTheme.Light, default);
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal);
        foreach (string role in new[]
        {
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Heading2,
            MarkdownElementKeys.List,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Emphasis,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.CodeInline,
        })
        {
            styles[role] = ThemeDefaultStyles.CreateDefaultStyle(
                role,
                isDark: false,
                isHighContrast: false,
                accentColor: null,
                platformColors,
                new FontWeight { Weight = 600 },
                systemTheme: null);
        }

        var overrides = new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal);
        var paragraphGap = new ElementStyleOverride { Margin = new Thickness(0, 0, 0, 16) };
        switch (customization)
        {
            case Customization.HostResources:
                styles[MarkdownElementKeys.Body] = ThemeSnapshot.ApplyOverride(styles[MarkdownElementKeys.Body], paragraphGap);
                styles[MarkdownElementKeys.List] = ThemeSnapshot.ApplyOverride(styles[MarkdownElementKeys.List], paragraphGap);
                break;
            case Customization.ThemeOverrides:
                overrides[MarkdownElementKeys.Body] = paragraphGap;
                overrides[MarkdownElementKeys.List] = paragraphGap;
                break;
        }

        var rules = new List<MarkdownStyleRule>();
        if (customization != Customization.None)
        {
            rules.Add(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body, minimumNestingDepth: 1),
                new ElementStyleOverride { Margin = new Thickness(0, 0, 0, 8) }));
        }

        rules.AddRange(extraRules);
        MarkdownStyleSheet? styleSheet = rules.Count == 0 ? null : new MarkdownStyleSheet(rules);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            overrides,
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: styleSheet);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context, semanticDocument: document).Build(parsed, LayoutWidth);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 0.01, expected + 0.01);
}
