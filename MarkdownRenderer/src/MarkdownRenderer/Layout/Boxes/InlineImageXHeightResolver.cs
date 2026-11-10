using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Resolves the font x-height an inline image middle-aligns to: reads it
/// from the run style's resolved DirectWrite fonts during measurement,
/// records it (or a stable fallback) in the box's per-run cache, and shares
/// it across runs of one measured pass through a bounded style table.
/// </summary>
internal static class InlineImageXHeightResolver
{
    private const int MaximumInlineImageFontMetricsPerMeasure = 32;

    // The x-height an inline image aligns its middle to, or -1 when it keeps
    // the plain bottom-on-baseline placement.
    internal static float ResolveBaselineXHeightDip(
        InlineContainerBox box,
        InlineImageRun image,
        bool computeInlineImageMetrics,
        ref CanvasTextAnalyzer? inlineImageMetricAnalyzer,
        ref Dictionary<InlineImageFontMetricKey, float>? inlineImageStyleMetrics)
    {
        float baselineXHeightDip = -1f;
        if (!image.Image.IsFailedDecorativeImageCollapsed &&
            box.Context.ThemeSnapshot.MiddleAlignInlineImages)
        {
            float xHeightDip = 0f;
            bool hasXHeight;
            if (computeInlineImageMetrics)
            {
                xHeightDip = ResolveInlineImageXHeightDipForMeasurement(
                    box,
                    image,
                    ref inlineImageMetricAnalyzer,
                    ref inlineImageStyleMetrics);
                hasXHeight = xHeightDip > 0f;
            }
            else
            {
                hasXHeight = InlineObjectGeometry.TryGetCachedInlineImageXHeightDip(box, image, out xHeightDip);
            }

            if (hasXHeight && xHeightDip > 0f)
                baselineXHeightDip = xHeightDip;
        }

        return baselineXHeightDip;
    }

    private static float ResolveInlineImageXHeightDipForMeasurement(
        InlineContainerBox box,
        InlineImageRun image,
        ref CanvasTextAnalyzer? analyzer,
        ref Dictionary<InlineImageFontMetricKey, float>? styleMetrics)
    {
        if (!box.Context.ThemeSnapshot.MiddleAlignInlineImages ||
            image.Image.IsFailedDecorativeImageCollapsed)
        {
            return 0f;
        }

        float[] metrics = box.GetOrCreateInlineImageXHeightCache();
        int runIndex = image.InlineIndex;
        if ((uint)runIndex >= (uint)metrics.Length)
            return 0f;

        // Zero means not resolved; a negative value records a stable fallback.
        // Positive values are the only metrics consumed by paint/selection.
        float alreadyResolved = metrics[runIndex];
        if (alreadyResolved != 0f)
            return alreadyResolved > 0f ? alreadyResolved : 0f;

        box.Context.CancellationToken.ThrowIfCancellationRequested();
        ElementStyle style = box.GetRunStyle(image);
        if (!float.IsFinite(style.FontSize) || style.FontSize <= 0f)
        {
            metrics[runIndex] = -1f;
            return 0f;
        }

        InlineImageFontMetricKey key = new InlineImageFontMetricKey(
            style.FontFamily ?? string.Empty,
            style.FontSize,
            style.FontWeight.Weight,
            style.FontStyle,
            box.Context.FlowDirection,
            box.Context.Language,
            box.Context.ThemeSnapshot.IsDark,
            box.Context.ThemeSnapshot.IsHighContrast,
            box.Context.ThemeSnapshot.TextScaleFactor);
        if (styleMetrics is { } existingStyleMetrics &&
            existingStyleMetrics.TryGetValue(key, out float cached))
        {
            metrics[runIndex] = cached;
            return cached > 0f ? cached : 0f;
        }

        float resolvedXHeightDip = ReadFontXHeightDip(box, style, ref analyzer);

        float storedMetric = resolvedXHeightDip > 0f ? resolvedXHeightDip : -1f;
        metrics[runIndex] = storedMetric;
        styleMetrics ??= new Dictionary<InlineImageFontMetricKey, float>(4);
        if (styleMetrics.Count < MaximumInlineImageFontMetricsPerMeasure)
            styleMetrics[key] = storedMetric;
        return resolvedXHeightDip;
    }

    // Reads the first usable lowercase-letter height of the style's resolved
    // fonts. Every font face is disposed; an inspection failure other than
    // cancellation or memory exhaustion leaves the established placement.
    private static float ReadFontXHeightDip(
        InlineContainerBox box,
        ElementStyle style,
        ref CanvasTextAnalyzer? analyzer)
    {
        float resolvedXHeightDip = 0f;
        try
        {
            using SharedCanvasTextFormatCache.Lease formatLease = box.CreateTextFormatLease(
                style,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left);
            if (analyzer is null)
            {
                CanvasTextDirection direction = box.Context.FlowDirection == FlowDirection.RightToLeft
                    ? CanvasTextDirection.RightToLeftThenTopToBottom
                    : CanvasTextDirection.LeftToRightThenTopToBottom;
                analyzer = new CanvasTextAnalyzer("x", direction);
            }

            IReadOnlyList<KeyValuePair<CanvasCharacterRange, CanvasScaledFont>> fonts =
                analyzer.GetFonts(formatLease.Format);
            foreach (KeyValuePair<CanvasCharacterRange, CanvasScaledFont> fontSpan in fonts)
            {
                CanvasScaledFont scaledFont = fontSpan.Value;
                CanvasFontFace face = scaledFont.FontFace;
                try
                {
                    if (resolvedXHeightDip <= 0f &&
                        float.IsFinite(scaledFont.ScaleFactor) && scaledFont.ScaleFactor > 0f)
                    {
                        double metric = (double)formatLease.Format.FontSize *
                            scaledFont.ScaleFactor * face.LowercaseLetterHeight;
                        if (double.IsFinite(metric) && metric > 0d && metric <= float.MaxValue)
                            resolvedXHeightDip = (float)metric;
                    }
                }
                finally
                {
                    face.Dispose();
                }
            }

            box.Context.CancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not OperationCanceledException)
        {
            // Font metrics are an optional visual-profile enhancement. If a
            // particular DirectWrite face cannot be inspected, retain the
            // renderer's established inline-image placement.
            MarkdownDiagnostics.WriteLine(
                $"[InlineContainerBox] inline image x-height unavailable: {exception.GetType().Name}");
        }

        return resolvedXHeightDip;
    }
}
