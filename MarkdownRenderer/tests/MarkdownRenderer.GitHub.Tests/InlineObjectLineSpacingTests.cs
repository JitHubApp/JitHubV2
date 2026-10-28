using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class InlineObjectLineSpacingTests
{
    private const float InlineWidth = 311f;
    private const float InlineHeight = 155f;
    private const float LayoutWidth = 660f;
    private const float MixedLayoutWidth = 420f;

    [Fact]
    public void ContentAwareSpacing_ExpandsTallInlineObjectAndMovesFollowingLine()
    {
        SharedCanvasTextFormatCache.Clear();
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            LineHeightMultiplier = 1.4f,
        };
        using SharedCanvasTextFormatCache.Lease cssTextFormat =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease contentAwareFormat =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                useContentAwareLineSpacing: true);

        Measurement clamped = Measure(cssTextFormat.Format);
        Measurement expanded = Measure(contentAwareFormat.Format);

        Assert.Equal(19.6f, cssTextFormat.Format.LineSpacing, precision: 3);
        Assert.Equal(-1f, contentAwareFormat.Format.LineSpacing);
        Assert.True(
            clamped.FirstLineHeight < InlineHeight,
            $"Explicit CSS line spacing should reproduce the clamped line box; got {clamped.FirstLineHeight:F2} DIPs.");
        Assert.True(
            clamped.FollowingLineY < InlineHeight,
            $"The fixed line box should reproduce the overlapping following line; got y={clamped.FollowingLineY:F2} DIPs.");
        Assert.True(
            expanded.FirstLineHeight >= InlineHeight,
            $"The content-aware line must accommodate the {InlineHeight:F0}-DIP inline object; got {expanded.FirstLineHeight:F2} DIPs.");
        Assert.True(
            expanded.FollowingLineY >= InlineHeight,
            $"The following line must begin below the inline object; got y={expanded.FollowingLineY:F2} DIPs.");
        Assert.True(
            expanded.LayoutHeight > clamped.LayoutHeight + 100,
            $"Expected the tall inline object's height to contribute to layout: fixed={clamped.LayoutHeight:F2}, content-aware={expanded.LayoutHeight:F2}.");
    }

    [Fact]
    public void MixedParagraph_DefaultSpacingPreservesObjectButProportionalOverexpandsObjectLine()
    {
        SharedCanvasTextFormatCache.Clear();
        const float fontSize = 14f;
        const float cssLineHeight = 28f;
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            LineHeightMultiplier = cssLineHeight / fontSize,
        };
        using SharedCanvasTextFormatCache.Lease cssTextFormat =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        var defaultFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        string words = string.Join(' ', Enumerable.Repeat("wrapped", 36));
        using var textOnlyLayout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(),
            words,
            defaultFormat,
            LayoutWidth,
            float.MaxValue);
        float naturalTextLineHeight = textOnlyLayout.LineMetrics.First().Height;
        using var cssTextOnlyLayout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(),
            words,
            cssTextFormat.Format,
            LayoutWidth,
            float.MaxValue);
        CanvasLineMetrics[] cssTextOnlyLines = cssTextOnlyLayout.LineMetrics;

        var proportionalFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
            LineSpacingMode = CanvasLineSpacingMode.Proportional,
            LineSpacing = cssLineHeight / naturalTextLineHeight,
            LineSpacingBaseline = (cssLineHeight / naturalTextLineHeight) * 0.8f,
        };
        string mixedText = $"Before \uFFFC {words}";
        using var naturalMixedLayout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(),
            mixedText,
            defaultFormat,
            LayoutWidth,
            float.MaxValue);
        using var proportionalMixedLayout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(),
            mixedText,
            proportionalFormat,
            LayoutWidth,
            float.MaxValue);
        int objectIndex = "Before ".Length;
        naturalMixedLayout.SetInlineObject(objectIndex, 1, new ProbeInlineObject(InlineWidth, InlineHeight));
        proportionalMixedLayout.SetInlineObject(objectIndex, 1, new ProbeInlineObject(InlineWidth, InlineHeight));

        CanvasLineMetrics[] naturalLines = naturalMixedLayout.LineMetrics;
        CanvasLineMetrics[] proportionalLines = proportionalMixedLayout.LineMetrics;
        CanvasTextLayoutRegion naturalObjectRegion = Assert.Single(naturalMixedLayout.GetCharacterRegions(objectIndex, 1));
        CanvasTextLayoutRegion proportionalObjectRegion = Assert.Single(proportionalMixedLayout.GetCharacterRegions(objectIndex, 1));
        string diagnostics =
            $"naturalText={naturalTextLineHeight:F2}, cssText=[{string.Join(',', cssTextOnlyLines.Select(line => line.Height.ToString("F2")))}], " +
            $"naturalMixed=[{string.Join(',', naturalLines.Select(line => line.Height.ToString("F2")))}], " +
            $"proportional=[{string.Join(',', proportionalLines.Select(line => line.Height.ToString("F2")))}], " +
            $"objectRegions=natural({naturalObjectRegion.LayoutBounds.Width:F2}x{naturalObjectRegion.LayoutBounds.Height:F2})/" +
            $"proportional({proportionalObjectRegion.LayoutBounds.Width:F2}x{proportionalObjectRegion.LayoutBounds.Height:F2})";

        Assert.Equal(28f, cssTextFormat.Format.LineSpacing, precision: 3);
        Assert.All(cssTextOnlyLines, line => Assert.Equal(cssLineHeight, line.Height, precision: 2));
        Assert.True(naturalLines.Length > 1, diagnostics);
        Assert.True(proportionalLines.Length > 1, diagnostics);
        Assert.True(naturalLines.Skip(1).Any(line => line.Height < cssLineHeight - 1f), diagnostics);
        Assert.True(proportionalLines[0].Height >= InlineHeight, diagnostics);
        Assert.All(proportionalLines.Skip(1), line => Assert.Equal(cssLineHeight, line.Height, precision: 1));
        Assert.Equal(InlineWidth, (float)naturalObjectRegion.LayoutBounds.Width, precision: 1);
        Assert.Equal(naturalLines[0].Height, (float)naturalObjectRegion.LayoutBounds.Height, precision: 1);
        Assert.Equal(proportionalLines[0].Height, (float)proportionalObjectRegion.LayoutBounds.Height, precision: 1);
        Assert.True(
            proportionalLines[0].Height > naturalLines[0].Height + 50,
            $"Proportional spacing recovered CSS text lines but over-expanded the object line. {diagnostics}");
    }

    [Theory]
    [InlineData(CanvasTextDirection.LeftToRightThenTopToBottom, "en-US", "Tall PNG")]
    [InlineData(CanvasTextDirection.RightToLeftThenTopToBottom, "ar-SA", "Tall vector")]
    public void InverseMetricProportionalSpacing_PreservesAuthoredTextLinesAndObjectClearance(
        CanvasTextDirection direction,
        string locale,
        string objectKind)
    {
        SharedCanvasTextFormatCache.Clear();
        const float fontSize = 14f;
        const float authoredLineHeight = 28f;
        var baseFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
            Direction = direction,
            LocaleName = locale,
        };
        // Include both the requested locale and mixed-script/fallback glyphs. This
        // makes the scale derive from the actual DirectWrite line metrics rather
        // than assuming font-size is itself a line height.
        string text = direction == CanvasTextDirection.RightToLeftThenTopToBottom
            ? "قبل \uFFFC\nالعربية עברית 😀 بعد ذلك نص طويل يلتف أسطرًا عديدة ومزيد من النص لاختبار الالتفاف عبر عدة أسطر"
            : "Before \uFFFC\nEnglish Ελληνικά 😀 after that a long paragraph wraps across several lines";
        int objectIndex = text.IndexOf('\uFFFC');
        Assert.True(objectIndex >= 0);
        using var layout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(),
            text,
            baseFormat,
            MixedLayoutWidth,
            float.MaxValue);
        // Match production ordering: measure the natural layout with its
        // replacement character before assigning the atomic inline-object metrics.
        float naturalTextLineHeight = layout.LineMetrics.First().Height;
        float scale = authoredLineHeight / naturalTextLineHeight;
        Assert.True(float.IsFinite(scale) && scale > 0f, $"Invalid proportional scale: {scale}.");

        layout.LineSpacingMode = CanvasLineSpacingMode.Proportional;
        layout.LineSpacing = scale;
        layout.LineSpacingBaseline = scale * 0.8f;
        // Inverse-scale the DirectWrite metrics; the renderer still paints the
        // PNG/vector at its real desired dimensions outside this callback.
        layout.SetInlineObject(
            objectIndex,
            1,
            new ProbeInlineObject(InlineWidth, InlineHeight / scale, baseline: InlineHeight / scale));

        CanvasLineMetrics[] lines = layout.LineMetrics;
        CanvasTextLayoutRegion objectRegion = Assert.Single(layout.GetCharacterRegions(objectIndex, 1));
        int trailingTextIndex = text.IndexOf(direction == CanvasTextDirection.RightToLeftThenTopToBottom ? "العربية" : "English", StringComparison.Ordinal);
        CanvasTextLayoutRegion trailingTextRegion = Assert.Single(layout.GetCharacterRegions(trailingTextIndex, 3));
        int objectLineIndex = FindLine(lines, objectRegion.LayoutBounds.Y);
        int trailingLineIndex = FindLine(lines, trailingTextRegion.LayoutBounds.Y);
        string metrics =
            $"kind={objectKind}, locale={locale}, scale={scale:F4}, natural={naturalTextLineHeight:F2}, " +
            $"lines=[{string.Join(',', lines.Select(line => line.Height.ToString("F2")))}], " +
            $"objectRegion={objectRegion.LayoutBounds.Width:F2}x{objectRegion.LayoutBounds.Height:F2}@{objectRegion.LayoutBounds.Y:F2}, " +
            $"trailingY={trailingTextRegion.LayoutBounds.Y:F2}";

        Assert.True(objectLineIndex >= 0, metrics);
        Assert.True(trailingLineIndex > objectLineIndex, metrics);
        Assert.True(lines.Length - objectLineIndex - 1 >= 2, metrics);
        Assert.True(lines[objectLineIndex].Height >= InlineHeight - 1f, metrics);
        Assert.True(
            lines[objectLineIndex].Height < InlineHeight + authoredLineHeight,
            $"The object line must not retain proportional over-expansion; {metrics}");
        Assert.True(
            trailingTextRegion.LayoutBounds.Y >= objectRegion.LayoutBounds.Y + InlineHeight - 1f,
            metrics);
        Assert.All(
            lines.Skip(objectLineIndex + 1),
            line => Assert.Equal(authoredLineHeight, line.Height, precision: 1));
        Assert.Equal(InlineWidth, (float)objectRegion.LayoutBounds.Width, precision: 1);
        Assert.InRange((float)objectRegion.LayoutBounds.Left, 0f, MixedLayoutWidth - InlineWidth);
    }

    private static Measurement Measure(CanvasTextFormat format)
    {
        using var layout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(),
            "\uFFFC\nLinks",
            format,
            LayoutWidth,
            float.MaxValue);
        layout.SetInlineObject(0, 1, new ProbeInlineObject(InlineWidth, InlineHeight));

        CanvasLineMetrics[] lines = layout.LineMetrics;
        Assert.Single(layout.GetCharacterRegions(0, 1));
        CanvasTextLayoutRegion followingRegion = Assert.Single(layout.GetCharacterRegions(2, 5));
        Assert.NotEmpty(lines);

        return new Measurement(
            layout.LayoutBounds.Height,
            lines[0].Height,
            followingRegion.LayoutBounds.Y);
    }

    private readonly record struct Measurement(
        double LayoutHeight,
        float FirstLineHeight,
        double FollowingLineY);

    private static int FindLine(CanvasLineMetrics[] lines, double y)
    {
        double top = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (y >= top - 0.1 && y < top + lines[i].Height - 0.1)
                return i;
            top += lines[i].Height;
        }

        return -1;
    }

    private sealed class ProbeInlineObject : ICanvasTextInlineObject
    {
        public ProbeInlineObject(float width, float height, float? baseline = null)
        {
            Size = new Size(width, height);
            DrawBounds = new Rect(0, 0, width, height);
            Baseline = baseline ?? height;
        }

        public Size Size { get; }
        public float Baseline { get; }
        public bool SupportsSideways => false;
        public Rect DrawBounds { get; }
        public CanvasLineBreakCondition BreakBefore => CanvasLineBreakCondition.Neutral;
        public CanvasLineBreakCondition BreakAfter => CanvasLineBreakCondition.Neutral;

        public void Draw(
            ICanvasTextRenderer textRenderer,
            System.Numerics.Vector2 point,
            bool isSideways,
            bool isRightToLeft,
            object brush)
        {
        }
    }
}
