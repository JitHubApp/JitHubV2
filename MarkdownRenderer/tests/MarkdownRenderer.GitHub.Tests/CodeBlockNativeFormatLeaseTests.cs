using System.Globalization;
using System.Reflection;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class CodeBlockNativeFormatLeaseTests
{
    private const float CanvasWidth = 360;
    private const float EnhancedHeaderHeight = 38;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void EquivalentCodeBlocksShareImmutableFormatsButKeepIndependentLeasesAcrossCacheClear()
    {
        SharedCanvasTextFormatCache.Clear();
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var first = CreateFixture(device, FlowDirection.LeftToRight, 1);
        var second = CreateFixture(device, FlowDirection.LeftToRight, 1);

        try
        {
            Paint(first.Box, device, 1);
            Paint(second.Box, device, 1);

            SharedCanvasTextFormatCache.Lease firstHeader = GetLease(first.Box, "_headerTextFormatLease");
            SharedCanvasTextFormatCache.Lease secondHeader = GetLease(second.Box, "_headerTextFormatLease");
            SharedCanvasTextFormatCache.Lease firstGutter = GetLease(first.Box, "_lineNumberTextFormatLease");
            SharedCanvasTextFormatCache.Lease secondGutter = GetLease(second.Box, "_lineNumberTextFormatLease");

            Assert.NotSame(firstHeader, secondHeader);
            Assert.NotSame(firstGutter, secondGutter);
            Assert.Same(firstHeader.Format, secondHeader.Format);
            Assert.Same(firstGutter.Format, secondGutter.Format);
            Assert.NotSame(firstHeader.Format, firstGutter.Format);

            CanvasTextFormat activeHeaderFormat = firstHeader.Format;
            CanvasTextFormat activeGutterFormat = firstGutter.Format;
            SharedCanvasTextFormatCache.Clear();

            // Clearing removes only cache ownership. The CodeBlockBox leases
            // keep both native descriptors alive until their boxes retire.
            Assert.Equal(first.LanguageStyle.FontSize, activeHeaderFormat.FontSize);
            Assert.Equal(first.LineNumberStyle.FontSize, activeGutterFormat.FontSize);
            Assert.Same(activeHeaderFormat, firstHeader.Format);
            Assert.Same(activeGutterFormat, firstGutter.Format);
        }
        finally
        {
            first.Box.Dispose();
            second.Box.Dispose();
            SharedCanvasTextFormatCache.Clear();
        }
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight, CanvasTextDirection.LeftToRightThenTopToBottom, CanvasHorizontalAlignment.Left)]
    [InlineData(FlowDirection.RightToLeft, CanvasTextDirection.RightToLeftThenTopToBottom, CanvasHorizontalAlignment.Right)]
    public void HeaderAndGutterUseDistinctAuthoredNativeFormatRoles(
        FlowDirection flowDirection,
        CanvasTextDirection expectedHeaderDirection,
        CanvasHorizontalAlignment expectedHeaderAlignment)
    {
        SharedCanvasTextFormatCache.Clear();
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var fixture = CreateFixture(device, flowDirection, 1);

        try
        {
            Paint(fixture.Box, device, 1);
            CanvasTextFormat header = GetLease(fixture.Box, "_headerTextFormatLease").Format;
            CanvasTextFormat gutter = GetLease(fixture.Box, "_lineNumberTextFormatLease").Format;

            Assert.Equal(fixture.LanguageStyle.FontFamily, header.FontFamily);
            Assert.Equal(fixture.LanguageStyle.FontSize, header.FontSize);
            Assert.Equal(fixture.LanguageStyle.FontWeight, header.FontWeight);
            Assert.Equal(fixture.LanguageStyle.FontStyle, header.FontStyle);
            Assert.Equal("en-US", header.LocaleName, ignoreCase: true);
            Assert.Equal(CanvasWordWrapping.NoWrap, header.WordWrapping);
            Assert.Equal(expectedHeaderDirection, header.Direction);
            Assert.Equal(expectedHeaderAlignment, header.HorizontalAlignment);
            Assert.Equal(CanvasVerticalAlignment.Center, header.VerticalAlignment);

            Assert.Equal(fixture.LineNumberStyle.FontFamily, gutter.FontFamily);
            Assert.Equal(fixture.LineNumberStyle.FontSize, gutter.FontSize);
            Assert.Equal(fixture.LineNumberStyle.FontWeight, gutter.FontWeight);
            Assert.Equal(fixture.LineNumberStyle.FontStyle, gutter.FontStyle);
            Assert.Equal("en-US", gutter.LocaleName, ignoreCase: true);
            Assert.Equal(CanvasWordWrapping.NoWrap, gutter.WordWrapping);
            // Gutter labels intentionally preserve CanvasTextFormat's original
            // default LTR direction even when the document is laid out RTL.
            Assert.Equal(CanvasTextDirection.LeftToRightThenTopToBottom, gutter.Direction);
            Assert.Equal(CanvasHorizontalAlignment.Right, gutter.HorizontalAlignment);
            Assert.Equal(CanvasVerticalAlignment.Top, gutter.VerticalAlignment);
            Assert.NotSame(header, gutter);
        }
        finally
        {
            fixture.Box.Dispose();
            SharedCanvasTextFormatCache.Clear();
        }
    }

    [Fact]
    public void OffscreenPaintDoesNotAcquireHeaderOrGutterNativeFormats()
    {
        SharedCanvasTextFormatCache.Clear();
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var fixture = CreateFixture(device, FlowDirection.LeftToRight, 1);

        try
        {
            using CanvasRenderTarget target = CreateTarget(device, fixture.Box, 1);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                Rect outside = new(
                    0,
                    fixture.Box.Bounds.Bottom + 10,
                    CanvasWidth,
                    20);
                fixture.Box.Paint(drawing, outside);
            }

            Assert.Null(GetPrivateField(fixture.Box, "_headerTextFormatLease"));
            Assert.Null(GetPrivateField(fixture.Box, "_lineNumberTextFormatLease"));

            _ = Paint(fixture.Box, device, 1);
            Assert.IsType<SharedCanvasTextFormatCache.Lease>(
                GetPrivateField(fixture.Box, "_headerTextFormatLease"));
            Assert.IsType<SharedCanvasTextFormatCache.Lease>(
                GetPrivateField(fixture.Box, "_lineNumberTextFormatLease"));
        }
        finally
        {
            fixture.Box.Dispose();
            SharedCanvasTextFormatCache.Clear();
        }
    }

    [Theory]
    [InlineData(1f, FlowDirection.LeftToRight, false)]
    [InlineData(1.5f, FlowDirection.LeftToRight, false)]
    [InlineData(2f, FlowDirection.LeftToRight, false)]
    [InlineData(4f, FlowDirection.LeftToRight, false)]
    [InlineData(1f, FlowDirection.RightToLeft, false)]
    [InlineData(1.5f, FlowDirection.RightToLeft, false)]
    [InlineData(2f, FlowDirection.RightToLeft, false)]
    [InlineData(4f, FlowDirection.RightToLeft, false)]
    [InlineData(1f, FlowDirection.LeftToRight, true)]
    [InlineData(1.5f, FlowDirection.LeftToRight, true)]
    [InlineData(2f, FlowDirection.LeftToRight, true)]
    [InlineData(4f, FlowDirection.LeftToRight, true)]
    [InlineData(1f, FlowDirection.RightToLeft, true)]
    [InlineData(1.5f, FlowDirection.RightToLeft, true)]
    [InlineData(2f, FlowDirection.RightToLeft, true)]
    [InlineData(4f, FlowDirection.RightToLeft, true)]
    public void CachedHeaderAndGutterPixelsMatchTheOriginalDirectFormatPath(
        float rasterizationScale,
        FlowDirection flowDirection,
        bool forceSoftwareRenderer)
    {
        SharedCanvasTextFormatCache.Clear();
        using var device = new CanvasDevice(forceSoftwareRenderer);
        var fixture = CreateFixture(device, flowDirection, rasterizationScale);

        try
        {
            Color[] actual = Paint(fixture.Box, device, rasterizationScale);
            Color[] expected = PaintOriginalDirectFormatPath(fixture, device, rasterizationScale);
            int pixelWidth = checked((int)(CanvasWidth * rasterizationScale));
            Assert.Equal(actual.Length, expected.Length);

            Rect headerRegion = GetHeaderTextBounds(fixture.Box);
            AssertPixelRegionEqual(actual, expected, pixelWidth, headerRegion, rasterizationScale);

            double outerLeft = fixture.Box.Bounds.X + fixture.Box.Margin.Left;
            int lineDigits = System.Math.Max(1, fixture.Box.Metadata.StartLine + fixture.Box.LineCount - 1)
                .ToString(CultureInfo.InvariantCulture).Length;
            double gutterWidth = 20 + System.Math.Max(2, lineDigits) *
                System.Math.Max(6, fixture.CodeStyle.FontSize * 0.56f);
            double numberWidth = System.Math.Max(0, gutterWidth - 10);
            VisualLine[] visualLines = GetVisualLines(fixture.Box);
            Assert.NotEmpty(visualLines);
            foreach (VisualLine line in visualLines)
            {
                Rect numberRegion = new(outerLeft, line.Top, numberWidth, line.Height);
                AssertPixelRegionEqual(actual, expected, pixelWidth, numberRegion, rasterizationScale);
            }

            Assert.True(fixture.Box.Bounds.Height > 0);
        }
        finally
        {
            fixture.Box.Dispose();
            SharedCanvasTextFormatCache.Clear();
        }
    }

    private static CodeBlockFixture CreateFixture(
        CanvasDevice device,
        FlowDirection flowDirection,
        double rasterizationScale)
    {
        var codeStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 14,
            Foreground = Color.FromArgb(255, 24, 25, 26),
        };
        var languageStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 17,
            FontWeight = new FontWeight { Weight = 650 },
            FontStyle = FontStyle.Italic,
            Foreground = Color.FromArgb(255, 52, 61, 72),
        };
        var lineNumberStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 12,
            FontWeight = new FontWeight { Weight = 400 },
            FontStyle = FontStyle.Normal,
            Foreground = Color.FromArgb(255, 88, 98, 108),
        };
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = codeStyle,
            [MarkdownElementKeys.CodeBlock] = codeStyle,
            [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLanguage] = languageStyle,
            [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLineNumber] = lineNumberStyle,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("one\ntwo"),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US")
        {
            RasterizationScale = rasterizationScale,
        };
        var attributes = new Dictionary<string, string> { ["startline"] = "7" };
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, 7), "one\ntwo", "csharp", attributes);
        const string codeText = "one\ntwo";
        var box = new CodeBlockBox(context, metadata, codeText, false, true)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            BlockIndex = context.NextBlockIndex(),
            CodeLanguage = metadata.Language,
            CodeBlockTextOffset = 0,
            CodeBlockTextLength = codeText.Length,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        chunk.Add(new TextRun(codeText));
        box.AddChunk(chunk);
        box.Measure(CanvasWidth);
        box.Arrange(0, 0, CanvasWidth);
        return new CodeBlockFixture(box, codeStyle, languageStyle, lineNumberStyle, context.Language);
    }

    private static Color[] Paint(CodeBlockBox box, CanvasDevice device, float rasterizationScale)
    {
        using CanvasRenderTarget target = CreateTarget(device, box, rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            box.Paint(drawing, box.Bounds);
        }

        return target.GetPixelColors();
    }

    private static Color[] PaintOriginalDirectFormatPath(
        CodeBlockFixture fixture,
        CanvasDevice device,
        float rasterizationScale)
    {
        using CanvasRenderTarget target = CreateTarget(device, fixture.Box, rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            Rect outer = new(
                fixture.Box.Bounds.X + fixture.Box.Margin.Left,
                fixture.Box.Bounds.Y + fixture.Box.Margin.Top,
                System.Math.Max(0, fixture.Box.Bounds.Width - fixture.Box.Margin.Left - fixture.Box.Margin.Right),
                System.Math.Max(0, fixture.Box.Bounds.Height - fixture.Box.Margin.Top - fixture.Box.Margin.Bottom));
            Color separator = fixture.LanguageStyle.Foreground;
            drawing.DrawLine(
                (float)outer.Left,
                (float)(outer.Top + EnhancedHeaderHeight),
                (float)outer.Right,
                (float)(outer.Top + EnhancedHeaderHeight),
                Color.FromArgb(System.Math.Min(separator.A, (byte)0x60), separator.R, separator.G, separator.B),
                1f);

            Rect headerBounds = GetHeaderTextBounds(fixture.Box);
            bool rtl = fixture.Box.Context.FlowDirection == FlowDirection.RightToLeft;
            using var headerFormat = new CanvasTextFormat
            {
                FontFamily = fixture.LanguageStyle.FontFamily,
                FontSize = fixture.LanguageStyle.FontSize,
                FontWeight = fixture.LanguageStyle.FontWeight,
                FontStyle = fixture.LanguageStyle.FontStyle,
                LocaleName = fixture.LocaleName,
                WordWrapping = CanvasWordWrapping.NoWrap,
                Direction = rtl
                    ? CanvasTextDirection.RightToLeftThenTopToBottom
                    : CanvasTextDirection.LeftToRightThenTopToBottom,
                HorizontalAlignment = rtl ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            drawing.DrawText(fixture.Box.HeaderText, headerBounds, fixture.LanguageStyle.Foreground, headerFormat);

            int lineDigits = System.Math.Max(1, fixture.Box.Metadata.StartLine + fixture.Box.LineCount - 1)
                .ToString(CultureInfo.InvariantCulture).Length;
            double gutterWidth = 20 + System.Math.Max(2, lineDigits) *
                System.Math.Max(6, fixture.CodeStyle.FontSize * 0.56f);
            double numberWidth = System.Math.Max(0, gutterWidth - 10);
            using var gutterFormat = new CanvasTextFormat
            {
                FontFamily = fixture.LineNumberStyle.FontFamily,
                FontSize = fixture.LineNumberStyle.FontSize,
                FontWeight = fixture.LineNumberStyle.FontWeight,
                FontStyle = fixture.LineNumberStyle.FontStyle,
                LocaleName = fixture.LocaleName,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Right,
                VerticalAlignment = CanvasVerticalAlignment.Top,
            };
            foreach (VisualLine line in GetVisualLines(fixture.Box))
            {
                string label = checked(fixture.Box.Metadata.StartLine + line.LogicalLineIndex)
                    .ToString(CultureInfo.InvariantCulture);
                var numberBounds = new Rect(outer.Left, line.Top, numberWidth, line.Height);
                drawing.DrawText(label, numberBounds, fixture.LineNumberStyle.Foreground, gutterFormat);
            }
        }

        return target.GetPixelColors();
    }

    private static CanvasRenderTarget CreateTarget(
        CanvasDevice device,
        CodeBlockBox box,
        float rasterizationScale)
    {
        float height = System.Math.Max(1, (float)System.Math.Ceiling(box.Bounds.Height));
        return new CanvasRenderTarget(device, CanvasWidth, height, 96 * rasterizationScale);
    }

    private static Rect GetHeaderTextBounds(CodeBlockBox box)
    {
        bool rtl = box.Context.FlowDirection == FlowDirection.RightToLeft;
        double outerLeft = box.Bounds.X + box.Margin.Left;
        double outerRight = box.Bounds.X + box.Bounds.Width - box.Margin.Right;
        const double padding = 12;
        const double gap = 8;
        double left = rtl ? outerLeft + padding + gap : outerLeft + padding;
        double right = rtl ? outerRight - padding : outerRight - padding - gap;
        return new Rect(left, box.Bounds.Y + box.Margin.Top, System.Math.Max(0, right - left), EnhancedHeaderHeight);
    }

    private static SharedCanvasTextFormatCache.Lease GetLease(CodeBlockBox box, string fieldName)
    {
        return Assert.IsType<SharedCanvasTextFormatCache.Lease>(GetPrivateField(box, fieldName));
    }

    private static object? GetPrivateField(CodeBlockBox box, string fieldName)
    {
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(typeof(CodeBlockBox).GetField(fieldName, PrivateInstance));
        return field.GetValue(box);
    }

    private static VisualLine[] GetVisualLines(CodeBlockBox box)
    {
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(typeof(CodeBlockBox).GetField("_visualLines", PrivateInstance));
        Array values = Assert.IsAssignableFrom<Array>(field.GetValue(box));
        Type valueType = values.GetType().GetElementType()!;
        FieldInfo logicalLineIndex = Assert.IsAssignableFrom<FieldInfo>(valueType.GetField("LogicalLineIndex", InstanceFields));
        FieldInfo top = Assert.IsAssignableFrom<FieldInfo>(valueType.GetField("Top", InstanceFields));
        FieldInfo height = Assert.IsAssignableFrom<FieldInfo>(valueType.GetField("Height", InstanceFields));
        var result = new VisualLine[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            object value = values.GetValue(index)!;
            result[index] = new VisualLine(
                Assert.IsType<int>(logicalLineIndex.GetValue(value)),
                Assert.IsType<double>(top.GetValue(value)),
                Assert.IsType<double>(height.GetValue(value)));
        }
        return result;
    }

    private static void AssertPixelRegionEqual(
        Color[] actual,
        Color[] expected,
        int pixelWidth,
        Rect dipRegion,
        float rasterizationScale)
    {
        int left = checked((int)System.Math.Floor(dipRegion.Left * rasterizationScale));
        int top = checked((int)System.Math.Floor(dipRegion.Top * rasterizationScale));
        int right = checked((int)System.Math.Ceiling(dipRegion.Right * rasterizationScale));
        int bottom = checked((int)System.Math.Ceiling(dipRegion.Bottom * rasterizationScale));
        int pixelHeight = actual.Length / pixelWidth;
        Assert.InRange(left, 0, pixelWidth - 1);
        Assert.InRange(right, left + 1, pixelWidth);
        Assert.InRange(top, 0, pixelHeight - 1);
        Assert.InRange(bottom, top + 1, pixelHeight);
        for (int y = top; y < bottom; y++)
        {
            int rowStart = checked(y * pixelWidth + left);
            for (int x = left; x < right; x++)
            {
                int offset = checked(rowStart + x - left);
                Assert.True(
                    expected[offset] == actual[offset],
                    $"Pixel mismatch at ({x},{y}) in region {dipRegion}.");
            }
        }
    }

    private sealed record CodeBlockFixture(
        CodeBlockBox Box,
        ElementStyle CodeStyle,
        ElementStyle LanguageStyle,
        ElementStyle LineNumberStyle,
        string LocaleName);

    private readonly record struct VisualLine(int LogicalLineIndex, double Top, double Height);
}
