using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AutomationHarnessSourceContractTests
{
    [Fact]
    public void AuthLifecycleProbeCoversRecoverableProductionStateTransitions()
    {
        string source = ReadAutomationSource("Probes", "ProbeCatalog.cs")
            + ReadAutomationSource("Probes", "Authentication", "AuthLifecycleProbe.cs");

        Assert.Contains("\"auth-lifecycle\"", source, StringComparison.Ordinal);
        Assert.Contains("RunAuthCancelScenario", source, StringComparison.Ordinal);
        Assert.Contains("RunAuthDeviceSuccessScenario", source, StringComparison.Ordinal);
        Assert.Contains("RunAuthExpiredTokenScenario", source, StringComparison.Ordinal);
        Assert.Contains("RunAuthNotificationReconnectScenario", source, StringComparison.Ordinal);
        Assert.Contains("RunAuthOfflineLaunchScenario", source, StringComparison.Ordinal);
        Assert.Contains("RunAuthMultiAccountCleanupScenario", source, StringComparison.Ordinal);
        Assert.Contains("SignOutRemoveAccountDataCheckBox", source, StringComparison.Ordinal);
        Assert.Contains("device.code.requested", source, StringComparison.Ordinal);
        Assert.Contains("GitHubDeviceAuthorizationDialog", source, StringComparison.Ordinal);
        Assert.Contains("automation-secondary-token", source, StringComparison.Ordinal);
        Assert.Contains("ReadAuthSetting(root, \"USER_ID\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeWidgetBoardProbeExercisesTheLiveModalDrawerKeyboardContract()
    {
        string source = ReadAutomationSource("Probes", "Home", "HomeWidgetBoardProbe.cs");
        int start = source.IndexOf("static void RunHomeWidgetBoardProbe", StringComparison.Ordinal);

        Assert.True(start >= 0);
        string probe = source[start..];
        Assert.Contains("VirtualKeyShort.SHIFT", probe, StringComparison.Ordinal);
        Assert.Contains("VirtualKeyShort.TAB", probe, StringComparison.Ordinal);
        Assert.Contains("VirtualKeyShort.ESCAPE", probe, StringComparison.Ordinal);
        Assert.Contains("Mouse.Click", probe, StringComparison.Ordinal);
        Assert.Contains("DashboardSideDrawerCloseButton", probe, StringComparison.Ordinal);
        Assert.Contains("DashboardOverviewDrawerButton", probe, StringComparison.Ordinal);
        Assert.Contains("IsFocusedElementWithin", probe, StringComparison.Ordinal);
        Assert.Contains("home-widget-board-drawer-close-focused.png", probe, StringComparison.Ordinal);
        Assert.Contains("home-widget-board-drawer-escape-focus-restored.png", probe, StringComparison.Ordinal);
        Assert.Contains("home-widget-board-drawer-light-dismiss-focus-restored.png", probe, StringComparison.Ordinal);
        Assert.Contains("DashboardOverviewMetricRepositories", probe, StringComparison.Ordinal);
        Assert.Contains("CreateProbeApplication(options)", probe, StringComparison.Ordinal);
        Assert.Contains("if (!isAttached)", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void StarsCategoryCleanupReacquiresVisibleStateAndUsesStableDialogIdentity()
    {
        string source = ReadAutomationSource("Probes", "Stars", "StarsCategories.cs");

        Assert.Contains("StarsDeleteCategoryDialog", source, StringComparison.Ordinal);
        Assert.Contains("FindVisibleStarsCategory(window, categoryName)", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(GetAutomationId(element), \"PrimaryButton\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplicitAppSelectionRejectsStaleBuildOutputs()
    {
        string source = ReadAutomationSource("Options", "CaptureOptions.cs");

        Assert.Contains("GetNewestSourceWriteTimeUtc", source, StringComparison.Ordinal);
        Assert.Contains("GetFreshnessArtifact(candidate)", source, StringComparison.Ordinal);
        Assert.Contains("File.GetLastWriteTimeUtc(freshnessArtifact) >= newestSourceWrite", source, StringComparison.Ordinal);
        Assert.Contains("firstSegment.StartsWith(\"obj\"", source, StringComparison.Ordinal);
        Assert.Contains("firstSegment.StartsWith(\"bin\"", source, StringComparison.Ordinal);
        Assert.Contains("No fresh JitHub executable was found for UI automation", source, StringComparison.Ordinal);
        Assert.Contains("\"publish\", \"JitHub.WinUI.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("EnsureAppBinaryIsFresh(appPath);", source, StringComparison.Ordinal);
        Assert.Contains("IsNativeAotExecutable(appPath)", source, StringComparison.Ordinal);
        Assert.Contains("using PEReader reader", source, StringComparison.Ordinal);
        Assert.Contains("reader.PEHeaders.CorHeader is null", source, StringComparison.Ordinal);
        Assert.Contains("Refusing stale JitHub automation binary", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopProbeOwnsOnlyItsExactLaunchProcesses()
    {
        string source = ReadAutomationSource("Applications", "ProbeApplicationLauncher.cs")
            + ReadAutomationSource("Applications", "ApplicationProcessCleanup.cs")
            + ReadAutomationSource("Native", "NativeMethods.Capture.cs")
            + ReadAutomationSource("Probes", "Theming", "ThemePaletteProbe.cs");

        Assert.Contains("AutomationApplicationRegistration", source, StringComparison.Ordinal);
        Assert.Contains("excludedProcessIds.Contains(process.Id)", source, StringComparison.Ordinal);
        Assert.Contains("process.StartTime.ToUniversalTime() < launchStartedUtc", source, StringComparison.Ordinal);
        Assert.Contains("ownedProcess.WaitForExit", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.OpenProcessExitHandle(processId)", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.GetProcessExitCode(processExitHandle)", source, StringComparison.Ordinal);
        Assert.Contains("TryTerminateOwnedProcess(registration)", source, StringComparison.Ordinal);
        Assert.Contains(
            "foregroundProcessId != (uint)expectedProcessId && IsWindowVisible(foreground)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("captureBounds.IntersectsWith(candidateBounds)", source, StringComparison.Ordinal);
        Assert.Contains("\"Shell_SystemDialogProxy\"", source, StringComparison.Ordinal);
        Assert.Contains("!bounds.IsEmpty && !captureBounds.IntersectsWith(bounds)", source, StringComparison.Ordinal);
        Assert.Contains("CountSignificantThemePaletteChromeChanges(initial, live)", source, StringComparison.Ordinal);

        int cleanupStart = source.IndexOf("static void KillExistingApplicationInstances", StringComparison.Ordinal);
        int cleanupEnd = source.IndexOf("static HashSet<int> GetApplicationProcessIds", cleanupStart, StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0 && cleanupEnd > cleanupStart);
        string cleanup = source[cleanupStart..cleanupEnd];
        Assert.Contains("GetOwnedRegistrations(appPath)", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcessesByName", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("process.Kill", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownInlineImagesSubscribeBeforeViewportGeometryIsAvailable()
    {
        // Planning inline images before reading their geometry, planning images with no
        // character region, and subscribing each plan once are covered by
        // InlineContainerPlansTests; ignoring completions of unsubscribed images by
        // ImageLoadCompletionTests. What stays here is the control's wiring of those
        // owners, which needs a live control.
        string embedSource = ReadRendererSource("Controls", "Embeds", "EmbedRealization.cs");
        Assert.Contains("InlineContainerPlans.Collect(this, snapshot, topLevelRoot, icb);", embedSource, StringComparison.Ordinal);
        Assert.Contains("void IInlinePlanCollector.AddImagePlan(ImageBox image) => _host.AddImagePlan(image);", embedSource, StringComparison.Ordinal);
        Assert.Contains(
            "void IEmbedRealizationHost.AddImagePlan(ImageBox image) => Owners.ImageSubscriptions.AddPlan(image);",
            ReadRendererSource("Controls", "Composition", "EmbedHosts.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "imageSubscriptions.LoadCompleted += imageLoadCompletion.OnLoadCompleted;",
            ReadRendererSource("Controls", "Composition", "MarkdownRendererComposition.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "_imageSubscriptions.UnsubscribeAll();",
            ReadRendererSource("Controls", "ViewAttachment.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownImageCompletionRelayoutsTheCommittedSnapshotWithoutResolverLoop()
    {
        // Queueing, coalescing, the Low dispatch, the missing-clock rebuild, rejected and
        // stale dispatches, rebuild and gesture deferral, generation and cancellation fences,
        // pointer-input retries and the scroll anchor are covered by ImageRelayoutTests;
        // completion membership and the queued root by ImageLoadCompletionTests; the
        // recovery clock's weak repeating timer and managed fallback by RecoveryClockTests.
        // What stays here needs a live control (host wiring), a performance session and
        // raster placement (private candidates), or a rebuild pipeline.
        string controlSource = ReadRendererSource("Controls", "MarkdownRendererControl.cs");
        string relayoutSource = ReadRendererSource("Controls", "Images", "ImageRelayout.cs");
        string workSource = ReadRendererSource("Controls", "Images", "ImageRelayoutWork.cs");
        string imageHostsSource = ReadRendererSource("Controls", "Composition", "ImageHosts.cs");
        Assert.Contains("void IImageRelayoutHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);", imageHostsSource, StringComparison.Ordinal);
        Assert.Contains("double? IImageRelayoutHost.VerticalScrollOffset => Owners.Surface.Scroll?.VerticalOffset;", imageHostsSource, StringComparison.Ordinal);

        // The tick's retry order, its geometry-recovery drain, failure fallback and idle stop
        // are covered by RecoveryClockWorkTests. What the host forwards each retry to needs a live control.
        string layoutHostsSource = ReadRendererSource("Controls", "Composition", "LayoutHosts.cs");
        string recoveryWorkSource = ReadRendererSource("Controls", "RecoveryClockWork.cs");
        Assert.Contains("_imageSubscriptions.ProcessBitmapOutputRecovery();", recoveryWorkSource, StringComparison.Ordinal);
        Assert.Contains("_imageRelayout.RecoverFaultedHandoff();", recoveryWorkSource, StringComparison.Ordinal);
        Assert.Contains("void IRecoveryClockHost.ProcessRecoveryTick() => Owners.RecoveryWork.ProcessTick();", layoutHostsSource, StringComparison.Ordinal);
        Assert.Contains("void IRecoveryClockWorkHost.PublishPendingLayout() => Owners.LayoutPublisher.PublishPending();", layoutHostsSource, StringComparison.Ordinal);
        string lazyLayoutSource = ReadRendererSource("Controls", "LazyLayout", "LazyLayoutRealization.cs");
        Assert.Contains("void IRecoveryClockWorkHost.ProcessQueuedLazyLayout() => Owners.LazyLayout.ProcessQueued();", layoutHostsSource, StringComparison.Ordinal);
        Assert.Contains("internal void ProcessQueued() => ProcessQueued(_dispatchTicket);", lazyLayoutSource, StringComparison.Ordinal);
        Assert.Contains("void IRecoveryClockWorkHost.TryPublishLazyLayoutCompletion() => Owners.LazyLayout.TryPublishCompletion();", layoutHostsSource, StringComparison.Ordinal);
        Assert.Contains("internal void TryPublishCompletion() => TryPublishCompletion(_generation);", lazyLayoutSource, StringComparison.Ordinal);
        Assert.Contains("void IRecoveryClockWorkHost.ProcessOverflowScrollRetry() => Owners.OverflowScroll.ProcessPendingRetry();", layoutHostsSource, StringComparison.Ordinal);
        Assert.Contains("void IRecoveryClockWorkHost.ProcessFocusGeometryWork() => Owners.FocusGeometryWork.Process();", layoutHostsSource, StringComparison.Ordinal);

        int processStart = relayoutSource.IndexOf("internal void ProcessQueued(long ticket)", StringComparison.Ordinal);
        int completionStart = relayoutSource.IndexOf("internal void Complete(", processStart, StringComparison.Ordinal);
        Assert.True(processStart >= 0 && completionStart > processStart);
        string process = relayoutSource[processStart..completionStart];
        Assert.Contains("bool IImageRelayoutHost.DefersToRebuild() => Owners.Rebuild.DefersToRebuild();", imageHostsSource, StringComparison.Ordinal);
        string schedulerSource = ReadRendererSource("Controls", "Pipeline", "RebuildScheduler.cs");
        int defersStart = schedulerSource.IndexOf("internal bool DefersToRebuild()", StringComparison.Ordinal);
        int defersEnd = schedulerSource.IndexOf("internal void QueuePendingIfIdle()", defersStart, StringComparison.Ordinal);
        Assert.True(defersStart >= 0 && defersEnd > defersStart);
        string defers = schedulerSource[defersStart..defersEnd];
        Assert.Contains("OnPipelineCompleted(_activeGeneration);", defers, StringComparison.Ordinal);
        Assert.Contains("return _coordinator.HasPendingOrBuilding;", defers, StringComparison.Ordinal);
        Assert.Contains("private readonly SortedSet<int> _pending = [];", relayoutSource, StringComparison.Ordinal);
        Assert.Contains("optedIn: session is not null", process, StringComparison.Ordinal);
        string rasterPublication = ReadRendererSource("Controls", "Images", "RasterPlacementPublisher.cs");
        Assert.Contains("TryPrepareCodeBlockActionPublication(plan", rasterPublication, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(newPlan.Box, identity.Placement)", rasterPublication, StringComparison.Ordinal);
        Assert.Contains("_host.TryCreateRasterPlacementInputs(snapshot, optedIn: session is not null);", process, StringComparison.Ordinal);
        Assert.Contains("Owners.RasterPlacement.TryCreateInputs(snapshot, optedIn)", imageHostsSource, StringComparison.Ordinal);
        int inputsStart = rasterPublication.IndexOf("internal RasterPlacementInputs? TryCreateInputs(", StringComparison.Ordinal);
        int inputsEnd = rasterPublication.IndexOf("internal void RaiseImageUnavailableIfCurrent(", inputsStart, StringComparison.Ordinal);
        Assert.True(inputsStart >= 0 && inputsEnd > inputsStart);
        string inputs = rasterPublication[inputsStart..inputsEnd];
        // The opt-in, root, hosted-element and image bounds are covered by RasterPlacementEligibilityTests.
        Assert.Contains("RasterPlacementEligibility.Allows(optedIn, snapshot.TopLevelBlockCount, _embeds.EmbedPlans.Count, _imagePlans.Count)", inputs, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeBlockActionPlans.Count", inputs, StringComparison.Ordinal);
        Assert.Contains("? new RasterPlacementInputs(new WeakReference<RasterPlacementPublisher>(this), _published.Generation,", inputs, StringComparison.Ordinal);
        Assert.Contains("_host.RequestFullRebuild();", process, StringComparison.Ordinal);

        int runStart = workSource.IndexOf("internal static async Task RunAsync(", StringComparison.Ordinal);
        int publicationStart = workSource.IndexOf("private static WeakSnapshotPublication<LayoutSnapshot> CreatePublication(", runStart, StringComparison.Ordinal);
        Assert.True(runStart >= 0 && publicationStart > runStart);
        string run = workSource[runStart..publicationStart];
        int privateCandidate = run.IndexOf("payload.Snapshot.CreateSingleRasterCandidate(", StringComparison.Ordinal);
        int fallbackRelayout = run.IndexOf("payload.Snapshot.TryRelayoutChangedBlocks(", StringComparison.Ordinal);
        int constructionDrain = run.IndexOf("candidateContext.DrainOwnedRetirementsAsync(failure)", StringComparison.Ordinal);
        int candidateBind = run.IndexOf("rasterPlacement!.Bind(candidate.Snapshot)", StringComparison.Ordinal);
        Assert.True(privateCandidate >= 0 && fallbackRelayout > privateCandidate);
        Assert.True(constructionDrain > fallbackRelayout && candidateBind > constructionDrain);
        int rootPreparation = run.IndexOf("payload.Snapshot.TryRelayoutCompletedImageRoots(", StringComparison.Ordinal);
        Assert.True(rootPreparation >= 0 && rootPreparation < privateCandidate && rootPreparation < fallbackRelayout);
        Assert.Contains("else if (result == PreparedImageRelayoutResult.Unavailable)", run, StringComparison.Ordinal);
        Assert.Contains("retryCurrentImages = true;", run, StringComparison.Ordinal);
        Assert.DoesNotContain("retryImageBlockIndices", run, StringComparison.Ordinal);
        Assert.Contains("rootMeasureContext.DrainOwnedRetirementsAsync(failure)", run, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(candidateDrain, rootMeasureDrain)", run, StringComparison.Ordinal);
        Assert.Contains("if (candidate is null)", run, StringComparison.Ordinal);
        Assert.Contains("!cancellation.IsCancellationRequested && session?.IsDisposed != true", run, StringComparison.Ordinal);
        Assert.Contains("if (!payload.TryOwnPublication(publication))", run, StringComparison.Ordinal);
        Assert.Contains("payload.ReleaseAfterWorkDrains();", run, StringComparison.Ordinal);
        Assert.Contains("candidate?.Dispose();", run, StringComparison.Ordinal);
        Assert.Contains("candidatePlan?.Dispose();", run, StringComparison.Ordinal);

        string publicationFactory = workSource[publicationStart..];
        Assert.Contains("int? retryImageBlockIndex", publicationFactory, StringComparison.Ordinal);
        Assert.Contains("bool retryCurrentImages", publicationFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("int[]", publicationFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("retryImageBlockIndices", publicationFactory, StringComparison.Ordinal);
        string completion = relayoutSource[completionStart..];
        Assert.Contains("current && snapshot is not null && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&\n                (!canceled || rootMeasureCommitted)", completion, StringComparison.Ordinal);
        Assert.Contains("// A private whole-tree candidate has not committed and is excluded.", completion, StringComparison.Ordinal);
        Assert.Contains("_host.TryPublishRasterPlacement(snapshot, candidate, generation)", completion, StringComparison.Ordinal);
        Assert.Contains("Owners.RasterPlacement.TryPublish(snapshot, candidate, generation)", imageHostsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("retryImageBlockIndices", completion, StringComparison.Ordinal);
        Assert.Contains("rootMeasureCommitted &&\n                        (canceled || capturedScrollOffset != _host.VerticalScrollOffset) ? null : anchor", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("Initial load / intrinsic-size change", controlSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Initial load / intrinsic-size change",
            ReadRendererSource("Controls", "Composition", "MarkdownRendererComposition.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownSelectionContextMenuOpensOnlyForTheCurrentSnapshot()
    {
        // The menu's snapshot, generation and layout revision fence, the keyboard capture of
        // the published tuple, and opening a gesture's menu only for the exact tuple it
        // hit-tested are covered by ContextMenuSnapshotFenceTests. What stays here needs
        // live XAML: right-tap recognition, the MenuFlyout and its target disposal, and
        // ending the snapshot lease before the menu opens.
        string pointerSource = ReadRendererSource("Controls", "Input", "PointerInputRouter.cs");
        string menuSource = ReadRendererSource("Controls", "Selection", "SelectionContextMenuPresenter.cs");
        string gestureSource = ReadRendererSource("Controls", "Input", "TouchGestureInteraction.cs");

        int rightTappedStart = gestureSource.IndexOf("internal void ProcessRightTapped(ref PointerInput e)", StringComparison.Ordinal);
        int rightTappedEnd = gestureSource.IndexOf("\n    }\n", rightTappedStart, StringComparison.Ordinal);
        Assert.True(rightTappedStart >= 0 && rightTappedEnd > rightTappedStart);
        string rightTapped = gestureSource[rightTappedStart..rightTappedEnd];
        Assert.Contains("long snapshotGeneration = _published.Generation;", rightTapped, StringComparison.Ordinal);
        Assert.Contains("if (touchOrPen && (!_selection.IsActive || !IsPointInsideSelection(snapshot, pt)))", rightTapped, StringComparison.Ordinal);
        Assert.Contains("TrySelectWordForTouchContext(\n                pt,\n                snapshot,\n                snapshotGeneration,\n                layoutRevision,", rightTapped, StringComparison.Ordinal);
        Assert.Contains("if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))", rightTapped, StringComparison.Ordinal);
        Assert.Contains("e.ShowContextMenuAfterInteraction = true;", rightTapped, StringComparison.Ordinal);
        Assert.Contains("e.TouchSelectionContextMenu = touchOrPen;", rightTapped, StringComparison.Ordinal);

        int menuStart = menuSource.IndexOf("internal bool Show(", StringComparison.Ordinal);
        int menuClosedStart = menuSource.IndexOf("private void OnClosed(", menuStart, StringComparison.Ordinal);
        Assert.True(menuStart >= 0 && menuClosedStart > menuStart);
        string menu = menuSource[menuStart..menuClosedStart];
        Assert.Contains("ContextMenuSnapshotFence.For(\n            _published, requiredSnapshot, requiredSnapshotGeneration, requiredLayoutRevision);", menu, StringComparison.Ordinal);
        Assert.Contains("\n        Close();\n", menu, StringComparison.Ordinal);
        Assert.Contains("if (!FindTarget(placementPoint, fence, out target))", menu, StringComparison.Ordinal);
        Assert.Contains("menu.ShowAt(canvas, placementPoint);", menu, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(menu, "DisposeContextMenuTarget\\(target\\);").Count);
        Assert.Contains("!ReferenceEquals(_menu, menu) ||\n                !fence.IsCurrent(_lifetime, _published) ||", menu, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)\n        {\n            ContextMenuTargets.DisposeContextMenuTarget(target);", menu, StringComparison.Ordinal);

        Assert.Contains("_contextMenuCompletion = new GestureContextMenuCompletion(lifetime, published, contextMenu);", pointerSource, StringComparison.Ordinal);
        string gestureRoutingSource = ReadRendererSource("Controls", "Input", "RecognizedGestureRouting.cs");
        int recognizedGestureStart = gestureRoutingSource.IndexOf("private bool DispatchUnderLease(", StringComparison.Ordinal);
        int geometryDeferralStart = gestureRoutingSource.IndexOf("private bool DeferForGeometry(", recognizedGestureStart, StringComparison.Ordinal);
        Assert.True(recognizedGestureStart >= 0 && geometryDeferralStart > recognizedGestureStart);
        string recognizedGesture = gestureRoutingSource[recognizedGestureStart..geometryDeferralStart];
        int endInteraction = recognizedGesture.IndexOf("snapshot.EndInteraction();", StringComparison.Ordinal);
        int postInteractionMenu = recognizedGesture.IndexOf("_contextMenuCompletion.Complete(", endInteraction, StringComparison.Ordinal);
        Assert.True(endInteraction >= 0 && postInteractionMenu > endInteraction);
        Assert.Contains("if (input.ShowContextMenuAfterInteraction)", recognizedGesture, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration,\n                layoutRevision);", recognizedGesture[postInteractionMenu..], StringComparison.Ordinal);
    }

    [Fact]
    public void RepoCodeAutomationRequiresDeterministicSourceAndLivePerformanceGates()
    {
        string source = ReadAutomationSource("Probes", "RepoCode", "RepoCodePerformanceProbe.cs")
            + ReadAutomationSource("Probes", "RepoCode", "RepoCodeNavigation.cs");

        Assert.Contains("--scenario=repo-code-performance", source, StringComparison.Ordinal);
        Assert.Contains("FindMandatoryRepoCodeSourceFile", source, StringComparison.Ordinal);
        Assert.Contains("deterministic App.cs source fixture", source, StringComparison.Ordinal);
        Assert.Contains("WaitForElementWithItemStatus", source, StringComparison.Ordinal);
        Assert.Contains("dispatcher heartbeat did not advance within 50 ms", source, StringComparison.Ordinal);
        Assert.Contains("first editor content took", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("firstContent.Elapsed <= TimeSpan.FromMilliseconds(150)", source, StringComparison.Ordinal);
        Assert.Contains("repo-code-performance-editor-timeout.png", source, StringComparison.Ordinal);
        Assert.Contains("observedStatuses", source, StringComparison.Ordinal);
        Assert.Contains("deterministic Repo Code outline item", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (sourceFile is not null)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTextBoxText(filter, \".dart\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoCodeResponsiveProbeExercisesCsvSemanticsAndSvgZoomExtremes()
    {
        string source = ReadAutomationSource("Probes", "RepoCode", "RepoCodeResponsiveWorkspaceProbe.cs")
            + ReadAutomationSource("Probes", "RepoCode", "RepoCodeContentSurfacesProbe.cs");

        Assert.Contains("ExerciseRepoCodeContentSurfaces(window, options)", source, StringComparison.Ordinal);
        Assert.Contains("candidate.Patterns.Grid.Pattern.RowCount.Value == 7", source, StringComparison.Ordinal);
        Assert.Contains("candidate.Patterns.Grid.Pattern.ColumnCount.Value == 5", source, StringComparison.Ordinal);
        Assert.Contains("firstCell.Patterns.GridItem.IsSupported", source, StringComparison.Ordinal);
        Assert.Contains("firstCell.Patterns.TableItem.IsSupported", source, StringComparison.Ordinal);
        Assert.Contains("plainEditor.Patterns.Value.Pattern.IsReadOnly.Value", source, StringComparison.Ordinal);
        Assert.Contains("CsvPreviewViewMode_Rich", source, StringComparison.Ordinal);
        Assert.Contains("CsvPreviewViewMode_Plain", source, StringComparison.Ordinal);
        Assert.Contains("svgViewport.Patterns.Transform2.IsSupported", source, StringComparison.Ordinal);
        Assert.Contains("SvgPreviewScrollViewer", source, StringComparison.Ordinal);
        Assert.Contains("VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 800", source, StringComparison.Ordinal);
        Assert.Contains("VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 10", source, StringComparison.Ordinal);
        Assert.Contains("AssertSvgViewportContainsRenderedColor", source, StringComparison.Ordinal);
        Assert.Contains("the {percent / 100:F1}x viewport was blank", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoCodeHighContrastProbeExercisesLiveEditorAtWideAndCompactWidths()
    {
        string source = ReadAutomationSource("Probes", "ProbeCatalog.cs")
            + ReadAutomationSource("Probes", "RepoCode", "RepoCodeHighContrastProbe.cs");

        Assert.Contains("repo-code-high-contrast", source, StringComparison.Ordinal);
        Assert.Contains("--high-contrast", source, StringComparison.Ordinal);
        Assert.Contains("High contrast editor colors active", source, StringComparison.Ordinal);
        Assert.Contains("repo-code-high-contrast-1366x900.png", source, StringComparison.Ordinal);
        Assert.Contains("repo-code-high-contrast-760x650.png", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoIssuesProbeAssertsCurrentDeterministicPreviewMarkdown()
    {
        string source = ReadAutomationSource("Probes", "Issues", "RepoIssuesPageProbe.cs");

        Assert.Contains(
            "This public preview issue demonstrates cached, responsive repository issue navigation.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "The cached issue detail stays visible while its discussion refreshes.",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Need something to control feature", source, StringComparison.Ordinal);
        Assert.Contains("bool openedInspectorDrawer", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesOpenInspectorPaneButton", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesCloseInspectorPaneButton", source, StringComparison.Ordinal);
        Assert.Contains("repository issue inspector drawer to close", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesCommentBox_Mode_Preview", source, StringComparison.Ordinal);
        Assert.Contains("if (!launcher.IsEnabled)", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesOpenCommentButton", source, StringComparison.Ordinal);
        Assert.Contains("Enabled issue comment launcher opened a disabled editor.", source, StringComparison.Ordinal);
        Assert.Contains("repo-issues-page-comment-read-only.png", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveWorkspaceDrawerProbeUsesRealPointerClicksForFocusTransitions()
    {
        string probe = ReadAutomationSource("Workspaces", "AdaptiveWorkspaceDrawers.cs");

        Assert.Contains("leadingButton.Click();", probe, StringComparison.Ordinal);
        Assert.Contains("leadingCloseButton.Click();", probe, StringComparison.Ordinal);
        Assert.Contains("trailingButton.Click();", probe, StringComparison.Ordinal);
        Assert.Contains("trailingCloseButton.Click();", probe, StringComparison.Ordinal);
        Assert.Contains("opener.Click();", probe, StringComparison.Ordinal);
        Assert.Contains("FocusForKeyboardActivation(window, leadingButton);", probe, StringComparison.Ordinal);
        Assert.Contains("FocusForKeyboardActivation(window, trailingButton);", probe, StringComparison.Ordinal);
        Assert.Contains("FocusForKeyboardActivation(window, opener);", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFlashListProbeReacquiresVirtualizedRowsByStableIdentity()
    {
        string probe = ReadAutomationSource("Workspaces", "WorkItemListSelection.cs");

        Assert.Contains("string listAutomationId = GetAutomationId(list);", probe, StringComparison.Ordinal);
        Assert.Contains("string targetAutomationId = GetAutomationId(target);", probe, StringComparison.Ordinal);
        Assert.Contains("FindCurrentVisibleByAutomationId(window, targetAutomationId)", probe, StringComparison.Ordinal);
        Assert.Contains("selected list row disappeared after click", probe, StringComparison.Ordinal);
        Assert.Contains("ClickListItemSurface(target);", probe, StringComparison.Ordinal);
        Assert.Contains("bounds.Left + 8, bounds.Top + 8", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationLifecycleRecordsTerminalProbeStatusAndRunnerExitCode()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("catch (Exception exception)", source, StringComparison.Ordinal);
        Assert.Contains("automationExitCode = 1;", source, StringComparison.Ordinal);
        Assert.Contains("Environment.ExitCode = automationExitCode;", source, StringComparison.Ordinal);
        Assert.Contains("\"probe-completed\"", source, StringComparison.Ordinal);
        Assert.Contains("automationExitCode={automationExitCode}", source, StringComparison.Ordinal);
        Assert.Contains("status={(automationExitCode == 0 ? \"passed\" : \"failed\")}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GracefulCloseRequiresExactOwnershipAndNormalExitWithoutForceFallback()
    {
        string source = ReadAutomationSource("Applications", "ApplicationProcessCleanup.cs")
            + ReadAutomationSource("Native", "NativeMethods.cs")
            + ReadAutomationSource("Native", "NativeMethods.Processes.cs");

        int closeStart = source.IndexOf("static void TryClose(Application app)", StringComparison.Ordinal);
        int cleanupStart = source.IndexOf("static void KillExistingApplicationInstances", closeStart, StringComparison.Ordinal);
        int processScanStart = source.IndexOf("static HashSet<int> GetApplicationProcessIds", cleanupStart, StringComparison.Ordinal);
        int waitStart = source.IndexOf("static bool WaitForProcessExit", processScanStart, StringComparison.Ordinal);
        int terminateStart = source.IndexOf("static bool TryTerminateOwnedProcess", waitStart, StringComparison.Ordinal);
        Assert.True(closeStart >= 0 && cleanupStart > closeStart && processScanStart > cleanupStart);
        Assert.True(waitStart > processScanStart && terminateStart > waitStart);

        string close = source[closeStart..cleanupStart];
        Assert.Contains("AutomationApplicationPathRegistry.TryGet(app", close, StringComparison.Ordinal);
        Assert.Contains("TryGetOwnedProcess(", close, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.TryRequestGracefulClose(processId, out IntPtr windowHandle)", close, StringComparison.Ordinal);
        Assert.Contains("transport=WM_CLOSE", close, StringComparison.Ordinal);
        Assert.Contains("ownedProcess.WaitForExit", close, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.OpenProcessExitHandle(processId)", close, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.GetProcessExitCode(processExitHandle)", close, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.CloseProcessExitHandle(processExitHandle);", close, StringComparison.Ordinal);
        Assert.Contains("if (exitCode != 0)", close, StringComparison.Ordinal);
        Assert.Contains("result=graceful; exitCode={exitCode}", close, StringComparison.Ordinal);
        Assert.DoesNotContain("TryTerminateOwnedProcess", close, StringComparison.Ordinal);
        Assert.DoesNotContain("app.Close(", close, StringComparison.Ordinal);
        Assert.DoesNotContain("app.Close();", close, StringComparison.Ordinal);
        Assert.DoesNotContain("result=owned-timeout-termination", close, StringComparison.Ordinal);

        Assert.Contains("private static extern bool EnumWindows", source, StringComparison.Ordinal);
        Assert.Contains("private static extern bool PostMessage", source, StringComparison.Ordinal);
        Assert.Contains("candidateProcessId != (uint)processId", source, StringComparison.Ordinal);
        Assert.Contains("GetWindow(candidate, GwOwner) != IntPtr.Zero", source, StringComparison.Ordinal);
        Assert.Contains("selectedProcessId != (uint)processId", source, StringComparison.Ordinal);
        Assert.Contains("return PostMessage(selectedWindow, WmClose", source, StringComparison.Ordinal);

        string cleanup = source[cleanupStart..processScanStart];
        Assert.Contains("owned-process-cleanup-failed", cleanup, StringComparison.Ordinal);
        Assert.Contains("result=forced-termination", cleanup, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", cleanup, StringComparison.Ordinal);

        string wait = source[waitStart..terminateStart];
        Assert.Contains("if (processId <= 0)", wait, StringComparison.Ordinal);
        Assert.Contains("catch (ArgumentException)\n        {", wait.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("Absence is the successful state this cleanup wait is proving.", wait, StringComparison.Ordinal);
        Assert.Contains("catch (InvalidOperationException)\n        {\n            return true;", wait.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsExportPickerUsesOwnedWindowAndLocalizationIndependentDialogContracts()
    {
        string source = ReadAutomationSource("Probes", "Settings", "SettingsExportPickerProbe.cs");

        int methodStart = source.IndexOf("static void AssertSettingsExportPicker(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);

        string method = source[methodStart..];
        Assert.Contains("NativeMethods.TryFindLargestOwnedTopLevelWindow(appWindowHandle, out IntPtr pickerHandle)", method, StringComparison.Ordinal);
        Assert.Contains("automation.FromHandle(pickerHandle)", method, StringComparison.Ordinal);
        Assert.Contains("ByAutomationId(\"FileNameControlHost\")", method, StringComparison.Ordinal);
        Assert.Contains("ByAutomationId(\"1\").And(cf.ByControlType(ControlType.Button))", method, StringComparison.Ordinal);
        Assert.Contains("ByAutomationId(\"2\").And(cf.ByControlType(ControlType.Button))", method, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(element.Name, \"Save\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(element.Name, \"Cancel\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("!= appProcessId", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellResponsiveProbeValidatesPersistentWideRailToggle()
    {
        string source = ReadAutomationSource("Probes", "Shell", "ShellResponsiveProbe.cs");

        int methodStart = source.IndexOf("static void RunShellResponsiveProbe", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);

        string method = source[methodStart..];
        Assert.Contains("user collapses wide shell rail", method, StringComparison.Ordinal);
        Assert.Contains("user-collapsed shell rail remains collapsed after resize", method, StringComparison.Ordinal);
        Assert.Contains("user expands wide shell rail", method, StringComparison.Ordinal);
        Assert.Contains("Wide shell did not expose the persistent navigation toggle", method, StringComparison.Ordinal);
        Assert.DoesNotContain("redundant navigation drawer button", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryActionsUseLiveSharedStarsStateAndGracefulRelaunchBoundaries()
    {
        string source = ReadAutomationSource("Probes", "Repositories", "RepositoryActionsProbe.cs");

        int probeStart = source.IndexOf("static void RunRepositoryActionsProbe", StringComparison.Ordinal);
        Assert.True(probeStart >= 0);
        string probe = source[probeStart..];

        Assert.DoesNotContain("RunStarsLibraryProbe", probe, StringComparison.Ordinal);
        Assert.Contains("ExerciseSharedStarsMutation(window, automation);", probe, StringComparison.Ordinal);
        Assert.Contains("ShellNav_stars", probe, StringComparison.Ordinal);
        Assert.Contains("StarsRepository_", probe, StringComparison.Ordinal);
        Assert.Contains("StarsHoverUnstar_", probe, StringComparison.Ordinal);
        Assert.Contains("StarsUndoUnstar", probe, StringComparison.Ordinal);
        Assert.Contains("disappears from shared Stars state", probe, StringComparison.Ordinal);
        Assert.Contains("returns to shared Stars state", probe, StringComparison.Ordinal);
        Assert.Contains("options.RepositoryFullName", probe, StringComparison.Ordinal);
        Assert.Contains("TryClose(firstApp);", probe, StringComparison.Ordinal);
        Assert.Contains("TryClose(relaunchedApp);", probe, StringComparison.Ordinal);

        foreach (string preservedCoverage in new[]
        {
            "RepoDetailWatchButton",
            "RepoDetailForkButton",
            "RepoDetailBranchSearchBox",
            "RepoDetailCompactCommandsButton",
            "VirtualKeyShort.ENTER",
            "VirtualKeyShort.SPACE",
            "repository-actions-route-overlap"
        })
        {
            Assert.Contains(preservedCoverage, probe, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThemePaletteProbeRequiresExactLiveAccentRepaintAndWindowOnlyEvidence()
    {
        string source = ReadAutomationSource("Probes", "Theming", "ThemeSwitchProbe.cs")
            + ReadAutomationSource("Probes", "Theming", "ThemePaletteProbe.cs")
            + ReadAutomationSource("Screenshots", "WindowCapture.cs")
            + ReadAutomationSource("Native", "NativeMethods.Capture.cs");

        int switchStart = source.IndexOf("static void RunThemeSwitchProbe", StringComparison.Ordinal);
        int paletteStart = source.IndexOf("static void RunThemePaletteProbe", switchStart, StringComparison.Ordinal);
        int paletteEnd = source.IndexOf("static void RunThemePaletteHomeMatrix", paletteStart, StringComparison.Ordinal);
        int captureStart = source.IndexOf("static void CaptureWindow(Window window, string path)", StringComparison.Ordinal);
        int captureEnd = source.IndexOf("static void CaptureElement", captureStart, StringComparison.Ordinal);
        Assert.True(switchStart >= 0 && paletteStart > switchStart && paletteEnd > paletteStart);
        Assert.True(captureStart >= 0 && captureEnd > captureStart);

        string themeProbe = source[switchStart..paletteEnd];
        string capture = source[captureStart..captureEnd];
        Assert.Contains("AssertLiveThemeAppearanceRepaint(beforePath, afterPath, restoredPath);", themeProbe, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(0x00, 0x5F, 0xB8)", themeProbe, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(0x00, 0x78, 0xD4)", themeProbe, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(0x77, 0xB5, 0x9A)", themeProbe, StringComparison.Ordinal);
        Assert.Contains("CountSignificantThemePaletteChromeChanges(initial, live)", themeProbe, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.CaptureWindowSurface(windowHandle)", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyFromScreen", capture, StringComparison.Ordinal);
        Assert.Contains("PrintWindow(windowHandle, deviceContext, PwRenderFullContent)", source, StringComparison.Ordinal);
        Assert.Contains("GetPhysicalWindowBounds(windowHandle)", source, StringComparison.Ordinal);
    }

    private static string ReadAutomationSource(params string[] pathFromAutomationProject) =>
        File.ReadAllText(Path.Combine(
            [FindRepositoryRoot(), "JitHub.WinUI.Automation", .. pathFromAutomationProject]));

    private static string ReadRendererSource(params string[] pathFromRendererProject) =>
        File.ReadAllText(Path.Combine(
            [FindRepositoryRoot(), "MarkdownRenderer", "src", "MarkdownRenderer", .. pathFromRendererProject]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string gitMarker = Path.Combine(directory.FullName, ".git");
            bool isRepositoryRoot = File.Exists(gitMarker)
                || Directory.Exists(gitMarker)
                || File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"));
            if (isRepositoryRoot
                && Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI"))
                && Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI.Automation")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
