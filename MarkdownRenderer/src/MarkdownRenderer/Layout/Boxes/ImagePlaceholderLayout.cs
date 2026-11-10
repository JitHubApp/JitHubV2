using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Lays out and paints an image box's placeholder: the loading or error
/// status around its alt text, the compact one-line failure pill of an
/// inline image, and the collapse of a failed decorative image.
/// </summary>
/// <remarks>
/// Block images and explicitly sized inline images keep their geometry when
/// they fail. A compact inline image without requested dimensions expands
/// once to show its alt text, and a failed image with neither alt text nor
/// dimensions collapses to nothing.
/// </remarks>
internal sealed class ImagePlaceholderLayout
{
    private readonly ImageBox _box;
    private readonly ImageMeasureCustody _measure;
    private readonly ImageLoadState _load;
    private readonly SafeHtmlLength? _requestedWidth;
    private readonly SafeHtmlLength? _requestedHeight;

    internal ImagePlaceholderLayout(ImageBox box, ImageMeasureCustody measure, ImageLoadState load)
    {
        _box = box;
        _measure = measure;
        _load = load;
        _requestedWidth = box.RequestedWidth;
        _requestedHeight = box.RequestedHeight;
    }

    private MarkdownLayoutContext Context => _box.Context;

    /// <summary>True when a failed unconstrained inline image expands to show its alt text.</summary>
    internal bool ShouldExpandInlineFailure =>
        _measure.IsInlineLayout &&
        _load.IsFailed &&
        _requestedWidth is null &&
        _requestedHeight is null;

    /// <summary>True when a failed decorative image without dimensions takes no space.</summary>
    internal bool ShouldCollapseFailedDecorativeImage =>
        _load.IsFailed &&
        string.IsNullOrWhiteSpace(_box.Alt) &&
        _requestedWidth is null &&
        _requestedHeight is null;

    /// <summary>Removes the placeholder and caption and gives the box zero height.</summary>
    internal void CollapseFailedDecorativeImage(float availableWidth)
    {
        _measure.ReplacePlaceholder(null, null);
        _measure.ReplaceCaption(null, null, 0f);
        _measure.ImageWidth = 0;
        _measure.ImageHeight = 0;
        _box.SetMeasuredBounds(new Rect(0, 0, Math.Max(0, availableWidth), 0));
    }

    /// <summary>
    /// Relays out the placeholder of a box whose load just failed, within its
    /// current measured width and height.
    /// </summary>
    internal void UpdateForFailure()
    {
        float maxWidth = Math.Max(
            1f,
            _measure.AvailableWidth - (float)(_box.Margin.Left + _box.Margin.Right));
        UpdatePlaceholder(maxWidth, _measure.ImageHeight);
    }

    /// <summary>Paints the placeholder frame and its status text into <paramref name="rect"/>.</summary>
    internal void PaintPlaceholder(CanvasDrawingSession ds, Rect rect)
    {
        var snapshot = Context.ThemeSnapshot;
        var body = snapshot.GetStyle(MarkdownElementKeys.Body);
        var caption = snapshot.GetStyle(MarkdownElementKeys.ImageCaption);
        Color fill = snapshot.IsHighContrast
            ? snapshot.SurfaceColor
            : Color.FromArgb(0x0F, body.Foreground.R, body.Foreground.G, body.Foreground.B);
        Color border = snapshot.IsHighContrast
            ? snapshot.FocusVisualColor
            : Color.FromArgb(0x52, caption.Foreground.R, caption.Foreground.G, caption.Foreground.B);
        const float radius = 4f;
        ds.FillRoundedRectangle(rect, radius, radius, fill);
        ds.DrawRoundedRectangle(rect, radius, radius, border, 1f);

        bool compactInlineFailure = _measure.IsInlineLayout && _load.IsFailed;
        if (_measure.Placeholder is null ||
            (!compactInlineFailure && (rect.Width < 96 || rect.Height < 32)))
        {
            return;
        }

        float horizontalPadding = compactInlineFailure ? 8f : 12f;
        float verticalPadding = compactInlineFailure ? 0f : 8f;
        NativePaintSessionResources.LayerLease clip = Context.RenderResources.PaintSessions.CreateLayer(ds, 1f, rect);
        try
        {
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(
                Context.PlacementTransform,
                ds,
                _measure.Placeholder,
                (float)rect.X + Math.Min(horizontalPadding, (float)rect.Width / 4f),
                (float)rect.Y + Math.Min(verticalPadding, (float)rect.Height / 4f),
                caption.Foreground);
        }
        catch (Exception primary)
        {
            clip.ClosePreservingPrimary(primary);
            throw;
        }
        clip.Close();
    }

