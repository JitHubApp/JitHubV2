using Markdig;
using Markdig.Extensions.EmphasisExtras;
using MarkdownRenderer.Document;
using MarkdownRenderer.Html;
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

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Builds the relative script and inline code style sheets, theme,
/// layout context and parsed snapshots that the relative font size tests
/// resolve run styles from.
/// </summary>
internal static class RelativeScriptFontSizeFixture
{
    internal static MarkdownStyleSheet RelativeSheet()
        => MarkdownStyleSheet.Empty.WithRelativeScriptFontSize(0.75f);

    internal static ElementStyle ResolveRunStyle(
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

    internal static ElementStyle ResolveRunStyle(
        string containerRole,
        InlineRun run,
        ThemeSnapshot theme)
        => ResolveRunStyle(CreateContext(theme, FlowDirection.LeftToRight), containerRole, run);

    internal static ElementStyle ResolveRunStyle(
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
            return box.GetRunStyle(run);
        }
        finally
        {
            box.Dispose();
        }
    }

    internal static ThemeSnapshot CreateTheme(
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

    internal static MarkdownLayoutContext CreateContext(ThemeSnapshot theme, FlowDirection flowDirection)
        => new(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap("script"),
            new MarkdownExtensionRegistry(),
            flowDirection);

    internal static LayoutSnapshot BuildParsedSnapshot(
        string source,
        MarkdownStyleSheet sheet,
        SafeHtmlOptions? safeHtmlOptions = null,
        bool useGfmContainerExtensions = false,
        double textScale = 1)
    {
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry()
            .ConfigureGitHubReadmeRegistry(safeHtmlOptions);
        // These focused cases opt into the GitHub-relevant emphasis extras
        // needed to exercise source-ordered link-label style paths without
        // changing the shipping GitHub profile.
        var pipelineBuilder = new MarkdownPipelineBuilder()
            .UseEmphasisExtras(
                EmphasisExtraOptions.Strikethrough |
                EmphasisExtraOptions.Subscript |
                EmphasisExtraOptions.Superscript);
        if (useGfmContainerExtensions)
            pipelineBuilder.UsePipeTables().UseTaskLists();
        var pipeline = pipelineBuilder.Build();
        var document = Markdown.Parse(source, pipeline);
        MarkdownLayoutContext context = new(
            CanvasDevice.GetSharedDevice(),
            CreateTheme(sheet, textScale),
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context).Build(document, availableWidth: 640f);
    }
}
