using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using Markdig;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Native-layout regression oracle for lazy placement. The optimized snapshot
/// realizes a viewport band normally; the comparison snapshot is then forced
/// through the private full-prefix placement pass. This intentionally compares
/// the actual DirectWrite/Win2D tree rather than callback-only fake boxes.
/// </summary>
public sealed class LayoutSnapshotLazyNativeParityTests
{
    private const float LayoutWidth = 640f;
    private const double InitialViewportHeight = 104;

    [Theory]
    [InlineData(1f, false, false)]
    [InlineData(1f, false, true)]
    [InlineData(1f, true, false)]
    [InlineData(1f, true, true)]
    [InlineData(1.5f, false, false)]
    [InlineData(1.5f, false, true)]
    [InlineData(1.5f, true, false)]
    [InlineData(1.5f, true, true)]
    [InlineData(2f, false, false)]
    [InlineData(2f, false, true)]
    [InlineData(2f, true, false)]
    [InlineData(2f, true, true)]
    [InlineData(4f, false, false)]
    [InlineData(4f, false, true)]
    [InlineData(4f, true, false)]
    [InlineData(4f, true, true)]
    public void NativeLazyPlacementMatchesFullPrefixRelayoutAcrossScaleDirectionAndDevice(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        var registry = new MarkdownExtensionRegistry();
        registry.ConfigureGfmRegistry();
        MarkdownPipeline pipeline = registry.BuildPipeline();
        MarkdownStyleSheet sheet = GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet;
        FlowDirection flowDirection = rightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        string source = CreateSource();
        object documentIdentity = new();
        var key = CreateMetricsKey(
            source,
            documentIdentity,
            registry,
            sheet,
            rasterizationScale,
            flowDirection);

        // Populate exact per-root measurements first. This exercises the
        // unchanged-geometry/no-reflow path after distant list/quote roots are
        // realized, without making the lazy snapshots share native objects.
        using (LayoutSnapshot seed = Build(
            source,
            pipeline,
            registry,
            device,
            sheet,
            rasterizationScale,
            flowDirection,
            sharedEngine: engine,
            metricsKey: key,
            lazy: false))
        {
            Assert.True(seed.Size.Height > InitialViewportHeight);
        }

        using (LayoutSnapshot exactOptimized = Build(
            source,
            pipeline,
            registry,
            device,
            sheet,
            rasterizationScale,
            flowDirection,
            sharedEngine: engine,
            metricsKey: key,
            lazy: true))
        using (LayoutSnapshot exactBaseline = Build(
            source,
            pipeline,
            registry,
            device,
            sheet,
            rasterizationScale,
            flowDirection,
            sharedEngine: engine,
            metricsKey: key,
            lazy: true))
        {
            AssertBandMatchesFullPrefix(
                exactOptimized,
                exactBaseline,
                device,
                rasterizationScale,
                expectExactEstimate: true);
        }

        // An unseeded metrics buffer uses conservative height estimates. The
        // long list item guarantees a real measured-height correction and thus
        // exercises the changed-suffix path before comparing against full reflow.
        using (LayoutSnapshot suffixOptimized = Build(
            source,
            pipeline,
            registry,
            device,
            sheet,
            rasterizationScale,
            flowDirection,
            sharedEngine: null,
            metricsKey: null,
            lazy: true))
        using (LayoutSnapshot suffixBaseline = Build(
            source,
            pipeline,
            registry,
            device,
            sheet,
            rasterizationScale,
            flowDirection,
            sharedEngine: null,
            metricsKey: null,
            lazy: true))
        {
            AssertBandMatchesFullPrefix(
                suffixOptimized,
                suffixBaseline,
                device,
                rasterizationScale,
                expectExactEstimate: false);
        }
    }

