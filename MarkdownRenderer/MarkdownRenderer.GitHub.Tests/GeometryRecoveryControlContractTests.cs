using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The snapshot failure latch has behavioral coverage in
/// <see cref="LayoutGeometryRecoveryTests"/>. These checks protect the WinUI
/// control boundary that turns that latch into one replacement request while
/// retaining the last painted surface and committed logical selection.
/// </summary>
public sealed class GeometryRecoveryControlContractTests
{
    [Fact]
    public void RecoveryIsScopedToTheCurrentLoadedSnapshotAndClaimedOnce()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.GeometryRecovery.cs"));
        int helper = source.IndexOf("private bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)", StringComparison.Ordinal);
        Assert.True(helper >= 0);
        string body = MethodBody(source, helper);

        int firstIf = body.IndexOf("if (", StringComparison.Ordinal);
        int guard = body.IndexOf("if (_isDisposed || _isUnloaded || snapshot is null ||", StringComparison.Ordinal);
        int currentSnapshot = body.IndexOf("!ReferenceEquals(snapshot, _snapshot)", StringComparison.Ordinal);
        int recoveryLatch = body.IndexOf("!snapshot.GeometryRecoveryRequired", StringComparison.Ordinal);
        int claim = body.IndexOf("snapshot.TryClaimGeometryRecoveryRequest()", StringComparison.Ordinal);

        Assert.True(guard == firstIf);
        Assert.True(currentSnapshot > guard && recoveryLatch > currentSnapshot && claim > recoveryLatch);
        Assert.Contains("if (!snapshot.TryClaimGeometryRecoveryRequest())", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryDropsGeometryBoundInteractionButPreservesLogicalSelection()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.GeometryRecovery.cs"));
        int helper = source.IndexOf("private bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)", StringComparison.Ordinal);
        Assert.True(helper >= 0);
        string body = MethodBody(source, helper);

        int claim = body.IndexOf("snapshot.TryClaimGeometryRecoveryRequest()", StringComparison.Ordinal);
        int replacement = body.IndexOf("RequestRebuild();", claim, StringComparison.Ordinal);
        Assert.True(claim >= 0 && replacement > claim);

        // Every action below must remain between the once-only claim and the
        // replacement scheduling boundary, but unrelated XAML cleanup order
        // is intentionally not coupled to this source contract.
        foreach (string action in new[]
        {
            "TryGeometryRecoveryCleanup(ResetDeferredPointerInput);",
            "ClearPendingFocusGeometryWork();",
            "_selectionAdornerRects.Clear();",
            "TryGeometryRecoveryCleanup(CancelLazyLayoutRealization);",
            "_pendingImageRelayoutBlockIndices.Clear();",
            "_imageRelayoutQueued = false;",
            "++_lastImageRelayoutDispatchTicket;",
            "CancellationTokenSourceRetirement.RequestCancellation(imageCancellation)",
            "ResetTransientInteractionState(clearSelection: false)",
            "SetSelectionHandleVisibility(Visibility.Collapsed)",
            "ReleaseSelectionAdornerSurface",
        })
        {
            int actionIndex = body.IndexOf(action, StringComparison.Ordinal);
            Assert.True(actionIndex > claim && actionIndex < replacement, $"Missing or misplaced recovery action: {action}");
        }

        Assert.Contains("if (snapshot.GeometryRecoveryIsDeviceLost)", body, StringComparison.Ordinal);
        Assert.Contains("HandleCanvasDeviceLost(snapshot.GeometryRecoveryFailureHResult)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void LazyCompletionChecksTaintBeforeStaleOrCanceledWorkerEarlyReturns()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        int completion = source.IndexOf("private void CompleteLazyLayoutRealization(", StringComparison.Ordinal);
        Assert.True(completion >= 0);
        string body = MethodBody(source, completion);

        int recovery = body.IndexOf("RequestGeometryRecoveryIfRequired(snapshot)", StringComparison.Ordinal);
        int staleGeneration = body.IndexOf("if (realizationGeneration != _lazyLayoutGeneration)", StringComparison.Ordinal);
        int canceled = body.IndexOf("canceled ||", StringComparison.Ordinal);
        Assert.True(recovery >= 0 && staleGeneration > recovery && canceled > recovery);
    }

    [Fact]
    public void PaintRetainsTheExistingTileInsteadOfOpeningOrClearingADrawingSession()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        int paint = source.IndexOf("private void PaintInvalidatedRegions(", StringComparison.Ordinal);
        Assert.True(paint >= 0);
        string body = MethodBody(source, paint);

        int snapshot = body.IndexOf("if (_snapshot is not { } snapshot) return;", StringComparison.Ordinal);
        int recovery = body.IndexOf("RequestGeometryRecoveryIfRequired(snapshot)", StringComparison.Ordinal);
        int createSession = body.IndexOf("sender.CreateDrawingSession(region)", StringComparison.Ordinal);
        int clear = body.IndexOf("ds.Clear(_canvasBackground);", StringComparison.Ordinal);
        Assert.True(snapshot >= 0 && recovery > snapshot && createSession > recovery && clear > createSession);
        Assert.Contains("No drawing session/clear: retain the last good tiles.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerDispatchChecksRecoveryBeforeCapturingInputOrTakingAnInteractionLease()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.PointerInput.cs"));
        int dispatch = source.IndexOf("private void DispatchPointerInput(", StringComparison.Ordinal);
        Assert.True(dispatch >= 0);
        string body = MethodBody(source, dispatch);

        int recovery = body.IndexOf("RequestGeometryRecoveryIfRequired(snapshot)", StringComparison.Ordinal);
        int input = body.IndexOf("var input = new PointerInput", StringComparison.Ordinal);
        int interaction = body.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        Assert.True(recovery >= 0 && input > recovery && interaction > input);
    }

