using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Behavioral P1-032 regressions for the selection-adornment surface owner.
/// Fake hosts execute the actual reentrant and failing Source assignments that
/// source-order contracts cannot; this is not live XAML or native-heap proof.
/// </summary>
public sealed class SelectionAdornerSurfaceLifetimeTests
{
    private const float Dpi100 = 96f;
    private const float Dpi150 = 144f;

    [Fact]
    public void RelayoutResizeAndReplacementSequenceReusesOneAttachedSurface()
    {
        var harness = new Harness();
        var host = new FakeHost();

        FakeSurface? first = harness.Lifetime.Ensure(host, 3060, 776, Dpi100, harness.Device);
        Assert.NotNull(first);

        // The original C14 sequence: narrow, restore, then repeated commits.
        foreach ((double width, double height) in new[] { (760.0, 860.0), (3060.0, 776.0), (3060.0, 776.0), (1200.0, 600.0) })
        {
            Assert.Same(first, harness.Lifetime.Ensure(host, width, height, Dpi100, harness.Device));
            harness.Lifetime.AdvanceFrame();
        }

        Assert.Single(harness.Created);
        Assert.Same(first, host.Source);
        Assert.Equal(1, host.SetCount);
        Assert.Equal(0, harness.Lifetime.RetiredCount);
        Assert.True(harness.Lifetime.Allocation.Width >= 3060 && harness.Lifetime.Allocation.Height >= 860);
    }

    [Fact]
    public void GrowingBeyondTheAllocationReplacesOnceAndRetiresThePriorSurfaceForBoundedFrames()
    {
        var harness = new Harness();
        var host = new FakeHost();

        FakeSurface first = harness.Lifetime.Ensure(host, 200, 200, Dpi100, harness.Device)!;
        FakeSurface second = harness.Lifetime.Ensure(host, 900, 300, Dpi100, harness.Device)!;

        Assert.NotSame(first, second);
        Assert.Same(second, host.Source);
        Assert.Equal(1, harness.Lifetime.RetiredCount);
        harness.Lifetime.AdvanceFrame();
        Assert.Equal(1, harness.Lifetime.RetiredCount);
        harness.Lifetime.AdvanceFrame();
        Assert.Equal(0, harness.Lifetime.RetiredCount);

        Assert.Same(second, harness.Lifetime.Ensure(host, 800, 300, Dpi100, harness.Device));
        Assert.Equal(2, harness.Created.Count);
    }

    [Fact]
    public void DpiOrDeviceChangeReplacesTheSurface()
    {
        var harness = new Harness();
        var host = new FakeHost();

        FakeSurface first = harness.Lifetime.Ensure(host, 400, 300, Dpi100, harness.Device)!;
        FakeSurface scaled = harness.Lifetime.Ensure(host, 400, 300, Dpi150, harness.Device)!;
        var recovered = new FakeDevice();
        FakeSurface rebuilt = harness.Lifetime.Ensure(host, 400, 300, Dpi150, recovered)!;

        Assert.NotSame(first, scaled);
        Assert.NotSame(scaled, rebuilt);
        Assert.Same(recovered, rebuilt.Device);
        Assert.Equal(Dpi150, rebuilt.Allocation.Dpi);
        Assert.Same(recovered, harness.Lifetime.Device);
    }

    [Fact]
    public void ANewAdornerImageAlwaysReceivesTheLiveSurfaceEvenWhenTheSizeMatches()
    {
        var harness = new Harness();
        var original = new FakeHost();
        FakeSurface surface = harness.Lifetime.Ensure(original, 500, 400, Dpi100, harness.Device)!;

        var reloaded = new FakeHost();
        Assert.Same(surface, harness.Lifetime.Ensure(reloaded, 500, 400, Dpi100, harness.Device));

        Assert.Same(surface, reloaded.Source);
        Assert.Single(harness.Created);
    }

    [Fact]
    public void AnExternallyClearedSourceIsRestoredBeforeTheSurfaceIsReturned()
    {
        var harness = new Harness();
        var host = new FakeHost();
        FakeSurface surface = harness.Lifetime.Ensure(host, 500, 400, Dpi100, harness.Device)!;

        host.Source = null;

        Assert.Same(surface, harness.Lifetime.Ensure(host, 500, 400, Dpi100, harness.Device));
        Assert.Same(surface, host.Source);
    }

