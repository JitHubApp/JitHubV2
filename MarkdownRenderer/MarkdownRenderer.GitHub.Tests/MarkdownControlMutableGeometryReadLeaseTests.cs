using System.IO;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Regressions for renderer-control readers that used to inspect mutable layout
/// geometry without pinning the exact snapshot generation that owned it.
/// </summary>
public sealed class MarkdownControlMutableGeometryReadLeaseTests
{
    [Fact]
    public void LazyBlockScrollTargetUsesOwnerEstimateUntilNestedTargetIsMeasured()
    {
        var root = new StackBox { BlockIndex = 1, InterChildSpacing = 5 };
        var first = new FixedBox(10, 20);
        var targetBlock = new FixedBox(11, 35);
        root.Add(first);
        root.Add(targetBlock);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { root },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 100,
            overscan: 0,
            CancellationToken.None);

        SnapshotBlockScrollTargetState estimatedState = snapshot.TryCaptureBlockScrollTarget(
            targetBlock.BlockIndex,
            out SnapshotBlockScrollTarget estimated);

        Assert.Equal(SnapshotBlockScrollTargetState.RequiresRealization, estimatedState);
        Assert.False(estimated.OwnerMeasured);
        Assert.Equal(targetBlock.BlockIndex, estimated.BlockIndex);
        Assert.Equal(estimated.OwnerTop, estimated.TargetTop);
        Assert.Equal(root.Bounds.Top, estimated.OwnerTop);
        Assert.Equal(root.Bounds.Bottom, estimated.OwnerBottom);

        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(estimated.OwnerTop, System.Math.Max(estimated.OwnerTop + 1, estimated.OwnerBottom)),
            CancellationToken.None);

        SnapshotBlockScrollTargetState measuredState = snapshot.TryCaptureBlockScrollTarget(
            targetBlock.BlockIndex,
            out SnapshotBlockScrollTarget measured);

        Assert.Equal(SnapshotBlockScrollTargetState.Ready, measuredState);
        Assert.True(measured.OwnerMeasured);
        Assert.Equal(targetBlock.Bounds.Top, measured.TargetTop);
        Assert.Equal(snapshot.LayoutRevision, measured.LayoutRevision);
        Assert.Equal(first.Bounds.Bottom + root.InterChildSpacing, measured.TargetTop);
    }

    [Fact]
    public async Task BlockScrollGeometryReadReturnsBusyInsteadOfWaitingForMeasurement()
    {
        var block = new GatedMeasureBox(73, 48);
        using var snapshot = CreateLazySnapshot(block);
        Task measurement = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            Task<SnapshotBlockScrollTargetState> read = Task.Run(() =>
                snapshot.TryCaptureBlockScrollTarget(block.BlockIndex, out _));
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(500)));
            Assert.Equal(SnapshotBlockScrollTargetState.Busy, await read);
            Assert.False(measurement.IsCompleted);
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await measurement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            SnapshotBlockScrollTargetState.Ready,
            snapshot.TryCaptureBlockScrollTarget(block.BlockIndex, out _));
    }

    [Fact]
    public void ScrollAnchorReplacementUsesOnlyTheCapturedBlockIdentity()
    {
        var first = new FixedBox(10, 20);
        var second = new FixedBox(20, 30);
        first.Measure(100);
        first.Arrange(0, 100, 100);
        second.Measure(100);
        second.Arrange(0, 200, 100);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 230);

        Assert.True(snapshot.TryCaptureScrollAnchorTarget(20, 5, out double exactOffset));
        Assert.Equal(195, exactOffset);

        // A replacement that removed the original anchor must preserve the
        // established height-ratio fallback, not snap to an unrelated block.
        Assert.False(snapshot.TryCaptureScrollAnchorTarget(15, 5, out double missingOffset));
        Assert.Equal(0, missingOffset);
    }

    [Fact]
    public void AutomationImageReadUsesPublishedPlanAndConservativeNonblockingLease()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string readiness = GetMethodBody(source, "HasVisibleLoadingImagesForAutomation()");

        Assert.Contains("Volatile.Read(ref _imagePlanPublication)", readiness, StringComparison.Ordinal);
        Assert.Contains("snapshot.TryBeginInteraction()", readiness, StringComparison.Ordinal);
        Assert.Contains("return true", readiness, StringComparison.Ordinal); // conservative busy/stale result
        Assert.Contains("finally", readiness, StringComparison.Ordinal);
        Assert.Contains("snapshot.EndInteraction()", readiness, StringComparison.Ordinal);
        Assert.Contains("publication.LayoutRevision", readiness, StringComparison.Ordinal);
        Assert.Contains("Layout.Boxes.ImageBox[] images = publication.Images", readiness, StringComparison.Ordinal);
        Assert.Contains("Rect[] imageBounds = publication.ImageBounds", readiness, StringComparison.Ordinal);
        Assert.Contains("imageBounds.Length != images.Length", readiness, StringComparison.Ordinal);
        // The viewport index is sorted by geometry; its slot is not necessarily
        // the published image ordinal. Image and bounds must use the same map.
        Assert.Contains("imageIndex.GetBlockOrdinal(index)", readiness, StringComparison.Ordinal);
        Assert.Contains("images[ordinal]", readiness, StringComparison.Ordinal);
        Assert.Contains("imageBounds[ordinal]", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("imageBounds[index]", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("image.Bounds", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("_imagePlans", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("_imagePlanIndex", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherQueue", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("await ", readiness, StringComparison.Ordinal);

        int publicationCheckStart = source.IndexOf("private bool IsCurrentImagePlanPublication(", StringComparison.Ordinal);
        Assert.True(publicationCheckStart >= 0);
        string currentPublication = source[publicationCheckStart..];
        currentPublication = currentPublication[..currentPublication.IndexOf(';')];
        Assert.Contains("ReferenceEquals(snapshot, _snapshot)", currentPublication, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration == Volatile.Read(ref _snapshotGeneration)", currentPublication, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(publication, Volatile.Read(ref _imagePlanPublication))", currentPublication, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(snapshot, publication.Snapshot)", currentPublication, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration == publication.SnapshotGeneration", currentPublication, StringComparison.Ordinal);

        int rebuildStart = source.IndexOf("private bool RebuildRealizationPlans(", StringComparison.Ordinal);
        Assert.True(rebuildStart >= 0);
        int beginLease = source.IndexOf("snapshot.TryBeginInteraction()", rebuildStart, StringComparison.Ordinal);
        int capturePublication = source.IndexOf("imagePlanPublication = new ImagePlanPublication(", beginLease, StringComparison.Ordinal);
        int endLease = source.IndexOf("snapshot.EndInteraction()", capturePublication, StringComparison.Ordinal);
        int publish = source.IndexOf("Volatile.Write(ref _imagePlanPublication, imagePlanPublication)", endLease, StringComparison.Ordinal);
        Assert.True(beginLease >= 0 && capturePublication > beginLease && endLease > capturePublication && publish > endLease);
    }

    [Fact]
    public void TransientInteractionResetDoesNotTraverseOrMutateRetainedSnapshotBoxes()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string reset = GetMethodBody(source, "private void ResetTransientInteractionState(bool clearSelection)");

        // Reset runs immediately after requesting lazy-layout cancellation, which
        // does not wait for an in-flight snapshot writer. HoveredRun is diagnostic
        // only, so clearing the control-owned references is sufficient.
        Assert.DoesNotContain("snapshot.Blocks", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearHover(", reset, StringComparison.Ordinal);
        Assert.Contains("_lastHoveredRun = null", reset, StringComparison.Ordinal);
        Assert.Contains("_lastHoveredBox = null", reset, StringComparison.Ordinal);

        // Pointer-driven hover cleanup remains in the input path, whose event
        // dispatcher admits an interaction lease before invoking the handlers.
        string pointerInput = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.PointerInput.cs");
        string pointerDispatch = GetMethodBody(pointerInput, "private void DispatchPointerInput(");
        string pointerMove = GetMethodBody(source, "private void ProcessPointerMoved(ref PointerInput e)");
        Assert.Contains("snapshot.TryBeginInteraction()", pointerDispatch, StringComparison.Ordinal);
        int processInput = pointerDispatch.IndexOf("ProcessPointerInput(ref input)", StringComparison.Ordinal);
        int finallyStart = processInput < 0
            ? -1
            : pointerDispatch.IndexOf("finally", processInput, StringComparison.Ordinal);
        int clearInteractionSnapshot = finallyStart < 0
            ? -1
            : pointerDispatch.IndexOf(
                "input.InteractionSnapshot = null;",
                finallyStart,
                StringComparison.Ordinal);
        int endInteraction = clearInteractionSnapshot < 0
            ? -1
            : pointerDispatch.IndexOf(
                "snapshot.EndInteraction();",
                clearInteractionSnapshot,
                StringComparison.Ordinal);
        Assert.True(
            processInput >= 0 &&
            finallyStart > processInput &&
            clearInteractionSnapshot > finallyStart &&
            endInteraction > clearInteractionSnapshot,
            "Pointer processing must run under the interaction lease, then clear its transient snapshot alias before releasing the lease in finally.");
        Assert.Contains("ClearHover(", pointerMove, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollToBlockUsesDetachedGenerationFencedGeometryAndRetriesAfterCommit()
    {
        string control = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string snapshot = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "LayoutSnapshot.cs");
        string scroll = GetMethodBody(control, "public void ScrollToBlock(int blockIndex)");
        string capture = GetMethodBody(snapshot, "TryCaptureBlockScrollTarget(");
        string anchorCapture = GetMethodBody(snapshot, "TryCaptureScrollAnchorTarget(");

        Assert.Contains("snapshot.TryCaptureBlockScrollTarget(", scroll, StringComparison.Ordinal);
        Assert.Contains("SnapshotBlockScrollTargetState.Busy", scroll, StringComparison.Ordinal);
        Assert.Contains("_pendingScrollToBlock = (snapshot, snapshotGeneration, blockIndex)", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Blocks", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain(".Bounds", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBeginInteraction", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor.Enter", scroll, StringComparison.Ordinal);

        int captureTarget = scroll.IndexOf("snapshot.TryCaptureBlockScrollTarget(", StringComparison.Ordinal);
        int queue = scroll.IndexOf("QueueLazyLayoutForBand(", StringComparison.Ordinal);
        int validateAfterQueue = scroll.IndexOf("IsCurrentScrollTarget(snapshot, snapshotGeneration, target.LayoutRevision)", queue, StringComparison.Ordinal);
        int scrollAction = scroll.IndexOf("ChangeView(", StringComparison.Ordinal);
        Assert.True(captureTarget >= 0 && queue > captureTarget && validateAfterQueue > queue && scrollAction > validateAfterQueue);
        Assert.Contains("target.OwnerTop", scroll, StringComparison.Ordinal);
        Assert.Contains("target.OwnerBottom", scroll, StringComparison.Ordinal);
        Assert.Contains("target.OwnerHeight", scroll, StringComparison.Ordinal);

        Assert.Contains("Monitor.TryEnter(_layoutLock)", capture, StringComparison.Ordinal);
        Assert.Contains("finally", capture, StringComparison.Ordinal);
        Assert.Contains("Monitor.Exit(_layoutLock)", capture, StringComparison.Ordinal);
        Assert.Contains("TryFindBlockBoundsNoLock", capture, StringComparison.Ordinal);
        Assert.Contains("_layoutRevision", capture, StringComparison.Ordinal);

        Assert.Contains("_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)", anchorCapture, StringComparison.Ordinal);
        Assert.Contains("CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Top - offsetFromTop", anchorCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("bestDistance", anchorCapture, StringComparison.Ordinal);
        Assert.Contains("if (newY is null)", control, StringComparison.Ordinal);
        Assert.Contains("ratio * snapshot.Size.Height", control, StringComparison.Ordinal);
        Assert.Contains("TryResumePendingScrollToBlock(snapshot, Volatile.Read(ref _snapshotGeneration))", control, StringComparison.Ordinal);
        Assert.Contains("TryResumePendingScrollToBlock(snapshot, pipelineGeneration)", control, StringComparison.Ordinal);
    }

    private static LayoutSnapshot CreateLazySnapshot(BlockBox block)
    {
        var snapshot = new LayoutSnapshot(
            new[] { block },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 100,
            overscan: 0,
            CancellationToken.None);
        return snapshot;
    }

    private static string GetMethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
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
            $"Could not locate repository source '{Path.Combine(pathParts)}' from '{AppContext.BaseDirectory}'.");
    }

    private sealed class FixedBox : BlockBox
    {
        private readonly float _height;

        public FixedBox(int blockIndex, double height)
        {
            BlockIndex = blockIndex;
            _height = checked((float)height);
        }

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }

    private sealed class GatedMeasureBox : BlockBox
    {
        private readonly float _height;

        internal ManualResetEventSlim MeasureEntered { get; } = new(initialState: false);
        internal ManualResetEventSlim ReleaseMeasure { get; } = new(initialState: false);

        public GatedMeasureBox(int blockIndex, double height)
        {
            BlockIndex = blockIndex;
            _height = checked((float)height);
        }

        public override float Measure(float availableWidth)
        {
            MeasureEntered.Set();
            ReleaseMeasure.Wait();
            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose()
        {
            ReleaseMeasure.Set();
            MeasureEntered.Dispose();
            ReleaseMeasure.Dispose();
        }
    }
}
