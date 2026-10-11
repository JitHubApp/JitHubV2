using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-contract regressions for keyboard focus geometry. These checks do
/// not create WinUI controls; the runtime snapshot lease itself is covered by
/// the headless LayoutSnapshot lease tests.
/// </summary>
public sealed class MarkdownControlFocusableGeometryReadLeaseTests
{
    [Fact]
    public void FocusRectAndSpatialMapReadsUseNonblockingExactRevisionLeases()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string itemRect = GetMethodBody(source, "private FocusGeometryReadStatus TryCaptureFocusableItemRect(");
        string map = GetMethodBody(source, "private FocusGeometryReadStatus TryCaptureFocusableRectMap(");
        string geometry = GetMethodBody(source, "private Rect? GetFocusableItemRectFromSnapshotNoLock(");
        string movement = GetMethodBody(source, "private FocusGeometryReadStatus TryMoveFocusSpatial(");

        AssertLeaseFences(itemRect, "rect = GetFocusableItemRectFromSnapshotNoLock(snapshot, item)");
        AssertLeaseFences(map, "GetFocusableItemRectFromSnapshotNoLock(snapshot, items[i])");
        Assert.Contains("out List<(int Index, Rect Rect)> result", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Blocks", geometry, StringComparison.Ordinal);
        Assert.Contains("TryGetHorizontalOverflowForBlockIndex", geometry, StringComparison.Ordinal);
        Assert.Contains("IsCurrentScrollTarget(snapshot, generation, revision)", movement, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor.Enter", itemRect + map + movement, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait(", itemRect + map + movement, StringComparison.Ordinal);
    }

    [Fact]
    public void BusySpatialKeyIntentIsBoundedAndStoresOnlyValueIdentity()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string queue = GetMethodBody(source, "private bool QueuePendingSpatialFocusKey(VirtualKey key)");
        string clear = GetMethodBody(source, "private void ClearPendingSpatialFocusKeys()");
        string retry = GetMethodBody(source, "private void ProcessPendingFocusGeometry()");