    private static void AssertBandMatchesFullPrefix(
        LayoutSnapshot optimized,
        LayoutSnapshot baseline,
        CanvasDevice device,
        float rasterizationScale,
        bool expectExactEstimate)
    {
        Assert.True(optimized.IsLazyLayoutEnabled);
        Assert.True(baseline.IsLazyLayoutEnabled);
        Assert.Equal(optimized.Blocks.Count, baseline.Blocks.Count);

        BlockBox optimizedList = FindListRoot(optimized);
        BlockBox baselineList = FindListRoot(baseline);
        BlockBox optimizedQuote = FindQuoteRoot(optimized, optimizedList);
        BlockBox baselineQuote = FindQuoteRoot(baseline, baselineList);
        int listOrdinal = FindOrdinal(optimized, optimizedList);
        int quoteOrdinal = FindOrdinal(optimized, optimizedQuote);
        Assert.Equal(listOrdinal, FindOrdinal(baseline, baselineList));
        Assert.Equal(quoteOrdinal, FindOrdinal(baseline, baselineQuote));

        Assert.False(optimized.IsTopLevelBlockMeasured(optimizedList));
        Assert.False(optimized.IsTopLevelBlockMeasured(optimizedQuote));
        Assert.False(baseline.IsTopLevelBlockMeasured(baselineList));
        Assert.False(baseline.IsTopLevelBlockMeasured(baselineQuote));

        double initialListHeight = optimizedList.Bounds.Height;
        double initialDocumentHeight = optimized.Size.Height;
        var band = new LazyLayoutBand(
            System.Math.Max(0, optimizedList.Bounds.Top - 4),
            optimizedQuote.Bounds.Bottom + 256);

        // Keep the lazy producer inputs independent, then force a fresh
        // full-prefix native placement on the baseline after fixed-point
        // realization. This provides an independent geometry oracle without
        // mutating the immutable viewport index/placement generation to
        // disable its document-order acceleration.
        LazyLayoutCommit optimizedCommit = optimized.EnsureMeasuredBand(
            band,
            CancellationToken.None);
        LazyLayoutCommit baselineCommit = baseline.EnsureMeasuredBand(
            band,
            CancellationToken.None);
        ForceFullPrefixRelayout(baseline);

        Assert.True(optimizedCommit.Changed);
        Assert.True(baselineCommit.Changed);
        Assert.True(optimized.IsTopLevelBlockMeasured(optimizedList));
        Assert.True(optimized.IsTopLevelBlockMeasured(optimizedQuote));
        Assert.True(baseline.IsTopLevelBlockMeasured(baselineList));
        Assert.True(baseline.IsTopLevelBlockMeasured(baselineQuote));
        if (expectExactEstimate)
        {
            Assert.Equal(initialListHeight, optimizedList.Bounds.Height);
            Assert.Equal(initialDocumentHeight, optimized.Size.Height);
        }
        else
        {
            Assert.NotEqual(initialListHeight, optimizedList.Bounds.Height);
        }

        bool[] optimizedMeasuredMask = optimized.Blocks
            .Select(optimized.IsTopLevelBlockMeasured)
            .ToArray();
        bool[] baselineMeasuredMask = baseline.Blocks
            .Select(baseline.IsTopLevelBlockMeasured)
            .ToArray();
        Assert.Equal(optimizedMeasuredMask, baselineMeasuredMask);

        AssertEquivalentTree(optimized, baseline);
        Assert.Equal(optimized.SemanticDocument.Text, baseline.SemanticDocument.Text);
        AssertSemanticTextAndSelectionGeometry(optimized, baseline, optimizedList, optimizedQuote,
            baselineList, baselineQuote);
        AssertInteractionAndViewportResults(optimized, baseline, optimizedList, optimizedQuote,
            baselineList, baselineQuote);

        double paintTop = System.Math.Max(0, optimizedList.Bounds.Top - 4);
        double paintBottom = System.Math.Max(paintTop + 1, optimizedQuote.Bounds.Bottom + 12);
        Color[] optimizedPixels = PaintViewport(
            optimized,
            device,
            rasterizationScale,
            paintTop,
            paintBottom);
        Color[] baselinePixels = PaintViewport(
            baseline,
            device,
            rasterizationScale,
            paintTop,
            paintBottom);
        Assert.Equal(optimizedPixels, baselinePixels);
    }

