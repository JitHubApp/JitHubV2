using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// C06 at 150%: a table cell's text range rectangle ended 4 DIP past the cell's
/// content box. The range crossed a wrap, and DirectWrite reports the space
/// that hangs past the wrap width as part of the line. Range and selection
/// rectangles must stay inside the text's own content box.
/// </summary>
public sealed class WrappedLineRangeRectsContainmentTests
{
    private const string Text = "The intrinsic 1600-pixel width must not become an unshrinkable column minimum.";

    [Fact]
    public async Task RangeRectanglesAcrossWrapsStayInsideTheContentBox()
    {
        var failures = new List<string>();
        for (int width = 100; width <= 320; width += 4)
        {
            using LayoutSnapshot snapshot = await Build(Text, width);
            InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
            Rect content = ContentBox(box);
            foreach (Rect rect in box.GetBufferRangeRects(0, Text.Length))
            {
                if (rect.X < content.X - 0.01 || rect.X + rect.Width > content.X + content.Width + 0.01)
                    failures.Add($"width {width}: rect [{rect.X:F2}, {rect.X + rect.Width:F2}] outside content [{content.X:F2}, {content.X + content.Width:F2}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(12)));
    }

    private static Rect ContentBox(InlineContainerBox box)
    {
        Thickness margin = box.Margin;
        return new Rect(
            box.Bounds.X + margin.Left,
            box.Bounds.Y + margin.Top,
            System.Math.Max(0, box.Bounds.Width - margin.Left - margin.Right),
            System.Math.Max(0, box.Bounds.Height - margin.Top - margin.Bottom));
    }

    private static async Task<LayoutSnapshot> Build(string source, float width)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle> { [MarkdownElementKeys.Body] = style },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, width);
    }
}
