using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm.Renderers;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using System.Globalization;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class AccessibilityReleaseGateTests
{
    [Fact]
    public void SemanticListsExposeStableLevelPositionAndSetSize()
    {
        MarkdownLayoutContext context = CreateContext("en-US");
        StackBox nested = CreateList(context, "nested");
        StackBox outer = new()
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = FlowDirection.LeftToRight,
        };
        outer.Add(CreateListItem(context, "first"));
        outer.Add(CreateListItem(context, "second", nested));

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { outer },
            context.SourceMap,
            width: 800,
            height: 600);
        MarkdownSemanticNode[] lists = MarkdownSemanticDocument
            .EnumerateDepthFirst(snapshot.SemanticDocument.Root)
            .Where(static node => node.Role == MarkdownSemanticRole.List)
            .ToArray();

        Assert.Equal(2, lists.Length);
        Assert.Equal(1, lists[0].Level);
        Assert.Equal(2, lists[1].Level);

        MarkdownSemanticNode[] outerItems = lists[0].Children
            .Where(static node => node.Role == MarkdownSemanticRole.ListItem)
            .ToArray();
        Assert.Equal(2, outerItems.Length);
        Assert.Collection(
            outerItems,
            first => Assert.Equal((1, 1, 2), (first.Level, first.PositionInSet, first.SizeOfSet)),
            second => Assert.Equal((1, 2, 2), (second.Level, second.PositionInSet, second.SizeOfSet)));

        MarkdownSemanticNode innerItem = Assert.Single(
            lists[1].Children,
            static node => node.Role == MarkdownSemanticRole.ListItem);
        Assert.Equal((2, 1, 1), (innerItem.Level, innerItem.PositionInSet, innerItem.SizeOfSet));
    }

    [Fact]
    public void SyntheticAutomationIdentitiesAreDeterministicAndUniqueForSemanticNodes()
    {
        MarkdownLayoutContext context = CreateContext("en-US");
        StackBox list = CreateList(context, "one", "two", "three");
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { list },
            context.SourceMap,
            width: 800,
            height: 600);
        MarkdownSemanticNode[] nodes = MarkdownSemanticDocument
            .EnumerateDepthFirst(snapshot.SemanticDocument.Root)
            .Skip(1)
            .ToArray();

        string[] firstPass = nodes.Select(MarkdownAutomationIdentity.ForNode).ToArray();
        string[] secondPass = nodes.Select(MarkdownAutomationIdentity.ForNode).ToArray();
        Assert.Equal(firstPass, secondPass);
        Assert.Equal(firstPass.Length, firstPass.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EmptyAltBlockImageIsExcludedButMeaningfulFailureRemainsSemantic()
    {
        MarkdownLayoutContext context = CreateContext("fr-FR");
        var decorative = new ImageBox(context, string.Empty, string.Empty)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var meaningful = new ImageBox(context, string.Empty, "Build status")
        {
            BlockIndex = context.NextBlockIndex(),
        };

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { decorative, meaningful },
            context.SourceMap,
            width: 800,
            height: 600);
        MarkdownSemanticNode image = Assert.Single(
            snapshot.SemanticDocument.Root.Children,
            static node => node.Role == MarkdownSemanticRole.Image);

        Assert.Same(meaningful, image.ImageBox);
        Assert.Equal(MarkdownImageAccessibilityState.Error, meaningful.AccessibilityState);
        Assert.Equal("Build status", snapshot.SemanticDocument.GetText(image));
        Assert.Equal("fr-FR", context.Language);
    }

    [Fact]
    public void EmptyAltInlineImageIsDecorativeUnlessItsLinkNeedsAnInvokableName()
    {
        MarkdownLayoutContext context = CreateContext(
            "en-US",
            new TestStringProvider((MarkdownStringKeys.ImageName, "Localized image")));
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new InlineImageRun(context, string.Empty, string.Empty)
        {
            SourceSpan = new SourceSpan(0, 4),
        });
        paragraph.Add(new InlineImageRun(
            context,
            string.Empty,
            string.Empty,
            linkUrl: "https://example.test")
        {
            SourceSpan = new SourceSpan(5, 4),
        });

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 100,
            height: 20);
        MarkdownSemanticNode semanticParagraph = Assert.Single(snapshot.SemanticDocument.Root.Children);
        MarkdownSemanticNode linkedImage = Assert.Single(
            semanticParagraph.Children,
            static node => node.Role == MarkdownSemanticRole.Image);

        Assert.Equal("Localized image", snapshot.SemanticDocument.GetText(linkedImage));
        InlineImageRun linkedRun = Assert.IsType<InlineImageRun>(linkedImage.InlineRun);
        Assert.True(linkedRun.IsLinked);
        Assert.Equal(paragraph.BlockIndex, linkedRun.Image.BlockIndex);
    }

    [Fact]
    public void TaskMarkerIdentityTracksItsUtf16SourceRange()
    {
        var range = new SourceSpan(17, 3);
        string first = TaskListItemRenderer.CreateTaskMarkerAutomationId(range);

        Assert.Equal(first, TaskListItemRenderer.CreateTaskMarkerAutomationId(range));
        Assert.NotEqual(
            first,
            TaskListItemRenderer.CreateTaskMarkerAutomationId(new SourceSpan(18, 3)));
    }

    [Fact]
    public void GfmTaskRendererPublishesReadOnlyStateBeforeElementRealization()
    {
        Markdig.Syntax.MarkdownDocument parsed = Markdig.Markdown.Parse(
            "- [x] completed",
            new MarkdownPipelineBuilder().UseTaskLists().Build());
        var list = Assert.IsType<ListBlock>(Assert.Single(parsed));
        var item = Assert.IsType<ListItemBlock>(Assert.Single(list));

        var renderer = new TaskListItemRenderer();
        var box = Assert.IsType<ListItemBox>(renderer.BuildBlock(item, CreateContext("en-US")));
        var marker = Assert.IsType<InlineEmbedRun>(Assert.Single(box.Marker.Runs));
        InlineEmbedAutomationMetadata metadata = Assert.IsType<InlineEmbedAutomationMetadata>(
            marker.AutomationMetadata);

        Assert.True(metadata.IsChecked);
        Assert.False(metadata.CanToggle);
        Assert.Equal("Completed task", metadata.CurrentName);
        Assert.Equal(metadata.CurrentName, marker.AccessibleText);
        Assert.StartsWith("MarkdownTask_", metadata.AutomationId, StringComparison.Ordinal);
    }

    [Fact]
    public void GfmTaskRendererUsesTheCultureCapturedByTheLayoutPass()
    {
        Markdig.Syntax.MarkdownDocument parsed = Markdig.Markdown.Parse(
            "- [x] completed",
            new MarkdownPipelineBuilder().UseTaskLists().Build());
        var list = Assert.IsType<ListBlock>(Assert.Single(parsed));
        var item = Assert.IsType<ListItemBlock>(Assert.Single(list));
        var provider = new CultureAwareTaskStringProvider();

        var renderer = new TaskListItemRenderer();
        var box = Assert.IsType<ListItemBox>(
            renderer.BuildBlock(item, CreateContext("fr-FR", provider)));
        var marker = Assert.IsType<InlineEmbedRun>(Assert.Single(box.Marker.Runs));
        InlineEmbedAutomationMetadata metadata = Assert.IsType<InlineEmbedAutomationMetadata>(
            marker.AutomationMetadata);

        Assert.Equal("fr-FR:Task.Completed", metadata.CheckedName);
        Assert.Equal("fr-FR:Task.Incomplete", metadata.UncheckedName);
        Assert.Equal("fr-FR:Task.ReadOnly", metadata.ReadOnlyHelpText);
        Assert.Equal("fr-FR:Task.Toggle", metadata.ToggleHelpText);
        Assert.Equal(4, provider.Cultures.Count);
        Assert.All(provider.Cultures, static culture => Assert.Equal("fr-FR", culture));
    }

    [Fact]
    public void VirtualizedTaskMetadataPreservesStateAndOnlyTogglesThroughARealCommand()
    {
        bool committed = false;
        var editable = new InlineEmbedAutomationMetadata(
            "Completed task",
            "Incomplete task",
            "Read-only task state",
            "Toggle task state",
            "task-1",
            isChecked: false,
            canSetState: _ => true,
            trySetState: requested => committed = requested);

        Assert.True(editable.CanToggle);
        Assert.Equal("Incomplete task", editable.CurrentName);
        Assert.True(editable.TrySetState(true));
        Assert.True(committed);
        Assert.True(editable.IsChecked);
        Assert.Equal("Completed task", editable.CurrentName);

        var readOnly = new InlineEmbedAutomationMetadata(
            "Completed task",
            "Incomplete task",
            "Read-only task state",
            "Toggle task state",
            "task-2",
            isChecked: true);
        Assert.False(readOnly.CanToggle);
        Assert.False(readOnly.TrySetState(false));
        Assert.True(readOnly.IsChecked);
        Assert.Equal("Read-only task state", readOnly.CurrentHelpText);

        MarkdownLayoutContext context = CreateContext("en-US");
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.ListMarker)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new InlineEmbedRun(20, 20, static () => throw new InvalidOperationException())
        {
            AutomationMetadata = readOnly,
            SourceSpan = new SourceSpan(4, 3),
        });
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 100,
            height: 20);
        MarkdownSemanticNode embed = Assert.Single(
            Assert.Single(snapshot.SemanticDocument.Root.Children).Children,
            static node => node.Role == MarkdownSemanticRole.Embed);
        Assert.Equal("Completed task", snapshot.SemanticDocument.GetText(embed));
        Assert.Equal("Completed task", embed.AccessibilityName);
        Assert.Equal("task-2", embed.AutomationId);
    }

    [Fact]
    public void EveryPublishedTextStyleHasAStableHostLocalizationKey()
    {
        string[] elementKeys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Heading1,
            MarkdownElementKeys.Heading2,
            MarkdownElementKeys.Heading3,
            MarkdownElementKeys.Heading4,
            MarkdownElementKeys.Heading5,
            MarkdownElementKeys.Heading6,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Emphasis,
            MarkdownElementKeys.Strikethrough,
            MarkdownElementKeys.Subscript,
            MarkdownElementKeys.Superscript,
            MarkdownElementKeys.Inserted,
            MarkdownElementKeys.Marked,
            MarkdownElementKeys.Abbreviation,
            MarkdownElementKeys.DefinitionTerm,
            MarkdownElementKeys.DefinitionDescription,
            MarkdownElementKeys.Figure,
            MarkdownElementKeys.FigureCaption,
            MarkdownElementKeys.Diagram,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];

        string[] localizationKeys = elementKeys
            .Select(MarkdownLocalizedStrings.StyleNameKey)
            .OfType<string>()
            .ToArray();
        Assert.Equal(elementKeys.Length, localizationKeys.Length);
        Assert.Equal(
            localizationKeys.Length,
            localizationKeys.Distinct(StringComparer.Ordinal).Count());
    }

    private static StackBox CreateList(MarkdownLayoutContext context, params string[] values)
    {
        var list = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = FlowDirection.LeftToRight,
        };
        foreach (string value in values)
            list.Add(CreateListItem(context, value));
        return list;
    }

    private static ListItemBox CreateListItem(
        MarkdownLayoutContext context,
        string value,
        StackBox? nested = null)
    {
        int sourceStart = context.NextBlockIndex() * 10;
        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        marker.Add(new TextRun("•")
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(sourceStart, 1),
        });

        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new TextRun(value)
        {
            SourceSpan = new SourceSpan(sourceStart + 2, value.Length),
        });

        var content = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = FlowDirection.LeftToRight,
        };
        content.Add(paragraph);
        if (nested is not null)
            content.Add(nested);

        return new ListItemBox(marker, content, markerWidth: 24)
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = FlowDirection.LeftToRight,
        };
    }

    private static MarkdownLayoutContext CreateContext(
        string language,
        IMarkdownStringProvider? stringProvider = null)
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.ListMarker,
        ];
        var styles = keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 2_000)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: language)
        {
            StringProvider = stringProvider,
        };
    }

    private sealed class TestStringProvider(params (string Key, string Value)[] values)
        : IMarkdownStringProvider
    {
        private readonly Dictionary<string, string> _values = values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        public string? GetString(string key, CultureInfo culture) =>
            _values.TryGetValue(key, out string? value) ? value : null;
    }

    private sealed class CultureAwareTaskStringProvider : IMarkdownStringProvider
    {
        public List<string> Cultures { get; } = [];

        public string? GetString(string key, CultureInfo culture)
        {
            Cultures.Add(culture.Name);
            return $"{culture.Name}:{key}";
        }
    }
}
