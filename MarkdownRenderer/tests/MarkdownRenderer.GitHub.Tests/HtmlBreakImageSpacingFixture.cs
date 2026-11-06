using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Markdig;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the GitHub README layouts and deterministic PNG images that
/// <see cref="HtmlBreakImageSpacingTests"/> measures, and holds its shared
/// line-break assertions.
/// </summary>
internal static class HtmlBreakImageSpacingFixture
{
    /// <summary>Asserts that only the last break before a block boundary uses single-line layout.</summary>
    internal static void AssertTerminalBreakLayout(
        LineBreakRun[] imageBreaks,
        LineBreakRun taglineBreak,
        LineBreakRun headingLineBreak,
        LineBreakRun[] ruleBreaks)
    {
        Assert.False(imageBreaks[0].UseSingleLineLayout);
        Assert.False(imageBreaks[1].UseSingleLineLayout);
        Assert.False(imageBreaks[2].UseSingleLineLayout);
        Assert.True(imageBreaks[3].UseSingleLineLayout);
        Assert.All(imageBreaks.Take(3), static lineBreak => Assert.Equal("\n", lineBreak.LayoutText));
        Assert.Equal(" ", imageBreaks[3].LayoutText);
        Assert.True(taglineBreak.UseSingleLineLayout);
        Assert.Equal(" ", taglineBreak.LayoutText);
        Assert.False(headingLineBreak.UseSingleLineLayout);
        Assert.All(ruleBreaks.Take(3), static lineBreak => Assert.False(lineBreak.UseSingleLineLayout));
        Assert.True(ruleBreaks[3].UseSingleLineLayout);
        Assert.All(ruleBreaks.Take(3), static lineBreak => Assert.Equal("\n", lineBreak.LayoutText));
        Assert.Equal(" ", ruleBreaks[3].LayoutText);
    }

    /// <summary>Asserts that every break keeps its newline text, its <c>&lt;br&gt;</c> source and a selection rectangle.</summary>
    internal static void AssertBreaksMapToSourceAndSelection(
        LayoutSnapshot snapshot,
        params InlineContainerBox[] boxes)
    {
        foreach (InlineContainerBox box in boxes)
        {
            foreach (LineBreakRun lineBreak in box.Runs.OfType<LineBreakRun>())
            {
                var range = new DocumentRange(
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, 0),
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
                Assert.Equal("\n", lineBreak.AccessibleText);
                Assert.Equal("<br>", snapshot.SourceMap.Slice(range));
                Assert.NotEmpty(box.GetSelectionRects(range));
            }
        }
    }

    /// <summary>Asserts that every authored break projects to one newline in the semantic text.</summary>
    internal static void AssertBreaksProjectToSemanticNewlines(LayoutSnapshot semanticProjectionControl)
    {
        string semanticText = semanticProjectionControl.SemanticDocument.Text;
        AssertSemanticRangesWithinText(semanticProjectionControl.SemanticDocument);
        foreach (InlineContainerBox box in EnumerateInlineBoxes(semanticProjectionControl.Blocks))
        {
            foreach (LineBreakRun lineBreak in box.Runs.OfType<LineBreakRun>())
            {
                var range = new DocumentRange(
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, 0),
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
                MarkdownTextSpan span = Assert.Single(
                    semanticProjectionControl.SemanticDocument.TextSpans,
                    candidate => ReferenceEquals(candidate.InlineBox, box) &&
                        ReferenceEquals(candidate.InlineRun, lineBreak));

                Assert.InRange(span.TextStart, 0, semanticText.Length);
                Assert.InRange(span.TextEnd, span.TextStart, semanticText.Length);
                Assert.Equal("\n", semanticText.Substring(span.TextStart, span.TextEnd - span.TextStart));
                Assert.Equal(span.TextStart,
                    semanticProjectionControl.SemanticDocument.TextOffsetFromDocumentPosition(range.Start));
                Assert.Equal(span.TextEnd,
                    semanticProjectionControl.SemanticDocument.TextOffsetFromDocumentPosition(range.End));
            }
        }
    }

    internal static LayoutSnapshot BuildGitHub(
        string source,
        IMarkdownImageResolver? imageResolver = null,
        MarkdownStyleSheet? styleSheet = null,
        FlowDirection flowDirection = FlowDirection.LeftToRight)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());

        var bodyStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = 1.5f,
            Margin = new Thickness(0, 0, 0, 16),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = bodyStyle,
            [MarkdownElementKeys.ImageCaption] = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = 12,
            },
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
            textScaleFactor: 1,
            styleSheet: styleSheet);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            flowDirection)
        {
            ImageResolver = imageResolver,
            ImageCancellationToken = CancellationToken.None,
        };

        return new LayoutBuilder(context).Build(document, availableWidth: 660);
    }

    internal static IEnumerable<InlineContainerBox> EnumerateInlineBoxes(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            if (block is InlineContainerBox inline)
            {
                yield return inline;
            }
            else if (block is StackBox stack)
            {
                foreach (InlineContainerBox nested in EnumerateInlineBoxes(stack.Children))
                    yield return nested;
            }
        }
    }

    internal static void AssertSemanticRangesWithinText(MarkdownSemanticDocument semantic)
    {
        int textLength = semantic.Text.Length;
        foreach (MarkdownTextSpan span in semantic.TextSpans)
        {
            Assert.InRange(span.TextStart, 0, textLength);
            Assert.InRange(span.TextEnd, span.TextStart, textLength);
        }

        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root))
        {
            Assert.InRange(node.TextStart, 0, textLength);
            Assert.InRange(node.TextEnd, node.TextStart, textLength);
        }
    }

    internal static async Task WaitForImageLoadAsync(ImageBox image)
    {
        if (image.Bitmap is not null)
            return;

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completed.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            if (image.Bitmap is not null)
                return;

            image.EnsureLoading();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    internal static byte[] CreatePng(int width, int height)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[checked(width * 4 + 1)];
            for (int index = 0; index < height; index++)
                zlib.Write(row);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string name, byte[] content)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, content.Length);
        destination.Write(length);
        byte[] type = Encoding.ASCII.GetBytes(name);
        destination.Write(type);
        destination.Write(content);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in type)
            crc = UpdateCrc(crc, value);
        foreach (byte value in content)
            crc = UpdateCrc(crc, value);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        destination.Write(checksum);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        return crc;
    }

    internal sealed class DeterministicPngResolver(byte[] bytes) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(
                    bytes,
                    "image/png",
                    new Uri(source, UriKind.Absolute),
                    CacheKey: source)));
    }
}