        Assert.Contains("MaxPendingSpatialFocusKeys = 64", source, StringComparison.Ordinal);
        Assert.Contains("Queue<VirtualKey>", source, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusKeys.Enqueue(key)", queue, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusKeys.Count >= MaxPendingSpatialFocusKeys", queue, StringComparison.Ordinal);
        Assert.Contains("Layout.FocusableItem _pendingSpatialFocusOrigin", source, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusGeneration", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutSnapshot", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockBox", queue, StringComparison.Ordinal);
        Assert.Contains("TryProcessFocusSpatialKey(key)", retry, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusKeys.Dequeue()", retry, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusQueueEpoch", retry, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusKeys.Clear()", clear, StringComparison.Ordinal);
        Assert.Contains("ClearPendingFocusGeometryWork()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusedItemScrollDetachesRectBeforeDispatchAndRetriesStaleRevision()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string scroll = GetMethodBody(source, "private bool ScrollFocusedItemIntoView()");
        string enqueue = GetMethodBody(source, "private bool QueueFocusedItemScroll(");
        string publish = GetMethodBody(source, "private void ApplyFocusedItemScroll(");

        int begin = scroll.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int geometry = scroll.IndexOf("GetFocusableItemRectFromSnapshotNoLock(snapshot, item)", StringComparison.Ordinal);
        int end = scroll.IndexOf("snapshot.EndInteraction()", geometry, StringComparison.Ordinal);
        int queue = scroll.IndexOf("QueueFocusedItemScroll(snapshot, generation, revision, item, detachedRect)", end, StringComparison.Ordinal);
        Assert.True(begin >= 0 && geometry > begin && end > geometry && queue > end);
        Assert.DoesNotContain("ChangeView(", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("BringDocumentRectIntoExternalViewport", scroll, StringComparison.Ordinal);
        Assert.Contains("new WeakReference<LayoutSnapshot>(snapshot)", enqueue, StringComparison.Ordinal);
        Assert.Contains("ApplyFocusedItemScroll(weakSnapshot, snapshotGeneration, layoutRevision, item, rect)", enqueue, StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision != layoutRevision", publish, StringComparison.Ordinal);
        Assert.Contains("RequestFocusGeometryRetry()", publish, StringComparison.Ordinal);
        Assert.Contains("SameFocusableIdentity(current, item)", publish, StringComparison.Ordinal);
        Assert.Contains("ChangeView(", publish, StringComparison.Ordinal);
        Assert.Contains("BringDocumentRectIntoExternalViewport", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowVisualsAreCapturedUnderLeaseAndPublishedOnlyAfterRelease()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string refresh = GetMethodBody(source, "private void RefreshHorizontalOverflowVisualState(");
        string capture = GetMethodBody(source, "private bool TryCaptureHorizontalOverflowVisualStateNoLock(");
        string apply = GetMethodBody(source, "private void ApplyHorizontalOverflowVisualState(");

        int begin = refresh.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int captureState = refresh.IndexOf("TryCaptureHorizontalOverflowVisualStateNoLock(", begin, StringComparison.Ordinal);
        int end = refresh.IndexOf("snapshot.EndInteraction()", captureState, StringComparison.Ordinal);
        int publish = refresh.IndexOf("PublishHorizontalOverflowVisualState(state)", end, StringComparison.Ordinal);
        Assert.True(begin >= 0 && captureState > begin && end > captureState && publish > end);
        Assert.Contains("WeakReference<LayoutSnapshot>", capture, StringComparison.Ordinal);
        Assert.Contains("WeakReference<IHorizontalOverflowBox>", capture, StringComparison.Ordinal);
        Assert.Contains("WeakReference<InlineEmbedPlan>", capture, StringComparison.Ordinal);
        Assert.Contains("target.HorizontalViewportBounds", capture, StringComparison.Ordinal);
        Assert.Contains("target.HorizontalScrollTrackBounds", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalViewportBounds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalScrollTrackBounds", apply, StringComparison.Ordinal);
        Assert.Contains("IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision)", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryTimerServicesAndRetainsPendingFocusWorkUntilResolvedOrInvalidated()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string timer = GetMethodBody(source, "private void StartImageRelayoutRecovery()");
        string tick = GetMethodBody(source, "private static void ProcessImageRelayoutRecovery(");
        string idle = GetMethodBody(source, "private void StopImageRelayoutRecoveryIfIdle()");
        string clear = GetMethodBody(source, "private void ClearPendingFocusGeometryWork()");
        string clearSpatial = GetMethodBody(source, "private void ClearPendingSpatialFocusKeys()");
        string release = GetMethodBody(source, "private void ReleaseLoadedResources()");

        Assert.Contains("HasImageRelayoutRecoveryClock || DispatcherQueue is not { } dispatcher", timer, StringComparison.Ordinal);
        Assert.Contains("ProcessImageRelayoutRecovery(control)", timer, StringComparison.Ordinal);
        Assert.Contains("callback => dispatcher.TryEnqueue(callback.Invoke)", timer, StringComparison.Ordinal);
        Assert.Contains("control.ProcessBitmapOutputRecovery()", tick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessQueuedImageRelayout(", tick, StringComparison.Ordinal);
        Assert.Contains("control._imageRelayoutPayload?.PublishAfterWorkDrains()", tick, StringComparison.Ordinal);
        Assert.Contains("control.PublishPendingLayoutCommit()", tick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessQueuedLazyLayoutRealization(", tick, StringComparison.Ordinal);
        Assert.Contains("control.TryPublishLazyLayoutCompletion(", tick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessPendingHorizontalOverflowVisualRetry()", tick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessPendingFocusGeometry()", tick, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusKeys.Count > 0", idle, StringComparison.Ordinal);
        Assert.Contains("_pendingFocusGeometryRefresh", idle, StringComparison.Ordinal);
        Assert.Contains("_hasPendingFocusResumePoint", idle, StringComparison.Ordinal);
        Assert.Contains("_hasPendingHorizontalOverflowRefresh", idle, StringComparison.Ordinal);
        Assert.Contains("ClearPendingSpatialFocusKeys()", clear, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusQueueEpoch", clearSpatial, StringComparison.Ordinal);
        Assert.Contains("_pendingSpatialFocusKeys.Clear()", clearSpatial, StringComparison.Ordinal);
        Assert.Contains("_hasPendingFocusResumePoint = false", clear, StringComparison.Ordinal);
        Assert.Contains("_hasPendingHorizontalOverflowRefresh = false", clear, StringComparison.Ordinal);
        Assert.Contains("ClearPendingFocusGeometryWork()", release, StringComparison.Ordinal);
        int generationPublish = source.IndexOf("_snapshotGeneration = generation;", StringComparison.Ordinal);
        Assert.True(generationPublish >= 0);
        int commitReset = source.LastIndexOf("ClearPendingFocusGeometryWork();", generationPublish, StringComparison.Ordinal);
        Assert.True(commitReset >= 0 && commitReset < generationPublish);
    }

    private static void AssertLeaseFences(string method, string geometryRead)
    {
        int before = method.IndexOf("IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision)", StringComparison.Ordinal);
        int begin = method.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int read = method.IndexOf(geometryRead, StringComparison.Ordinal);
        int end = method.IndexOf("snapshot.EndInteraction()", read, StringComparison.Ordinal);
        int after = method.LastIndexOf("IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision)", StringComparison.Ordinal);
        Assert.True(before >= 0 && begin > before && read > begin && end > read && after > end);
        Assert.Contains("finally", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor.Enter", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherQueue", method, StringComparison.Ordinal);
        Assert.DoesNotContain("await ", method, StringComparison.Ordinal);
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
}