    private static void AssertEquivalentTree(LayoutSnapshot optimized, LayoutSnapshot baseline)
    {
        Assert.Equal(optimized.Size, baseline.Size);
        Assert.Equal(optimized.LayoutRevision, baseline.LayoutRevision);
        BlockBox[] optimizedBoxes = optimized.Blocks.SelectMany(EnumerateTree).ToArray();
        BlockBox[] baselineBoxes = baseline.Blocks.SelectMany(EnumerateTree).ToArray();
        Assert.Equal(optimizedBoxes.Length, baselineBoxes.Length);
        for (int index = 0; index < optimizedBoxes.Length; index++)
        {
            BlockBox left = optimizedBoxes[index];
            BlockBox right = baselineBoxes[index];
            Assert.Equal(left.GetType(), right.GetType());
            Assert.Equal(left.BlockIndex, right.BlockIndex);
            Assert.Equal(left.Bounds, right.Bounds);
            Assert.Equal(left.Margin, right.Margin);
            Assert.Equal(left.RawPlacementMargin, right.RawPlacementMargin);
            Assert.Equal(left.CollapsedTopMargin, right.CollapsedTopMargin);
            Assert.Equal(left.SuppressTopMargin, right.SuppressTopMargin);
            Assert.Equal(left.SuppressBottomMargin, right.SuppressBottomMargin);
            Assert.Equal(left.SafeHtmlInlineFlowScopeId, right.SafeHtmlInlineFlowScopeId);
            Assert.Equal(left.IsDirty, right.IsDirty);
        }
    }

    private static void AssertSemanticTextAndSelectionGeometry(
        LayoutSnapshot optimized,
        LayoutSnapshot baseline,
        BlockBox optimizedList,
        BlockBox optimizedQuote,
        BlockBox baselineList,
        BlockBox baselineQuote)
    {
        InlineContainerBox[] optimizedInlineBoxes = GetInlineBoxes(optimizedList, optimizedQuote);
        InlineContainerBox[] baselineInlineBoxes = GetInlineBoxes(baselineList, baselineQuote);
        Assert.Equal(optimizedInlineBoxes.Length, baselineInlineBoxes.Length);

        HashSet<InlineContainerBox> optimizedInlineSet = new(
            optimizedInlineBoxes,
            ReferenceEqualityComparer.Instance);
        HashSet<InlineContainerBox> baselineInlineSet = new(
            baselineInlineBoxes,
            ReferenceEqualityComparer.Instance);
        MarkdownTextSpan[] optimizedSpans = optimized.SemanticDocument.TextSpans
            .Where(span => span.InlineBox is not null && optimizedInlineSet.Contains(span.InlineBox))
            .ToArray();
        MarkdownTextSpan[] baselineSpans = baseline.SemanticDocument.TextSpans
            .Where(span => span.InlineBox is not null && baselineInlineSet.Contains(span.InlineBox))
            .ToArray();
        Assert.Equal(optimizedSpans.Length, baselineSpans.Length);
        var optimizedSelectionRects = new List<SelectionGeometry>();
        var baselineSelectionRects = new List<SelectionGeometry>();
        var optimizedSeenRuns = new HashSet<(int BlockIndex, int InlineIndex)>();
        var baselineSeenRuns = new HashSet<(int BlockIndex, int InlineIndex)>();

        for (int index = 0; index < optimizedSpans.Length; index++)
        {
            MarkdownTextSpan left = optimizedSpans[index];
            MarkdownTextSpan right = baselineSpans[index];
            Assert.Equal(left.TextStart, right.TextStart);
            Assert.Equal(left.TextEnd, right.TextEnd);
            Assert.Equal(left.InlineRun?.GetType(), right.InlineRun?.GetType());
            Assert.Equal(
                optimized.SemanticDocument.Text.Substring(left.TextStart, left.TextEnd - left.TextStart),
                baseline.SemanticDocument.Text.Substring(right.TextStart, right.TextEnd - right.TextStart));

            Rect[] leftDocumentRects = optimized.SemanticDocument
                .GetDocumentRects(left.TextStart, left.TextEnd)
                .ToArray();
            Rect[] rightDocumentRects = baseline.SemanticDocument
                .GetDocumentRects(right.TextStart, right.TextEnd)
                .ToArray();
            Assert.Equal(leftDocumentRects, rightDocumentRects);

            AddSelectionGeometry(left, optimizedSelectionRects, optimizedSeenRuns);
            AddSelectionGeometry(right, baselineSelectionRects, baselineSeenRuns);
        }

        Assert.NotEmpty(optimizedSelectionRects);
        Assert.Equal(optimizedSelectionRects.Count, baselineSelectionRects.Count);
        for (int index = 0; index < optimizedSelectionRects.Count; index++)
        {
            Assert.Equal(optimizedSelectionRects[index].BlockIndex, baselineSelectionRects[index].BlockIndex);
            Assert.Equal(optimizedSelectionRects[index].InlineIndex, baselineSelectionRects[index].InlineIndex);
            Assert.Equal(optimizedSelectionRects[index].Rects, baselineSelectionRects[index].Rects);
        }
    }

