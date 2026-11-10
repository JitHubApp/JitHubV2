using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Layout;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Runs the per-span geometry callback that UI Automation range queries use,
/// one semantic span at a time, so a failure names the exact span and stage.
/// </summary>
internal static class SemanticSpanGeometryDiagnostics
{
    public static string DescribeFailingSpans(LayoutSnapshot snapshot, MarkdownSemanticDocument semantic)
    {
        var failures = new List<string>();
        for (int index = 0; index < semantic.TextSpans.Count; index++)
        {
            MarkdownTextSpan span = semantic.TextSpans[index];
            try
            {
                using LayoutPlacementTransformContext.Scope scope =
                    snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(span.LayoutBlockIndex);
                bool logicalInlineCoordinates = span.InlineBox is not null;
                var rectangles = MarkdownTextSpanProjection.GetDocumentRectsForSpan(
                        span, span.TextStart, span.TextEnd, logicalInlineCoordinates)
                    .Select(rect => span.InlineBox is not null
                        ? rect
                        : snapshot.ToLogicalRectForBlockIndexUnderPaint(span.LayoutBlockIndex, rect))
                    .ToArray();
                if (rectangles.Any(static rect =>
                        !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
                        !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)))
                {
                    failures.Add($"span[{index}] produced a non-finite rect; {DescribeSpan(semantic, index, span)}");
                }
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"span[{index}] callback=GetDocumentRectsForSpan threw {exception.GetType().FullName}: {exception.Message}; " +
                    DescribeSpan(semantic, index, span));
            }
        }

        if (failures.Count == 0)
            return $"No isolated span failed across {semantic.TextSpans.Count} spans; " +
                   "the exception is in combined rect coercion or the peer/UIA bridge.";
        return string.Join(" | ", failures);
    }

    public static void AssertEveryTextSpanHasConsumableGeometry(
        LayoutSnapshot snapshot,
        MarkdownSemanticDocument semantic)
    {
        Assert.True(snapshot.TryBeginInteraction(), "Could not acquire layout interaction lease for the eager span pass.");
        try
        {
            for (int index = 0; index < semantic.TextSpans.Count; index++)
            {
                MarkdownTextSpan span = semantic.TextSpans[index];
                string stage = "enter-placement-scope";
                try
                {
                    using LayoutPlacementTransformContext.Scope scope =
                        snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(span.LayoutBlockIndex);
                    bool logicalInlineCoordinates = span.InlineBox is not null;

                    stage = "invoke-get-document-rects-for-span";
                    IEnumerable<Rect> sourceRects = MarkdownTextSpanProjection.GetDocumentRectsForSpan(
                        span, span.TextStart, span.TextEnd, logicalInlineCoordinates);

                    stage = "enumerate-span-rectangles";
                    Rect[] physicalRectangles = sourceRects.ToArray();
                    var mappedRectangles = new List<Rect>(physicalRectangles.Length);
                    for (int rectIndex = 0; rectIndex < physicalRectangles.Length; rectIndex++)
                    {
                        Rect physicalRect = physicalRectangles[rectIndex];
                        // A vector semantic region may be clipped completely
                        // outside the scene's visible content. Rect.Empty is a
                        // valid no-geometry result and must not be sent through
                        // the logical-placement mapper as ordinary coordinates.
                        if (physicalRect.IsEmpty)
                            continue;

                        try
                        {
                            mappedRectangles.Add(logicalInlineCoordinates
                                ? physicalRect
                                : snapshot.ToLogicalRectForBlockIndexUnderPaint(
                                    span.LayoutBlockIndex,
                                    physicalRect));
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                $"Placement mapping rejected span rect[{rectIndex}]=" +
                                $"({physicalRect.X:R},{physicalRect.Y:R},{physicalRect.Width:R},{physicalRect.Height:R}); " +
                                $"logicalInlineCoordinates={logicalInlineCoordinates}.",
                                exception);
                        }
                    }

                    stage = "validate-mapped-span-rectangles";
                    Rect[] rectangles = mappedRectangles.ToArray();

                    Assert.DoesNotContain(rectangles, static rect =>
                        !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
                        !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height));
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Eager semantic range geometry failed at {stage}; {DescribeSpan(semantic, index, span)}; " +
                        $"exception={exception.GetType().FullName}; hresult=0x{exception.HResult:X8}; message={exception.Message}");
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static string DescribeSpan(MarkdownSemanticDocument semantic, int index, MarkdownTextSpan span)
    {
        int start = global::System.Math.Clamp(span.TextStart, 0, semantic.Text.Length);
        int length = global::System.Math.Clamp(span.TextEnd - start, 0, semantic.Text.Length - start);
        string text = semantic.Text.Substring(start, length)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        if (text.Length > 100)
            text = text[..100] + "…";
        string run = span.InlineRun is { } inlineRun
            ? $"{inlineRun.GetType().Name}(inline={inlineRun.InlineIndex}, rendered={inlineRun.RenderedLength}, " +
              $"layout={inlineRun.LayoutTextLength}, text='{inlineRun.Text}')"
            : "<no-inline-run>";
        return $"index={index}; range=[{span.TextStart},{span.TextEnd}); block={span.LayoutBlockIndex}; " +
               $"owner={OwnerKind(span)}; run={run}; semantic='{text}'";
    }

    private static string OwnerKind(MarkdownTextSpan span) =>
        span.InlineBox is not null ? nameof(span.InlineBox) :
        span.ImageBox is not null ? nameof(span.ImageBox) :
        span.EmbedBox is not null ? nameof(span.EmbedBox) :
        span.HostedElementBox is not null ? nameof(span.HostedElementBox) :
        span.VectorSceneBox is not null ? nameof(span.VectorSceneBox) :
        "<no-layout-owner>";
}
