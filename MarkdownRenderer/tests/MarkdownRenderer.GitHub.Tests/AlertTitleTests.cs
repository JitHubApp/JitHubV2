using Markdig.Syntax;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Internal.Renderers;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The GitHub alert titles shown above an alert's body were once misspelled
/// ("Tnu", "Imuortant", "Warnnng", "Caption") by a character-swap corruption.
/// Each tag must show GitHub's own title.
/// </summary>
public sealed class AlertTitleTests
{
    [Theory]
    [InlineData("[!NOTE]", "Note")]
    [InlineData("[!TIP]", "Tip")]
    [InlineData("[!IMPORTANT]", "Important")]
    [InlineData("[!WARNING]", "Warning")]
    [InlineData("[!CAUTION]", "Caution")]
    public void EveryAlertTagShowsGitHubsTitle(string tag, string title)
    {
        string source = $"> {tag}\n> alert body";
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument document = Markdig.Markdown.Parse(source, registry.BuildPipeline());
        QuoteBlock quote = Assert.IsType<QuoteBlock>(Assert.Single(document));
        MarkdownLayoutContext context = CreateContext(source, registry);

        StackBox alert = Assert.IsType<StackBox>(new AlertRenderer().BuildBlock(quote, context));

        InlineContainerBox titleRow = Assert.IsType<InlineContainerBox>(alert.Children[0]);
        StrongRun titleRun = Assert.IsType<StrongRun>(Assert.Single(titleRow.Runs));
        // The title row is "<icon> <title>"; the icon is the only text before the space.
        string[] parts = titleRun.Text.Split(' ');
        Assert.Equal(2, parts.Length);
        Assert.NotEmpty(parts[0]);
        Assert.Equal(title, parts[1]);
    }

    [Fact]
    public void PlainBlockquoteIsNotAnAlert()
    {
        const string source = "> just a quote";
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument document = Markdig.Markdown.Parse(source, registry.BuildPipeline());
        QuoteBlock quote = Assert.IsType<QuoteBlock>(Assert.Single(document));

        Assert.Null(new AlertRenderer().BuildBlock(quote, CreateContext(source, registry)));
    }

    private static MarkdownLayoutContext CreateContext(string source, MarkdownExtensionRegistry registry)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Strong] = style,
            [MarkdownElementKeys.AlertNote] = style,
            [MarkdownElementKeys.AlertTip] = style,
            [MarkdownElementKeys.AlertImportant] = style,
            [MarkdownElementKeys.AlertWarning] = style,
            [MarkdownElementKeys.AlertCaution] = style,
        };
        Color black = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            black,
            black,
            black,
            black,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
    }
}