    private static void AddSelectionGeometry(
        MarkdownTextSpan span,
        List<SelectionGeometry> result,
        HashSet<(int BlockIndex, int InlineIndex)> seenRuns)
    {
        if (span.InlineBox is not { } inline || span.InlineRun is not { } run ||
            run.RenderedLength <= 0 ||
            !seenRuns.Add((inline.BlockIndex, run.InlineIndex)))
        {
            return;
        }

        var selection = new DocumentRange(
            new DocumentPosition(inline.BlockIndex, run.InlineIndex, 0),
            new DocumentPosition(inline.BlockIndex, run.InlineIndex, run.RenderedLength));
        Rect[] rects = inline.GetSelectionRects(selection).ToArray();
        Assert.NotEmpty(rects);
        result.Add(new SelectionGeometry(inline.BlockIndex, run.InlineIndex, rects));
    }

    private static void AssertInteractionAndViewportResults(
        LayoutSnapshot optimized,
        LayoutSnapshot baseline,
        BlockBox optimizedList,
        BlockBox optimizedQuote,
        BlockBox baselineList,
        BlockBox baselineQuote)
    {
        InlineContainerBox[] optimizedInlineBoxes = GetInlineBoxes(optimizedList, optimizedQuote);
        InlineContainerBox[] baselineInlineBoxes = GetInlineBoxes(baselineList, baselineQuote);
        Assert.Equal(optimizedInlineBoxes.Length, baselineInlineBoxes.Length);
        HashSet<InlineContainerBox> optimizedInlineSet = new(
            optimizedInlineBoxes,
            ReferenceEqualityComparer.Instance);
        HashSet<InlineContainerBox> baselineInlineSet = new(
            baselineInlineBoxes,
            ReferenceEqualityComparer.Instance);

        MarkdownTextSpan optimizedLink = optimized.SemanticDocument.TextSpans.First(
            span => span.InlineBox is not null && optimizedInlineSet.Contains(span.InlineBox) &&
                span.InlineRun is LinkRun);
        MarkdownTextSpan baselineLink = baseline.SemanticDocument.TextSpans.First(
            span => span.InlineBox is not null && baselineInlineSet.Contains(span.InlineBox) &&
                span.InlineRun is LinkRun);
        Assert.IsType<LinkRun>(optimizedLink.InlineRun);
        Assert.IsType<LinkRun>(baselineLink.InlineRun);
        Assert.Equal(optimizedLink.TextStart, baselineLink.TextStart);
        Assert.Equal(
            FocusTargetLayoutState.Ready,
            optimized.TryGetFocusTargetLayoutState(optimizedLink.InlineBox!.BlockIndex, out _));
        Assert.Equal(
            FocusTargetLayoutState.Ready,
            baseline.TryGetFocusTargetLayoutState(baselineLink.InlineBox!.BlockIndex, out _));

        Point optimizedHitPoint = GetTextHitPoint(optimized, optimizedInlineSet);
        Point baselineHitPoint = GetTextHitPoint(baseline, baselineInlineSet);
        Assert.Equal(optimizedHitPoint, baselineHitPoint);
        Assert.True(optimized.HitTest(optimizedHitPoint, out DocumentPosition optimizedPosition));
        Assert.True(baseline.HitTest(baselineHitPoint, out DocumentPosition baselinePosition));
        Assert.Equal(optimizedPosition, baselinePosition);

        double verticalOffset = optimizedList.Bounds.Top + System.Math.Min(8, optimizedList.Bounds.Height / 2);
        Assert.Equal(optimized.CaptureScrollAnchor(verticalOffset), baseline.CaptureScrollAnchor(verticalOffset));
        Assert.True(optimized.TryCaptureScrollAnchor(verticalOffset, out SnapshotScrollAnchor optimizedAnchor));
        Assert.True(baseline.TryCaptureScrollAnchor(verticalOffset, out SnapshotScrollAnchor baselineAnchor));
        Assert.Equal(optimizedAnchor, baselineAnchor);
        Assert.True(optimizedAnchor.BlockIndex.HasValue);
        Assert.True(optimized.TryCaptureScrollAnchorTarget(
            optimizedAnchor.BlockIndex!.Value,
            optimizedAnchor.OffsetFromTop,
            out double optimizedTarget));
        Assert.True(baseline.TryCaptureScrollAnchorTarget(
            baselineAnchor.BlockIndex!.Value,
            baselineAnchor.OffsetFromTop,
            out double baselineTarget));
        Assert.Equal(optimizedTarget, baselineTarget);

        var finalBand = new LazyLayoutBand(
            System.Math.Max(0, optimizedList.Bounds.Top - 4),
            optimizedQuote.Bounds.Bottom + 12);
        Assert.True(optimized.IsBandMeasured(finalBand));
        Assert.True(baseline.IsBandMeasured(finalBand));
        int[] optimizedProbeOrdinals = GetProbeOrdinals(optimized, finalBand);
        int[] baselineProbeOrdinals = GetProbeOrdinals(baseline, finalBand);
        Assert.Equal(optimizedProbeOrdinals, baselineProbeOrdinals);
        Assert.Contains(FindOrdinal(optimized, optimizedList), optimizedProbeOrdinals);
        Assert.Contains(FindOrdinal(optimized, optimizedQuote), optimizedProbeOrdinals);
    }

