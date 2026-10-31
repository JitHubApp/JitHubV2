using System;
using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// 150% screenshot review: a failed inline image in a narrow table cell drew
/// its centered "Image unavailable: ..." label wider than its pill, so the
/// pill's clip cut the label off on both sides with no ellipsis. The label of
/// a compact inline failure must fit its own layout width.
/// </summary>
public sealed class InlineFailurePlaceholderTrimmingTests
{
    [Theory]
    [InlineData(80f)]
    [InlineData(120f)]
    [InlineData(160f)]
    public void CompactInlineFailureLabelFitsItsPillWidth(float maxWidth)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        try
        {
            var image = new ImageBox(
                CreateContext(device),
                string.Empty,
                "Script-bearing SVG fixture whose alternative text is longer than a narrow table cell");
            try
            {
                _ = image.MeasureInline(maxWidth, 22f);
                CanvasTextLayout label = Assert.IsType<CanvasTextLayout>(image.PlaceholderLayoutForTests);
                Assert.True(
                    label.LayoutBounds.Width <= label.RequestedSize.Width + 0.5,
                    $"The inline failure label is {label.LayoutBounds.Width:F1} DIP wide in a {label.RequestedSize.Width:F1} DIP layout; it must be trimmed, not clipped.");
                Assert.Equal(CanvasTrimmingSign.Ellipsis, label.TrimmingSign);
            }
            finally
            {
                image.Dispose();
            }
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
        }
    }

    private static MarkdownLayoutContext CreateContext(CanvasDevice device)
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
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
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("inline failure trimming"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            DisclosureStates = new Dictionary<string, bool>(StringComparer.Ordinal),
        };
    }
}
