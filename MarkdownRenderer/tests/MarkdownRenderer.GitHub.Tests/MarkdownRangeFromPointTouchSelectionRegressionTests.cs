using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Markdig;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises the same eager semantic-span geometry pass that TextPattern.RangeFromPoint
/// runs before hit testing. The fixture and viewport coordinates are taken from the
/// physical TouchSelection sample, but this regression stays headless and does not
/// depend on UI Automation COM activation.
/// </summary>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MarkdownRangeFromPointTouchSelectionRegressionTests
{
    private const float RendererWidth = 345f;
    private const float RendererHeight = 693f;
    private const double RendererScreenX = 423d;
    private const double RendererScreenY = 175d;
    private const double RecordedScreenX = 580d;
    private const double RecordedScreenY = 184d;
    private const double RecordedVerticalPercent = 33.93162458370894d;
    private const double RecordedVerticalViewSize = 42.5414364640884d;
    private const double RasterizationScale = 1d;
    private const string TargetText = "family";

    [Fact]
    public async Task TouchSelectionRangeFromPointEagerGeometryCompletesForOwnedAndAncestorViewportMapping()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using MarkdownEngine engine = TouchSelectionSampleFixture.CreateEngine();
        Assert.Contains(MarkdownSyntaxKinds.Block.FencedCode, engine.Extensions.BlockSyntaxKinds);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        TouchSelectionHostedElementExtension.Reset();
        MarkdownRenderer.Document.MarkdownDocument parsed = await engine.ParseAsync(TouchSelectionSampleFixture.Source);
        AssertHostedElementFenceWasParsed(engine, parsed);
        var context = TouchSelectionSampleFixture.CreateContext(device, registry);
        using LayoutSnapshot snapshot = TouchSelectionSampleFixture.BuildSnapshot(context, parsed, RendererWidth);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;

        Assert.False(snapshot.IsLazyLayoutEnabled);
        Assert.True(snapshot.Blocks.Count > 10);
        AssertHostedElementSpanText(semantic);

        // RangeFromPoint enumerates every semantic span before hit testing,
        // not just the visible target. Run the same span callback and placement
        // mapping independently so a failure names the exact span and stage.
        SemanticSpanGeometryDiagnostics.AssertEveryTextSpanHasConsumableGeometry(snapshot, semantic);

        int targetStart = semantic.Text.IndexOf(TargetText, StringComparison.Ordinal);
        Assert.True(targetStart >= 0, "The TouchSelection fixture must expose its ordinary 'family' text span.");
        int targetEnd = targetStart + TargetText.Length;
        Rect target = Union(semantic.GetDocumentRects(targetStart, targetEnd, expandDegenerate: false).ToArray());
        Assert.True(target.Width > 0 && target.Height > 0, $"Target span had no geometry: {target}.");

        double recordedScrollOffset = GetRecordedVerticalOffset(RendererHeight);
        (Point recordedOwnedDocumentPoint, Point recordedAncestorDocumentPoint) =
            MapRecordedPointThroughBothViewports(recordedScrollOffset);
        (Point targetOwnedDocumentPoint, Point targetAncestorDocumentPoint) =
            MapTargetCenterThroughBothViewports(target, recordedScrollOffset);

        // The live point is the important reproduction input: the owned ScrollViewer
        // supplies its vertical offset, while an ancestor ScrollViewer has already
        // moved the owner origin and therefore contributes no inner scroll offset.
        ProbeRangeFromPointEquivalent(
            snapshot,
            semantic,
            recordedOwnedDocumentPoint,
            "recorded point / renderer-owned ScrollViewer");
        ProbeRangeFromPointEquivalent(
            snapshot,
            semantic,
            recordedAncestorDocumentPoint,
            "recorded point / ancestor ScrollViewer");

        HitResult ownedTarget = ProbeRangeFromPointEquivalent(
            snapshot,
            semantic,
            targetOwnedDocumentPoint,
            "native target center / renderer-owned ScrollViewer");
        HitResult ancestorTarget = ProbeRangeFromPointEquivalent(
            snapshot,
            semantic,
            targetAncestorDocumentPoint,
            "native target center / ancestor ScrollViewer");

        Assert.InRange(ownedTarget.TextOffset, targetStart, targetEnd - 1);
        Assert.InRange(ancestorTarget.TextOffset, targetStart, targetEnd - 1);
    }

    private static void AssertHostedElementFenceWasParsed(
        MarkdownEngine engine,
        MarkdownRenderer.Document.MarkdownDocument parsed)
    {
        Assert.True(
            TouchSelectionHostedElementExtension.InvocationCount > 0,
            $"The parser did not invoke the registered fenced-code extension. Registered block kinds: " +
            string.Join(", ", engine.Extensions.BlockSyntaxKinds));
        Assert.Contains(
            TouchSelectionHostedElementExtension.ObservedInfo,
            static info => info.StartsWith("button:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            parsed.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items),
            static content => content.Kind == MarkdownContentKind.HostedElement &&
                              content.AccessibilityName == "Touch hosted action");
    }

    private static void AssertHostedElementSpanText(MarkdownSemanticDocument semantic)
    {
        MarkdownTextSpan hostedSpan = Assert.Single(
            semantic.TextSpans,
            static span => span.HostedElementBox is not null &&
                           span.HostedElementBox.AccessibilityName == "Touch hosted action");
        Assert.Equal("Touch hosted action", semantic.Text.Substring(
            hostedSpan.TextStart,
            hostedSpan.TextEnd - hostedSpan.TextStart));
    }

    private static (Point Owned, Point Ancestor) MapRecordedPointThroughBothViewports(double recordedScrollOffset)
    {
        Point recordedOwnedDocumentPoint = ScreenToDocumentPoint(
            new Point(RecordedScreenX, RecordedScreenY),
            ownerScreenX: RendererScreenX,
            ownerScreenY: RendererScreenY,
            scrollOffset: recordedScrollOffset);
        Point recordedAncestorDocumentPoint = ScreenToDocumentPoint(
            new Point(RecordedScreenX, RecordedScreenY),
            ownerScreenX: RendererScreenX,
            ownerScreenY: RendererScreenY - recordedScrollOffset,
            scrollOffset: 0);

        Assert.Equal(recordedOwnedDocumentPoint.X, recordedAncestorDocumentPoint.X, precision: 4);
        Assert.Equal(recordedOwnedDocumentPoint.Y, recordedAncestorDocumentPoint.Y, precision: 4);
        return (recordedOwnedDocumentPoint, recordedAncestorDocumentPoint);
    }

    private static (Point Owned, Point Ancestor) MapTargetCenterThroughBothViewports(
        Rect target,
        double recordedScrollOffset)
    {
        // Reproduce the captured UIA point as well as a point computed from this
        // exact native layout. The latter proves that coercion and hit testing
        // work even if theme/font differences move the live sample's text a few DIPs.
        Point targetScreenPoint = new(
            RendererScreenX + target.X * RasterizationScale + target.Width * RasterizationScale / 2,
            RendererScreenY + (target.Y - recordedScrollOffset) * RasterizationScale +
                target.Height * RasterizationScale / 2);
        Point targetOwnedDocumentPoint = ScreenToDocumentPoint(
            targetScreenPoint,
            ownerScreenX: RendererScreenX,
            ownerScreenY: RendererScreenY,
            scrollOffset: recordedScrollOffset);
        Point targetAncestorDocumentPoint = ScreenToDocumentPoint(
            targetScreenPoint,
            ownerScreenX: RendererScreenX,
            ownerScreenY: RendererScreenY - recordedScrollOffset,
            scrollOffset: 0);

        Assert.Equal(target.X + target.Width / 2, targetOwnedDocumentPoint.X, precision: 3);
        Assert.Equal(target.Y + target.Height / 2, targetOwnedDocumentPoint.Y, precision: 3);
        Assert.Equal(targetOwnedDocumentPoint.X, targetAncestorDocumentPoint.X, precision: 3);
        Assert.Equal(targetOwnedDocumentPoint.Y, targetAncestorDocumentPoint.Y, precision: 3);
        Assert.InRange(targetScreenPoint.X, RendererScreenX, RendererScreenX + RendererWidth);
        Assert.InRange(targetScreenPoint.Y, RendererScreenY, RendererScreenY + RendererHeight);
        return (targetOwnedDocumentPoint, targetAncestorDocumentPoint);
    }

    [Fact]
    public async Task ClippedMermaidRangesProduceNoScreenBoundsAndCollapsedCaretRemainsEmpty()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using MarkdownEngine engine = TouchSelectionSampleFixture.CreateEngine();
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        TouchSelectionHostedElementExtension.Reset();
        MarkdownRenderer.Document.MarkdownDocument parsed = await engine.ParseAsync(TouchSelectionSampleFixture.Source);
        Assert.True(TouchSelectionHostedElementExtension.InvocationCount > 0);
        var context = TouchSelectionSampleFixture.CreateContext(device, registry);
        using LayoutSnapshot snapshot = TouchSelectionSampleFixture.BuildSnapshot(context, parsed, RendererWidth);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        Assert.False(snapshot.IsLazyLayoutEnabled);

        MarkdownTextSpan[] clippedDiagramSpans = semantic.TextSpans
            .Where(static span => span.VectorSceneBox is not null)
            .Where(span => semantic.Text.AsSpan(span.TextStart, span.TextEnd - span.TextStart)
                .Trim()
                is var text &&
                (text.SequenceEqual("Word selection") ||
                 text.SequenceEqual("44 epx handles") ||
                 text.SequenceEqual("Native menu")))
            .ToArray();
        Assert.Equal(3, clippedDiagramSpans.Length);

        // These three real Mermaid accessibility spans are outside the scene's
        // clipped content. Exercise the same committed-placement transform used
        // by TextRange.GetBoundingRectangles, rather than testing Rect.Empty alone.
        Assert.True(snapshot.TryBeginInteraction(), "Could not acquire layout interaction lease for Mermaid bounds.");
        try
        {
            // No owner and no clip source: an empty rect must return before either is read.
            var emptyGeometry = new MarkdownAutomationScreenGeometry(owner: null!, clip: null!);
            Rect visibleScreenBounds = new(
                RendererScreenX,
                RendererScreenY,
                RendererWidth,
                RendererHeight);

            foreach (MarkdownTextSpan span in clippedDiagramSpans)
                AssertClippedSpanHasNoScreenBounds(snapshot, semantic, span, emptyGeometry, visibleScreenBounds);

            AssertCollapsedCaretPublishesNoBounds(snapshot, semantic, visibleScreenBounds);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static void AssertClippedSpanHasNoScreenBounds(
        LayoutSnapshot snapshot,
        MarkdownSemanticDocument semantic,
        MarkdownTextSpan span,
        MarkdownAutomationScreenGeometry emptyGeometry,
        Rect visibleScreenBounds)
    {
        Rect[] documentRects = semantic.GetDocumentRects(
                span.TextStart,
                span.TextEnd,
                expandDegenerate: false,
                placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
                placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint)
            .ToArray();
        Assert.NotEmpty(documentRects);
        Assert.All(documentRects, static rect => Assert.True(rect.IsEmpty));

        Rect[] scrollIntoViewRects = semantic.GetDocumentRects(
                span.TextStart,
                span.TextEnd,
                expandDegenerate: true,
                placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
                placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint)
            .ToArray();
        Assert.NotEmpty(scrollIntoViewRects);
        Assert.All(scrollIntoViewRects, static rect => Assert.True(rect.IsEmpty));
        Assert.False(MarkdownTextRangeGeometry.TryGetFirstNonEmptyScrollRect(
            scrollIntoViewRects,
            out Rect scrollTarget));
        Assert.Equal(default, scrollTarget);
        Assert.True(semantic.TryGetDocumentRange(span.TextStart, span.TextEnd, out DocumentRange range));
        Assert.Equal(span.LayoutBlockIndex, range.Normalized().Start.BlockIndex);

        // The empty case is deliberately invoked on geometry without an owner:
        // its production early return must happen before owner/XamlRoot access.
        Rect[] screenRects = documentRects
            .Select(emptyGeometry.GetScreenRectForDocumentRect)
            .ToArray();
        Assert.All(screenRects, static rect => Assert.True(rect.IsEmpty));
        Assert.Empty(MarkdownTextRangeGeometry.BuildVisibleBoundingRectangles(
            span.TextStart,
            span.TextEnd,
            screenRects,
            visibleScreenBounds));
    }

    private static void AssertCollapsedCaretPublishesNoBounds(
        LayoutSnapshot snapshot,
        MarkdownSemanticDocument semantic,
        Rect visibleScreenBounds)
    {
    // The provider intentionally publishes no bounding rectangle for a
    // collapsed range even when its geometry source can provide a caret box.
    int caretOffset = semantic.Text.IndexOf(TargetText, StringComparison.Ordinal) + 2;
    Assert.InRange(caretOffset, 1, semantic.Text.Length - 1);
    Rect[] expandedCaretRects = semantic.GetDocumentRects(
            caretOffset,
            caretOffset,
            expandDegenerate: true,
            placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
            placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint)
        .ToArray();
    Assert.NotEmpty(expandedCaretRects);
    Assert.Contains(expandedCaretRects, static rect =>
        !rect.IsEmpty &&
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.Height > 0);

    Rect finiteZeroWidthCaret = new(RendererScreenX + 20, RendererScreenY + 20, 0, 18);
    Assert.False(finiteZeroWidthCaret.IsEmpty);
    Assert.True(MarkdownTextRangeGeometry.TryGetFirstNonEmptyScrollRect(
        [Rect.Empty, finiteZeroWidthCaret],
        out Rect selectedCaret));
    Assert.Equal(finiteZeroWidthCaret, selectedCaret);
    Assert.False(MarkdownTextRangeGeometry.TryGetFirstNonEmptyScrollRect(
        [Rect.Empty, Rect.Empty],
        out Rect noScrollTarget));
    Assert.Equal(default, noScrollTarget);

    Assert.Empty(MarkdownTextRangeGeometry.BuildVisibleBoundingRectangles(
        caretOffset,
        caretOffset,
        [new Rect(RendererScreenX + 20, RendererScreenY + 20, 0, 18)],
        visibleScreenBounds));
    }

    private static HitResult ProbeRangeFromPointEquivalent(
        LayoutSnapshot snapshot,
        MarkdownSemanticDocument semantic,
        Point documentPoint,
        string path)
    {
        Assert.True(snapshot.TryBeginInteraction(), $"Could not acquire layout interaction lease for {path}.");
        try
        {
            bool found;
            Point coerced;
            try
            {
                // This is the exact pre-hit-test geometry callback used by
                // MarkdownAutomationPeer.RangeFromPoint while holding its lease.
                found = semantic.TryCoercePointToNearestTextRect(
                    documentPoint,
                    snapshot.ToLogicalRectForBlockIndexUnderPaint,
                    snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint,
                    out coerced);
            }
            catch (Exception exception)
            {
                string failingSpans = SemanticSpanGeometryDiagnostics.DescribeFailingSpans(snapshot, semantic);
                throw new Xunit.Sdk.XunitException(
                    $"RangeFromPoint-equivalent semantic geometry failed on {path}; " +
                    $"callback=MarkdownSemanticDocument.TryCoercePointToNearestTextRect -> " +
                    $"EnumerateTextRects -> GetDocumentRectsForSpan. " +
                    $"point={documentPoint}; original={exception}; isolated span diagnostics: {failingSpans}");
            }

            Assert.True(found, $"No text rectangle was available for {path} at {documentPoint}.");
            bool hit = snapshot.TryHitTestVectorText(coerced, out DocumentPosition position) ||
                       snapshot.HitTest(coerced, out position);
            Assert.True(hit,
                $"The semantic span pass succeeded but the native snapshot hit test missed {path}. " +
                $"raw={documentPoint}; coerced={coerced}; spanCount={semantic.TextSpans.Count}; textLength={semantic.Text.Length}.");
            int textOffset = semantic.TextOffsetFromDocumentPosition(position);
            Assert.InRange(textOffset, 0, semantic.Text.Length);
            return new HitResult(coerced, position, textOffset);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static Point ScreenToDocumentPoint(
        Point screen,
        double ownerScreenX,
        double ownerScreenY,
        double scrollOffset) =>
        new(
            (screen.X - ownerScreenX) / RasterizationScale,
            (screen.Y - ownerScreenY) / RasterizationScale + scrollOffset);

    private static double GetRecordedVerticalOffset(double viewportHeight)
    {
        double extent = viewportHeight / (RecordedVerticalViewSize / 100d);
        double scrollableHeight = global::System.Math.Max(0, extent - viewportHeight);
        return scrollableHeight * RecordedVerticalPercent / 100d;
    }

    private static Rect Union(IReadOnlyList<Rect> rectangles)
    {
        Assert.NotEmpty(rectangles);
        double left = rectangles.Min(static rect => rect.Left);
        double top = rectangles.Min(static rect => rect.Top);
        double right = rectangles.Max(static rect => rect.Right);
        double bottom = rectangles.Max(static rect => rect.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private readonly record struct HitResult(Point Coerced, DocumentPosition Position, int TextOffset);
}
