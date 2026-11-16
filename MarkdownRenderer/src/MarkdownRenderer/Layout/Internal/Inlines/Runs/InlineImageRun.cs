using System;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.Inlines.SpeculativeMeasurement;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

internal sealed class InlineImageRun : InlineRun
{
    public readonly string Url;
    public readonly string? Title;
    public readonly string? LinkUrl;
    public readonly string? LinkTitle;
    public bool IsLinked => !string.IsNullOrWhiteSpace(LinkUrl);
    public readonly string AltText;
    public readonly ImageBox Image;
    internal float DesiredWidth { get; private set; }
    internal float DesiredHeight { get; private set; }

    public InlineImageRun(
        MarkdownLayoutContext context,
        string altText,
        string url,
        string? title = null,
        string? linkUrl = null,
        string? linkTitle = null,
        SafeHtmlLength? requestedWidth = null,
        SafeHtmlLength? requestedHeight = null)
    {
        AltText = altText ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        LinkUrl = linkUrl;
        LinkTitle = linkTitle;
        Image = new ImageBox(context, context.ImageResources, Url, AltText, requestedWidth, requestedHeight)
        {
            Margin = default
        };
        DesiredWidth = 24f;
        DesiredHeight = 24f;
        RenderedLength = 1;
    }

    private InlineImageRun(InlineImageRun source, ImageBox image)
        : base(source)
    {
        Url = source.Url;
        Title = source.Title;
        LinkUrl = source.LinkUrl;
        LinkTitle = source.LinkTitle;
        AltText = source.AltText;
        Image = image;
        DesiredWidth = source.DesiredWidth;
        DesiredHeight = source.DesiredHeight;
    }

    internal bool TryClassifyPrivateImagePlacement(
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration) =>
        Image.TryClassifyPrivateImagePlacement(
            targetContext,
            allowIdleImagePlacements,
            out isIdleDescriptor,
            out expectedIdleLoadDemandGeneration);

    internal bool TryCreatePrivateImagePlacementClone(
        MarkdownLayoutContext targetContext,
        out InlineImageRun? placement,
        out ImageBox? expectedSourceImage,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration)
    {
        placement = null;
        expectedSourceImage = null;
        expectedSourceLease = null;
        svgMetadataFence = null;
        isIdleDescriptor = false;
        expectedIdleLoadDemandGeneration = 0;

        if (!Image.TryCreatePrivateImagePlacementClone(
                targetContext,
                out ImageBox? clonedImage,
                out expectedSourceLease,
                out svgMetadataFence,
                out isIdleDescriptor,
                out expectedIdleLoadDemandGeneration) ||
            clonedImage is null)
        {
            expectedSourceLease = null;
            svgMetadataFence = null;
            isIdleDescriptor = false;
            expectedIdleLoadDemandGeneration = 0;
            return false;
        }

        try
        {
            placement = new InlineImageRun(this, clonedImage);
            expectedSourceImage = Image;
            return true;
        }
        catch (Exception constructionFailure)
        {
            try
            {
                targetContext.DiscardConstructionBlock(clonedImage);
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    constructionFailure.Data["MarkdownRenderer.PrivateImageRunCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Cleanup diagnostics must not mask the primary failure.
                }
            }
            expectedSourceImage = null;
            expectedSourceLease = null;
            svgMetadataFence = null;
            isIdleDescriptor = false;
            expectedIdleLoadDemandGeneration = 0;
            throw;
        }
    }

    internal bool TryCreatePrivateRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        out InlineImageRun? placement,
        out ImageBox? expectedSourceImage,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadata)
    {
        placement = null;
        expectedSourceImage = null;
        expectedSourceLease = null;
        svgMetadata = null;
        if (!Image.TryCreateCompletedRasterPlacementClone(
                targetContext,
                out ImageBox? clonedImage,
                out expectedSourceLease,
                out svgMetadata) ||
            clonedImage is null)
            return false;

        try
        {
            placement = new InlineImageRun(this, clonedImage);
            expectedSourceImage = Image;
            return true;
        }
        catch (Exception constructionFailure)
        {
            try
            {
                targetContext.DiscardConstructionBlock(clonedImage);
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    constructionFailure.Data["MarkdownRenderer.PrivateRasterRunCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Cleanup diagnostics must not mask the primary construction failure.
                }
            }
            expectedSourceLease = null;
            svgMetadata = null;
            throw;
        }
    }

    public override string Text => InlineEmbedRun.PlaceholderChar;

    public override string AccessibleText => AltText;

    internal void Measure(
        float maxWidth,
        float lineHeight,
        InlineMeasureCheckpoint? checkpoint = null)
    {
        var size = checkpoint is { } transaction
            ? Image.MeasureInline(maxWidth, lineHeight, transaction)
            : Image.MeasureInline(maxWidth, lineHeight);
        if (Image.IsFailedDecorativeImageCollapsed)
        {
            DesiredWidth = 0;
            DesiredHeight = 0;
        }
        else
        {
            DesiredWidth = Math.Max(1f, (float)size.Width);
            DesiredHeight = Math.Max(1f, (float)size.Height);
        }
    }

    internal void RestoreMeasuredSize(float width, float height)
    {
        DesiredWidth = width;
        DesiredHeight = height;
    }
}
