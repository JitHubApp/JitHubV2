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

            result = ReadIntrinsicWidths(box, layout, style.FontSize, measurementWidth, horizontalPadding);
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
                    RestoreSpeculativeMeasures(candidateMeasures, ref failure);
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

    // Reads the no-wrap layout's preferred width and the widest unbreakable
    // segment, then observes cancellation before publishing either.
    private static IntrinsicWidthMetrics ReadIntrinsicWidths(
        InlineContainerBox box,
        CanvasTextLayout layout,
        float fontSize,
        float measurementWidth,
        float horizontalPadding)
    {
        var layoutBounds = layout.LayoutBounds;
        var drawBounds = layout.DrawBounds;
        float preferredContent = (float)Math.Max(
            Math.Max(layoutBounds.Width, drawBounds.Width),
            1d);
        preferredContent = Math.Min(preferredContent, measurementWidth);

        float minimumContent = MeasureWidestUnbreakableSegment(box, layout, fontSize);
        minimumContent = Math.Clamp(minimumContent, 1f, preferredContent);
        box.ThrowIfCancellationRequested();
        return new IntrinsicWidthMetrics(
            minimumContent + horizontalPadding,
            preferredContent + horizontalPadding);
    }

    // Restores every speculative atomic measurement, folding each failure
    // into the aggregate so one failure never skips the rest.
    private static void RestoreSpeculativeMeasures(
        List<InlineMeasureRollbackState> candidateMeasures,
        ref Exception? failure)
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
                widest = WidestRegion(layout, tokenStart, length, widest);
                tokenStart = -1;
            }

            if (index < box.LayoutBuffer.Length && box.LayoutBuffer[index] == InlineEmbedRun.PlaceholderChar[0])
                widest = WidestAtomicObject(currentRun, layout, index, fallbackFontSize, widest);
        }

        return Math.Max(widest, Math.Max(1f, fallbackFontSize * 2f));
    }

    // Hosted controls and vector scenes are genuinely atomic, so they
    // contribute their full desired width. Images are also atomic for
    // selection and line layout, but their rendering contract is
    // max-width:100%: they may shrink while preserving aspect ratio.
    // Giving an image its full width as a min-content contribution
    // makes an image table column impossible to shrink and pushes its
    // sibling columns out of the viewport.
    private static float WidestAtomicObject(
        InlineRun? currentRun,
        CanvasTextLayout layout,
        int index,
        float fallbackFontSize,
        float widest)
    {
        if (currentRun is InlineImageRun image)
        {
            return Math.Max(
                widest,
                Math.Min(image.DesiredWidth, Math.Max(1f, fallbackFontSize * 2f)));
        }

        if (currentRun is InlineSpacerRun spacer)
        {
            // Match inline images: a replaced object follows the
            // max-width:100% rule and may shrink to fit a cell. Its
            // full authored width must not pin a table column at an
            // enormous min-content size.
            return Math.Max(
                widest,
                Math.Min(spacer.Width, Math.Max(1f, fallbackFontSize * 2f)));
        }

        return WidestRegion(layout, index, 1, widest);
    }

    private static float WidestRegion(CanvasTextLayout layout, int start, int length, float widest)
    {
        var regions = layout.GetCharacterRegions(start, length);
        if (regions is not null)
        {
            foreach (var region in regions)
                widest = Math.Max(widest, (float)region.LayoutBounds.Width);
        }

        return widest;
    }

    private static bool IsIntrinsicBreakOpportunity(char value)
        => char.IsWhiteSpace(value) || value is '-' or '\u2010' or '\u2013' or '/';
}
