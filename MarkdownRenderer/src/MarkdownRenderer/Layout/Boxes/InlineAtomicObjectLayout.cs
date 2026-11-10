using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Lays out the atomic inline objects of an inline container (images,
/// hosted embeds, vector scenes and decorative spacers): measures them
/// speculatively with rollback state, installs their DirectWrite inline
/// objects, and applies content-aware line spacing around tall objects.
/// </summary>
internal static class InlineAtomicObjectLayout
{
    private const float MinimumContentAwareLineScale = 0.1f;
    private const float MaximumContentAwareLineScale = 32f;
    private const float MaximumContentAwareLineHeight = 1_000_000f;
    private const float MinimumInverseInlineMetricHeight = 1f;

    /// <summary>
    /// Restores the authored block line-height for text while retaining
    /// content-aware growth for atomic inline runs. DirectWrite's proportional
    /// mode scales inline-object metrics as well as text metrics, so the inline
    /// objects receive inverse-scaled dimensions below. Text-only layouts keep
    /// their existing absolute CSS line-spacing format.
    /// </summary>
    internal static float ApplyContentAwareLineSpacing(
        InlineContainerBox box,
        CanvasTextLayout layout,
        ElementStyle style,
        float maxInlineWidth)
    {
        if (!box.HasAtomicInlineObject)
            return 1f;

        float requestedHeight = style.FontSize * style.LineHeightMultiplier;
        if (!float.IsFinite(requestedHeight) ||
            requestedHeight <= 0f ||
            requestedHeight > MaximumContentAwareLineHeight)
        {
            return 1f;
        }

        // A short atomic run must still occupy the authored text line box.
        // DirectWrite can collapse an image-only line to the image's height
        // when the content-aware format has no absolute line spacing. Use the
        // ordinary CSS line spacing while every inline object fits within it;
        // only taller objects need the proportional, content-aware path below.
        if (!HasInlineObjectTallerThan(box, requestedHeight, maxInlineWidth))
        {
            layout.LineSpacing = requestedHeight;
            layout.LineSpacingBaseline = requestedHeight * 0.8f;
            return 1f;
        }

        CanvasLineMetrics[] naturalLines;
        try
        {
            naturalLines = layout.LineMetrics;
        }
        catch (NotSupportedException)
        {
            // Some WinRT projections cannot marshal DirectWrite line metrics.
            // Preserve the existing content-aware fallback instead of failing
            // the entire paragraph.
            return 1f;
        }

        if (naturalLines.Length == 0 ||
            !float.IsFinite(naturalLines[0].Height) ||
            naturalLines[0].Height <= 0f)
        {
            return 1f;
        }

        float scale = requestedHeight / naturalLines[0].Height;
        if (!float.IsFinite(scale) ||
            scale < MinimumContentAwareLineScale ||
            scale > MaximumContentAwareLineScale ||
            !CanInverseScaleInlineMetrics(box, scale, maxInlineWidth))
        {
            return 1f;
        }

        layout.LineSpacingMode = CanvasLineSpacingMode.Proportional;
        layout.LineSpacing = scale;
        layout.LineSpacingBaseline = scale * 0.8f;
        return scale;
    }

    private static bool HasInlineObjectTallerThan(InlineContainerBox box, float requestedHeight, float maxInlineWidth)
    {
        foreach (InlineRun run in box.RunList)
        {
            if (GetAtomicInlineHeight(run, maxInlineWidth) > requestedHeight)
                return true;
        }

        return false;
    }

    private static float GetAtomicInlineHeight(InlineRun run, float maxInlineWidth)
        => run switch
        {
            InlineEmbedRun embed => embed.DesiredHeight,
            InlineImageRun image => image.DesiredHeight,
            InlineVectorSceneRun vector => vector.DesiredHeight,
            InlineSpacerRun spacer => GetFittedSpacerHeight(spacer, maxInlineWidth),
            _ => 0f,
        };

    private static bool CanInverseScaleInlineMetrics(InlineContainerBox box, float scale, float maxInlineWidth)
    {
        foreach (InlineRun run in box.RunList)
        {
            float height = GetAtomicInlineHeight(run, maxInlineWidth);
            if (height > 0f)
            {
                float inverseHeight = height / scale;
                if (!float.IsFinite(inverseHeight) ||
                    inverseHeight < MinimumInverseInlineMetricHeight ||
                    inverseHeight > MaximumContentAwareLineHeight)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves a decorative spacer's authored size against the current
    /// inline formatting width. This mirrors the renderer's inline-image
    /// max-width:100% behavior while preserving the authored ratio and keeps
    /// the immutable run dimensions independent from text scaling.
    /// </summary>
    private static void GetFittedSpacerDimensions(
        InlineSpacerRun spacer,
        float maxInlineWidth,
        out float width,
        out float height)
    {
        width = spacer.Width;
        height = spacer.Height;
        if (float.IsPositiveInfinity(maxInlineWidth))
            return;

        float available = float.IsFinite(maxInlineWidth) && maxInlineWidth > 0f
            ? maxInlineWidth
            : 1f;
        width = Math.Min(width, available);
        float scale = width / spacer.Width;
        height = Math.Max(float.Epsilon, spacer.Height * scale);
    }

    private static float GetFittedSpacerHeight(InlineSpacerRun spacer, float maxInlineWidth)
    {
        GetFittedSpacerDimensions(spacer, maxInlineWidth, out _, out float height);
        return height;
    }

    internal static void ApplyEmbedSpacing(
        InlineContainerBox box,
        CanvasTextLayout layout,
        bool observeCancellation = true,
        float inverseMetricScale = 1f,
        float maxInlineWidth = float.PositiveInfinity,
        bool computeInlineImageMetrics = true)
    {
        // CanvasTextAnalyzer is a short-lived WinRT projection and is not
        // actually IClosable in the native Win2D implementation. Keep it
        // local to this measured pass; never retain it on the box.
        CanvasTextAnalyzer? inlineImageMetricAnalyzer = null;
        Dictionary<InlineImageFontMetricKey, float>? inlineImageStyleMetrics = null;
        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            if (observeCancellation)
            {
                box.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            int len = run.TextLength;
            if (run is InlineEmbedRun emb && len > 0)
            {
                try
                {
                    layout.SetInlineObject(
                        cumulative,
                        len,
                        new InlineLayoutObject(emb.DesiredWidth, emb.DesiredHeight, inverseMetricScale));
                }
                catch (Exception ex)
                {
                    MarkdownDiagnostics.WriteLine($"[InlineContainerBox] inline embed layout failed: {ex.Message}");
                }
            }
            else if (run is InlineImageRun image && len > 0)
            {
                float baselineXHeightDip = InlineImageXHeightResolver.ResolveBaselineXHeightDip(
                    box,
                    image,
                    computeInlineImageMetrics,
                    ref inlineImageMetricAnalyzer,
                    ref inlineImageStyleMetrics);
                SetImageInlineObject(layout, image, cumulative, len, inverseMetricScale, baselineXHeightDip);
            }
            else if (run is InlineVectorSceneRun vector && len > 0)
            {
                try
                {
                    layout.SetInlineObject(
                        cumulative,
                        len,
                        new InlineLayoutObject(
                            vector.DesiredWidth,
                            vector.DesiredHeight,
                            inverseMetricScale));
                }
                catch (Exception ex)
                {
                    MarkdownDiagnostics.WriteLine($"[InlineContainerBox] vector scene layout failed: {ex.Message}");
                }
            }
            else if (run is InlineSpacerRun spacer && len > 0)
            {
                SetSpacerInlineObject(layout, spacer, cumulative, len, inverseMetricScale, maxInlineWidth);
            }

            cumulative += len;
        }
    }

    private static void SetImageInlineObject(
        CanvasTextLayout layout,
        InlineImageRun image,
        int cumulative,
        int len,
        float inverseMetricScale,
        float baselineXHeightDip)
    {
        try
        {
            layout.SetInlineObject(
                cumulative,
                len,
                new InlineLayoutObject(
                    image.DesiredWidth,
                    image.DesiredHeight,
                    inverseMetricScale,
                    allowZeroDimensions: image.Image.IsFailedDecorativeImageCollapsed,
                    baselineXHeightDip: baselineXHeightDip));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[InlineContainerBox] inline image layout failed: {ex.Message}");
        }
    }

    private static void SetSpacerInlineObject(
        CanvasTextLayout layout,
        InlineSpacerRun spacer,
        int cumulative,
        int len,
        float inverseMetricScale,
        float maxInlineWidth)
    {
        GetFittedSpacerDimensions(
            spacer,
            maxInlineWidth,
            out float effectiveWidth,
            out float effectiveHeight);
        // Keep this native call inside the enclosing measurement
        // transaction. Swallowing failure would leave the private
        // U+FFFC fallback glyph visible instead of an invisible spacer.
        layout.SetInlineObject(
            cumulative,
            len,
            new InlineLayoutObject(
                effectiveWidth,
                effectiveHeight,
                inverseMetricScale,
                preserveSubUnitDimensions: true));
    }

    internal static bool MeasureAtomicInlineRuns(
        InlineContainerBox box,
        float maxWidth,
        float lineHeight,
        out List<InlineMeasureRollbackState>? rollbackStates)
    {
        bool changed = false;
        rollbackStates = null;
        // Keep the out list published before each mutation: a later run may
        // throw or cancel, and the caller must be able to restore the prefix.
        foreach (var run in box.RunList)
        {
            box.Context.CancellationToken.ThrowIfCancellationRequested();
            if (run is InlineImageRun image)
            {
                float oldW = image.DesiredWidth;
                float oldH = image.DesiredHeight;
                List<InlineMeasureRollbackState> imageStates =
                    rollbackStates ??= new List<InlineMeasureRollbackState>();
                imageStates.EnsureCapacity(imageStates.Count + 1);
                InlineMeasureCheckpoint checkpoint =
                    image.Image.BeginInlineCheckpoint();
                imageStates.Add(new InlineMeasureRollbackState(
                    image,
                    null,
                    oldW,
                    oldH,
                    0,
                    default,
                    checkpoint));
                image.Measure(maxWidth, lineHeight, checkpoint);
                if (!float.IsFinite(image.DesiredWidth) || !float.IsFinite(image.DesiredHeight))
                    throw new InvalidOperationException("Inline image measurement returned non-finite dimensions.");
                if (Math.Abs(oldW - image.DesiredWidth) > 0.5f ||
                    Math.Abs(oldH - image.DesiredHeight) > 0.5f)
                {
                    changed = true;
                }
            }
            else if (run is InlineVectorSceneRun vector)
            {
                float oldW = vector.DesiredWidth;
                float oldH = vector.DesiredHeight;
                float oldBaseline = vector.DesiredBaseline;
                Rect oldLocalBounds = vector.LocalBounds;
                List<InlineMeasureRollbackState> vectorStates =
                    rollbackStates ??= new List<InlineMeasureRollbackState>();
                vectorStates.EnsureCapacity(vectorStates.Count + 1);
                vectorStates.Add(new InlineMeasureRollbackState(
                    null,
                    vector,
                    oldW,
                    oldH,
                    oldBaseline,
                    oldLocalBounds));

                vector.Measure(lineHeight);
                if (!float.IsFinite(vector.DesiredWidth) ||
                    !float.IsFinite(vector.DesiredHeight) ||
                    !float.IsFinite(vector.DesiredBaseline))
                {
                    throw new InvalidOperationException("Inline vector measurement returned non-finite dimensions.");
                }
                if (Math.Abs(oldW - vector.DesiredWidth) > 0.5f ||
                    Math.Abs(oldH - vector.DesiredHeight) > 0.5f)
                {
                    changed = true;
                }
            }
        }

        return changed;
    }
}
