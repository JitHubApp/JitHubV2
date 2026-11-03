using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Measures the min-content and max-content widths of an inline container
/// with a speculative no-wrap layout, without replacing the layout used for
/// painting. Table column sizing uses these widths.
/// </summary>
internal static class InlineIntrinsicWidthMeasurer
{
    internal static IntrinsicWidthMetrics MeasureIntrinsicWidths(InlineContainerBox box, float maximumPreferredWidth = 65_536f)
    {
        box.ThrowIfCancellationRequested();
        box.EnsureBuffer();

        var style = box.GetContainerStyle();
        var padding = box.GetEffectivePadding(style);
        float horizontalPadding = (float)(padding.Left + padding.Right);
        float measurementWidth = Math.Clamp(maximumPreferredWidth, 1f, 1_000_000f);
        List<InlineMeasureRollbackState>? candidateMeasures = null;
        NativeResourceRetirementRecord? layoutRetirementRecord = null;
        CanvasTextLayout? layout = null;
        IntrinsicWidthMetrics result = default;
        Exception? failure = null;
        try
        {
            _ = InlineAtomicObjectLayout.MeasureAtomicInlineRuns(
                box,
                measurementWidth,
                style.FontSize,
                out candidateMeasures);

            using var formatLease = box.CreateTextFormatLease(
                style,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left);
            layoutRetirementRecord = box.NativeRetirement.Reserve(
                resourceGroupCount: 0,
                temporarySlotCount: 1,
                disposer: box.NativeDisposer);
            layout = CanvasTextLayoutFactory.Create(
                box.Context.ResourceCreator,
                box.LayoutBuffer,
                formatLease.Format,
                measurementWidth,
                float.MaxValue,
                box.Context.LayoutDiagnostics);
            layoutRetirementRecord.SetTemporary(0, layout);
            layout.Options = CanvasDrawTextOptions.EnableColorFont;
            InlineRunStyleApplier.ApplyRunStyles(box, layout, applyColors: false);
            InlineAtomicObjectLayout.ApplyEmbedSpacing(box, layout, maxInlineWidth: measurementWidth);

            var layoutBounds = layout.LayoutBounds;
            var drawBounds = layout.DrawBounds;
            float preferredContent = (float)Math.Max(
                Math.Max(layoutBounds.Width, drawBounds.Width),
                1d);
            preferredContent = Math.Min(preferredContent, measurementWidth);

            float minimumContent = MeasureWidestUnbreakableSegment(box, layout, style.FontSize);
            minimumContent = Math.Clamp(minimumContent, 1f, preferredContent);
            box.ThrowIfCancellationRequested();
            result = new IntrinsicWidthMetrics(
                minimumContent + horizontalPadding,
                preferredContent + horizontalPadding);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Intrinsic sizing is a query, not a committed image layout. Its
            // atomic measurements are speculative even when it succeeds, and
            // must not replace bounds or native placeholder/caption resources
            // belonging to the currently published painting layout.
            try
            {
                if (candidateMeasures is not null)
                {
                    foreach (InlineMeasureRollbackState state in candidateMeasures)
                    {
                        try
                        {
                            state.Restore();
                        }
                        catch (Exception exception)
                        {
                            failure = InlineNativeLayoutRetirement.CombineFailures(
                                "restoring speculative inline measurements",
                                failure,
                                exception);
                        }
                    }
                }
            }
            finally
            {
                // This nested finally ensures a speculative CanvasTextLayout
                // reaches its pre-reserved owner even if restoring inline
                // dimensions or aggregating that failure itself throws.
                Exception? layoutCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                    ref layout,
                    layoutRetirementRecord,
                    "disposing intrinsic inline measurement layout");
                failure = InlineNativeLayoutRetirement.CombineFailures(
                    "measuring intrinsic inline widths",
                    failure,
                    layoutCleanupFailure);
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static float MeasureWidestUnbreakableSegment(InlineContainerBox box, CanvasTextLayout layout, float fallbackFontSize)
    {
        if (box.LayoutBuffer.Length == 0)
            return Math.Max(1f, fallbackFontSize);

        float widest = 0;
        int tokenStart = -1;
        int runIndex = 0;
        int runStart = 0;
        int runEnd = box.RunList.Count > 0 ? box.RunList[0].TextLength : 0;
        for (int index = 0; index <= box.LayoutBuffer.Length; index++)
        {
            while (runIndex < box.RunList.Count && index >= runEnd)
            {
                runStart = runEnd;
                runIndex++;
                if (runIndex < box.RunList.Count)
                    runEnd = Math.Min(box.LayoutBuffer.Length, runStart + box.RunList[runIndex].TextLength);
            }

            InlineRun? currentRun = index < box.LayoutBuffer.Length && runIndex < box.RunList.Count &&
                index >= runStart && index < runEnd
                    ? box.RunList[runIndex]
                    : null;
            bool atomicInlineObject = currentRun is InlineImageRun or InlineSpacerRun;
            bool boundary = index == box.LayoutBuffer.Length || atomicInlineObject || IsIntrinsicBreakOpportunity(box.LayoutBuffer[index]);
            if (!boundary)
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            if (tokenStart >= 0)
            {
                box.Context.CancellationToken.ThrowIfCancellationRequested();
                int length = index - tokenStart;
                var regions = layout.GetCharacterRegions(tokenStart, length);
                if (regions is not null)
                {
                    foreach (var region in regions)
                        widest = Math.Max(widest, (float)region.LayoutBounds.Width);
                }
                tokenStart = -1;
            }

            // Hosted controls and vector scenes are genuinely atomic, so they
            // contribute their full desired width. Images are also atomic for
            // selection and line layout, but their rendering contract is
            // max-width:100%: they may shrink while preserving aspect ratio.
            // Giving an image its full width as a min-content contribution
            // makes an image table column impossible to shrink and pushes its
            // sibling columns out of the viewport.
            if (index < box.LayoutBuffer.Length && box.LayoutBuffer[index] == InlineEmbedRun.PlaceholderChar[0])
            {
                if (currentRun is InlineImageRun image)
                {
                    widest = Math.Max(
                        widest,
                        Math.Min(image.DesiredWidth, Math.Max(1f, fallbackFontSize * 2f)));
                }
                else if (currentRun is InlineSpacerRun spacer)
                {
                    // Match inline images: a replaced object follows the
                    // max-width:100% rule and may shrink to fit a cell. Its
                    // full authored width must not pin a table column at an
                    // enormous min-content size.
                    widest = Math.Max(
                        widest,
                        Math.Min(spacer.Width, Math.Max(1f, fallbackFontSize * 2f)));
                }
                else
                {
                    var regions = layout.GetCharacterRegions(index, 1);
                    if (regions is not null)
                    {
                        foreach (var region in regions)
                            widest = Math.Max(widest, (float)region.LayoutBounds.Width);
                    }
                }
            }
        }

        return Math.Max(widest, Math.Max(1f, fallbackFontSize * 2f));
    }

    private static bool IsIntrinsicBreakOpportunity(char value)
        => char.IsWhiteSpace(value) || value is '-' or '\u2010' or '\u2013' or '/';
}
