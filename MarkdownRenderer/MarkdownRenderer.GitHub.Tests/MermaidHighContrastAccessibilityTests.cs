using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MermaidHighContrastAccessibilityTests
{
    private const string FlowchartSource = "flowchart LR\n  A[Start] --> B[Done]";
    private const string PieSource = "pie title Delivery\n  \"Complete\" : 60\n  \"In progress\" : 30\n  \"Planned\" : 10";
    private const string SequenceSource = "sequenceDiagram\n  participant A as Alice\n  participant B as Bob\n  A->>B: Hello\n  B-->>A: Reply";

    private static readonly VectorScenePaintPalette HighContrastPalette = new(
        Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5),
        Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A),
        Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));

    [Fact]
    public void DiagramHighContrastDefaultsUseTheWindowsWindowColorPair()
    {
        MarkdownHighContrastStyleRoles roles =
            MarkdownHighContrastDefaults.Resolve(MarkdownElementKeys.Diagram);

        Assert.Equal(MarkdownHighContrastColorRole.WindowText, roles.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, roles.Background);
    }

    [Theory]
    [InlineData(MermaidThemeVariant.Default)]
    [InlineData(MermaidThemeVariant.Dark)]
    public async Task SequenceMessageArrowheadsPreserveThemeColorAndRemainVisibleInHighContrast(
        MermaidThemeVariant theme)
    {
        MarkdownVectorScene scene = (await ParseContentAsync(SequenceSource, theme)).VectorScene!;
        int[] markerIndices = Enumerable.Range(1, scene.Commands.Count - 1)
            .Where(index =>
                scene.Commands[index - 1].Kind == MarkdownVectorCommandKind.DrawLine &&
                scene.Commands[index].Kind == MarkdownVectorCommandKind.DrawPath &&
                scene.Commands[index].IsClosed &&
                HasVisibleFill(scene.Commands[index].Style))
            .ToArray();
        uint canvasFill = Assert.Single(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Diagram).Style!.FillArgb!.Value;

        Assert.Equal(2, markerIndices.Length);
        foreach (int markerIndex in markerIndices)
        {
            MarkdownVectorCommand line = scene.Commands[markerIndex - 1];
            MarkdownVectorCommand marker = scene.Commands[markerIndex];

            Assert.True(HasVisibleStroke(line.Style));
            Assert.NotEqual(canvasFill, marker.Style!.FillArgb);
            Assert.Equal(MarkdownVectorPaintRole.Authored, ResolveFillRole(scene, marker, highContrast: false));
            Assert.Equal(
                ApplyOpacity(marker.Style.FillArgb!.Value, marker.Style.Opacity),
                ResolveFill(scene, marker, highContrast: false));
            Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveFillRole(scene, marker, highContrast: true));
            Assert.Equal(HighContrastPalette.Foreground, ResolveFill(scene, marker, highContrast: true));
        }
    }

    [Fact]
    public async Task FlowchartPreservesAuthoredPaletteOutsideHighContrast()
    {
        MarkdownVectorScene scene = (await ParseContentAsync(FlowchartSource)).VectorScene!;
        MarkdownVectorCommand canvas = Assert.Single(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Diagram &&
            command.Style?.FillArgb == 0xFFFFFFFFu);
        MarkdownVectorCommand label = Assert.Single(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text == "Start");
        MarkdownVectorCommand node = Assert.Single(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            command.SemanticIndex == label.SemanticIndex);
        MarkdownVectorCommand edge = Assert.Single(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Edge &&
            HasVisibleStroke(command.Style) &&
            !command.IsClosed);

        Assert.Equal(0xFFECECFFu, node.Style!.FillArgb);
        Assert.Equal(0xFF9370DBu, node.Style.StrokeArgb);
        Assert.Equal(0xFF333333u, label.Style!.FillArgb);
        Assert.Equal(0xFF333333u, edge.Style!.StrokeArgb);

        Assert.Equal(MarkdownVectorPaintRole.Authored, ResolveFillRole(scene, canvas, highContrast: false));
        Assert.Equal(MarkdownVectorPaintRole.Authored, ResolveFillRole(scene, node, highContrast: false));
        Assert.Equal(MarkdownVectorPaintRole.Authored, ResolveFillRole(scene, label, highContrast: false));
        Assert.Equal(MarkdownVectorPaintRole.Authored, ResolveStrokeRole(scene, edge, highContrast: false));
        Assert.Equal(ToColor(0xFFFFFFFFu), ResolveFill(scene, canvas, highContrast: false));
        Assert.Equal(ToColor(0xFFECECFFu), ResolveFill(scene, node, highContrast: false));
        Assert.Equal(ToColor(0xFF333333u), ResolveFill(scene, label, highContrast: false));
        Assert.Equal(ToColor(0xFF333333u), ResolveStroke(scene, edge, highContrast: false));
    }

    [Fact]
    public async Task FlowchartMapsCanvasNodesLabelsEdgesAndArrowheadsToSystemRolesInHighContrast()
    {
        MarkdownVectorScene scene = (await ParseContentAsync(FlowchartSource)).VectorScene!;
        MarkdownVectorCommand canvas = Assert.Single(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Diagram);
        MarkdownVectorCommand node = scene.Commands.First(command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Node);
        MarkdownVectorCommand label = scene.Commands.First(static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText);
        MarkdownVectorCommand edge = scene.Commands.First(command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Edge &&
            HasVisibleStroke(command.Style) &&
            !command.IsClosed);
        MarkdownVectorCommand arrowhead = Assert.Single(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            SemanticRole(scene, command) == MarkdownVectorSemanticRole.Edge &&
            HasVisibleFill(command.Style));

        Assert.Equal(MarkdownVectorPaintRole.Surface, ResolveFillRole(scene, canvas, highContrast: true));
        Assert.Equal(MarkdownVectorPaintRole.Surface, ResolveFillRole(scene, node, highContrast: true));
        Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveStrokeRole(scene, node, highContrast: true));
        Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveFillRole(scene, label, highContrast: true));
        Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveStrokeRole(scene, edge, highContrast: true));
        Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveFillRole(scene, arrowhead, highContrast: true));

        Assert.Equal(HighContrastPalette.Surface, ResolveFill(scene, canvas, highContrast: true));
        Assert.Equal(HighContrastPalette.Surface, ResolveFill(scene, node, highContrast: true));
        Assert.Equal(HighContrastPalette.Foreground, ResolveStroke(scene, node, highContrast: true));
        Assert.Equal(HighContrastPalette.Foreground, ResolveFill(scene, label, highContrast: true));
        Assert.Equal(HighContrastPalette.Foreground, ResolveStroke(scene, edge, highContrast: true));
        Assert.Equal(HighContrastPalette.Foreground, ResolveFill(scene, arrowhead, highContrast: true));
    }

    [Fact]
    public async Task PieKeepsAuthoredCategoriesAndExposesHighContrastBoundariesAndLabels()
    {
        MarkdownContent content = await ParseContentAsync(PieSource);
        MarkdownVectorScene scene = content.VectorScene!;
        MarkdownVectorCommand[] slices = scene.Commands.Where(static command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            command.IsClosed &&
            HasVisibleFill(command.Style)).ToArray();
        MarkdownVectorCommand[] boundaries = scene.Commands.Where(static command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            command.IsClosed &&
            HasVisibleStroke(command.Style)).ToArray();
        MarkdownVectorCommand[] labels = scene.Commands.Where(static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText).ToArray();

        Assert.Equal(3, slices.Length);
        Assert.True(boundaries.Length >= slices.Length);
        Assert.Equal(3, slices.Select(static command => command.Style!.FillArgb).Distinct().Count());
        Assert.All(slices, command =>
        {
            Assert.Equal(MarkdownVectorPaintRole.Authored, ResolveFillRole(scene, command, highContrast: false));
            Assert.Equal(
                ApplyOpacity(command.Style!.FillArgb!.Value, command.Style.Opacity),
                ResolveFill(scene, command, highContrast: false));
            Assert.Equal(MarkdownVectorPaintRole.Surface, ResolveFillRole(scene, command, highContrast: true));
            Assert.Equal(HighContrastPalette.Surface, ResolveFill(scene, command, highContrast: true));
        });
        Assert.All(boundaries, command =>
        {
            Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveStrokeRole(scene, command, highContrast: true));
            Assert.Equal(HighContrastPalette.Foreground, ResolveStroke(scene, command, highContrast: true));
        });
        Assert.All(labels, command =>
        {
            Assert.Equal(MarkdownVectorPaintRole.Foreground, ResolveFillRole(scene, command, highContrast: true));
            Assert.Equal(HighContrastPalette.Foreground, ResolveFill(scene, command, highContrast: true));
        });

        Assert.Contains("Complete", content.SemanticText, StringComparison.Ordinal);
        Assert.Contains("In progress", content.SemanticText, StringComparison.Ordinal);
        Assert.Contains("Planned", content.SemanticText, StringComparison.Ordinal);
        Assert.Contains("60%", content.SemanticText, StringComparison.Ordinal);
        Assert.Contains("30%", content.SemanticText, StringComparison.Ordinal);
        Assert.Contains("10%", content.SemanticText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0xFF333333u, 0xFFECECFFu)]
    [InlineData(true, 0xFFF5F5F5u, 0xFF0A0A0Au)]
    public async Task UiaTextStyleReportsTheEffectiveNodeForegroundAndBackdrop(
        bool highContrast,
        uint expectedForeground,
        uint expectedBackground)
    {
        MarkdownContent content = await ParseContentAsync(FlowchartSource);
        MarkdownVectorScene scene = content.VectorScene!;
        int semanticIndex = Assert.Single(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text == "Start").SemanticIndex;
        var box = new VectorSceneBox(
            CreateContext(highContrast),
            content,
            MarkdownElementKeys.Diagram);
        try
        {
            ElementStyle style = box.ResolveSemanticTextStyle(semanticIndex);

            Assert.Equal(ToColor(expectedForeground), style.Foreground);
            Assert.Equal(ToColor(expectedBackground), style.Background);
        }
        finally
        {
            box.Dispose();
        }
    }

    private static MarkdownVectorPaintRole ResolveFillRole(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        bool highContrast)
    {
        MarkdownVectorPaintStyle style = command.Style!;
        return VectorSceneDrawing.ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(
                command,
                style,
                SemanticRole(scene, command)),
            highContrast,
            scene.GetLinkAction(command.SemanticIndex) is not null);
    }

    private static MarkdownVectorPaintRole ResolveStrokeRole(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        bool highContrast)
    {
        MarkdownVectorPaintStyle style = command.Style!;
        return VectorSceneDrawing.ResolvePaintRole(
            command,
            style.StrokeRole,
            style.StrokeArgb,
            style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
            highContrast,
            scene.GetLinkAction(command.SemanticIndex) is not null);
    }

    private static Color ResolveFill(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        bool highContrast) =>
        VectorSceneDrawing.ResolveColorForTesting(
            command.Style!.FillArgb,
            command.Style.Opacity,
            HighContrastPalette,
            ResolveFillRole(scene, command, highContrast),
            highContrast);

    private static Color ResolveStroke(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        bool highContrast) =>
        VectorSceneDrawing.ResolveColorForTesting(
            command.Style!.StrokeArgb,
            command.Style.Opacity,
            HighContrastPalette,
            ResolveStrokeRole(scene, command, highContrast),
            highContrast);

    private static MarkdownVectorSemanticRole? SemanticRole(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command) =>
        (uint)command.SemanticIndex < (uint)scene.Semantics.Count
            ? scene.Semantics[command.SemanticIndex].Role
            : null;

    private static bool HasVisibleFill(MarkdownVectorPaintStyle? style) =>
        style?.FillArgb is { } color && (color >> 24) != 0;

    private static bool HasVisibleStroke(MarkdownVectorPaintStyle? style) =>
        style is { StrokeWidth: > 0, StrokeArgb: { } color } && (color >> 24) != 0;

    private static async Task<MarkdownContent> ParseContentAsync(
        string source,
        MermaidThemeVariant theme = MermaidThemeVariant.Default)
    {
        using var renderer = new MermaidRenderer(
            MermaidRenderOptions.Default with { Theme = theme });
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");
        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(
            codeBlock.SourceSpan,
            out MarkdownContentFragment? fragment));
        return Assert.Single(fragment!.Items);
    }

    private static MarkdownLayoutContext CreateContext(bool highContrast)
    {
        var diagram = new ElementStyle
        {
            FontFamily = "Host Diagram Face",
            FontSize = 16,
            Foreground = highContrast
                ? HighContrastPalette.Foreground
                : Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Background = highContrast
                ? HighContrastPalette.Surface
                : Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
        };
        var link = new ElementStyle
        {
            FontSize = 16,
            Foreground = highContrast
                ? HighContrastPalette.Link
                : Color.FromArgb(0xFF, 0x00, 0x55, 0xCC),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = diagram,
            [MarkdownElementKeys.Diagram] = diagram,
            [MarkdownElementKeys.Link] = link,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            surfaceColor: diagram.Background!.Value,
            selectionHighlightColor: Color.FromArgb(0, 0, 0, 0),
            selectionForegroundColor: Color.FromArgb(0, 0, 0, 0),
            focusVisualColor: diagram.Foreground,
            isDark: false,
            isHighContrast: highContrast,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 256)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US");
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private static Color ApplyOpacity(uint argb, float opacity) => Color.FromArgb(
        (byte)System.Math.Clamp((int)System.Math.Round((argb >> 24) * opacity), 0, 255),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);
}
