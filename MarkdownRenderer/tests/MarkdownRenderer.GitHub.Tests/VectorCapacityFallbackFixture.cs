using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Contexts, scenes, boxes and paint helpers for the vector capacity fallback
/// tests: a progressive-residency context with one body style, a simple path
/// scene whose native preparation can be denied, and block paints with an
/// optional selection.
/// </summary>
internal static class VectorCapacityFallbackFixture
{
    internal static readonly Color ClearColor = Color.FromArgb(255, 255, 255, 255);
    internal static readonly Color SceneColor = Color.FromArgb(255, 18, 34, 50);
    internal static readonly Color SelectionColor = Color.FromArgb(255, 12, 220, 28);
    internal static readonly Color SelectionBackgroundColor = Color.FromArgb(255, 0, 90, 190);

    internal static int PreparedFallbackDescriptorCount(VectorNativeResourceAdmission admission) =>
        (admission.CapacityFallbackFormats.TryGetFormat(FlowDirection.LeftToRight, out _) ? 1 : 0) +
        (admission.CapacityFallbackFormats.TryGetFormat(FlowDirection.RightToLeft, out _) ? 1 : 0);

    internal static VectorNativeResourceCredit ReserveEntireAdmission(VectorNativeResourceAdmission admission)
    {
        Assert.Equal(
            VectorNativeAdmissionResult.Granted,
            admission.TryReserve(100_000, out VectorNativeResourceCredit? credit, out _));
        return Assert.IsType<VectorNativeResourceCredit>(credit);
    }

    internal static MarkdownLayoutContext CreateContext(
        ICanvasResourceCreator resourceCreator,
        MarkdownPerformanceSession session,
        string source,
        double textScaleFactor = 1,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        bool highContrast = false)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            Foreground = SceneColor,
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.Diagram] = body,
            [MarkdownElementKeys.Math] = body,
            [MarkdownElementKeys.Link] = body,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor: ClearColor,
            selectionHighlightColor: Color.FromArgb(255, 0, 90, 190),
            selectionForegroundColor: SelectionColor,
            focusVisualColor: SceneColor,
            isDark: false,
            isHighContrast: highContrast,
            textScaleFactor: textScaleFactor);
        return new MarkdownLayoutContext(
            resourceCreator,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            PerformanceSession = session,
            UseProgressiveNativeSceneResidency = true,
        };
    }

    internal static MarkdownVectorScene CreatePathScene()
    {
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(2, 2)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(70, 2)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(70, 38)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(2, 38)),
            MarkdownVectorPathOperation.Close(),
        ];
        return new MarkdownVectorScene(
            width: 100,
            height: 48,
            baseline: 48,
            commands: [MarkdownVectorCommand.FillPath(path)]);
    }

    /// <summary>
    /// Builds a diagram block over the whole source of <paramref name="context"/>
    /// whose native preparation is decided by <paramref name="admission"/>.
    /// </summary>
    internal static VectorSceneBox CreateDiagramBox(
        MarkdownLayoutContext context,
        VectorNativeResourceAdmission admission,
        string semanticText,
        string? accessibilityName = null,
        string? accessibilityDescription = null)
    {
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            CreatePathScene(),
            new SourceSpan(0, context.SourceMap.SourceText.Length),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: accessibilityName,
            accessibilityDescription: accessibilityDescription,
            semanticText: semanticText);
        return new VectorSceneBox(
            context,
            Assert.Single(content.Build().Items),
            MarkdownElementKeys.Diagram,
            admission)
        {
            BlockIndex = 0,
        };
    }

    internal static Color[] PaintBlock(
        VectorSceneBox box,
        float height,
        DocumentRange? selection)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 200, 100, 96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(ClearColor);
            box.Paint(drawing, new Rect(0, 0, 200, 100));
            if (selection is { } range)
            {
                bool highContrast = box.LayoutContext.ThemeSnapshot.IsHighContrast;
                foreach (SelectionBackgroundGeometry background in box.GetSelectionBackgroundGeometry(range))
                    background.Paint(drawing, SelectionBackgroundColor, highContrast);
                box.PaintSelectionForeground(drawing, range, SelectionColor, new Rect(0, 0, 200, 100));
            }
        }
        return target.GetPixelColors();
    }
}