    [Fact]
    public void ReentrantReleaseDuringAttachmentIsDeferredUntilTheOuterAssignmentReturns()
    {
        var harness = new Harness();
        var host = new FakeHost();
        host.OnSet = _ => Assert.False(harness.Lifetime.Release(host));

        FakeSurface? result = harness.Lifetime.Ensure(host, 640, 480, Dpi100, harness.Device);

        Assert.Null(result);
        Assert.Null(host.Source);
        Assert.Null(harness.Lifetime.Surface);
        Assert.Equal(1, host.MaximumSetDepth);
        Assert.Equal(1, harness.Lifetime.RetiredCount);
        Assert.True(harness.Lifetime.IsRetired(harness.Created[0]));
    }

    [Fact]
    public void ReentrantEnsureDuringAttachmentNeverNestsASourceAssignment()
    {
        var harness = new Harness();
        var host = new FakeHost();
        FakeSurface? nested = null;
        host.OnSet = _ =>
        {
            host.OnSet = null;
            nested = harness.Lifetime.Ensure(host, 2000, 2000, Dpi100, harness.Device);
        };

        FakeSurface? outer = harness.Lifetime.Ensure(host, 640, 480, Dpi100, harness.Device);

        Assert.Null(nested);
        Assert.NotNull(outer);
        Assert.Same(outer, host.Source);
        Assert.Equal(1, host.MaximumSetDepth);
        Assert.Single(harness.Created);
    }

    [Fact]
    public void AThrowingDetachIsReportedAndLeavesNoBoundOwnerOrUnrootedSurface()
    {
        var harness = new Harness();
        var host = new FakeHost();
        FakeSurface surface = harness.Lifetime.Ensure(host, 300, 200, Dpi100, harness.Device)!;
        host.ThrowOnSet = new InvalidOperationException("detached input site");

        Assert.False(harness.Lifetime.Release(host));

        Exception reported = Assert.Single(harness.ReportedFailures);
        Assert.Same(host.ThrowOnSet, reported);
        Assert.Null(harness.Lifetime.Surface);
        Assert.Equal(1, harness.Lifetime.RetiredCount);
        Assert.True(harness.Lifetime.IsRetired(surface));

        host.ThrowOnSet = null;
        FakeSurface replacement = harness.Lifetime.Ensure(host, 300, 200, Dpi100, harness.Device)!;
        Assert.NotSame(surface, replacement);
        Assert.Same(replacement, host.Source);
    }