    [Fact]
    public void SelectionOverlayDoesNotReadFailedGeometryOrRecreateItsSurface()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        int update = source.IndexOf("private void UpdateSelectionOverlay()", StringComparison.Ordinal);
        Assert.True(update >= 0);
        string body = MethodBody(source, update);

        int recovery = body.IndexOf("RequestGeometryRecoveryIfRequired(_snapshot)", StringComparison.Ordinal);
        int request = body.IndexOf("InvalidateSelectionAdorner()", StringComparison.Ordinal);
        Assert.True(recovery >= 0 && request > recovery);
        Assert.DoesNotContain("GetHighlightRects", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSelectionAdorner", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionAdornerRects.Clear()", body, StringComparison.Ordinal);

        // Geometry/surface work now runs in the frame coordinator, not in the
        // selection notification. Preserve the recovery contract at both
        // entry points and require real paint admission before that operation.
        int attempt = source.IndexOf("private void TrySelectionAdornerRefreshAttempt()", StringComparison.Ordinal);
        Assert.True(attempt >= 0);
        string attemptBody = MethodBody(source, attempt);
        int requestRecovery = attemptBody.IndexOf("RequestGeometryRecoveryIfRequired(snapshot)", StringComparison.Ordinal);
        int admission = attemptBody.IndexOf("SelectionAdornerRefreshCoordinator.TryRefresh", StringComparison.Ordinal);
        Assert.True(requestRecovery >= 0 && admission > requestRecovery);

        string coordinator = File.ReadAllText(FindControlSource("SelectionAdornerRefreshCoordinator.cs"));
        int beginPaint = coordinator.IndexOf("snapshot.TryBeginPaint()", StringComparison.Ordinal);
        int operation = coordinator.IndexOf("operation.RefreshUnderPaintLease(snapshot)", StringComparison.Ordinal);
        int endPaint = coordinator.IndexOf("snapshot.EndPaint()", StringComparison.Ordinal);
        Assert.True(beginPaint >= 0 && operation > beginPaint && endPaint > operation);
        Assert.Contains("finally", coordinator[operation..endPaint], StringComparison.Ordinal);

