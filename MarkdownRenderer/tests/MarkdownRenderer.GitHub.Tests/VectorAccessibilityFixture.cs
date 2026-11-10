using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the semantic vector scenes, content and layout context that the
/// vector accessibility tests measure.
/// </summary>
internal static class VectorAccessibilityFixture
{
    internal static MarkdownVectorScene CreateSemanticScene(bool includeLink)
    {
        MarkdownVectorSemanticFlags alphaFlags = includeLink
            ? MarkdownVectorSemanticFlags.Focusable |
              MarkdownVectorSemanticFlags.Selectable |
              MarkdownVectorSemanticFlags.Linked
            : MarkdownVectorSemanticFlags.None;
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Flow diagram",
                null,
                -1,
                new SourceSpan(0, 10),
                new MarkdownVectorRectangle(0, 0, 200, 60)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Alpha",
                "First node",
                0,
                new SourceSpan(0, 5),
                new MarkdownVectorRectangle(10, 5, 60, 20),
                alphaFlags),
            new(
                3,
                MarkdownVectorSemanticRole.Label,
                "Beta",
                null,
                0,
                new SourceSpan(6, 4),
                new MarkdownVectorRectangle(120, 35, 40, 15),
                includeLink ? MarkdownVectorSemanticFlags.Selectable : MarkdownVectorSemanticFlags.None),
        ];
        IReadOnlyList<MarkdownVectorLinkAction> links = includeLink
            ? [new MarkdownVectorLinkAction(1, "https://example.test/alpha", action: null, external: true)]
            : Array.Empty<MarkdownVectorLinkAction>();
        return new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 200, 60),
            semantics,
            links);
    }

    internal static MarkdownContent CreateContent(MarkdownVectorScene scene)
    {
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, 10),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Flow diagram",
            semanticText: "Alpha\nBeta");
        return Assert.Single(builder.Build().Items);
    }

    internal static MarkdownLayoutContext CreateContext(
        string language = "en-US",
        IMarkdownStringProvider? stringProvider = null,
        bool isHighContrast = false,
        float fontSize = 16,
        double textScaleFactor = 1)
    {
        var diagram = new ElementStyle
        {
            FontFamily = "Host Diagram Face",
            FontSize = fontSize,
            Foreground = isHighContrast
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Background = isHighContrast
                ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
                : Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
        };
        var link = new ElementStyle
        {
            FontSize = fontSize,
            Foreground = isHighContrast
                ? Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x00, 0x55, 0xCC),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = diagram,
            [MarkdownElementKeys.Diagram] = diagram,
            [MarkdownElementKeys.Link] = link,
        };
        Color selectionHighlight = isHighContrast
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00)
            : Color.FromArgb(0xFF, 0x00, 0x66, 0xCC);
        Color selectionForeground = isHighContrast
            ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            surfaceColor: diagram.Background!.Value,
            selectionHighlightColor: selectionHighlight,
            selectionForegroundColor: selectionForeground,
            focusVisualColor: diagram.Foreground,
            isDark: false,
            isHighContrast: isHighContrast,
            textScaleFactor: textScaleFactor);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 100)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: language)
        {
            StringProvider = stringProvider,
        };
    }
}
