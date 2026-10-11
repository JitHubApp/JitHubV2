using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class VectorSceneCapacityFallbackTests
{
    private static readonly Color ClearColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color SceneColor = Color.FromArgb(255, 18, 34, 50);
    private static readonly Color SelectionColor = Color.FromArgb(255, 12, 220, 28);

    [Fact]
    public void DeferredBlockScenePaintsBoundedFallbackAndKeepsSourceSelectionAndNativeFactoryUntouched()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var admission = new VectorNativeResourceAdmission(
            capacityWeightUnits: 100_000,
            maximumSceneWeightUnits: 100_000);
        using VectorNativeResourceCredit blocker = ReserveEntireAdmission(admission);
        var resourceCreator = new CountingResourceCreator(CanvasDevice.GetSharedDevice());
        MarkdownLayoutContext context = CreateContext(
            resourceCreator,
            session,
            "flowchart TD\n    A[review the release] --> B[ship safely]");
        const string semantic = "Review the release and ship safely.";
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            CreatePathScene(),
            new SourceSpan(0, context.SourceMap.SourceText.Length),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Release flow",
            accessibilityDescription: "A two-step release flow.",
            semanticText: semantic);
        var box = new VectorSceneBox(
            context,
            Assert.Single(content.Build().Items),
            MarkdownElementKeys.Diagram,
            admission)
        {
            BlockIndex = 0,
        };

        try
        {
            float height = box.Measure(180);
            box.Arrange(0, 0, 180);
            Assert.True(height > 0);
            Assert.Equal(admission, box.NativeAdmission);

            Assert.False(box.TryPrepareNativeResources(CancellationToken.None, out VectorNativePreparationOutcome outcome));
            Assert.Equal(VectorNativePreparationStatus.DeferredCapacity, outcome.Status);
            Assert.Equal(VectorNativePreparationStatus.DeferredCapacity, box.NativePreparationStatus);
            Assert.False(box.IsNativeResourcesPrepared);
            Assert.True(box.IsCapacityFallbackPaintable);
            Assert.Equal(0, resourceCreator.DeviceRequestCount);
            Assert.InRange(VectorSceneCapacityFallback.PreparedDescriptorCountForTests, 1, 2);

            Assert.Equal("Release flow", box.AccessibilityName);
            Assert.Equal("A two-step release flow.", box.AccessibilityDescription);
            Assert.Equal(semantic, box.SemanticText);
            Assert.Equal(new SourceSpan(0, context.SourceMap.SourceText.Length), box.SourceRange);
            var selection = new DocumentRange(
                new DocumentPosition(0, 0, 0),
                new DocumentPosition(0, 0, 1));
            Assert.Contains(box.Bounds, box.GetSelectionRects(selection));

            Color[] normalPixels = PaintBlock(box, height, selection: null);
            Assert.Contains(normalPixels, pixel => pixel != ClearColor);

            Color[] selectedPixels = PaintBlock(box, height, selection);
            Assert.Contains(selectedPixels, pixel =>
                pixel.G > pixel.R + 40 && pixel.G > pixel.B + 40);

            var cacheBeforePaint = SharedCanvasTextFormatCache.Statistics;
            for (int index = 0; index < 4; index++)
                _ = PaintBlock(box, height, selection: null);
            Assert.Equal(cacheBeforePaint, SharedCanvasTextFormatCache.Statistics);
            Assert.InRange(VectorSceneCapacityFallback.PreparedDescriptorCountForTests, 1, 2);

            box.Dispose();
            Assert.Throws<ObjectDisposedException>(() => box.Paint(null!, new Rect(0, 0, 200, 100)));
        }
        finally
        {
            if (!box.IsDisposed)
                box.Dispose();
        }
    }

    [Fact]
    public void TooLargeInlineScenePaintsBoundedFallbackAndCloneRemainsNativeFreeAndNonPaintable()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var admission = new VectorNativeResourceAdmission(
            capacityWeightUnits: 10_000,
            maximumSceneWeightUnits: 1);
        var resourceCreator = new CountingResourceCreator(CanvasDevice.GetSharedDevice());
        MarkdownLayoutContext context = CreateContext(resourceCreator, session, "inline math source");
        const string semantic = "An inline equation: x squared plus y squared.";
        var run = new InlineVectorSceneRun(
            context,
            CreatePathScene(),
            semantic,
            "x squared plus y squared",
            "A bounded fallback for the equation.",
            MarkdownElementKeys.Math,
            admission)
        {
            LinkUrl = "https://example.test/equation",
            LinkTitle = "Equation source",
        };
        run.SetLocalBounds(new Rect(4, 4, 150, 44));

        InlineVectorSceneRun clone = run.CloneForMeasuredPlacement();
        try
        {
            Assert.False(run.TryPrepareNativeResources(CancellationToken.None, out VectorNativePreparationOutcome outcome));
            Assert.Equal(VectorNativePreparationStatus.TooLarge, outcome.Status);
            Assert.Equal(VectorNativePreparationStatus.TooLarge, run.NativePreparationStatus);
            Assert.False(run.IsNativeResourcesPrepared);
            Assert.True(run.IsCapacityFallbackPaintable);
            Assert.Equal(0, resourceCreator.DeviceRequestCount);

            Assert.Equal(semantic, run.SemanticText);
            Assert.Equal("x squared plus y squared", run.AccessibilityName);
            Assert.Equal("A bounded fallback for the equation.", run.AccessibilityDescription);
            Assert.Equal("https://example.test/equation", run.LinkUrl);
            Assert.Equal("Equation source", run.LinkTitle);

            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 170, 60, 96);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(ClearColor);
                run.Paint(drawing, 0, 0, SceneColor, highContrast: false);
            }
            Assert.Contains(target.GetPixelColors(), pixel => pixel != ClearColor);

            run.Dispose();
            Assert.Throws<ObjectDisposedException>(() => run.Paint(
                null!, 0, 0, SceneColor, highContrast: false));

            Assert.False(clone.IsNativeResourcesPrepared);
            Assert.False(clone.IsCapacityFallbackPaintable);
            Assert.Equal(VectorNativePreparationStatus.NotPrepared, clone.NativePreparationStatus);
            Assert.Throws<InvalidOperationException>(() => clone.Paint(
                null!, 0, 0, SceneColor, highContrast: false));
        }
        finally
        {
            clone.Dispose();
            if (!run.IsDisposed)
                run.Dispose();
        }
    }

    [Fact]
    public void InlineCapacityFallbackHonorsTextScaleRtlHighContrastAndPhysicalDpi()
    {
        var pixelHeights = new Dictionary<(double Scale, int Dpi, FlowDirection Direction), double>();
        foreach (FlowDirection direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
        {
            foreach (int dpi in new[] { 96, 192 })
            {
                foreach (double scale in new[] { 0.5d, 1d, 3d })
                {
                    using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
                    var admission = new VectorNativeResourceAdmission(
                        capacityWeightUnits: 10_000,
                        maximumSceneWeightUnits: 1);
                    MarkdownLayoutContext context = CreateContext(
                        new CountingResourceCreator(CanvasDevice.GetSharedDevice()),
                        session,
                        "bounded text scale",
                        scale,
                        direction,
                        highContrast: true);
                    var run = new InlineVectorSceneRun(
                        context,
                        CreatePathScene(),
                        "Scale",
                        "Scale",
                        null,
                        MarkdownElementKeys.Math,
                        admission);
                    run.SetLocalBounds(new Rect(10, 10, 160, 54));

                    try
                    {
                        Assert.False(run.TryPrepareNativeResources(CancellationToken.None, out VectorNativePreparationOutcome outcome));
                        Assert.Equal(VectorNativePreparationStatus.TooLarge, outcome.Status);
                        Assert.True(run.IsCapacityFallbackPaintable);

                        using var target = new CanvasRenderTarget(
                            CanvasDevice.GetSharedDevice(),
                            190,
                            80,
                            dpi);
                        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
                        {
                            drawing.Clear(ClearColor);
                            Matrix3x2 originalTransform = Matrix3x2.CreateTranslation(3, 5);
                            drawing.Transform = originalTransform;
                            var cacheBeforePaint = SharedCanvasTextFormatCache.Statistics;
                            run.Paint(
                                drawing,
                                0,
                                0,
                                SceneColor,
                                highContrast: true,
                                foregroundOverride: SelectionColor);
                            Assert.Equal(originalTransform, drawing.Transform);

                            Assert.True(VectorSceneCapacityFallback.TryGetFormat(direction, out Microsoft.Graphics.Canvas.Text.CanvasTextFormat format));
                            Matrix3x2 callerTransform = Matrix3x2.CreateTranslation(2, 3);
                            drawing.Transform = callerTransform;
                            VectorSceneCapacityFallback.DrawTextWithScale(
                                drawing,
                                "Scale",
                                new Rect(20, 15, 120, 45),
                                SelectionColor,
                                format,
                                scale);
                            Assert.Equal(callerTransform, drawing.Transform);
                            Assert.Equal(cacheBeforePaint, SharedCanvasTextFormatCache.Statistics);
                        }

                        Windows.Graphics.Imaging.BitmapSize pixelSize = target.SizeInPixels;
                        Color[] pixels = target.GetPixelColors();
                        Assert.Equal((long)pixelSize.Width * pixelSize.Height, pixels.LongLength);
                        InkBounds ink = FindSelectionInkBounds(pixels, checked((int)pixelSize.Width));
                        Assert.True(ink.Width > 0 && ink.Height > 0);
                        Assert.InRange(ink.Left, (int)System.Math.Floor(13d * dpi / 96d) - 1, (int)System.Math.Ceiling(173d * dpi / 96d));
                        Assert.InRange(ink.Right, (int)System.Math.Floor(13d * dpi / 96d), (int)System.Math.Ceiling(173d * dpi / 96d) + 1);
                        Assert.InRange(ink.Top, (int)System.Math.Floor(15d * dpi / 96d) - 1, (int)System.Math.Ceiling(69d * dpi / 96d));
                        Assert.InRange(ink.Bottom, (int)System.Math.Floor(15d * dpi / 96d), (int)System.Math.Ceiling(69d * dpi / 96d) + 1);
                        pixelHeights[(scale, dpi, direction)] = ink.Height * 96d / dpi;
                        Assert.InRange(VectorSceneCapacityFallback.PreparedDescriptorCountForTests, 1, 2);
                    }
                    finally
                    {
                        run.Dispose();
                    }
                }
            }
        }

        foreach (FlowDirection direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
        {
            double small = pixelHeights[(0.5d, 96, direction)];
            double normal = pixelHeights[(1d, 96, direction)];
            double large = pixelHeights[(3d, 96, direction)];
            Assert.True(small < normal * 0.8d, $"0.5x text should be smaller than 1x for {direction}: {small} vs {normal} DIPs.");
            Assert.True(large > normal * 1.5d, $"3x text should be larger than 1x for {direction}: {large} vs {normal} DIPs.");

            foreach (double scale in new[] { 0.5d, 1d, 3d })
            {
                double at96 = pixelHeights[(scale, 96, direction)];
                double at192 = pixelHeights[(scale, 192, direction)];
                Assert.InRange(System.Math.Abs(at96 - at192), 0d, 2.5d);
            }
        }
    }

    [Fact]
    public void ParticipatingDetachedFallbackUsesSharedBoundedTextAndPreservesFailedRebindState()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var admission = new VectorNativeResourceAdmission(
            capacityWeightUnits: 100_000,
            maximumSceneWeightUnits: 100_000);
        using VectorNativeResourceCredit blocker = ReserveEntireAdmission(admission);
        var resourceCreator = new CountingResourceCreator(CanvasDevice.GetSharedDevice());
        MarkdownLayoutContext context = CreateContext(resourceCreator, session, "a readable source caption");
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            CreatePathScene(),
            new SourceSpan(0, context.SourceMap.SourceText.Length),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Capacity placeholder",
            semanticText: "Semantic caption remains available.");
        var box = new VectorSceneBox(
            context,
            Assert.Single(content.Build().Items),
            MarkdownElementKeys.Diagram,
            admission)
        {
            BlockIndex = 0,
        };
        try
        {
            float height = box.Measure(180);
            box.Arrange(0, 0, 180);
            Assert.False(box.TryPrepareNativeResources(CancellationToken.None, out _));
            box.ReleaseSceneGraph();
            Assert.True(box.IsGraphDetached);

            int layoutFactoryCalls = 0;
            bool sourceFallbackChanged = box.PrepareDetachedFallback(
                CancellationToken.None,
                (_, _) =>
                {
                    layoutFactoryCalls++;
                    throw new InvalidOperationException("Participating fallbacks must not build per-box layouts.");
                },
                replaceExisting: true);
            Assert.True(sourceFallbackChanged);
            Assert.True(box.HasDetachedFallback);
            Assert.Equal(0, layoutFactoryCalls);

            box.MarkGraphRebindFailed();
            bool errorFallbackChanged = box.PrepareDetachedFallback(
                CancellationToken.None,
                MarkdownDetachedSceneFallback.CreateFailed,
                replaceExisting: true);
            Assert.True(errorFallbackChanged);
            Assert.True(box.GraphRebindFailed);
            Assert.Equal("Capacity placeholder", box.AccessibilityName);
            Assert.Equal("Semantic caption remains available.", box.SemanticText);

            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 180, 90, 96);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(ClearColor);
                box.Paint(drawing, new Rect(0, 0, 180, height));
            }
            Assert.Contains(target.GetPixelColors(), pixel => pixel != ClearColor);
            Assert.Equal(0, layoutFactoryCalls);

            box.Dispose();
            Assert.Throws<ObjectDisposedException>(() => box.Paint(null!, new Rect(0, 0, 180, 90)));
        }
        finally
        {
            if (!box.IsDisposed)
                box.Dispose();
        }
    }

    [Fact]
    public void SemanticFallbackTruncationNeverSplitsASurrogatePair()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(
            new CountingResourceCreator(CanvasDevice.GetSharedDevice()),
            session,
            string.Empty);
        string source = new string('x', 254) + "😀" + "tail";

        string fallback = VectorSceneCapacityFallback.CreateInlineText(context, source, null);

        Assert.InRange(fallback.Length, 1, VectorSceneCapacityFallback.MaximumCharacters);
        Assert.Equal('…', fallback[^1]);
        Assert.False(fallback.Length > 1 && char.IsHighSurrogate(fallback[^2]));
    }

    [Fact]
    public void DescriptorFactoryFailuresAreNotDowngradedToCapacityDeferral()
    {
        string blockSource = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "VectorSceneBox.cs");
        string blockPreparation = Section(
            blockSource,
            "private bool PrepareCapacityFallback(",
            "/// <summary>Releases only device resources;");
        Assert.Contains("VectorSceneCapacityFallback.PrepareFormat(", blockPreparation, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", blockPreparation, StringComparison.Ordinal);

        string inlineSource = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "InlineVectorSceneRun.cs");
        string inlinePreparation = Section(
            inlineSource,
            "internal bool PrepareCapacityFallback(",
            "internal void ReleaseNativeResources()");
        Assert.Contains("VectorSceneCapacityFallback.PrepareFormat(", inlinePreparation, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", inlinePreparation, StringComparison.Ordinal);

        string detachedSource = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer.Performance", "MarkdownDetachedSceneFallback.cs");
        string participatingFallback = Section(
            detachedSource,
            "if (box.NativeAdmission is not null)",
            "// Reserve custody before invoking either the built-in or a supplied");
        Assert.Contains("return box.PrepareCapacityFallback(cancellationToken);", participatingFallback, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", participatingFallback, StringComparison.Ordinal);
    }

    private static VectorNativeResourceCredit ReserveEntireAdmission(VectorNativeResourceAdmission admission)
    {
        Assert.Equal(
            VectorNativeAdmissionResult.Granted,
            admission.TryReserve(100_000, out VectorNativeResourceCredit? credit, out _));
        return Assert.IsType<VectorNativeResourceCredit>(credit);
    }

    private static MarkdownLayoutContext CreateContext(
        ICanvasResourceCreator resourceCreator,
        MarkdownPerformanceSession session,
        string source,
        double textScaleFactor = 1,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        bool highContrast = false)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            Foreground = SceneColor,
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.Diagram] = body,
            [MarkdownElementKeys.Math] = body,
            [MarkdownElementKeys.Link] = body,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor: ClearColor,
            selectionHighlightColor: Color.FromArgb(255, 0, 90, 190),
            selectionForegroundColor: SelectionColor,
            focusVisualColor: SceneColor,
            isDark: false,
            isHighContrast: highContrast,
            textScaleFactor: textScaleFactor);
        return new MarkdownLayoutContext(
            resourceCreator,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            PerformanceSession = session,
            UseProgressiveNativeSceneResidency = true,
        };
    }

    private static MarkdownVectorScene CreatePathScene()
    {
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(2, 2)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(70, 2)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(70, 38)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(2, 38)),
            MarkdownVectorPathOperation.Close(),
        ];
        return new MarkdownVectorScene(
            width: 100,
            height: 48,
            baseline: 48,
            commands: [MarkdownVectorCommand.FillPath(path)]);
    }

    private static string Section(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker after '{startMarker}': {endMarker}");
        return source[start..end];
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate repository source '{string.Join(Path.DirectorySeparatorChar, pathParts)}'.");
    }

    private static Color[] PaintBlock(
        VectorSceneBox box,
        float height,
        DocumentRange? selection)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 200, 100, 96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(ClearColor);
            box.Paint(drawing, new Rect(0, 0, 200, 100));
            if (selection is { } range)
                box.PaintSelectionForeground(drawing, range, SelectionColor, new Rect(0, 0, 200, 100));
        }
        return target.GetPixelColors();
    }

    private static InkBounds FindSelectionInkBounds(Color[] pixels, int pixelWidth)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = -1;
        int bottom = -1;
        for (int index = 0; index < pixels.Length; index++)
        {
            Color pixel = pixels[index];
            if (pixel.G <= pixel.R + 40 || pixel.G <= pixel.B + 40)
                continue;

            int x = index % pixelWidth;
            int y = index / pixelWidth;
            left = System.Math.Min(left, x);
            top = System.Math.Min(top, y);
            right = System.Math.Max(right, x);
            bottom = System.Math.Max(bottom, y);
        }

        return right < left || bottom < top
            ? default
            : new InkBounds(left, top, right, bottom);
    }

    private readonly record struct InkBounds(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right < Left ? 0 : Right - Left + 1;

        internal int Height => Bottom < Top ? 0 : Bottom - Top + 1;
    }

    private sealed class CountingResourceCreator(CanvasDevice device) : ICanvasResourceCreator
    {
        private int _deviceRequestCount;

        internal int DeviceRequestCount => Volatile.Read(ref _deviceRequestCount);

        public CanvasDevice Device
        {
            get
            {
                Interlocked.Increment(ref _deviceRequestCount);
                return device;
            }
        }
    }
}