    /// <summary>Replaces the placeholder layout with one sized to <paramref name="maxWidth"/> and <paramref name="maxHeight"/>.</summary>
    internal void UpdatePlaceholder(float maxWidth, float maxHeight = float.MaxValue)
    {
        var style = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
        bool compactInlineFailure = _measure.IsInlineLayout && _load.IsFailed;
        using SharedCanvasTextFormatCache.Lease formatLease = Context.RenderResources.TextFormats.Acquire(
            style,
            compactInlineFailure ? CanvasWordWrapping.NoWrap : CanvasWordWrapping.Wrap,
            CanvasHorizontalAlignment.Center,
            Context.FlowDirection,
            Context.Language,
            true,
            CanvasVerticalAlignment.Center);
        string text = GetPlaceholderText(compactInlineFailure);
        float horizontalPadding = compactInlineFailure ? 16f : 24f;
        float verticalPadding = compactInlineFailure ? 0f : 16f;
        float layoutWidth = Math.Max(1f, maxWidth - Math.Min(horizontalPadding, maxWidth / 2f));
        float layoutHeight = float.IsFinite(maxHeight)
            ? Math.Max(1f, maxHeight - Math.Min(verticalPadding, maxHeight / 2f))
            : float.MaxValue;
        CanvasTextLayout replacement = _measure.CreateOwnedTextLayout(
            text,
            formatLease.Format,
            layoutWidth,
            layoutHeight,
            out NativeResourceRetirementRecord replacementRecord);
        if (compactInlineFailure)
        {
            // The one-line pill is clamped to the available width. Trim the
            // label with an ellipsis instead of letting the centered text
            // spill past both edges into the pill's clip. The accessible
            // name keeps the full text.
            replacement.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            replacement.TrimmingSign = CanvasTrimmingSign.Ellipsis;
        }
        _measure.ReplacePlaceholder(replacement, replacementRecord);
    }

    private string GetPlaceholderText(bool compactInlineFailure)
    {
        string description = compactInlineFailure
            ? GetInlineFailureText()
            : string.IsNullOrWhiteSpace(_box.Alt) ? _box.Url : _box.Alt;
        const int maxDescriptionLength = 240;
        if (description.Length > maxDescriptionLength)
        {
            description = description[..maxDescriptionLength] + "...";
        }

        return compactInlineFailure
            ? description
            : _load.IsFailed
                ? ResolveImageStatus(
                    MarkdownStringKeys.ImageError,
                    MarkdownLocalizedStrings.ImageErrorFormat,
                    description)
                : ResolveImageStatus(
                    MarkdownStringKeys.ImageLoading,
                    MarkdownLocalizedStrings.ImageLoadingFormat,
                    description);
    }

    /// <summary>The width of the compact failure pill for <paramref name="height"/>, at most <paramref name="maxWidth"/>.</summary>
    internal float MeasureInlineFailureWidth(float maxWidth, float height)
    {
        var style = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
        using SharedCanvasTextFormatCache.Lease formatLease = Context.RenderResources.TextFormats.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            CanvasHorizontalAlignment.Left,
            Context.FlowDirection,
            Context.Language,
            true,
            CanvasVerticalAlignment.Top);
        NativeResourceRetirementRecord retirementRecord = _measure.ReserveNativeLayoutRetirementRecord();
        CanvasTextLayout? layout = null;
        float measuredWidth;
        try
        {
            layout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                GetInlineFailureText(),
                formatLease.Format,
                Math.Max(maxWidth, 4096f),
                height,
                Context.LayoutDiagnostics);
            retirementRecord.SetTemporary(0, layout);
            const float horizontalPadding = 16f;
            float textWidth = (float)Math.Max(layout.LayoutBounds.Width, layout.DrawBounds.Width);
            measuredWidth = Math.Clamp(MathF.Ceiling(textWidth) + horizontalPadding, height, maxWidth);
        }
        catch
        {
            if (layout is not null)
                retirementRecord.SetTemporary(0, layout);
            // Temporary measurement resources are terminal on both success
            // and failure, so a later native Dispose failure is retried by
            // the same source-free custody path as persistent placements.
            retirementRecord.Publish();
            throw;
        }

        try
        {
            retirementRecord.Disposer(layout!);
            retirementRecord.SetTemporary(0, null);
            retirementRecord.Publish();
        }
        catch
        {
            retirementRecord.Publish();
            throw;
        }

        return measuredWidth;
    }

    private string GetInlineFailureText()
    {
        string description = string.IsNullOrWhiteSpace(_box.Alt)
            ? Context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName)
            : _box.Alt.Trim();
        description = ResolveImageStatus(
            MarkdownStringKeys.ImageError,
            MarkdownLocalizedStrings.ImageErrorFormat,
            description);
        const int maxLength = 80;
        return description.Length <= maxLength
            ? description
            : description[..(maxLength - 3)] + "...";
    }

    private string ResolveImageStatus(string key, string fallbackFormat, string description)
        => Context.ResolveFormattedString(key, fallbackFormat, description);
}