        int refresh = source.IndexOf("RefreshSelectionAdornerUnderPaintLease(", StringComparison.Ordinal);
        Assert.True(refresh >= 0);
        string refreshBody = MethodBody(source, refresh);
        int taint = refreshBody.IndexOf("snapshot.GeometryRecoveryRequired", StringComparison.Ordinal);
        int selectionGeometry = refreshBody.IndexOf("_selection.AppendVisibleHighlightRects", StringComparison.Ordinal);
        int surface = refreshBody.IndexOf("EnsureSelectionAdorner()", StringComparison.Ordinal);
        Assert.True(taint >= 0 && selectionGeometry > taint && surface > selectionGeometry);
    }

    [Fact]
    public void ReadAdmissionRejectsTaintedAndRetiredSnapshots()
    {
        string snapshotSource = File.ReadAllText(FindControlSource("LayoutSnapshot.cs", "Layout"));
        int read = snapshotSource.IndexOf("private bool TryBeginRead()", StringComparison.Ordinal);
        Assert.True(read >= 0);
        string body = MethodBody(snapshotSource, read);
        int busy = body.IndexOf("if (!Monitor.TryEnter(_layoutLock))", StringComparison.Ordinal);
        int recovery = body.IndexOf("if (!GeometryRecoveryRequired &&", StringComparison.Ordinal);
        int retiring = body.IndexOf("Volatile.Read(ref _retirementRequested) == 0", StringComparison.Ordinal);
        int disposed = body.IndexOf("Volatile.Read(ref _disposed) == 0", StringComparison.Ordinal);

        // Lock contention exits as a retryable miss without mutating recovery
        // state. Once acquired, the same nonblocking read gate rejects a
        // geometry-tainted snapshot as well as one being retired or disposed.
        Assert.True(busy > 0 && recovery > busy && retiring > recovery && disposed > retiring);
    }

    [Fact]
    public void DeviceLossRetryUsesAnOwnedOneShotUiTimerAndRejectsStaleTicks()
    {
        string control = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        int handle = control.IndexOf("private void HandleCanvasDeviceLost(int failureHResult)", StringComparison.Ordinal);
        Assert.True(handle >= 0);
        string scheduling = MethodBody(control, handle);
        Assert.Contains("if (_canvasDeviceRecoveryQueued || _isDisposed || _isUnloaded)", scheduling, StringComparison.Ordinal);
        Assert.Contains("_canvasDeviceRecoveryAttempt = Math.Min(_canvasDeviceRecoveryAttempt + 1, 6);", scheduling, StringComparison.Ordinal);
        Assert.Contains("int delayMs = Math.Min(5000, 250 << (_canvasDeviceRecoveryAttempt - 1));", scheduling, StringComparison.Ordinal);
        Assert.Contains("var timer = DispatcherQueue.CreateTimer();", scheduling, StringComparison.Ordinal);
        Assert.Contains("timer.IsRepeating = false;", scheduling, StringComparison.Ordinal);
        Assert.Contains("timer.Tick += OnCanvasDeviceRecoveryTimer;", scheduling, StringComparison.Ordinal);
        Assert.Contains("_canvasDeviceRecoveryTimer = timer;", scheduling, StringComparison.Ordinal);
        Assert.Contains("_canvasDeviceRecoveryQueued = true;", scheduling, StringComparison.Ordinal);
        Assert.Contains("timer.Start();", scheduling, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", scheduling, StringComparison.Ordinal);

        string recovery = File.ReadAllText(FindControlSource("MarkdownRendererControl.GeometryRecovery.cs"));
        int tick = recovery.IndexOf("private void OnCanvasDeviceRecoveryTimer(", StringComparison.Ordinal);
        int stop = recovery.IndexOf("private void StopCanvasDeviceRecovery()", StringComparison.Ordinal);
        Assert.True(tick >= 0 && stop > tick);
        string tickBody = MethodBody(recovery, tick);
        int stale = tickBody.IndexOf("if (!ReferenceEquals(sender, _canvasDeviceRecoveryTimer))", StringComparison.Ordinal);
        int stopCurrent = tickBody.IndexOf("StopCanvasDeviceRecovery();", StringComparison.Ordinal);
        int rebuild = tickBody.IndexOf("RequestRebuild();", StringComparison.Ordinal);
        Assert.True(stale >= 0 && stopCurrent > stale && rebuild > stopCurrent);
        Assert.Contains("if (_isDisposed || _isUnloaded)", tickBody, StringComparison.Ordinal);

        string stopBody = MethodBody(recovery, stop);
        int clearTimer = stopBody.IndexOf("_canvasDeviceRecoveryTimer = null;", StringComparison.Ordinal);
        int clearQueued = stopBody.IndexOf("_canvasDeviceRecoveryQueued = false;", StringComparison.Ordinal);
        int timerStop = stopBody.IndexOf("timer.Stop();", StringComparison.Ordinal);
        int unsubscribe = stopBody.IndexOf("timer.Tick -= OnCanvasDeviceRecoveryTimer;", StringComparison.Ordinal);
        Assert.True(clearTimer >= 0 && clearQueued > clearTimer && timerStop > clearQueued && unsubscribe > timerStop);

        int unload = control.IndexOf("private void OnUnloaded()", StringComparison.Ordinal);
        int dispose = control.IndexOf("public void Dispose()", StringComparison.Ordinal);
        int paint = control.IndexOf("private void PaintInvalidatedRegions(", StringComparison.Ordinal);
        Assert.True(unload >= 0 && dispose > unload && paint > dispose);
        Assert.Contains("StopCanvasDeviceRecovery();", MethodBody(control, unload), StringComparison.Ordinal);
        Assert.Contains("StopCanvasDeviceRecovery();", MethodBody(control, dispose), StringComparison.Ordinal);
        string paintBody = MethodBody(control, paint);
        int successfulPaint = paintBody.IndexOf("if (paintedRegion)", StringComparison.Ordinal);
        int resetBackoff = paintBody.IndexOf("_canvasDeviceRecoveryAttempt = 0;", successfulPaint, StringComparison.Ordinal);
        int stopAfterSuccess = paintBody.IndexOf("StopCanvasDeviceRecovery();", resetBackoff, StringComparison.Ordinal);
        Assert.True(successfulPaint >= 0 && resetBackoff > successfulPaint && stopAfterSuccess > resetBackoff);
    }

    [Fact]
    public void RecoveryStopsImageRetryAndStalePointerTimerBeforeTouchingFailedGeometry()
    {
        string control = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        int recovery = control.IndexOf("private static void ProcessImageRelayoutRecovery(MarkdownRendererControl control)", StringComparison.Ordinal);
        Assert.True(recovery >= 0);
        string recoveryBody = MethodBody(control, recovery);
        int recoveryGate = recoveryBody.IndexOf("control.RequestGeometryRecoveryIfRequired(control._snapshot)", StringComparison.Ordinal);
        int publishDrain = recoveryBody.IndexOf("control._imageRelayoutPayload?.PublishAfterWorkDrains();", recoveryGate, StringComparison.Ordinal);
        int gateReturn = recoveryBody.IndexOf("return;", publishDrain, StringComparison.Ordinal);
        int bitmapProbe = recoveryBody.IndexOf("control.ProcessBitmapOutputRecovery();", recoveryGate, StringComparison.Ordinal);
        int relayoutRetry = recoveryBody.IndexOf("control.ProcessQueuedImageRelayout(", recoveryGate, StringComparison.Ordinal);
        Assert.True(recoveryGate >= 0 && publishDrain > recoveryGate && gateReturn > publishDrain);
        Assert.True(bitmapProbe > gateReturn && relayoutRetry > bitmapProbe);

        string pointer = File.ReadAllText(FindControlSource("MarkdownRendererControl.PointerInput.cs"));
        int tick = pointer.IndexOf("private void OnPointerRetryTimer(", StringComparison.Ordinal);
        Assert.True(tick >= 0);
        string tickBody = MethodBody(pointer, tick);
        int timerIdentity = tickBody.IndexOf("if (!ReferenceEquals(sender, _pointerRetryTimer))", StringComparison.Ordinal);
        int snapshotIdentity = tickBody.IndexOf("!ReferenceEquals(_snapshot, _deferredPointerSnapshot)", StringComparison.Ordinal);
        int failedGeometry = tickBody.IndexOf("RequestGeometryRecoveryIfRequired(snapshot)", StringComparison.Ordinal);
        int interaction = tickBody.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        Assert.True(timerIdentity >= 0 && snapshotIdentity > timerIdentity);
        Assert.True(failedGeometry > snapshotIdentity && interaction > failedGeometry);

        int reset = pointer.IndexOf("private void ResetDeferredPointerInput()", StringComparison.Ordinal);
        Assert.True(reset >= 0);
        string resetBody = MethodBody(pointer, reset);
        int detachTimer = resetBody.IndexOf("_pointerRetryTimer = null;", StringComparison.Ordinal);
        int stopTimer = resetBody.IndexOf("timer.Stop();", StringComparison.Ordinal);
        int detachHandler = resetBody.IndexOf("timer.Tick -= OnPointerRetryTimer;", StringComparison.Ordinal);
        Assert.True(detachTimer >= 0 && stopTimer > detachTimer && detachHandler > stopTimer);
    }

    private static string MethodBody(string source, int signatureStart)
    {
        int openingBrace = source.IndexOf('{', signatureStart);
        Assert.True(openingBrace >= 0);
        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[openingBrace..(index + 1)];
        }

        throw new InvalidOperationException("The source method body is not balanced.");
    }

    private static string FindControlSource(string fileName, string? relativeDirectory = null)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                relativeDirectory ?? "Controls",
                fileName);
            if (File.Exists(path))
                return path;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                relativeDirectory ?? "Controls",
                fileName);
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test base directory.");
    }
}
