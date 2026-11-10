using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A safe-HTML <c>details</c> element opens from its <c>open</c> attribute
/// until the view records a toggle for its disclosure id, and a collapsed
/// disclosure leaves its body out of the document.
/// </summary>
public sealed class SafeHtmlDisclosureStateTests
{
    private const string Body = "Hidden answer";

    [Theory]
    [InlineData("<details><summary>Answer</summary><p>Hidden answer</p></details>", false)]
    [InlineData("<details open><summary>Answer</summary><p>Hidden answer</p></details>", true)]
    public void DisclosureFollowsItsOpenAttributeUntilTheViewRecordsAToggle(string source, bool open)
    {
        using LayoutSnapshot authored = Build(source, new Dictionary<string, bool>(StringComparer.Ordinal));
        LinkRun disclosure = SingleDisclosure(authored);
        Assert.True(disclosure.IsDisclosure);
        Assert.Equal(open, disclosure.IsExpanded);
        Assert.Equal(open, ContainsBody(authored));

        var toggled = new Dictionary<string, bool>(StringComparer.Ordinal) { [disclosure.DisclosureId!] = !open };
        using LayoutSnapshot rebuilt = Build(source, toggled);
        LinkRun rebuiltDisclosure = SingleDisclosure(rebuilt);

        Assert.Equal(disclosure.DisclosureId, rebuiltDisclosure.DisclosureId);
        Assert.Equal(!open, rebuiltDisclosure.IsExpanded);
        Assert.Equal(!open, ContainsBody(rebuilt));
    }

    private static bool ContainsBody(LayoutSnapshot snapshot) =>
        snapshot.SemanticDocument.Text.Contains(Body, StringComparison.Ordinal);

    private static LinkRun SingleDisclosure(LayoutSnapshot snapshot) =>
        Assert.Single(
            snapshot.Blocks.SelectMany(FlattenRuns).OfType<LinkRun>(),
            static run => run.DisclosureId is not null);

    private static LayoutSnapshot Build(string source, IReadOnlyDictionary<string, bool> disclosureStates)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(SafeHtmlOptions.Default);
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Link] = style,
            [MarkdownElementKeys.Strong] = style,
        };
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
            textScaleFactor: 1f);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            DisclosureStates = disclosureStates,
        };
        return new LayoutBuilder(context).Build(Markdown.Parse(source, registry.BuildPipeline()), 800);
    }

    private static IEnumerable<InlineRun> FlattenRuns(BlockBox block) =>
        block switch
        {
            InlineContainerBox inline => inline.Runs,
            StackBox stack => stack.Children.SelectMany(FlattenRuns),
            _ => [],
        };
}
