using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Markdig;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
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

    // Match the sample's default light MarkdownTheme rather than supplying a
    // convenient but materially different 16-DIP body style.  Geometry around
    // a captured point depends on headings, tables, quotes, lists, and code
    // blocks preceding the target as much as on the target paragraph itself.
    private static readonly string[] SampleDefaultThemeRoles =
    [
        MarkdownElementKeys.Heading1, MarkdownElementKeys.Heading2,
        MarkdownElementKeys.Heading3, MarkdownElementKeys.Heading4,
        MarkdownElementKeys.Heading5, MarkdownElementKeys.Heading6,
        MarkdownElementKeys.Body, MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock, MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockLanguage, MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber, MarkdownElementKeys.Quote,
        MarkdownElementKeys.Link, MarkdownElementKeys.Strong,
        MarkdownElementKeys.Emphasis, MarkdownElementKeys.Strikethrough,
        MarkdownElementKeys.Subscript, MarkdownElementKeys.Superscript,
        MarkdownElementKeys.Inserted, MarkdownElementKeys.Marked,
        MarkdownElementKeys.Abbreviation, MarkdownElementKeys.ListMarker,
        MarkdownElementKeys.ThematicBreak, MarkdownElementKeys.ImageCaption,
        MarkdownElementKeys.Figure, MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.Diagram, MarkdownElementKeys.Math,
        MarkdownElementKeys.DefinitionTerm, MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Table, MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell, MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip, MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning, MarkdownElementKeys.AlertCaution,
    ];

    private const string TouchSelectionSource = """
        # Touch selection

        Use one finger to scroll. **Press and hold a word** or **double-tap it**
        to select it, then drag either round handle to extend the range. Release
        a long press—or tap inside an existing selection—to open the native Copy,
        Copy as Markdown, and Select All menu.

        A plain tap keeps its normal meaning: [this link remains touchable](https://example.invalid/touch-link),
        while tapping elsewhere clears an active selection.

        ## Unicode and mixed direction

        Drag across emoji and combining text without splitting a visible character:
        👩🏽‍💻 family 👨‍👩‍👧‍👦 and café written as café.

        Mixed bidi text remains logically selectable: English before العربية שלום
        and English after. اسحب مقابض التحديد عبر هذا السطر لاختبار الاتجاه المختلط.

        ## Formatting boundaries

        Selection crosses **bold words**, *emphasized words*, `inline code`, and
        [linked labels](https://learn.microsoft.com/windows/apps/design/input/).

        | Surface | Expected touch behavior |
        |---|---|
        | Body text | Hold or double-tap selects a word |
        | Link label | Hold selects; tap activates |
        | Table text | Handles cross cell content atomically |
        | Hosted control | Receives direct touch input |
        | Diagram | Non-selectable regions remain direct interaction targets |

        - [ ] Native task checkbox remains independently touchable

        ```button:Touch_hosted_action
        ```

        ```csharp
        // Code remains selectable, while its Copy button remains a native control.
        string greeting = "Touch selection stays grapheme-safe";
        ```

        ```mermaid
        flowchart LR
            Pan[One-finger pan] --> Hold[Long press]
            Hold --> Word[Word selection]
            Word --> Handles[44 epx handles]
            Handles --> Copy[Native menu]
        ```

        ## Edge-scroll exercise

        Select a word above, drag the end handle into the bottom edge band, and
        hold your finger still. The viewport must continue scrolling while the
        endpoint extends. Repeat upward after crossing the handles.

        1. This paragraph provides distance for continuous edge scrolling.
        2. Selection must continue across list items and inline formatting.
        3. The document surface must not relayout or repaint its tiles per frame.
        4. Scroll inertia must remain owned by the platform ScrollViewer.
        5. Canceling the gesture must leave no capture, timer, flyout, or glyph residue.

        > Touch testing should also cover Light, Dark, Windows High Contrast,
        > 100%, 150%, and 200% display scaling, narrow snap widths, and RTL flow.

        Final selection target: **stationary edge dragging continues here**.
        """;

    private static readonly MethodInfo GetDocumentRectsForSpan = typeof(MarkdownSemanticDocument)
        .GetMethod("GetDocumentRectsForSpan", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find the semantic span geometry callback.");

    [Fact]
    public async Task TouchSelectionRangeFromPointEagerGeometryCompletesForOwnedAndAncestorViewportMapping()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .UseMarkdownExtra()
            .UseMathematics()
            .UseMermaid()
            .UseExtension(TestHostedElementExtension.Instance)
            .Build();
        Assert.Contains(MarkdownSyntaxKinds.Block.FencedCode, engine.Extensions.BlockSyntaxKinds);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        TestHostedElementExtension.Reset();
        MarkdownRenderer.Document.MarkdownDocument parsed = await engine.ParseAsync(TouchSelectionSource);
        Assert.True(
            TestHostedElementExtension.InvocationCount > 0,
            $"The parser did not invoke the registered fenced-code extension. Registered block kinds: " +
            string.Join(", ", engine.Extensions.BlockSyntaxKinds));
        Assert.Contains(
            TestHostedElementExtension.ObservedInfo,
            static info => info.StartsWith("button:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            parsed.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items),
            static content => content.Kind == MarkdownContentKind.HostedElement &&
                              content.AccessibilityName == "Touch hosted action");
        var context = CreateContext(device, registry);
        using LayoutSnapshot snapshot = new LayoutBuilder(
                context,
                enableDeclarativeHostedElements: true,
                semanticDocument: parsed)
            .Build(parsed.ParsedDocument!, RendererWidth);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;

        Assert.False(snapshot.IsLazyLayoutEnabled);
        Assert.True(snapshot.Blocks.Count > 10);
        MarkdownTextSpan hostedSpan = Assert.Single(
            semantic.TextSpans,
            static span => span.HostedElementBox is not null &&
                           span.HostedElementBox.AccessibilityName == "Touch hosted action");
        Assert.Equal("Touch hosted action", semantic.Text.Substring(
            hostedSpan.TextStart,
            hostedSpan.TextEnd - hostedSpan.TextStart));

        // RangeFromPoint enumerates every semantic span before hit testing,
        // not just the visible target. Run the same span callback and placement
        // mapping independently so a failure names the exact span and stage.
        AssertEveryTextSpanHasConsumableGeometry(snapshot, semantic);

        int targetStart = semantic.Text.IndexOf(TargetText, StringComparison.Ordinal);
        Assert.True(targetStart >= 0, "The TouchSelection fixture must expose its ordinary 'family' text span.");
        int targetEnd = targetStart + TargetText.Length;
        Rect target = Union(semantic.GetDocumentRects(targetStart, targetEnd, expandDegenerate: false).ToArray());
        Assert.True(target.Width > 0 && target.Height > 0, $"Target span had no geometry: {target}.");

        double recordedScrollOffset = GetRecordedVerticalOffset(RendererHeight);
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

    [Fact]
    public async Task ClippedMermaidRangesProduceNoScreenBoundsAndCollapsedCaretRemainsEmpty()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .UseMarkdownExtra()
            .UseMathematics()
            .UseMermaid()
            .UseExtension(TestHostedElementExtension.Instance)
            .Build();
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        TestHostedElementExtension.Reset();
        MarkdownRenderer.Document.MarkdownDocument parsed = await engine.ParseAsync(TouchSelectionSource);
        Assert.True(TestHostedElementExtension.InvocationCount > 0);
        var context = CreateContext(device, registry);
        using LayoutSnapshot snapshot = new LayoutBuilder(
                context,
                enableDeclarativeHostedElements: true,
                semanticDocument: parsed)
            .Build(parsed.ParsedDocument!, RendererWidth);
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
            var emptyPeer = (MarkdownAutomationPeer)RuntimeHelpers.GetUninitializedObject(typeof(MarkdownAutomationPeer));
            Rect visibleScreenBounds = new(
                RendererScreenX,
                RendererScreenY,
                RendererWidth,
                RendererHeight);

            foreach (MarkdownTextSpan span in clippedDiagramSpans)
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
                Assert.False(MarkdownTextRangeProvider.TryGetFirstNonEmptyScrollRect(
                    scrollIntoViewRects,
                    out Rect scrollTarget));
                Assert.Equal(default, scrollTarget);
                Assert.True(semantic.TryGetDocumentRange(span.TextStart, span.TextEnd, out DocumentRange range));
                Assert.Equal(span.LayoutBlockIndex, range.Normalized().Start.BlockIndex);

                // The empty case is deliberately invoked on an uninitialized peer:
                // its production early return must happen before owner/XamlRoot access.
                Rect[] screenRects = documentRects
                    .Select(emptyPeer.GetScreenRectForDocumentRect)
                    .ToArray();
                Assert.All(screenRects, static rect => Assert.True(rect.IsEmpty));
                Assert.Empty(MarkdownTextRangeProvider.BuildVisibleBoundingRectangles(
                    span.TextStart,
                    span.TextEnd,
                    screenRects,
                    visibleScreenBounds));
            }

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
            Assert.True(MarkdownTextRangeProvider.TryGetFirstNonEmptyScrollRect(
                [Rect.Empty, finiteZeroWidthCaret],
                out Rect selectedCaret));
            Assert.Equal(finiteZeroWidthCaret, selectedCaret);
            Assert.False(MarkdownTextRangeProvider.TryGetFirstNonEmptyScrollRect(
                [Rect.Empty, Rect.Empty],
                out Rect noScrollTarget));
            Assert.Equal(default, noScrollTarget);

            Assert.Empty(MarkdownTextRangeProvider.BuildVisibleBoundingRectangles(
                caretOffset,
                caretOffset,
                [new Rect(RendererScreenX + 20, RendererScreenY + 20, 0, 18)],
                visibleScreenBounds));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        MarkdownExtensionRegistry registry)
    {
        ThemeResolver.PlatformThemeColors platformColors =
            ThemeResolver.ResolvePlatformThemeColorsForTesting(ElementTheme.Light, default);
        FontWeight semiBold = new() { Weight = 600 };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal);
        foreach (string role in SampleDefaultThemeRoles)
        {
            styles.Add(
                role,
                ThemeResolver.CreateDefaultStyle(
                    role,
                    isDark: false,
                    isHighContrast: false,
                    accentColor: null,
                    platformColors: platformColors,
                    semiBoldFontWeight: semiBold,
                    systemTheme: null));
        }

        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: null);
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(TouchSelectionSource),
            registry,
            FlowDirection.LeftToRight);
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
                string failingSpans = DiagnoseSpanGeometry(snapshot, semantic);
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

    private static string DiagnoseSpanGeometry(LayoutSnapshot snapshot, MarkdownSemanticDocument semantic)
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
                var rectangles = ((IEnumerable<Rect>)GetDocumentRectsForSpan.Invoke(
                        null,
                        [span, span.TextStart, span.TextEnd, logicalInlineCoordinates])!)
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

    private static void AssertEveryTextSpanHasConsumableGeometry(
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
                    IEnumerable<Rect> sourceRects = (IEnumerable<Rect>)GetDocumentRectsForSpan.Invoke(
                        null,
                        [span, span.TextStart, span.TextEnd, logicalInlineCoordinates])!;

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
                    Exception cause = exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception;
                    throw new Xunit.Sdk.XunitException(
                        $"Eager semantic range geometry failed at {stage}; {DescribeSpan(semantic, index, span)}; " +
                        $"exception={cause.GetType().FullName}; hresult=0x{cause.HResult:X8}; message={cause.Message}");
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

    private sealed class TestHostedElementExtension : MarkdownRenderer.Extensions.IMarkdownExtension
    {
        internal static TestHostedElementExtension Instance { get; } = new();
        private static int _invocationCount;
        private static string[] _observedInfo = [];

        internal static int InvocationCount => Volatile.Read(ref _invocationCount);
        internal static IReadOnlyList<string> ObservedInfo => Volatile.Read(ref _observedInfo);

        internal static void Reset()
        {
            Volatile.Write(ref _invocationCount, 0);
            Volatile.Write(ref _observedInfo, []);
        }

        public string Id => "MarkdownRenderer.GitHub.Tests.TouchSelectionHostedElement";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder
                .AddFeature("test-touch-selection-hosted-element")
                .RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, RenderFence);

        private static void RenderFence(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            Interlocked.Increment(ref _invocationCount);
            string info = context.Node.Attributes.TryGetValue("info", out string? observed)
                ? observed
                : "<missing-info>";
            while (true)
            {
                string[] current = Volatile.Read(ref _observedInfo);
                string[] updated = [.. current, info];
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _observedInfo, updated, current),
                    current))
                {
                    break;
                }
            }

            if (!info.StartsWith("button:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string label = info["button:".Length..].Trim().Replace('_', ' ');
            content.AddHostedElement(
                factoryKey: "MarkdownRenderer.Sample.Button",
                sourceSpan: context.Node.SourceSpan,
                styleRole: MarkdownStyleRole.Body,
                accessibilityRole: MarkdownAccessibilityRole.Group,
                attributes: new Dictionary<string, string>
                {
                    [MarkdownHostedElementAttributes.DesiredHeight] = "48",
                    ["label"] = label,
                },
                accessibilityName: label,
                accessibilityDescription: "Native viewport-realized Markdown sample content",
                semanticText: label);
        }
    }
}
