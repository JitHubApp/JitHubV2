using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Runtime tests for snapshot-owned focus-plan admission. These exercise the
/// real LayoutSnapshot lease and plan; they do not instantiate a WinUI control.
/// </summary>
public sealed class FocusablePlanAdmissionTests
{
    [Fact]
    public void FixedPlanRequiresLeaseAndReturnsTheLegacyArrayInDocumentOrder()
    {
        var first = NewEmbed(41, "first focus target");
        var second = NewEmbed(84, "second focus target");
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            new MarkdownSourceMap(string.Empty),
            width: 320,
            height: 0);

        Assert.Throws<InvalidOperationException>(() =>
            snapshot.TryCollectFocusableItemsUnderReadLease(out _));

        FocusableItem[] expected =
        [
            new FocusableItem(41, 0, FocusableItemKind.BlockEmbed),
            new FocusableItem(84, 0, FocusableItemKind.BlockEmbed),
        ];

        IReadOnlyList<FocusableItem> firstRead = CollectUnderLease(snapshot);
        Assert.Equal(expected, firstRead);
        Assert.Same(snapshot.CollectFocusableItems(), firstRead);

        IReadOnlyList<FocusableItem> secondRead = CollectUnderLease(snapshot);
        Assert.Same(firstRead, secondRead);
        Assert.Equal(expected, secondRead);
    }

    [Fact]
    public void DynamicOverflowPlanMatchesLegacyFilteringBeforeAndAfterLazyMeasurement()
    {
        var fixedTarget = NewEmbed(101, "fixed target");
        var overflowTarget = new OverflowProbeBlock(blockIndex: 202, height: 24)
        {
            CanScroll = true,
        };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { fixedTarget, overflowTarget },
            new MarkdownSourceMap(string.Empty),
            width: 320,
            height: 0);

        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: 0,
            viewportHeight: 10,
            overscan: 0,
            CancellationToken.None);

        Assert.True(snapshot.IsTopLevelBlockMeasured(fixedTarget));
        Assert.False(snapshot.IsTopLevelBlockMeasured(overflowTarget));
        Assert.Null(ReadFixedPlan(snapshot));

        FocusableItem fixedItem = new(101, 0, FocusableItemKind.BlockEmbed);
        FocusableItem overflowItem = new(202, 0, FocusableItemKind.HorizontalOverflow);
        AssertFocusableResultsMatchLegacy(snapshot, fixedItem);

        LazyLayoutBand estimatedOverflowBand = new(
            overflowTarget.Bounds.Top,
            overflowTarget.Bounds.Bottom);
        snapshot.EnsureMeasuredBand(estimatedOverflowBand, CancellationToken.None);

        Assert.True(snapshot.IsTopLevelBlockMeasured(overflowTarget));
        AssertFocusableResultsMatchLegacy(snapshot, fixedItem, overflowItem);

        overflowTarget.CanScroll = false;
        AssertFocusableResultsMatchLegacy(snapshot, fixedItem);

        overflowTarget.CanScroll = true;
        AssertFocusableResultsMatchLegacy(snapshot, fixedItem, overflowItem);
    }

    [Fact]
    public async Task BusyRelayoutDoesNotReadDynamicGeometryAndRetryMatchesCommittedPlan()
    {
        var overflowTarget = new OverflowProbeBlock(blockIndex: 303, height: 24)
        {
            CanScroll = true,
        };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { overflowTarget },
            new MarkdownSourceMap(string.Empty),
            width: 320,
            height: 0);

        FocusableItem expectedItem = new(303, 0, FocusableItemKind.HorizontalOverflow);
        IReadOnlyList<FocusableItem> previouslyPublished = snapshot.CollectFocusableItems();
        Assert.Equal(new[] { expectedItem }, previouslyPublished);
        int geometryReadsBeforeMutation = overflowTarget.CanScrollReadCount;

        overflowTarget.HoldNextMeasure();
        Task mutation = Task.Run(() => snapshot.RelayoutMeasuredBlocks(320, CancellationToken.None));
        Task<bool>? admissionAttempt = null;
        try
        {
            try
            {
                Assert.True(overflowTarget.MeasureEntered.Wait(TimeSpan.FromSeconds(3)));
                admissionAttempt = Task.Run(() =>
                {
                    bool acquired = snapshot.TryBeginInteraction();
                    try
                    {
                        return acquired;
                    }
                    finally
                    {
                        if (acquired)
                            snapshot.EndInteraction();
                    }
                });

                Task completed = await Task.WhenAny(admissionAttempt, Task.Delay(TimeSpan.FromSeconds(1)));
                Assert.Same(admissionAttempt, completed);
                Assert.False(await admissionAttempt);

                // No focus-plan helper can run without the denied lease; most
                // importantly, the dynamic overflow geometry was not consulted and
                // the already published list remains untouched.
                Assert.Equal(geometryReadsBeforeMutation, overflowTarget.CanScrollReadCount);
                Assert.Equal(new[] { expectedItem }, previouslyPublished);
                Assert.False(mutation.IsCompleted);
            }
            finally
            {
                overflowTarget.ReleaseMeasure.Set();
            }
        }
        finally
        {
            await mutation.WaitAsync(TimeSpan.FromSeconds(5));
            if (admissionAttempt is not null)
                await admissionAttempt.WaitAsync(TimeSpan.FromSeconds(3));
        }

        AssertFocusableResultsMatchLegacy(snapshot, expectedItem);
    }

    [Fact]
    public async Task RecoveryAndRetirementFailClosedEvenForAnAlreadyHeldLease()
    {
        var recoveryTarget = new OverflowProbeBlock(blockIndex: 404, height: 24);
        using (var snapshot = new LayoutSnapshot(
            new BlockBox[] { recoveryTarget },
            new MarkdownSourceMap(string.Empty),
            width: 320,
            height: 0))
        {
            snapshot.EnableLazyLayout(
                availableWidth: 320,
                viewportTop: 1_000,
                viewportHeight: 20,
                overscan: 0,
                CancellationToken.None);
            Assert.False(snapshot.IsTopLevelBlockMeasured(recoveryTarget));

            recoveryTarget.ThrowAfterMeasureMutation = true;
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.Throws<InvalidOperationException>(() => snapshot.EnsureMeasuredBand(
                    new LazyLayoutBand(recoveryTarget.Bounds.Top, recoveryTarget.Bounds.Bottom),
                    CancellationToken.None));
                Assert.True(snapshot.GeometryRecoveryRequired);
                Assert.False(snapshot.TryCollectFocusableItemsUnderReadLease(out IReadOnlyList<FocusableItem> rejected));
                Assert.Empty(rejected);
            }
            finally
            {
                snapshot.EndInteraction();
            }
        }

        var retiringTarget = NewEmbed(505, "retiring focus target");
        var retiringSnapshot = new LayoutSnapshot(
            new BlockBox[] { retiringTarget },
            new MarkdownSourceMap(string.Empty),
            width: 320,
            height: 0);

        Assert.True(retiringSnapshot.TryBeginInteraction());
        Task retirement = Task.CompletedTask;
        try
        {
            retirement = retiringSnapshot.Retire();
            Assert.False(retirement.IsCompleted);
            Assert.False(retiringSnapshot.TryCollectFocusableItemsUnderReadLease(
                out IReadOnlyList<FocusableItem> rejected));
            Assert.Empty(rejected);
        }
        finally
        {
            retiringSnapshot.EndInteraction();
            await retirement.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static IReadOnlyList<FocusableItem> CollectUnderLease(LayoutSnapshot snapshot)
    {
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.TryCollectFocusableItemsUnderReadLease(out IReadOnlyList<FocusableItem> items));
            return items;
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static void AssertFocusableResultsMatchLegacy(
        LayoutSnapshot snapshot,
        params FocusableItem[] expected)
    {
        IReadOnlyList<FocusableItem> legacy = snapshot.CollectFocusableItems();
        Assert.Equal(expected, legacy);

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.TryCollectFocusableItemsUnderReadLease(out IReadOnlyList<FocusableItem> admitted));
            Assert.Equal(expected, admitted);
            Assert.Equal(legacy, admitted);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static EmbedBox NewEmbed(int blockIndex, string text)
        => new(Markdig.Markdown.Parse(text)[0], StubEmbedFactory.Instance)
        {
            BlockIndex = blockIndex,
        };

    private static FocusableItem[]? ReadFixedPlan(LayoutSnapshot snapshot)
    {
        var field = typeof(LayoutSnapshot).GetField(
            "_fixedFocusableItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Snapshot fixed focus-plan storage was not found.");
        return (FocusableItem[]?)field.GetValue(snapshot);
    }

    private sealed class StubEmbedFactory : IMarkdownEmbedFactory
    {
        internal static StubEmbedFactory Instance { get; } = new();

        public bool CanCreate(Markdig.Syntax.Block block) => true;

        public float MeasureHeight(Markdig.Syntax.Block block, float availableWidth) => 48;

        public FrameworkElement CreateBlock(Markdig.Syntax.Block block) =>
            throw new NotSupportedException("Focusable snapshot tests do not realize XAML.");
    }

    private sealed class OverflowProbeBlock : BlockBox, IHorizontalOverflowBox
    {
        private readonly float _height;
        private readonly ManualResetEventSlim _measureEntered = new(initialState: false);
        private readonly ManualResetEventSlim _releaseMeasure = new(initialState: false);
        private int _holdNextMeasure;
        private int _canScrollReadCount;
        private bool _canScroll;

        internal OverflowProbeBlock(int blockIndex, float height)
        {
            BlockIndex = blockIndex;
            _height = height;
        }

        internal ManualResetEventSlim MeasureEntered => _measureEntered;
        internal ManualResetEventSlim ReleaseMeasure => _releaseMeasure;
        internal bool ThrowAfterMeasureMutation { get; set; }
        internal bool CanScroll
        {
            get => _canScroll;
            set => _canScroll = value;
        }
        internal int CanScrollReadCount => Volatile.Read(ref _canScrollReadCount);

        internal void HoldNextMeasure()
        {
            _measureEntered.Reset();
            _releaseMeasure.Reset();
            Volatile.Write(ref _holdNextMeasure, 1);
        }

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, _height + 7);
            if (Interlocked.Exchange(ref _holdNextMeasure, 0) != 0)
            {
                _measureEntered.Set();
                if (!_releaseMeasure.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The admission test did not release the relayout gate.");
            }

            if (ThrowAfterMeasureMutation)
            {
                ThrowAfterMeasureMutation = false;
                throw new InvalidOperationException("Focusable plan recovery probe.");
            }

            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose()
        {
            _measureEntered.Dispose();
            _releaseMeasure.Dispose();
        }

        public double HorizontalOffset => 0;
        public double HorizontalExtent => 240;
        public double HorizontalViewport => 120;
        public bool CanScrollHorizontally
        {
            get
            {
                Interlocked.Increment(ref _canScrollReadCount);
                return _canScroll;
            }
        }
        public bool IsRightToLeft => false;
        public Rect HorizontalViewportBounds => default;
        public Rect HorizontalScrollTrackBounds => default;
        public Rect HorizontalScrollThumbBounds => default;
        public bool SetHorizontalOffset(double offset) => false;
        public bool ScrollHorizontal(double delta) => false;
    }
}
