using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Parsing.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the safe HTML and GitHub README layouts that the SafeHtml rendering
/// test classes, such as <see cref="SafeHtmlContentStructureTests"/>, inspect,
/// flattens their boxes and runs, and provides their string provider and
/// deferred image resolver.
/// </summary>
internal static class SafeHtmlRenderingFixture
{
    internal static LayoutSnapshot Build(
        string source,
        SafeHtmlOptions options,
        IMarkdownStringProvider? stringProvider = null,
        float textScaleFactor = 1f)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(options);
        return Build(source, registry, stringProvider, textScaleFactor: textScaleFactor);
    }

    internal static LayoutSnapshot Build(
        string source,
        MarkdownExtensionRegistry registry,
        IMarkdownStringProvider? stringProvider = null,
        Markdig.Syntax.MarkdownDocument? parsedDocument = null,
        bool lazy = false,
        float availableWidth = 800,
        bool measureAllLazyViewports = true,
        IMarkdownImageResolver? imageResolver = null,
        ElementStyle? elementStyle = null,
        IReadOnlyDictionary<string, ElementStyle>? keyedStyles = null,
        float textScaleFactor = 1f)
    {
        Markdig.Syntax.MarkdownDocument document =
            parsedDocument ?? Markdown.Parse(source, registry.BuildPipeline());
        ThemeSnapshot theme = CreateTheme(elementStyle, keyedStyles, textScaleFactor);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            StringProvider = stringProvider,
            ImageResolver = imageResolver,
        };
        var builder = new LayoutBuilder(context);
        if (!lazy)
            return builder.Build(document, availableWidth);

        return BuildLazy(builder, document, availableWidth, measureAllLazyViewports);
    }

    private static ThemeSnapshot CreateTheme(
        ElementStyle? elementStyle,
        IReadOnlyDictionary<string, ElementStyle>? keyedStyles,
        float textScaleFactor)
    {
        var style = elementStyle ?? new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Emphasis,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockGutter,
            MarkdownElementKeys.CodeBlockLineNumber,
            MarkdownElementKeys.Strikethrough,
            MarkdownElementKeys.Subscript,
            MarkdownElementKeys.Superscript,
            MarkdownElementKeys.Inserted,
            MarkdownElementKeys.Marked,
            MarkdownElementKeys.ThematicBreak,
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];
        var styles = keys.Distinct(StringComparer.Ordinal)
            .ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        if (keyedStyles is not null)
        {
            foreach ((string key, ElementStyle keyedStyle) in keyedStyles)
                styles[key] = keyedStyle;
        }
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: textScaleFactor);
    }

    private static LayoutSnapshot BuildLazy(
        LayoutBuilder builder,
        Markdig.Syntax.MarkdownDocument document,
        float availableWidth,
        bool measureAllLazyViewports)
    {
        LayoutSnapshot snapshot = builder.BuildLazy(
            document,
            availableWidth,
            viewportTop: 0,
            viewportHeight: 499,
            overscan: 0,
            cancellationToken: CancellationToken.None);
        if (!measureAllLazyViewports)
            return snapshot;
        for (int viewport = 0; viewport < 64; viewport++)
        {
            double top = viewport * 449;
            if (top >= snapshot.Size.Height)
                break;
            snapshot.EnsureMeasuredViewport(top, 499, 0, CancellationToken.None);
        }
        return snapshot;
    }

    internal static LayoutSnapshot BuildGitHub(
        string source,
        ElementStyle? elementStyle = null,
        IReadOnlyDictionary<string, ElementStyle>? keyedStyles = null,
        float availableWidth = 800,
        float textScaleFactor = 1f)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        return Build(
            source,
            registry,
            availableWidth: availableWidth,
            elementStyle: elementStyle,
            keyedStyles: keyedStyles,
            textScaleFactor: textScaleFactor);
    }

    internal static void AssertDisabledLinkAndImageFallback(LayoutSnapshot snapshot)
    {
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        string rendered = string.Concat(runs.Select(static run => run.Text));
        Assert.Contains("linked", rendered, StringComparison.Ordinal);
        Assert.Contains("diagram", rendered, StringComparison.Ordinal);
    }

    internal static IEnumerable<InlineRun> FlattenRuns(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            foreach (InlineRun run in FlattenRuns(block))
                yield return run;
        }
    }

    internal static IEnumerable<BlockBox> FlattenBoxes(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            yield return block;
            if (block is StackBox stack)
            {
                foreach (BlockBox child in FlattenBoxes(stack))
                    yield return child;
            }
            else if (block is TableBox table)
            {
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
            }
        }
    }

    internal static IEnumerable<BlockBox> FlattenBoxes(StackBox stack)
    {
        foreach (BlockBox child in stack.Children)
        {
            yield return child;
            if (child is StackBox nested)
            {
                foreach (BlockBox descendant in FlattenBoxes(nested))
                    yield return descendant;
            }
            else if (child is TableBox table)
            {
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
            }
        }
    }

    internal static IEnumerable<InlineRun> FlattenRuns(BlockBox block)
    {
        if (block is InlineContainerBox inline)
        {
            foreach (InlineRun run in inline.Runs)
                yield return run;
        }
        else if (block is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                foreach (InlineRun run in FlattenRuns(child))
                    yield return run;
            }
        }
        else if (block is ListItemBox listItem)
        {
            foreach (InlineRun run in FlattenRuns(listItem.Marker))
                yield return run;
            foreach (InlineRun run in FlattenRuns(listItem.Content))
                yield return run;
        }
    }

    internal sealed class TestStringProvider : IMarkdownStringProvider
    {
        public string? GetString(string key, System.Globalization.CultureInfo culture) =>
            key == MarkdownStringKeys.HtmlBudgetExceeded ? "localized budget notice" : null;
    }

    internal sealed class DeferredImageResolver : IMarkdownImageResolver
    {
        private readonly TaskCompletionSource<MarkdownImageResolution> _resolution = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResolveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            ResolveStarted.TrySetResult();
            return new ValueTask<MarkdownImageResolution>(_resolution.Task.WaitAsync(cancellationToken));
        }

        public void Complete(MarkdownImageResolution resolution) => _resolution.TrySetResult(resolution);
    }
}
