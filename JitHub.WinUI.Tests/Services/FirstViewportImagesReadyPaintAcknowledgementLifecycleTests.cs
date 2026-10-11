using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class FirstViewportImagesReadyPaintAcknowledgementLifecycleTests
{
    [Fact]
    public void AcceptedPaintThatBecomesStaleMustBeRearmedBeforeReadyCanBePublished()
    {
        const long generation = 12;
        var lifecycle = new FirstViewportImagesReadyPaintAcknowledgementLifecycle();
        lifecycle.Begin(generation);

        Assert.Equal(generation, lifecycle.RequestedGeneration);
        Assert.Equal(0, lifecycle.PaintedGeneration);
        Assert.Equal(0, lifecycle.PaintedAtTicks);
        Assert.Null(lifecycle.LastPaintProbeAccepted);
        Assert.Null(lifecycle.LastPaintAcknowledgementRecorded);
        Assert.False(lifecycle.RecordAcceptedPaint(generation - 1, paintedAtTicks: 50));
        Assert.Equal(generation, lifecycle.RequestedGeneration);
        Assert.Equal(0, lifecycle.PaintedGeneration);
        Assert.Equal(0, lifecycle.PaintedAtTicks);

        Assert.True(lifecycle.RecordAcceptedPaint(generation, paintedAtTicks: 100));
        lifecycle.RecordPaintCallbackOutcome(probeAccepted: true, acknowledgementRecorded: true);
        Assert.Equal(generation, lifecycle.RequestedGeneration);
        Assert.Equal(generation, lifecycle.PaintedGeneration);
        Assert.Equal(100, lifecycle.PaintedAtTicks);
        Assert.True(lifecycle.LastPaintProbeAccepted == true);
        Assert.True(lifecycle.LastPaintAcknowledgementRecorded == true);

        // The viewport/layout identity changed after the accepted paint but before polling.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));
        Assert.Equal(generation, lifecycle.RequestedGeneration);
        // Keep the accepted paint's identity for the diagnostic rearm row;
        // readiness uses a separate current-ack latch that was cleared above.
        Assert.Equal(generation, lifecycle.PaintedGeneration);
        Assert.Equal(100, lifecycle.PaintedAtTicks);
        Assert.True(lifecycle.LastPaintProbeAccepted == true);
        Assert.True(lifecycle.LastPaintAcknowledgementRecorded == true);

        // Until the re-armed callback paints the new viewport, readiness remains unavailable.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));
        Assert.Equal(generation, lifecycle.PaintedGeneration);
        Assert.Equal(100, lifecycle.PaintedAtTicks);

        Assert.True(lifecycle.RecordAcceptedPaint(generation, paintedAtTicks: 250));
        Assert.Equal(generation, lifecycle.PaintedGeneration);
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.Ready,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: true, hasVisibleLoadingImages: false));
        Assert.Equal(250, lifecycle.PaintedAtTicks);
    }

    [Fact]
    public void AcceptedProbeWithoutRecordedAcknowledgementGetsOneRearmAfterImagesSettle()
    {
        const long generation = 22;
        var lifecycle = new FirstViewportImagesReadyPaintAcknowledgementLifecycle();
        lifecycle.Begin(generation);
        lifecycle.RecordPaintCallbackOutcome(probeAccepted: true, acknowledgementRecorded: false);

        // Do not invalidate while visible images are still allowed to paint naturally.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: true));
        Assert.True(lifecycle.LastPaintProbeAccepted == true);
        Assert.True(lifecycle.LastPaintAcknowledgementRecorded == false);
        Assert.Equal(generation, lifecycle.RequestedGeneration);
        Assert.Equal(0, lifecycle.PaintedGeneration);
        Assert.Equal(0, lifecycle.PaintedAtTicks);

        // An accepted coverage probe whose lifecycle handoff failed is not a paint
        // acknowledgement. Once images settle, request one fresh current-viewport paint.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));
        Assert.True(lifecycle.LastPaintProbeAccepted == true);
        Assert.True(lifecycle.LastPaintAcknowledgementRecorded == false);

        // A missing callback after that retry remains unready without repeatedly
        // invalidating the renderer.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));

        Assert.True(lifecycle.RecordAcceptedPaint(generation, paintedAtTicks: 300));
        lifecycle.RecordPaintCallbackOutcome(probeAccepted: true, acknowledgementRecorded: true);
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.Ready,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: true, hasVisibleLoadingImages: false));

        lifecycle.Begin(0);
        Assert.Null(lifecycle.LastPaintProbeAccepted);
        Assert.Null(lifecycle.LastPaintAcknowledgementRecorded);
        Assert.Equal(0, lifecycle.PaintedGeneration);
        Assert.Equal(0, lifecycle.PaintedAtTicks);
    }

    [Fact]
    public void MissingCallbackGetsOneRetryOnlyAfterVisibleImagesSettleAndStillNeedsPaintProof()
    {
        const long generation = 32;
        var lifecycle = new FirstViewportImagesReadyPaintAcknowledgementLifecycle();
        lifecycle.Begin(generation);

        // A stale generation cannot consume the retry for this request.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation + 1, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));

        // Do not spend the one-shot retry while visible images can still paint naturally.
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: true));

        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));

        // The retry only requests another paint. It does not synthesize readiness,
        // and repeated polls cannot keep invalidating the Canvas.
        Assert.Equal(0, lifecycle.PaintedGeneration);
        Assert.Equal(0, lifecycle.PaintedAtTicks);
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));

        Assert.True(lifecycle.RecordAcceptedPaint(generation, paintedAtTicks: 400));
        lifecycle.RecordPaintCallbackOutcome(probeAccepted: true, acknowledgementRecorded: true);
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.Ready,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: true, hasVisibleLoadingImages: false));
    }

    [Fact]
    public void RejectedPartialCallbackGetsOneRetryAfterImagesSettle()
    {
        const long generation = 42;
        var lifecycle = new FirstViewportImagesReadyPaintAcknowledgementLifecycle();
        lifecycle.Begin(generation);
        lifecycle.RecordPaintCallbackOutcome(probeAccepted: false, acknowledgementRecorded: false);

        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: true));
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));
        Assert.Equal(
            FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            lifecycle.ObservePoll(generation, acknowledgementIsCurrent: false, hasVisibleLoadingImages: false));
        Assert.Equal(0, lifecycle.PaintedGeneration);
        Assert.Equal(0, lifecycle.PaintedAtTicks);
        Assert.False(lifecycle.LastPaintProbeAccepted);
        Assert.False(lifecycle.LastPaintAcknowledgementRecorded);
    }

    [Fact]
    public void PaintAcknowledgementRequiresTheExactSnapshotAndLayoutRevisionThatWasPainted()
    {
        var paintedSnapshot = new object();
        var replacementSnapshot = new object();

        Assert.True(FirstViewportImagesReadyProbeContract.IsPaintedSnapshotIdentityCurrent(
            paintedSnapshot,
            paintedSnapshot,
            currentLayoutRevision: 8,
            paintedLayoutRevision: 8));
        Assert.False(FirstViewportImagesReadyProbeContract.IsPaintedSnapshotIdentityCurrent(
            replacementSnapshot,
            paintedSnapshot,
            currentLayoutRevision: 8,
            paintedLayoutRevision: 8));
        Assert.False(FirstViewportImagesReadyProbeContract.IsPaintedSnapshotIdentityCurrent(
            paintedSnapshot,
            paintedSnapshot,
            currentLayoutRevision: 9,
            paintedLayoutRevision: 8));
    }

    [Fact]
    public void CanvasPaintCallbackCarriesThePaintedSnapshotIdentityIntoTheProbe()
    {
        string repositoryRoot = FindRepositoryRoot();
        string rendererControl = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));
        string rendererProbe = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "FirstViewportImagesReadyRendererProbe.cs"));
        string evidenceWriter = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "FirstViewportImagesReadyEvidenceWriter.cs"));
        string viewer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));

        Assert.Contains("long paintedLayoutRevision = snapshot.LayoutRevision;", rendererControl, StringComparison.Ordinal);
        Assert.Contains("automationPaintCallback(snapshot, paintedLayoutRevision, region)", rendererControl, StringComparison.Ordinal);
        Assert.Contains("paintedSnapshot.TryBeginPaint()", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("paintedSnapshot.EndPaint()", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("IsPaintedSnapshotIdentityCurrent(", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("paintedSnapshot", viewer, StringComparison.Ordinal);
        Assert.Contains("paintedLayoutRevision", viewer, StringComparison.Ordinal);
        Assert.Contains(
            "generationCancellationToken: renderer.GetAutomationPipelineCancellationToken(generation)",
            viewer,
            StringComparison.Ordinal);
        Assert.Contains("generationCancellationToken.IsCancellationRequested", evidenceWriter, StringComparison.Ordinal);
        Assert.Contains("generationCancellationToken.IsCancellationRequested && writeSignal()", evidenceWriter, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredSceneReadinessGatesPaintPollingAndRootUiaStatusWithoutRealizingTheDocument()
    {
        string repositoryRoot = FindRepositoryRoot();
        string rendererControl = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));
        string rendererProbe = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "FirstViewportImagesReadyRendererProbe.cs"));
        string automationPeer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Accessibility",
            "MarkdownAutomationPeer.cs"));
        string scenePlan = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Performance",
            "MarkdownProgressiveScenePlan.cs"));
        string performanceSession = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Performance",
            "MarkdownPerformanceSession.cs"));
        string sceneResidency = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Performance",
            "MarkdownVectorSceneResidencyPolicy.cs"));
        string inlineContainer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "Boxes",
            "InlineContainerBox.cs"));
        int sceneGuardStart = rendererControl.IndexOf(
            "internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneStatusForAutomation()",
            StringComparison.Ordinal);
        int sceneGuardEnd = rendererControl.IndexOf(
            "internal List<Layout.Boxes.ImageBox> AutomationImagePlans",
            sceneGuardStart,
            StringComparison.Ordinal);
        Assert.True(sceneGuardStart >= 0 && sceneGuardEnd > sceneGuardStart);
        string sceneGuard = rendererControl.Substring(sceneGuardStart, sceneGuardEnd - sceneGuardStart);

        int paintGuard = rendererProbe.IndexOf(
            "renderer.GetVisibleDeferredSceneReadinessForAutomation(",
            StringComparison.Ordinal);
        int paintCoverage = rendererProbe.IndexOf(
            "_coverage.AddPaintedRegionExcluding(",
            StringComparison.Ordinal);
        int currentAck = rendererProbe.IndexOf(
            "internal bool IsCurrentAcknowledgement(",
            StringComparison.Ordinal);
        int currentAckSceneGuard = rendererProbe.IndexOf(
            "renderer.GetVisibleDeferredSceneReadinessForAutomation(",
            currentAck,
            StringComparison.Ordinal);

        Assert.True(paintGuard >= 0 && paintCoverage > paintGuard);
        Assert.True(currentAck >= 0 && currentAckSceneGuard > currentAck);
        Assert.Contains("session.GetVisibleDeferredSceneReadiness(", sceneGuard, StringComparison.Ordinal);
        Assert.Contains("MarkdownVisibleSceneReadiness.Busy", sceneGuard, StringComparison.Ordinal);
        Assert.Contains("TryBeginPaint()", sceneGuard, StringComparison.Ordinal);
        Assert.Contains("EndPaint()", sceneGuard, StringComparison.Ordinal);
        int readinessStart = performanceSession.IndexOf(
            "MarkdownVisibleSceneReadiness IMarkdownPerformanceSessionInternal.GetVisibleDeferredSceneReadiness(",
            StringComparison.Ordinal);
        int readinessEnd = performanceSession.IndexOf(
            "private bool IsDeferredSceneReadinessCurrent(",
            readinessStart,
            StringComparison.Ordinal);
        Assert.True(readinessStart >= 0 && readinessEnd > readinessStart);
        string readiness = performanceSession.Substring(readinessStart, readinessEnd - readinessStart);
        Assert.Contains("IsDeferredSceneReadinessCurrent(", readiness, StringComparison.Ordinal);
        int freshnessEnd = performanceSession.IndexOf(
            "#pragma warning restore MR1001", readinessEnd, StringComparison.Ordinal);
        Assert.True(freshnessEnd > readinessEnd);
        string freshness = performanceSession.Substring(readinessEnd, freshnessEnd - readinessEnd);
        Assert.Contains("owner.IsDeferredSceneSnapshotCurrent(", freshness, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(owner.PerformanceSession, this)", freshness, StringComparison.Ordinal);
        Assert.Contains("TryBeginPaint()", readiness, StringComparison.Ordinal);
        Assert.Contains("finally", readiness, StringComparison.Ordinal);
        Assert.Contains("expectedSnapshot.EndPaint()", readiness, StringComparison.Ordinal);
        Assert.Contains("IsBandMeasured(visibleBand)", readiness, StringComparison.Ordinal);
        Assert.Contains("TryGetMeasuredTopLevelBlocksInBandForSceneProbe(", readiness, StringComparison.Ordinal);
        Assert.Contains("expectedSnapshot.VectorSceneResidencyPolicy", readiness, StringComparison.Ordinal);
        Assert.Contains("TryCollectVisibleDeferredSceneMarkers(", readiness, StringComparison.Ordinal);
        Assert.Contains("IsRegisteredDeferredSceneMarker(owner, marker)", readiness, StringComparison.Ordinal);
        Assert.Contains("residencyPolicy.TryAppendVisibleInlineSceneMarkers(", scenePlan, StringComparison.Ordinal);
        int indexedProbeStart = sceneResidency.IndexOf(
            "public bool TryAppendVisibleInlineSceneMarkers(", StringComparison.Ordinal);
        int indexedProbeEnd = sceneResidency.IndexOf(
            "private static int FindFirstStackChildIntersecting(", indexedProbeStart, StringComparison.Ordinal);
        Assert.True(indexedProbeStart >= 0 && indexedProbeEnd > indexedProbeStart);
        string indexedProbe = sceneResidency.Substring(indexedProbeStart, indexedProbeEnd - indexedProbeStart);
        Assert.Contains("_activeInlineSceneIndexes.TryGetValue(", indexedProbe, StringComparison.Ordinal);
        Assert.Contains("index.Generation != generation", indexedProbe, StringComparison.Ordinal);
        Assert.Contains("sceneIndex.Covers(", indexedProbe, StringComparison.Ordinal);
        Assert.Contains("FindLastStartingAtOrBefore(", indexedProbe, StringComparison.Ordinal);
        Assert.Contains("FindFirstWithBottomAtOrAfter(", indexedProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCaptureCurrentSceneGeometry(", indexedProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetRunCharacterRegionsForSceneReadiness(", indexedProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetInlineSceneGeometryIndex(", indexedProbe, StringComparison.Ordinal);
        Assert.Contains("TryGetRunCharacterRegionsForSceneReadiness(", inlineContainer, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectVisibleDeferredSceneMarkers(", inlineContainer, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureBuffer()", inlineContainer.Substring(
            inlineContainer.IndexOf("internal bool TryGetRunCharacterRegionsForSceneReadiness(", StringComparison.Ordinal),
            inlineContainer.IndexOf("internal bool IsPointInsideRunBounds(", StringComparison.Ordinal) -
                inlineContainer.IndexOf("internal bool TryGetRunCharacterRegionsForSceneReadiness(", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        Assert.Contains("FindFirstStackChildIntersecting(", scenePlan, StringComparison.Ordinal);
        Assert.Contains("IsFiniteNonNegativeRect(bounds)", scenePlan, StringComparison.Ordinal);
        Assert.Contains("_owner.GetVisibleDeferredSceneStatusForAutomation()", automationPeer, StringComparison.Ordinal);
        Assert.Contains("MarkdownVisibleSceneReadiness.Failed", automationPeer, StringComparison.Ordinal);
        Assert.Contains("MarkdownStringKeys.EmbeddedContentUnavailable", automationPeer, StringComparison.Ordinal);
        Assert.Contains("!= MarkdownVisibleSceneReadiness.Ready", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("== MarkdownVisibleSceneReadiness.Ready", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("MarkdownStringKeys.EmbeddedContentName", automationPeer, StringComparison.Ordinal);
        Assert.Contains("MarkdownStringKeys.ImageLoading", automationPeer, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMeasuredTopLevelBlocks()", sceneGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredSceneAndImageStatusTransitionsInvalidateOnlyTheExistingRootPeer()
    {
        string repositoryRoot = FindRepositoryRoot();
        string rendererControl = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));
        string automationPeer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Accessibility",
            "MarkdownAutomationPeer.cs"));
        string scenePlan = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Performance",
            "MarkdownProgressiveScenePlan.cs"));

        int commitStart = rendererControl.IndexOf("if (semanticInputChanged)", StringComparison.Ordinal);
        int commitEnd = rendererControl.IndexOf("long publicationEnded", commitStart, StringComparison.Ordinal);
        Assert.True(commitStart >= 0 && commitEnd > commitStart);
        string commitNotification = rendererControl.Substring(commitStart, commitEnd - commitStart);
        Assert.Contains("NotifyDocumentChanged()", commitNotification, StringComparison.Ordinal);
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged()", commitNotification, StringComparison.Ordinal);

        int statusNotifierStart = rendererControl.IndexOf(
            "internal void NotifyVisibleDeferredSceneItemStatusMayHaveChanged()",
            StringComparison.Ordinal);
        int statusNotifierEnd = rendererControl.IndexOf(
            "internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneReadinessForAutomation(",
            statusNotifierStart,
            StringComparison.Ordinal);
        Assert.True(statusNotifierStart >= 0 && statusNotifierEnd > statusNotifierStart);
        string statusNotifier = rendererControl.Substring(statusNotifierStart, statusNotifierEnd - statusNotifierStart);
        Assert.Contains("FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer", statusNotifier, StringComparison.Ordinal);
        Assert.Contains("NotifyLoadingStatusMayHaveChanged()", statusNotifier, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatePeerForElement", statusNotifier, StringComparison.Ordinal);

        int peerStatusNotifierStart = automationPeer.IndexOf(
            "internal void NotifyLoadingStatusMayHaveChanged()",
            StringComparison.Ordinal);
        int peerStatusNotifierEnd = automationPeer.IndexOf(
            "internal void NotifyDocumentChanged()",
            peerStatusNotifierStart,
            StringComparison.Ordinal);
        Assert.True(peerStatusNotifierStart >= 0 && peerStatusNotifierEnd > peerStatusNotifierStart);
        string peerStatusNotifier = automationPeer.Substring(
            peerStatusNotifierStart,
            peerStatusNotifierEnd - peerStatusNotifierStart);
        Assert.Contains("AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)", peerStatusNotifier, StringComparison.Ordinal);
        Assert.Contains("InvalidatePeer()", peerStatusNotifier, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseAutomationEvent(", peerStatusNotifier, StringComparison.Ordinal);
        Assert.Contains(
            "NotifyLoadingStatusMayHaveChanged();",
            automationPeer.Substring(
                automationPeer.IndexOf("internal void NotifyImageStatusChanged(ImageBox image)", StringComparison.Ordinal),
                peerStatusNotifierStart - automationPeer.IndexOf("internal void NotifyImageStatusChanged(ImageBox image)", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        Assert.Contains("policy.ClearGraphStatusChanges()", statusNotifier, StringComparison.Ordinal);
        Assert.Contains("finally", statusNotifier, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _deferredSceneStatusRefreshAfterPaint)", rendererControl, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 1)", rendererControl, StringComparison.Ordinal);
        Assert.Contains("RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded()", rendererControl, StringComparison.Ordinal);
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged()", rendererControl, StringComparison.Ordinal);
        int paintMethodStart = rendererControl.IndexOf("private void PaintInvalidatedRegions(", StringComparison.Ordinal);
        int paintLockRelease = rendererControl.IndexOf("snapshot.EndPaint();", paintMethodStart, StringComparison.Ordinal);
        int deferredStatusRefresh = rendererControl.IndexOf(
            "RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded();",
            paintLockRelease,
            StringComparison.Ordinal);
        Assert.True(paintMethodStart >= 0 && paintLockRelease > paintMethodStart && deferredStatusRefresh > paintLockRelease);
        int releaseResources = rendererControl.IndexOf("private void ReleaseLoadedResources()", StringComparison.Ordinal);
        int nextThemeCleanup = rendererControl.IndexOf("if (Theme is { } t)", releaseResources, StringComparison.Ordinal);
        Assert.True(releaseResources >= 0 && nextThemeCleanup > releaseResources);
        Assert.Contains(
            "Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 0)",
            rendererControl.Substring(releaseResources, nextThemeCleanup - releaseResources),
            StringComparison.Ordinal);
        Assert.DoesNotContain("CreatePeerForElement", rendererControl.Substring(
            rendererControl.IndexOf("private void RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded()", StringComparison.Ordinal),
            rendererControl.IndexOf("internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneReadinessForAutomation(", StringComparison.Ordinal) -
                rendererControl.IndexOf("private void RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded()", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        int visibleStatusStart = scenePlan.IndexOf(
            "private void NotifyVisiblePlaceholderStatusIfChanged(",
            StringComparison.Ordinal);
        int visibleStatusEnd = scenePlan.IndexOf(
            "private void EnsureControlCallbacks(",
            visibleStatusStart,
            StringComparison.Ordinal);
        Assert.True(visibleStatusStart >= 0 && visibleStatusEnd > visibleStatusStart);
        string visibleStatus = scenePlan.Substring(visibleStatusStart, visibleStatusEnd - visibleStatusStart);
        Assert.Contains("_visiblePlaceholderStatusPending != pending", visibleStatus, StringComparison.Ordinal);
        Assert.Contains("if (changed)", visibleStatus, StringComparison.Ordinal);
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged()", visibleStatus, StringComparison.Ordinal);

        int visibleGeometryStart = scenePlan.IndexOf(
            "private static bool TryCollectVisibleDeferredSceneMarkers(",
            StringComparison.Ordinal);
        int visibleGeometryEnd = scenePlan.IndexOf(
            "private static int FindFirstStackChildIntersecting(",
            visibleGeometryStart,
            StringComparison.Ordinal);
        Assert.True(visibleGeometryStart >= 0 && visibleGeometryEnd > visibleGeometryStart);
        string visibleGeometry = scenePlan.Substring(
            visibleGeometryStart,
            visibleGeometryEnd - visibleGeometryStart);
        Assert.Contains("if (!IsFiniteNonNegativeRect(bounds))", visibleGeometry, StringComparison.Ordinal);
        Assert.Contains("bounds.Width == 0 || bounds.Height == 0", visibleGeometry, StringComparison.Ordinal);
        Assert.Contains("bounds.Bottom < viewportTop || bounds.Top > viewportBottom", visibleGeometry, StringComparison.Ordinal);
        Assert.Contains("viewport is { } viewportBounds && !Intersects(bounds, viewportBounds)", visibleGeometry, StringComparison.Ordinal);
        Assert.Contains("return false;", visibleGeometry, StringComparison.Ordinal);
        Assert.Contains("return true;", visibleGeometry, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerRearmsOnlyForCurrentMeasuredRequestAndKeepsPaintAcknowledgementGate()
    {
        string viewerPath = Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs");
        string viewer = File.ReadAllText(viewerPath);
        int observe = viewer.IndexOf("_firstViewportImagesReadyPaintLifecycle.ObservePoll(", StringComparison.Ordinal);
        int rearm = viewer.IndexOf(
            "FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport",
            observe,
            StringComparison.Ordinal);
        int generationGuard = viewer.IndexOf("renderer.AutomationPipelineGeneration != generation", StringComparison.Ordinal);
        int measuredViewport = viewer.IndexOf(
            "FirstViewportImagesReadyProbeContract.IsMeasuredViewport(",
            StringComparison.Ordinal);
        int callbackRequestGuard = viewer.IndexOf("ticket != _firstViewportImagesReadyProbeTicket", StringComparison.Ordinal);
        int currentViewportAcknowledgement = viewer.IndexOf(
            "probe.IsCurrentAcknowledgement(renderer, generation, viewport)",
            StringComparison.Ordinal);
        int beginPaintProbe = viewer.IndexOf(
            "renderer.BeginAutomationFirstViewportImagesReadyProbe(paintCallback)",
            rearm,
            StringComparison.Ordinal);
        int readyWrite = viewer.IndexOf("MarkdownLifecycleAutomationBridge.RecordFirstViewportImagesReady(", observe, StringComparison.Ordinal);

        Assert.True(generationGuard >= 0 && measuredViewport > 0 && measuredViewport < observe);
        Assert.True(callbackRequestGuard >= 0 && currentViewportAcknowledgement > measuredViewport &&
            currentViewportAcknowledgement < observe);
        Assert.True(observe >= 0 && rearm > observe && beginPaintProbe > rearm && readyWrite > beginPaintProbe);
        Assert.Contains("_firstViewportImagesReadyPaintGeneration = 0", viewer, StringComparison.Ordinal);
        Assert.Contains("_firstViewportImagesReadyPaintTimestamp = 0", viewer, StringComparison.Ordinal);
        Assert.Contains("paintProbeAccepted: probeAccepted", viewer, StringComparison.Ordinal);
        Assert.Contains("paintAcknowledgementRecorded: acknowledgementRecorded", viewer, StringComparison.Ordinal);
        Assert.Contains("paintAcknowledgementIsCurrent: acknowledgementIsCurrent", viewer, StringComparison.Ordinal);
        Assert.Contains("paintPollAction: paintPollAction", viewer, StringComparison.Ordinal);
        Assert.Contains("RecordPaintCallbackOutcome", viewer, StringComparison.Ordinal);
        Assert.Contains("_firstViewportImagesReadyPaintLifecycle.RequestedGeneration", viewer, StringComparison.Ordinal);
        Assert.Contains("_firstViewportImagesReadyPaintLifecycle.PaintedGeneration", viewer, StringComparison.Ordinal);
        Assert.Contains("_firstViewportImagesReadyPaintLifecycle.PaintedAtTicks", viewer, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the JitHub repository root.");
    }
}
