using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Windows.UI;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class HtmlBreakImageSpacingTests(ITestOutputHelper output)
{
    [Fact]
    public void StandaloneHtmlBreakAndFollowingMarkdownImageExposeTheirMeasuredBlockGeometry()
    {
        const string source = "<br>\n\n![](https://example.test/divider.png)\n\nAfter the divider.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        Assert.Equal(3, snapshot.Blocks.Count);
        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox breakBox = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        LineBreakRun lineBreak = Assert.IsType<LineBreakRun>(Assert.Single(breakBox.Runs));
        ImageBox image = Assert.IsType<ImageBox>(snapshot.Blocks[1]);
        InlineContainerBox followingParagraph = Assert.IsType<InlineContainerBox>(snapshot.Blocks[2]);

        float breakContentHeight = (float)(breakBox.Bounds.Height - breakBox.Margin.Top - breakBox.Margin.Bottom);
        float contentGap = (float)(image.Bounds.Top + image.Margin.Top -
            (breakBox.Bounds.Bottom - breakBox.Margin.Bottom));
        var breakRange = new DocumentRange(
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
        int semanticStart = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(breakRange.Start);
        int semanticEnd = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(breakRange.End);
        Rect selectionRect = Assert.Single(breakBox.GetSelectionRects(breakRange));

        output.WriteLine($"break bounds: {breakBox.Bounds}; margin: {breakBox.Margin}; content-height: {breakContentHeight}");
        output.WriteLine($"HTML block bounds: {htmlBlock.Bounds}; image bounds: {image.Bounds}; image margin: {image.Margin}");
        output.WriteLine($"image measured content: {image.MeasuredImageWidth} x {image.MeasuredImageHeight}; break-content-to-image-content gap: {contentGap}");
        output.WriteLine($"following paragraph bounds: {followingParagraph.Bounds}");
        output.WriteLine($"semantic break text: '{snapshot.SemanticDocument.Text.Substring(semanticStart, semanticEnd - semanticStart)}'; selection rect: {selectionRect}");

        Assert.Equal(default, breakBox.Margin);
        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal("\n", lineBreak.AccessibleText);
        Assert.Equal(" ", lineBreak.LayoutText);
        Assert.Equal("\n", snapshot.SemanticDocument.Text.Substring(semanticStart, semanticEnd - semanticStart));
        Assert.DoesNotContain('\u200B', snapshot.SemanticDocument.Text);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(breakRange));
        Assert.Equal(24, breakContentHeight, precision: 2);
        Assert.Equal(24, breakBox.Bounds.Height, precision: 2);
        Assert.Equal(24, selectionRect.Height, precision: 2);
        Assert.True(selectionRect.Width > 0);
        Assert.Equal(new Thickness(0, 0, 0, 16), image.Margin);
        Assert.Equal(0, contentGap, precision: 2);
        Assert.Equal(32, image.MeasuredImageHeight);
        Assert.Equal(48, image.Bounds.Height);
        Assert.Equal(image.Bounds.Top, htmlBlock.Bounds.Bottom, precision: 2);
        Assert.Equal(image.Bounds.Bottom, followingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public void BreakOnlySafeHtmlContainerUsesOneLineBoxWithoutBodyParagraphMargin()
    {
        const string source = "<div><br></div>\n\nAfter.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox breakBox = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        LineBreakRun lineBreak = Assert.IsType<LineBreakRun>(Assert.Single(breakBox.Runs));

        Assert.Equal(default, breakBox.Margin);
        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal(24, breakBox.Bounds.Height, precision: 2);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(new DocumentRange(
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 1))));
    }

    [Fact]
    public void BreakOnlySafeHtmlParagraphRetainsItsParagraphMargin()
    {
        const string source = "<p><br></p>\n\nAfter.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox breakBox = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        LineBreakRun lineBreak = Assert.IsType<LineBreakRun>(Assert.Single(breakBox.Runs));

        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal(new Thickness(0, 0, 0, 16), breakBox.Margin);
        Assert.Equal(40, breakBox.Bounds.Height, precision: 2);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(new DocumentRange(
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 1))));
    }

    [Fact]
    public void MarkdownImageOnlyParagraphIsPromotedWithBodyParagraphMargins()
    {
        const string source = "![](https://example.test/divider.png)\n\nFollowing paragraph.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        ImageBox image = Assert.IsType<ImageBox>(snapshot.Blocks[0]);
        InlineContainerBox followingParagraph = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        output.WriteLine($"image-only paragraph bounds: {image.Bounds}; margin: {image.Margin}; measured content-height: {image.MeasuredImageHeight}");
        output.WriteLine($"following paragraph bounds: {followingParagraph.Bounds}; inter-block gap: {followingParagraph.Bounds.Top - image.Bounds.Bottom}");

        Assert.Equal(new Thickness(0, 0, 0, 16), image.Margin);
        Assert.Equal(32, image.MeasuredImageHeight);
        Assert.Equal(48, image.Bounds.Height);
        Assert.Equal(image.Bounds.Bottom, followingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public void SafeHtmlImageParagraphProvidesReferenceForMarkdownImagePromotionGeometry()
    {
        const string source = "<p><img src=\"https://example.test/divider.png\" alt=\"\"></p>\n\nFollowing paragraph.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        InlineImageRun imageRun = Assert.IsType<InlineImageRun>(Assert.Single(paragraph.Runs));
        InlineContainerBox followingParagraph = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        output.WriteLine($"safe HTML image paragraph bounds: {paragraph.Bounds}; margin: {paragraph.Margin}");
        output.WriteLine($"HTML inline image box bounds: {imageRun.Image.Bounds}; image margin: {imageRun.Image.Margin}; measured content-height: {imageRun.DesiredHeight}");
        output.WriteLine($"following paragraph top: {followingParagraph.Bounds.Top}; HTML paragraph bottom: {paragraph.Bounds.Bottom}");

        Assert.Equal(new Thickness(0, 0, 0, 16), paragraph.Margin);
        Assert.Equal(default, imageRun.Image.Margin);
        Assert.Equal(paragraph.Bounds.Bottom, followingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public async Task LoadedMarkdownAndHtmlImagesMatchBrowserParagraphFlowGeometry()
    {
        const string markdownUrl = "https://example.test/markdown-loaded.png";
        const string htmlUrl = "https://example.test/html-loaded.png";
        byte[] png = CreatePng(width: 160, height: 80);
        Assert.Equal(
            "7a658bc11f15e70a1e500525dfdb86a4eaebe1ed1c5b0157e0888f32094d9bf0",
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant());
        var resolver = new DeterministicPngResolver(png);
        using LayoutSnapshot markdownSnapshot = BuildGitHub(
            $"![]({markdownUrl})\n\nFollowing paragraph.",
            resolver);
        using LayoutSnapshot htmlSnapshot = BuildGitHub(
            $"<p><img src=\"{htmlUrl}\" alt=\"\"></p>\n\nFollowing paragraph.",
            resolver);

        ImageBox markdownImage = Assert.IsType<ImageBox>(markdownSnapshot.Blocks[0]);
        InlineContainerBox markdownFollowingParagraph = Assert.IsType<InlineContainerBox>(markdownSnapshot.Blocks[1]);
        StackBox htmlBlock = Assert.IsType<StackBox>(htmlSnapshot.Blocks[0]);
        InlineContainerBox htmlParagraph = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        InlineImageRun htmlImageRun = Assert.IsType<InlineImageRun>(Assert.Single(htmlParagraph.Runs));
        InlineContainerBox htmlFollowingParagraph = Assert.IsType<InlineContainerBox>(htmlSnapshot.Blocks[1]);

        await WaitForImageLoadAsync(markdownImage);
        await WaitForImageLoadAsync(htmlImageRun.Image);
        Assert.NotNull(markdownImage.Bitmap);
        Assert.NotNull(htmlImageRun.Image.Bitmap);

        markdownSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);
        htmlSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);

        output.WriteLine($"loaded markdown block: {markdownImage.Bounds}; content: {markdownImage.MeasuredImageWidth} x {markdownImage.MeasuredImageHeight}; margin: {markdownImage.Margin}");
        output.WriteLine($"loaded HTML paragraph: {htmlParagraph.Bounds}; image: {htmlImageRun.DesiredWidth} x {htmlImageRun.DesiredHeight}; margin: {htmlParagraph.Margin}");

        Assert.Equal(160, markdownImage.MeasuredImageWidth);
        Assert.Equal(80, markdownImage.MeasuredImageHeight);
        Assert.Equal(160, htmlImageRun.DesiredWidth);
        Assert.Equal(80, htmlImageRun.DesiredHeight);
        Assert.Equal(new Thickness(0, 0, 0, 16), markdownImage.Margin);
        Assert.Equal(new Thickness(0, 0, 0, 16), htmlParagraph.Margin);
        Assert.Equal(96, markdownImage.Bounds.Height, precision: 2);
        Assert.Equal(96, htmlParagraph.Bounds.Height, precision: 2);
        Assert.Equal(markdownImage.Bounds.Bottom, markdownFollowingParagraph.Bounds.Top, precision: 2);
        Assert.Equal(htmlParagraph.Bounds.Bottom, htmlFollowingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public async Task TinyMarkdownImageRetainsTheSameParagraphLineBoxAsHtml()
    {
        const string markdownUrl = "https://example.test/tiny-markdown.png";
        const string htmlUrl = "https://example.test/tiny-html.png";
        var resolver = new DeterministicPngResolver(CreatePng(width: 160, height: 5));
        using LayoutSnapshot markdownSnapshot = BuildGitHub(
            $"![]({markdownUrl})\n\nFollowing paragraph.",
            resolver);
        using LayoutSnapshot htmlSnapshot = BuildGitHub(
            $"<p><img src=\"{htmlUrl}\" alt=\"\"></p>\n\nFollowing paragraph.",
            resolver);

        ImageBox markdownImage = Assert.IsType<ImageBox>(markdownSnapshot.Blocks[0]);
        StackBox htmlBlock = Assert.IsType<StackBox>(htmlSnapshot.Blocks[0]);
        InlineContainerBox htmlParagraph = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        InlineImageRun htmlImage = Assert.IsType<InlineImageRun>(Assert.Single(htmlParagraph.Runs));

        await WaitForImageLoadAsync(markdownImage);
        await WaitForImageLoadAsync(htmlImage.Image);
        markdownSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);
        htmlSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);

        Assert.Equal(5, markdownImage.MeasuredImageHeight);
        Assert.Equal(24, htmlParagraph.Bounds.Height - htmlParagraph.Margin.Bottom, precision: 2);
        Assert.Equal(htmlParagraph.Bounds.Height, markdownImage.Bounds.Height, precision: 2);
        Rect htmlImageRect = Assert.Single(htmlParagraph.EnumerateInlineImageRects()).Rect;
        Rect markdownImageRect = markdownImage.AutomationRenderDestination;
        Assert.Equal(htmlImageRect.Y, markdownImageRect.Y, precision: 2);
        Assert.Equal(htmlImageRect.Height, markdownImageRect.Height, precision: 2);
    }

    private static LayoutSnapshot BuildGitHub(
        string source,
        IMarkdownImageResolver? imageResolver = null)
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
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            ImageResolver = imageResolver,
            ImageCancellationToken = CancellationToken.None,
        };

        return new LayoutBuilder(context).Build(document, availableWidth: 660);
    }

    private static async Task WaitForImageLoadAsync(ImageBox image)
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

    private static byte[] CreatePng(int width, int height)
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

    private sealed class DeterministicPngResolver(byte[] bytes) : IMarkdownImageResolver
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