    [Fact]
    public void AThrowingAttachmentKeepsThePreviousOwnerAndRootsTheRejectedReplacement()
    {
        var harness = new Harness();
        var host = new FakeHost();
        FakeSurface first = harness.Lifetime.Ensure(host, 300, 200, Dpi100, harness.Device)!;
        var failure = new InvalidOperationException("attachment failed");
        host.ThrowOnSet = failure;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => harness.Lifetime.Ensure(host, 300, 200, Dpi150, harness.Device)));

        Assert.Same(first, harness.Lifetime.Surface);
        Assert.Equal(1, harness.Lifetime.RetiredCount);
        Assert.True(harness.Lifetime.IsRetired(harness.Created[1]));

        // The attachment state is unknown, so the next request rebinds.
        host.ThrowOnSet = null;
        harness.Lifetime.Ensure(host, 300, 200, Dpi100, harness.Device);
        Assert.Same(first, host.Source);
    }

    [Fact]
    public void ACreationFailureLeavesTheCurrentOwnerUntouched()
    {
        var harness = new Harness();
        var host = new FakeHost();
        FakeSurface first = harness.Lifetime.Ensure(host, 300, 200, Dpi100, harness.Device)!;
        harness.ThrowOnCreate = new InvalidOperationException("device removed");

        Assert.Throws<InvalidOperationException>(
            () => harness.Lifetime.Ensure(host, 300, 200, Dpi150, harness.Device));

        Assert.Same(first, harness.Lifetime.Surface);
        Assert.Same(first, host.Source);
        Assert.Equal(0, harness.Lifetime.RetiredCount);
    }

    [Fact]
    public void ReleaseIsIdempotentAndInvalidViewportsAreDeclinedWithoutAllocation()
    {
        var harness = new Harness();
        var host = new FakeHost();

        Assert.Null(harness.Lifetime.Ensure(host, double.NaN, 100, Dpi100, harness.Device));
        Assert.Null(harness.Lifetime.Ensure(host, 100, 0, Dpi100, harness.Device));
        Assert.Null(harness.Lifetime.Ensure(host, 100, 100, 0f, harness.Device));
        Assert.Empty(harness.Created);

        harness.Lifetime.Ensure(host, 100, 100, Dpi100, harness.Device);
        Assert.True(harness.Lifetime.Release(host));
        Assert.True(harness.Lifetime.Release(host));
        Assert.Null(host.Source);
        Assert.Equal(1, harness.Lifetime.RetiredCount);
        harness.Lifetime.DropRetired();
        Assert.Equal(0, harness.Lifetime.RetiredCount);
    }

    [Theory]
    [InlineData(3060, 776, 96f)]
    [InlineData(760, 860, 144f)]
    [InlineData(1, 1, 192f)]
    public void AllocationsCoverTheViewportInCoarseBuckets(int width, int height, float dpi)
    {
        Assert.True(SelectionAdornerSurfaceAllocationPolicy.TryGetRequired(width, height, dpi, out var required));
        var allocation = SelectionAdornerSurfaceAllocationPolicy.Allocate(required, current: null);

        Assert.True(allocation.Width >= width && allocation.Height >= height);
        Assert.Equal(0, allocation.Width % SelectionAdornerSurfaceAllocationPolicy.BucketDips);
        Assert.Equal(0, allocation.Height % SelectionAdornerSurfaceAllocationPolicy.BucketDips);
        Assert.True(SelectionAdornerSurfaceAllocationPolicy.CanReuse(allocation, required));
    }

    [Fact]
    public void AnOversizedAllocationIsReplacedForAMuchSmallerViewport()
    {
        Assert.True(SelectionAdornerSurfaceAllocationPolicy.TryGetRequired(4000, 4000, 96f, out var large));
        var allocation = SelectionAdornerSurfaceAllocationPolicy.Allocate(large, current: null);
        Assert.True(SelectionAdornerSurfaceAllocationPolicy.TryGetRequired(300, 200, 96f, out var small));

        Assert.False(SelectionAdornerSurfaceAllocationPolicy.CanReuse(allocation, small));
        var shrunk = SelectionAdornerSurfaceAllocationPolicy.Allocate(small, allocation);
        Assert.Equal(512, shrunk.Width);
        Assert.Equal(256, shrunk.Height);
    }

    [Fact]
    public void AllocationNeverExceedsTheWin2DSurfaceLimitUnlessTheViewportRequiresIt()
    {
        Assert.True(SelectionAdornerSurfaceAllocationPolicy.TryGetRequired(8150, 100, 192f, out var required));
        var allocation = SelectionAdornerSurfaceAllocationPolicy.Allocate(required, current: null);

        Assert.True(allocation.Width >= 8150);
        Assert.True(allocation.Width * 2 <= SelectionAdornerSurfaceAllocationPolicy.MaximumPixelDimension);
    }

    [Fact]
    public void RetiredSurfacesAreDisposedExactlyOnceWhenTheirFramesElapse()
    {
        var harness = new Harness();
        var host = new FakeHost();

        FakeSurface first = harness.Lifetime.Ensure(host, 200, 200, Dpi100, harness.Device)!;
        FakeSurface second = harness.Lifetime.Ensure(host, 900, 300, Dpi100, harness.Device)!;
        harness.Lifetime.AdvanceFrame();
        Assert.Empty(harness.Disposed);
        harness.Lifetime.AdvanceFrame();
        harness.Lifetime.AdvanceFrame();

        Assert.Equal(new[] { first }, harness.Disposed);
        Assert.Same(second, host.Source);
        Assert.DoesNotContain(second, harness.Disposed);
    }

    [Fact]
    public void DropRetiredDisposesReleasedSurfacesButNeverTheLiveOne()
    {
        var harness = new Harness();
        var host = new FakeHost();

        FakeSurface live = harness.Lifetime.Ensure(host, 200, 200, Dpi100, harness.Device)!;
        harness.Lifetime.DropRetired();
        Assert.Empty(harness.Disposed);

        Assert.True(harness.Lifetime.Release(host));
        Assert.Null(host.Source);
        harness.Lifetime.DropRetired();
        harness.Lifetime.DropRetired();

        Assert.Equal(new[] { live }, harness.Disposed);
        Assert.Equal(0, harness.Lifetime.RetiredCount);
    }

    [Fact]
    public void AThrowingDisposeIsReportedAndDoesNotStrandOtherRetiredSurfaces()
    {
        var harness = new Harness();
        var host = new FakeHost();

        FakeSurface first = harness.Lifetime.Ensure(host, 200, 200, Dpi100, harness.Device)!;
        FakeSurface second = harness.Lifetime.Ensure(host, 900, 300, Dpi100, harness.Device)!;
        Assert.True(harness.Lifetime.Release(host));
        harness.ThrowOnDispose = first;

        harness.Lifetime.DropRetired();

        Assert.Equal(new[] { first, second }, harness.DisposeAttempts);
        Assert.Single(harness.ReportedFailures);
        Assert.Equal(0, harness.Lifetime.RetiredCount);
    }

    [Fact]
    public void SharedDeviceProjectionIsIdentityStableSoTheLayoutDeviceGateCannotLoop()
    {
        // The control compares the snapshot's layout device with the shared
        // device by reference; a fresh wrapper per call would rebuild forever.
        Microsoft.Graphics.Canvas.CanvasDevice first = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        Microsoft.Graphics.Canvas.CanvasDevice second = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();

        Assert.Same(first, second);
    }

    private sealed class Harness
    {
        internal Harness()
        {
            Lifetime = new SelectionAdornerSurfaceLifetime<FakeSurface, FakeDevice>(
                (device, allocation) =>
                {
                    if (ThrowOnCreate is { } failure)
                        throw failure;
                    var surface = new FakeSurface(device, allocation);
                    Created.Add(surface);
                    return surface;
                },
                ReportedFailures.Add,
                surface =>
                {
                    DisposeAttempts.Add(surface);
                    if (ReferenceEquals(surface, ThrowOnDispose))
                        throw new InvalidOperationException("dispose failed");
                    Disposed.Add(surface);
                });
        }

        internal FakeDevice Device { get; } = new();
        internal List<FakeSurface> Created { get; } = new();
        internal List<Exception> ReportedFailures { get; } = new();
        internal Exception? ThrowOnCreate { get; set; }
        internal FakeSurface? ThrowOnDispose { get; set; }
        internal List<FakeSurface> Disposed { get; } = new();
        internal List<FakeSurface> DisposeAttempts { get; } = new();
        internal SelectionAdornerSurfaceLifetime<FakeSurface, FakeDevice> Lifetime { get; }
    }

    private sealed class FakeDevice;

    private sealed class FakeSurface(FakeDevice device, SelectionAdornerSurfaceAllocation allocation)
    {
        internal FakeDevice Device { get; } = device;
        internal SelectionAdornerSurfaceAllocation Allocation { get; } = allocation;
    }

    private sealed class FakeHost : ISelectionAdornerSurfaceHost<FakeSurface>
    {
        private FakeSurface? _source;
        private int _depth;

        internal Action<FakeSurface?>? OnSet { get; set; }
        internal Exception? ThrowOnSet { get; set; }
        internal int SetCount { get; private set; }
        internal int MaximumSetDepth { get; private set; }

        public FakeSurface? Source
        {
            get => _source;
            set
            {
                if (ThrowOnSet is { } failure)
                    throw failure;
                _depth++;
                MaximumSetDepth = System.Math.Max(MaximumSetDepth, _depth);
                try
                {
                    SetCount++;
                    _source = value;
                    OnSet?.Invoke(value);
                }
                finally
                {
                    _depth--;
                }
            }
        }
    }
}
