using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
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

public sealed class LayoutBuilderOwnershipTests
{
    [Fact]
    public async Task EagerBuildScopesPassTokenAndRestoresOuterScope()
        => await AssertBuildScopesPassTokenAsync(lazy: false);

    [Fact]
    public async Task LazyBuildScopesPassTokenAndRestoresOuterScope()
        => await AssertBuildScopesPassTokenAsync(lazy: true);

    [Fact]
    public void EagerBuildWithoutExplicitTokenPreservesAmbientAndContextCancellation()
        => AssertBuildWithoutExplicitTokenObservesCancellation(lazy: false);

    [Fact]
    public void LazyBuildWithoutExplicitTokenPreservesAmbientAndContextCancellation()
        => AssertBuildWithoutExplicitTokenObservesCancellation(lazy: true);

    private static void AssertBuildWithoutExplicitTokenObservesCancellation(bool lazy)
    {
        const string source = "must not construct this root";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int rendererCalls = 0;
        LayoutBuilder ambientBuilder = CreateBuilder(source, (_, _) =>
        {
            rendererCalls++;
            return new TrackingBox();
        });
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
            {
                if (lazy)
                    ambientBuilder.BuildLazy(Markdown.Parse(source), 320, 0, 400, 100, CancellationToken.None);
                else
                    ambientBuilder.Build(Markdown.Parse(source), 320);
            });
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(cancellation.Token, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
        }

