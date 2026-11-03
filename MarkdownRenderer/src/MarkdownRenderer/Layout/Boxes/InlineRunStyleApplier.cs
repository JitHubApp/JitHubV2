using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Applies resolved run, nested link-span and code-highlight styles to an
/// inline container's native text layout, then queues the inline-code
/// padding pass.
/// </summary>
internal static class InlineRunStyleApplier
{
    internal static void ApplyRunStyles(InlineContainerBox box, CanvasTextLayout layout, bool applyColors, bool observeCancellation = true)
    {
        var containerStyle = box.GetContainerStyle();
        string containerFontFamily = SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(
            containerStyle.FontFamily);
        List<InlineCodePaddingRange>? inlineCodePaddingRanges = null;
        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            if (observeCancellation)
                box.Context.CancellationToken.ThrowIfCancellationRequested();
            int len = run.TextLength;
            if (len == 0) continue;
            if (run is InlineSpacerRun)
            {
                // The placeholder is replaced by a geometry-only inline
                // object. Do not apply inherited text styles or OpenType
                // baseline features to it.
                cumulative += len;
                continue;
            }
            ElementStyle? appliedRunStyle = null;
            if (box.ShouldApplyRunTextStyle(run))
            {
                ElementStyle runStyle = box.GetRunStyle(run);
                appliedRunStyle = runStyle;
                ApplyTextStyle(
                    layout,
                    cumulative,
                    len,
                    runStyle,
                    containerStyle,
                    containerFontFamily,
                    applyColors);
            }
            if (run is CodeInlineRun)
            {
                ElementStyle codeStyle = appliedRunStyle ?? box.GetRunStyle(run);
                InlineCodePaddingLayout.QueueInlineCodePaddingRange(
                    box,
                    ref inlineCodePaddingRanges, cumulative, len, codeStyle.Padding,
                    observeCancellation);
            }
            if (run is LinkRun { InlineStyleMap: { } inlineStyleMap } styledLink)
            {
                ApplyLinkSpanStyles(
                    box,
                    layout,
                    styledLink,
                    inlineStyleMap,
                    appliedRunStyle,
                    cumulative,
                    applyColors,
                    observeCancellation,
                    ref inlineCodePaddingRanges);
            }

            cumulative += len;
        }

        InlineCodePaddingLayout.ApplyInlineCodePaddingRanges(box, layout, inlineCodePaddingRanges, observeCancellation);
    }

    private static void ApplyLinkSpanStyles(
        InlineContainerBox box,
        CanvasTextLayout layout,
        LinkRun styledLink,
        LinkInlineStyleMap inlineStyleMap,
        ElementStyle? appliedRunStyle,
        int cumulative,
        bool applyColors,
        bool observeCancellation,
        ref List<InlineCodePaddingRange>? inlineCodePaddingRanges)
    {
        // The full link run has already been styled above. Compare
        // each nested range with that actual base, not the paragraph
        // style, so a nested value equal to the paragraph but
        // different from the link still replaces the link formatting.
        ElementStyle linkStyle = appliedRunStyle ?? box.GetRunStyle(styledLink);
        string linkFontFamily = SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(
            linkStyle.FontFamily);
        ElementStyle[] spanStyles = box.GetLinkInlineSpanStyles(styledLink);
        for (int spanIndex = 0; spanIndex < inlineStyleMap.Count; spanIndex++)
        {
            if (observeCancellation &&
                (spanIndex % InlineCodePaddingLayout.InlineCodeGraphemeCancellationCheckInterval) == 0)
            {
                box.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            LinkInlineStyleSpan span = inlineStyleMap.GetSpan(spanIndex);
            int spanStart = cumulative + span.Start;
            ApplyTextStyle(
                layout,
                spanStart,
                span.Length,
                spanStyles[spanIndex],
                linkStyle,
                linkFontFamily,
                applyColors);
        }

        InlineCodePaddingLayout.QueueLinkInlineCodePadding(
            box,
            ref inlineCodePaddingRanges,
            cumulative,
            inlineStyleMap,
            spanStyles,
            observeCancellation);
    }

    private static void ApplyTextStyle(
        CanvasTextLayout layout,
        int start,
        int length,
        ElementStyle style,
        ElementStyle containerStyle,
        string containerFontFamily,
        bool applyColors)
    {
        string fontFamily = SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(style.FontFamily);
        if (!string.Equals(fontFamily, containerFontFamily, StringComparison.Ordinal))
            layout.SetFontFamily(start, length, fontFamily);
        if (style.FontWeight.Weight != containerStyle.FontWeight.Weight)
            layout.SetFontWeight(start, length, style.FontWeight);
        if (style.FontStyle != containerStyle.FontStyle)
            layout.SetFontStyle(start, length, style.FontStyle);
        if (style.FontSize != containerStyle.FontSize)
            layout.SetFontSize(start, length, style.FontSize);
        if (applyColors && style.Foreground != containerStyle.Foreground)
            layout.SetColor(start, length, style.Foreground);
    }

    internal static void ApplyForegroundSpans(InlineContainerBox box, CanvasTextLayout layout)
    {
        if (box.ForegroundSpans.Count == 0)
            return;

        box.EnsureBuffer();
        foreach (var span in box.ForegroundSpans)
        {
            int start = Math.Clamp(span.Start, 0, box.LayoutBuffer.Length);
            int length = Math.Clamp(span.Length, 0, box.LayoutBuffer.Length - start);
            if (length > 0)
                layout.SetColor(start, length, span.Foreground);
        }
    }
}