    private static int[] GetProbeOrdinals(LayoutSnapshot snapshot, LazyLayoutBand band)
    {
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                band.Top,
                band.Bottom,
                out IReadOnlyList<BlockBox> blocks));
            var ordinals = new Dictionary<BlockBox, int>(ReferenceEqualityComparer.Instance);
            for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++)
                ordinals.Add(snapshot.Blocks[ordinal], ordinal);
            return blocks.Select(block => ordinals[block]).ToArray();
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static Point GetTextHitPoint(
        LayoutSnapshot snapshot,
        HashSet<InlineContainerBox> targetInlineBoxes)
    {
        foreach (MarkdownTextSpan span in snapshot.SemanticDocument.TextSpans)
        {
            if (span.InlineBox is not { } inline ||
                !targetInlineBoxes.Contains(inline) ||
                span.InlineRun is not TextRun ||
                span.TextEnd <= span.TextStart)
            {
                continue;
            }

            foreach (Rect rect in snapshot.SemanticDocument.GetDocumentRects(span.TextStart, span.TextEnd))
            {
                if (rect.Width > 1 && rect.Height > 1)
                    return new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            }
        }

        throw new InvalidOperationException("The measured list and quote contain no hit-testable native text rect.");
    }

    private static Color[] PaintViewport(
        LayoutSnapshot snapshot,
        CanvasDevice device,
        float rasterizationScale,
        double top,
        double bottom)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(bottom - top));
        using var target = new CanvasRenderTarget(
            device,
            LayoutWidth,
            height,
            96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.Transform = Matrix3x2.CreateTranslation(0, (float)-top);
            snapshot.Paint(drawing, new Rect(0, top, LayoutWidth, height));
        }

        return target.GetPixelColors();
    }

    private static BlockBox FindListRoot(LayoutSnapshot snapshot)
        => snapshot.Blocks.Single(block => block is StackBox stack &&
            stack.Children.Any(child => child is ListItemBox));

    private static BlockBox FindQuoteRoot(LayoutSnapshot snapshot, BlockBox listRoot)
        => snapshot.Blocks.Single(block => block is StackBox && !ReferenceEquals(block, listRoot));

    private static int FindOrdinal(LayoutSnapshot snapshot, BlockBox block)
    {
        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++)
        {
            if (ReferenceEquals(snapshot.Blocks[ordinal], block))
                return ordinal;
        }

        throw new InvalidOperationException("The target root is not present in the snapshot.");
    }

    private static IEnumerable<BlockBox> EnumerateTree(BlockBox root)
    {
        yield return root;
        switch (root)
        {
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    foreach (BlockBox descendant in EnumerateTree(child))
                        yield return descendant;
                }
                break;
            case ListItemBox item:
                foreach (BlockBox descendant in EnumerateTree(item.Marker))
                    yield return descendant;
                foreach (BlockBox descendant in EnumerateTree(item.Content))
                    yield return descendant;
                break;
        }
    }

    private static InlineContainerBox[] GetInlineBoxes(params BlockBox[] roots)
        => roots.SelectMany(EnumerateTree)
            .OfType<InlineContainerBox>()
            .ToArray();

    private static void ForceFullPrefixRelayout(LayoutSnapshot snapshot)
    {
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            MethodInfo reflow = typeof(LayoutSnapshot).GetMethod(
                "ReflowNoLock",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(bool), typeof(int)],
                modifiers: null)
                ?? throw new InvalidOperationException("The full-reflow method is missing.");
            reflow.Invoke(snapshot, new object?[] { false, 0 });

            MethodInfo publish = typeof(LayoutSnapshot).GetMethod(
                "PublishCommittedMetadataNoLock",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
                ?? throw new InvalidOperationException("The committed-metadata publisher is missing.");
            publish.Invoke(snapshot, Array.Empty<object?>());
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static LayoutSnapshot Build(
        string source,
        MarkdownPipeline pipeline,
        MarkdownExtensionRegistry registry,
        CanvasDevice device,
        MarkdownStyleSheet sheet,
        float rasterizationScale,
        FlowDirection flowDirection,
        MarkdownEngine? sharedEngine,
        SharedLayoutMetricsKey? metricsKey,
        bool lazy)
    {
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, pipeline);
        MarkdownRenderer.Document.MarkdownDocument semantic =
            MarkdownRenderer.Document.MarkdownDocument.FromParsed(source, parsed);
        var theme = CreateTheme(sheet);
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            registry,
            flowDirection)
        {
            RasterizationScale = rasterizationScale,
        };
        var builder = new LayoutBuilder(
            context,
            semanticDocument: semantic,
            sharedLayoutEngine: sharedEngine,
            sharedLayoutMetricsKey: metricsKey);
        return lazy
            ? builder.BuildLazy(
                parsed,
                LayoutWidth,
                viewportTop: 0,
                viewportHeight: InitialViewportHeight,
                overscan: 0,
                CancellationToken.None)
            : builder.Build(parsed, LayoutWidth, CancellationToken.None);
    }

    private static SharedLayoutMetricsKey CreateMetricsKey(
        string source,
        object documentIdentity,
        MarkdownExtensionRegistry registry,
        MarkdownStyleSheet sheet,
        float rasterizationScale,
        FlowDirection flowDirection)
    {
        ThemeSnapshot theme = CreateTheme(sheet);
        return new SharedLayoutMetricsKey(
            source,
            documentIdentity,
            widthInSixtyFourthDips: (int)System.Math.Round(LayoutWidth * 64d),
            typographyFingerprint: theme.LayoutFingerprint,
            flowDirection: (int)flowDirection,
            codeWrappingMode: 0,
            lineNumberMode: 0,
            codeCopyEnabled: true,
            taskEditingEnabled: false,
            rasterScaleInThousandths: (int)System.Math.Round(rasterizationScale * 1_000d),
            language: "en-US",
            registryRevision: registry.Revision,
            disclosureFingerprint: 0,
            imageContext: new SharedLayoutImageContext(null, null, false, null),
            styleSheet: sheet,
            registry: registry,
            embedFactory: null,
            hostedElementFactory: null,
            imageResolver: null,
            svgRenderer: null,
            commandProvider: null,
            stringProvider: null);
    }

    private static ThemeSnapshot CreateTheme(MarkdownStyleSheet sheet)
    {
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(),
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: sheet,
            blockSpacing: 16);
    }

    private static string CreateSource()
    {
        var source = new System.Text.StringBuilder();
        for (int index = 0; index < 16; index++)
        {
            source.Append("Preface ").Append(index)
                .AppendLine(": Cafe\u0301 remains decomposed; Arabic العربية and Hebrew שלום remain readable.")
                .AppendLine();
        }

        source.AppendLine("- First item with **strong emphasis**, _soft emphasis_, and a [rich link](https://example.test/list) before Cafe\u0301 👩🏽‍💻.")
            .Append("- A wrapping list item keeps its entire native text layout intact: ")
            .AppendLine(string.Join(" ", Enumerable.Repeat(
                "long-content-wraps-with-native-directwrite-glyph-placement",
                40)))
            .AppendLine("- Final list item with mixed RTL: مرحبًا بالعالم שלום.")
            .AppendLine()
            .AppendLine("> Quoted **text** with a [second link](https://example.test/quote), Cafe\u0301, 👩🏽‍💻, and العربية.")
            .AppendLine(">")
            .AppendLine("> A second quote paragraph verifies the nested stack's sequential placement.")
            .AppendLine();

        for (int index = 0; index < 8; index++)
        {
            source.Append("Trailing block ").Append(index)
                .AppendLine(": text after the quote must retain exact position and hit testing.")
                .AppendLine();
        }

        return source.ToString();
    }

    private sealed record SelectionGeometry(int BlockIndex, int InlineIndex, Rect[] Rects);
}