        LayoutBuilder contextBuilder = CreateBuilder(source, (_, _) =>
        {
            rendererCalls++;
            return new TrackingBox();
        }, cancellation.Token);
        using (LayoutPassCancellation.Push(CancellationToken.None))
        {
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
            {
                if (lazy)
                    contextBuilder.BuildLazy(Markdown.Parse(source), 320, 0, 400, 100, CancellationToken.None);
                else
                    contextBuilder.Build(Markdown.Parse(source), 320);
            });
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(CancellationToken.None, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
        }
        Assert.Equal(0, rendererCalls);
    }

    private static async Task AssertBuildScopesPassTokenAsync(bool lazy)
    {
        const string source = "scoped paragraph";
        using var outerCancellation = new CancellationTokenSource();
        using var passCancellation = new CancellationTokenSource();
        SemanticTextProjectionAccumulator? accumulator = null;
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            Assert.Equal(passCancellation.Token,
                LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
            accumulator = new SemanticTextProjectionAccumulator();
            accumulator.Append("semantic callback projection");
            return new TrackingBox();
        }, out MarkdownLayoutContext context);

        LayoutSnapshot? snapshot = null;
        try
        {
            using (LayoutPassCancellation.Push(outerCancellation.Token))
            {
                snapshot = lazy
                    ? builder.BuildLazy(Markdown.Parse(source), 320, 0, 400, 100, passCancellation.Token)
                    : builder.Build(Markdown.Parse(source), 320, passCancellation.Token);
                Assert.Equal(outerCancellation.Token,
                    LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
                passCancellation.Cancel();
                OperationCanceledException failure = Assert.Throws<OperationCanceledException>(
                    () => Assert.IsType<SemanticTextProjectionAccumulator>(accumulator).GetText());
                Assert.Equal(passCancellation.Token, failure.CancellationToken);
            }
        }
        finally
        {
            // ThreadStatic pass scopes end synchronously, before either await.
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await context.DrainOwnedRetirementsAsync();
        }
    }

    [Fact]
    public async Task EagerCancellationAfterLastMeasureRetiresRootsBeforeSemanticPublication()
    {
        const string source = "last measured root";
        using var cancellation = new CancellationTokenSource();
        var box = new TrackingBox(afterMeasure: cancellation.Cancel);
        LayoutBuilder builder = CreateBuilder(source, (_, _) => box, out MarkdownLayoutContext context);

        using (LayoutPassCancellation.Push(CancellationToken.None))
        {
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(
                () => builder.Build(Markdown.Parse(source), 320, cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(CancellationToken.None,
                LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
        }
        await context.DrainOwnedRetirementsAsync();
        Assert.Equal(1, box.DisposeCount);
        Assert.Equal(1, box.NativeLeaseDisposeCount);
    }

    [Fact]
    public async Task EagerAmbientCancellationDuringMeasureStopsNextBlockAndRetiresRoots()
        => await AssertAmbientCancellationDuringMeasureStopsNextBlockAndRetiresRootsAsync(lazy: false);

    [Fact]
    public async Task LazyAmbientCancellationDuringInitialRealizationStopsNextBlockAndRetiresRoots()
        => await AssertAmbientCancellationDuringMeasureStopsNextBlockAndRetiresRootsAsync(lazy: true);

    [Fact]
    public async Task DirectLazyRealizationWithoutExplicitTokenUsesAmbientCancellationAndRetiresSnapshot()
    {
        const string source = "first\n\nsecond";
        using var ambientCancellation = new CancellationTokenSource();
        var boxes = new List<TrackingBox>();
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            var box = new TrackingBox(afterMeasure: boxes.Count == 0 ? ambientCancellation.Cancel : null);
            boxes.Add(box);
            return box;
        }, out MarkdownLayoutContext context);

        LayoutSnapshot? snapshot = null;
        try
        {
            LayoutSnapshot builtSnapshot = builder.BuildLazy(
                Markdown.Parse(source),
                availableWidth: 320,
                viewportTop: 10_000,
                viewportHeight: 1,
                overscan: 0,
                cancellationToken: CancellationToken.None);
            snapshot = builtSnapshot;
            Assert.Equal(2, boxes.Count);
            Assert.All(boxes, box => Assert.Equal(0, box.MeasureCount));

            using (LayoutPassCancellation.Push(ambientCancellation.Token))
            {
                OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
                    () => builtSnapshot.EnsureMeasuredBand(
                        new LazyLayoutBand(0, 400),
                        CancellationToken.None));

                Assert.Equal(ambientCancellation.Token, actual.CancellationToken);
                Assert.Equal(
                    ambientCancellation.Token,
                    LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
                Assert.Equal(1, boxes[0].MeasureCount);
                Assert.Equal(0, boxes[1].MeasureCount);
            }

            Assert.Equal(CancellationToken.None, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
        }
        finally
        {
            // Retire snapshot-owned roots and drain context barriers even if
            // the assertion fails. The thread-static pass scope above is gone
            // before asynchronous cleanup starts.
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await context.DrainOwnedRetirementsAsync();
            }
        }

        Assert.All(boxes, box =>
        {
            Assert.Equal(1, box.DisposeCount);
            Assert.Equal(1, box.NativeLeaseDisposeCount);
        });
    }

    private static async Task AssertAmbientCancellationDuringMeasureStopsNextBlockAndRetiresRootsAsync(bool lazy)
    {
        const string source = "first\n\nsecond";
        using var ambientCancellation = new CancellationTokenSource();
        var boxes = new List<TrackingBox>();
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            var box = new TrackingBox(afterMeasure: boxes.Count == 0 ? ambientCancellation.Cancel : null);
            boxes.Add(box);
            return box;
        }, out MarkdownLayoutContext context);

        LayoutSnapshot? unexpectedlyPublishedSnapshot = null;
        try
        {
            using (LayoutPassCancellation.Push(ambientCancellation.Token))
            {
                OperationCanceledException actual = Assert.Throws<OperationCanceledException>(() =>
                {
                    if (lazy)
                    {
                        unexpectedlyPublishedSnapshot = builder.BuildLazy(
                            Markdown.Parse(source),
                            availableWidth: 320,
                            viewportTop: 0,
                            viewportHeight: 400,
                            overscan: 100,
                            cancellationToken: CancellationToken.None);
                    }
                    else
                    {
                        unexpectedlyPublishedSnapshot = builder.Build(
                            Markdown.Parse(source),
                            320,
                            CancellationToken.None);
                    }
                });

                Assert.Equal(ambientCancellation.Token, actual.CancellationToken);
                Assert.Equal(
                    ambientCancellation.Token,
                    LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
            }

            Assert.Equal(CancellationToken.None, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
            Assert.Equal(2, boxes.Count);
            Assert.Equal(1, boxes[0].MeasureCount);
            Assert.Equal(0, boxes[1].MeasureCount);
        }
        finally
        {
            // Keep snapshot-owned roots and queued retirement barriers drained
            // even when a regression unexpectedly publishes or one assertion
            // fails. The thread-static scope above has exited before awaiting.
            try
            {
                if (unexpectedlyPublishedSnapshot is not null)
                    await unexpectedlyPublishedSnapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await context.DrainOwnedRetirementsAsync();
            }
        }

        Assert.All(boxes, box =>
        {
            Assert.Equal(1, box.DisposeCount);
            Assert.Equal(1, box.NativeLeaseDisposeCount);
        });
    }

    [Fact]
    public async Task BlockConstructionFailureDisposesEveryPriorRootAndPreservesOriginalException()
    {
        const string source = "first\n\nsecond\n\nthird";
        var buildFailure = new InvalidOperationException("second renderer failed");
        var firstDisposeFailure = new InvalidOperationException("first dispose failed");
        var boxes = new List<TrackingBox>();
        MarkdownLayoutContext context;
        int rendered = 0;
        LayoutBuilder builder = CreateBuilder(source, (node, _) =>
        {
            if (++rendered == 2)
                throw buildFailure;

            var box = new TrackingBox(disposeFailure: rendered == 1 ? firstDisposeFailure : null);
            boxes.Add(box);
            return box;
        }, out context);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => builder.Build(Markdown.Parse(source), availableWidth: 320));

        Assert.Same(buildFailure, actual);
        Assert.Single(boxes);
        Assert.Equal(1, boxes[0].DisposeCount);
        Assert.Equal(1, boxes[0].NativeLeaseDisposeCount);

        MarkdownLayoutRetirementException retirementFailure = await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
            () => context.DrainOwnedRetirementsAsync(buildFailure));
        Assert.Same(buildFailure, retirementFailure.PrimaryFailure);
        AggregateException cleanupFailures = Assert.IsType<AggregateException>(retirementFailure.InnerException);
        MarkdownLayoutRetirementFailureSummary summary = Assert.Single(
            cleanupFailures.Flatten().InnerExceptions.OfType<MarkdownLayoutRetirementFailureSummary>());
        Assert.Equal(firstDisposeFailure.GetType().FullName, summary.ExceptionType);
        Assert.Equal<int?>(firstDisposeFailure.HResult, summary.HResultCode);
        Assert.DoesNotContain(firstDisposeFailure, cleanupFailures.Flatten().InnerExceptions);
    }

    [Fact]
    public void CancellationDuringBlockConstructionDisposesAlreadyCreatedBoxes()
    {
        const string source = "first\n\nsecond";
        using var cancellation = new CancellationTokenSource();
        var boxes = new List<TrackingBox>();
        LayoutBuilder builder = CreateBuilder(source, (node, _) =>
        {
            var box = new TrackingBox();
            boxes.Add(box);
            cancellation.Cancel();
            return box;
        }, cancellation.Token);

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => builder.Build(Markdown.Parse(source), 320, cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Single(boxes);
        Assert.Equal(1, boxes[0].DisposeCount);
        Assert.Equal(1, boxes[0].NativeLeaseDisposeCount);
    }

    [Fact]
    public void MeasureFailureDisposesAllBuiltBoxesAndPreservesMeasureException()
    {
        const string source = "first\n\nsecond";
        var measureFailure = new InvalidOperationException("measure failed");
        var boxes = new List<TrackingBox>();
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            var box = new TrackingBox(measureFailure: measureFailure);
            boxes.Add(box);
            return box;
        });

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => builder.Build(Markdown.Parse(source), 320));

        Assert.Same(measureFailure, actual);
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, box => Assert.Equal(1, box.DisposeCount));
    }

    [Fact]
    public void NestedParentOwnsCompletedChildWhenLaterChildBuildFails()
    {
        const string source = "> first\n>\n> second";
        var buildFailure = new InvalidOperationException("second paragraph failed");
        var boxes = new List<TrackingBox>();
        int rendered = 0;
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            if (++rendered == 2)
                throw buildFailure;

            var box = new TrackingBox();
            boxes.Add(box);
            return box;
        });

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => builder.Build(Markdown.Parse(source), 320));

        Assert.Same(buildFailure, actual);
        Assert.Single(boxes);
        Assert.Equal(1, boxes[0].DisposeCount);
        Assert.Equal(1, boxes[0].NativeLeaseDisposeCount);
    }

    [Fact]
    public void LazyBuildCancellationBeforeSnapshotConstructionDisposesCreatedBoxes()
    {
        const string source = "first";
        using var cancellation = new CancellationTokenSource();
        var box = new TrackingBox();
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            cancellation.Cancel();
            return box;
        }, cancellation.Token);

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => builder.BuildLazy(
                Markdown.Parse(source),
                availableWidth: 320,
                viewportTop: 0,
                viewportHeight: 400,
                overscan: 100,
                cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(1, box.DisposeCount);
    }

    [Fact]
    public async Task LazyRealizationFailureAfterSnapshotConstructionDisposesSnapshotOwnedBoxes()
    {
        const string source = "first";
        using var cancellation = new CancellationTokenSource();
        var realizationFailure = new OperationCanceledException(cancellation.Token);
        var box = new TrackingBox(measureFailure: realizationFailure);
        MarkdownLayoutContext context;
        LayoutBuilder builder = CreateBuilder(source, (_, _) => box, out context);

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => builder.BuildLazy(
                Markdown.Parse(source),
                availableWidth: 320,
                viewportTop: 0,
                viewportHeight: 400,
                overscan: 100,
                cancellationToken: cancellation.Token));

        Assert.Same(realizationFailure, actual);
        await context.DrainOwnedRetirementsAsync();
        Assert.Equal(1, box.DisposeCount);
        Assert.Equal(1, box.NativeLeaseDisposeCount);
    }

    [Fact]
    public void SuccessfulSnapshotTakesOwnershipUntilSnapshotDisposal()
    {
        const string source = "first";
        var box = new TrackingBox();
        LayoutBuilder builder = CreateBuilder(source, (_, _) => box);

        LayoutSnapshot snapshot = builder.Build(Markdown.Parse(source), 320);

        Assert.Equal(0, box.DisposeCount);
        snapshot.Dispose();
        Assert.Equal(1, box.DisposeCount);
    }

    [Fact]
    public async Task InlineImageOwnershipMovesFromBuilderToSnapshotAndRetiresWithIt()
    {
        const string source = "before ![inline image](https://example.test/image.png) after";
        LayoutBuilder builder = CreateBuilder(source, static (_, _) => null);
        LayoutSnapshot snapshot = builder.Build(Markdown.Parse(source), availableWidth: 320);
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(
            Assert.Single(snapshot.Blocks));
        ImageBox image = Assert.Single(paragraph.InlineImageRuns).Run.Image;

        Assert.False(image.ImageLifetimeDisposedForTests);

        await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(image.ImageLifetimeDisposedForTests);
        Assert.True(image.RetirementTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FailedBuildRetirementBarrierIsTrackedAndDrainedWithoutBlockingBuild()
    {
        const string source = "first\n\nsecond";
        var buildFailure = new InvalidOperationException("second renderer failed");
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boxes = new List<TrackingBox>();
        var observedRetirements = new List<Task>();
        MarkdownLayoutContext context;
        int rendered = 0;
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            if (++rendered == 2)
                throw buildFailure;

            var box = new TrackingBox(retirementTask: retirement.Task);
            boxes.Add(box);
            return box;
        }, out context, retirementSink: observedRetirements.Add);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => builder.Build(Markdown.Parse(source), 320));

        Assert.Same(buildFailure, actual);
        Task observedRetirement = Assert.Single(observedRetirements);
        Assert.Same(retirement.Task, observedRetirement);
        Assert.False(retirement.Task.IsCompleted);
        Task drain = context.DrainOwnedRetirementsAsync();
        Assert.False(drain.IsCompleted);

        retirement.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RetirementDrainBoundsRedactedFailureDetails()
    {
        const int failureCount = 40;
        string source = string.Join("\n\n", Enumerable.Range(0, failureCount + 1).Select(static index => $"p{index}"));
        var buildFailure = new InvalidOperationException("renderer failed");
        var boxes = new List<TrackingBox>();
        MarkdownLayoutContext context;
        int rendered = 0;
        LayoutBuilder builder = CreateBuilder(source, (_, _) =>
        {
            if (++rendered > failureCount)
                throw buildFailure;

            var box = new TrackingBox(disposeFailure: new InvalidOperationException($"private payload {rendered}"));
            boxes.Add(box);
            return box;
        }, out context);

        Assert.Same(
            buildFailure,
            Assert.Throws<InvalidOperationException>(() => builder.Build(Markdown.Parse(source), 320)));
        Assert.All(boxes, box => Assert.Equal(1, box.DisposeCount));

        MarkdownLayoutRetirementException retirementFailure = await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
            () => context.DrainOwnedRetirementsAsync(buildFailure));
        Assert.Same(buildFailure, retirementFailure.PrimaryFailure);
        var summaries = Assert.IsType<AggregateException>(retirementFailure.InnerException)
            .Flatten()
            .InnerExceptions
            .OfType<MarkdownLayoutRetirementFailureSummary>()
            .ToArray();
        Assert.Equal(33, summaries.Length);
        Assert.Equal(8, summaries[^1].OmittedFailureCount);
        Assert.All(summaries.Take(failureCount > 32 ? 32 : failureCount), summary =>
        {
            Assert.NotNull(summary.ExceptionType);
            Assert.NotNull(summary.HResultCode);
            Assert.False(summary.Message.Contains("private payload", StringComparison.Ordinal));
        });
    }

    private static LayoutBuilder CreateBuilder(
        string source,
        Func<ParagraphBlock, MarkdownLayoutContext, BlockBox?> build,
        CancellationToken cancellationToken = default)
        => CreateBuilder(source, build, out _, cancellationToken);

    private static LayoutBuilder CreateBuilder(
        string source,
        Func<ParagraphBlock, MarkdownLayoutContext, BlockBox?> build,
        out MarkdownLayoutContext createdContext,
        CancellationToken cancellationToken = default,
        Action<Task>? retirementSink = null)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Quote] = style,
            [MarkdownElementKeys.List] = style,
            [MarkdownElementKeys.ListMarker] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var registry = new MarkdownExtensionRegistry()
            .RegisterRenderer<ParagraphBlock>(new CallbackParagraphRenderer(build));
        createdContext = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            CancellationToken = cancellationToken,
            RetirementSink = retirementSink,
        };
        return new LayoutBuilder(createdContext);
    }

    private sealed class CallbackParagraphRenderer(
        Func<ParagraphBlock, MarkdownLayoutContext, BlockBox?> build)
        : MarkdownNodeRenderer<ParagraphBlock>
    {
        public override BlockBox? BuildBlock(ParagraphBlock node, MarkdownLayoutContext context)
            => build(node, context);
    }

    private sealed class TrackingBox(
        Exception? measureFailure = null,
        InvalidOperationException? disposeFailure = null,
        Task? retirementTask = null,
        Action? afterMeasure = null) : BlockBox
    {
        private readonly CountingNativeLease _nativeLease = new();
        private int _disposeCount;
        private int _measureCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int NativeLeaseDisposeCount => _nativeLease.DisposeCount;
        internal int MeasureCount => Volatile.Read(ref _measureCount);

        public override float Measure(float availableWidth)
        {
            Interlocked.Increment(ref _measureCount);
            if (measureFailure is not null)
                throw measureFailure;
            afterMeasure?.Invoke();
            return 20;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }

        internal override void CollectRetirementTasks(List<Task> pendingTasks)
        {
            if (retirementTask is { IsCompletedSuccessfully: false })
                pendingTasks.Add(retirementTask);
        }

        public override void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _nativeLease.Dispose();
            if (disposeFailure is not null)
                throw disposeFailure;
        }
    }

    private sealed class CountingNativeLease : IDisposable
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
