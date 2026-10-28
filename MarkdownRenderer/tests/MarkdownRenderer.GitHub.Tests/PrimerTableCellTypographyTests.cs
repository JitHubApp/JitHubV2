using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class PrimerTableCellTypographyTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1.5, false, true)]
    [InlineData(2, true, false)]
    [InlineData(3, true, true)]
    public void CellTextUsesCssLineHeightAtTextScalesAndBothDirections(double scale, bool rtl, bool header)
    {
        string role = header ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell;
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        InlineContainerBox previous = CreateCell(device, GitHubReadmeVisualStyleProfiles.PrimerLightV6.StyleSheet, role, scale, rtl);
        InlineContainerBox? current = null;
        try
        {
            current = CreateCell(device, GitHubReadmeVisualStyleProfiles.PrimerLightV7.StyleSheet, role, scale, rtl);
            float previousHeight = previous.Measure(2000);
            float currentHeight = current.Measure(2000);

            Assert.Equal(4 * 16 * scale * 1.4, previousHeight, precision: 2);
            Assert.Equal(4 * 16 * scale * 1.5, currentHeight, precision: 2);
            Assert.True(currentHeight > previousHeight);
        }
        finally
        {
            try { current?.Dispose(); }
            finally { previous.Dispose(); }
        }
    }

    private static InlineContainerBox CreateCell(CanvasDevice device, MarkdownStyleSheet sheet, string role, double scale, bool rtl)
    {
        const string text = "first line\nsecond line\nthird line\nlast line";
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16 };
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.TableHeader] = style,
            [MarkdownElementKeys.TableCell] = style,
        };
        Color foreground = Color.FromArgb(255, 31, 35, 40);
        var theme = new ThemeSnapshot(styles, new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(255, 255, 255, 255), foreground, foreground, foreground,
            isDark: false, isHighContrast: false, textScaleFactor: scale, styleSheet: sheet);
        var context = new MarkdownLayoutContext(device, theme, new MarkdownSourceMap(text),
            new MarkdownExtensionRegistry(), rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
        var cell = new InlineContainerBox(context, role)
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };
        cell.Add(new TextRun(text));
        return cell;
    }
}
