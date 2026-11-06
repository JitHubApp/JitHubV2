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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));
        int start = source.IndexOf("static void RunHomeWidgetBoardProbe", StringComparison.Ordinal);
        int end = source.IndexOf("static void RunHomeCustomizeProbe", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        string probe = source[start..end];
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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("StarsDeleteCategoryDialog", source, StringComparison.Ordinal);
        Assert.Contains("FindVisibleStarsCategory(window, categoryName)", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(GetAutomationId(element), \"PrimaryButton\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplicitAppSelectionRejectsStaleBuildOutputs()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
    public void MarkdownLifecycleMatrixUsesDeterministicPerCaseProcesses()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("from theme in options.Themes", source, StringComparison.Ordinal);
        Assert.Contains("from target in selectedTargets", source, StringComparison.Ordinal);
        Assert.Contains("from textScale in textScales", source, StringComparison.Ordinal);
        Assert.Contains("from viewport in viewports", source, StringComparison.Ordinal);
        Assert.Contains("ResizeWindow(window, viewport.Width, viewport.Height, reactivate: false);", source, StringComparison.Ordinal);
        Assert.Contains("WriteMarkdownLifecycleRuntimeSettings(runPaths.RuntimeSettingsPath, textScale, revision: 1);", source, StringComparison.Ordinal);
        Assert.Contains("AssertMarkdownLifecycleCloseState(window, target, caseId);", source, StringComparison.Ordinal);
        Assert.Contains("result.ExitCode = exitCode;", source, StringComparison.Ordinal);
        Assert.Contains("result.UnhandledLogCount = unhandledLogCount;", source, StringComparison.Ordinal);
        Assert.Contains("AppAssemblySha256 = appAssemblySha256", source, StringComparison.Ordinal);
        Assert.Contains("AppAssemblyLastWriteUtc = File.GetLastWriteTimeUtc(appAssemblyPath)", source, StringComparison.Ordinal);
        Assert.Contains("AutomationAssemblySha256 = automationAssemblySha256", source, StringComparison.Ordinal);
        int runStart = source.IndexOf("static void RunMarkdownHostLifecycleProbe", StringComparison.Ordinal);
        int firstClose = source.IndexOf("window.Close();", runStart, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycle.Process.Kill", source[runStart..firstClose], StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycleMatrixSupportsBoundedHashSafeResume()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("JITHUB_AUTOMATION_MARKDOWN_RESUME", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_MARKDOWN_MAX_CASES", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_MARKDOWN_TEXT_SCALE_PERCENT", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_MARKDOWN_VIEWPORT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JITHUB_AUTOMATION_MARKDOWN_CONTEXT_GESTURE", source, StringComparison.Ordinal);
        Assert.Contains("pendingCases.Take(maxCases)", source, StringComparison.Ordinal);
        Assert.Contains("LoadMarkdownLifecycleManifest(manifestPath)", source, StringComparison.Ordinal);
        Assert.Contains("ValidateResumedMarkdownLifecycleManifest", source, StringComparison.Ordinal);
        Assert.Contains("manifest.AppSha256, appSha256", source, StringComparison.Ordinal);
        Assert.Contains("manifest.AppAssemblySha256, appAssemblySha256", source, StringComparison.Ordinal);
        Assert.Contains("manifest.AutomationAssemblySha256, automationAssemblySha256", source, StringComparison.Ordinal);
        Assert.Contains("Distinct(StringComparer.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
        Assert.Contains("const int maximumAttempts = 9", source, StringComparison.Ordinal);
        Assert.Contains("catch (UnauthorizedAccessException) when (attempt < maximumAttempts)", source, StringComparison.Ordinal);
        Assert.Contains("ITextRange contextSelectionRange = textPattern.GetSelection().FirstOrDefault()", source, StringComparison.Ordinal);
        Assert.Contains("Mouse.MoveTo(contextSelectionPoint)", source, StringComparison.Ordinal);
        Assert.Contains("Mouse.RightClick()", source, StringComparison.Ordinal);
        Assert.Contains("right-clicking selected text revoked the selection", source, StringComparison.Ordinal);
        Assert.Contains("SendMarkdownPointerDrag(pointerAttemptStart, pointerAttemptEnd)", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.ActivateForKeyboard(windowHandle)", source, StringComparison.Ordinal);
        Assert.Contains("raw pointer drag did not create a cross-line text selection", source, StringComparison.Ordinal);
        Assert.Contains("foreach (AutomationElement host in FindMarkdownLifecycleHosts(root, target))", source, StringComparison.Ordinal);
        Assert.Contains("recycled UIA peer", source, StringComparison.Ordinal);
        Assert.Contains("Use a new output directory rather than combining incompatible evidence", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycleInventoryCoversAllRealHostsAndAccessibilityStates()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        string[] hosts =
        [
            "issue-body",
            "issue-comment",
            "issue-comment-form",
            "pull-request-body",
            "pull-request-comment",
            "pull-request-review",
            "pull-request-review-comment",
            "pull-request-review-reply-form",
            "pull-request-comment-form",
            "commit-body",
            "commit-comment",
            "commit-comment-form",
            "my-issues-body",
            "my-issues-comment",
            "my-pull-requests-body",
            "my-pull-requests-comment",
            "my-pull-requests-review",
            "my-pull-requests-review-comment",
            "repository-readme",
            "profile-readme",
        ];
        foreach (string host in hosts)
        {
            Assert.Contains($"new(\"{host}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains("new(\"wide\", 1366, 900)", source, StringComparison.Ordinal);
        Assert.Contains("new(\"snapped\", 760, 650)", source, StringComparison.Ordinal);
        Assert.Contains("new(\"compact\", 640, 600)", source, StringComparison.Ordinal);
        Assert.Contains("double[] textScales = [1, 1.5, 2];", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(RequiredViewportName) ||", source, StringComparison.Ordinal);
        Assert.Contains("LauncherControlAutomationId: \"RepoIssuesOpenCommentButton\"", source, StringComparison.Ordinal);
        Assert.Contains("LauncherControlAutomationId: \"RepoPullRequestsOpenCompactCommentButton\"", source, StringComparison.Ordinal);
        string pullRequestPageSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));
        Assert.DoesNotContain("forceInlineComposerForLifecycle", pullRequestPageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetsHost(inlineComposerHostId)", pullRequestPageSource, StringComparison.Ordinal);
        Assert.Contains("Markdown audit selection marker", source, StringComparison.Ordinal);
        Assert.Contains("raw pointer drag did not create a cross-line text selection", source, StringComparison.Ordinal);
        Assert.Contains("SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_LEFTDOWN)", source, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", source, StringComparison.Ordinal);
        Assert.Contains("context Copy", source, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Pressing(VirtualKeyShort.LSHIFT)", source, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Press(VirtualKeyShort.F10)", source, StringComparison.Ordinal);
        Assert.Contains("keyboard link focus", source, StringComparison.Ordinal);
        Assert.Contains("internal repository route", source, StringComparison.Ordinal);
        Assert.Contains("internal user route", source, StringComparison.Ordinal);
        Assert.Contains("external browser route", source, StringComparison.Ordinal);
        Assert.Contains("active inline SVG", source, StringComparison.Ordinal);
        Assert.Contains("TextPattern reading order", source, StringComparison.Ordinal);
        Assert.Contains("RunRepeatedMarkdownRelayout", source, StringComparison.Ordinal);
        Assert.Contains("popup dismissal", source, StringComparison.Ordinal);
        Assert.Contains("popup focus restoration", source, StringComparison.Ordinal);
        Assert.Contains("popup Markdown host escaped the app", source, StringComparison.Ordinal);
        Assert.Contains("RetainedMemoryBudget", source, StringComparison.Ordinal);
        Assert.Contains("AssertAndRecordMarkdownMemoryBudget", source, StringComparison.Ordinal);
        Assert.Contains("RunMarkdownSecurityPolicyLifecycleCase", source, StringComparison.Ordinal);
        // The security lifecycle case looks for this image in the fixture the app renders.
        Assert.Contains(
            "Lifecycle oversized SVG",
            new JitHub.Markdown.Automation.MarkdownLifecycleFixtureDocument(
                targetHost: null,
                corpusPath: null,
                includeSecurityFixture: true).Compose(string.Empty, "MarkdownHost"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycleUsesReadinessSignalsAndProvesResourceFallback()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("JITHUB_MARKDOWN_APP_READY_PATH", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_HOST_READY_PATH", source, StringComparison.Ordinal);
        Assert.Contains("WaitForMarkdownLifecycleProcess", source, StringComparison.Ordinal);
        Assert.Contains("WaitForMarkdownLifecycleSignal", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT", source, StringComparison.Ordinal);
        Assert.Contains("WaitForResourceMapFallbackEvidence", source, StringComparison.Ordinal);
        Assert.Contains("ResourceMapAbsentCase", source, StringComparison.Ordinal);
        Assert.Contains("PrepareRealMarkdownHost", source, StringComparison.Ordinal);
        Assert.Contains("synthetic Markdown lifecycle fixture replaced the real product page", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_LINK_EVIDENCE_PATH", source, StringComparison.Ordinal);
        int resourceCaseStart = source.IndexOf(
            "static void RunForcedResourceMapAbsentLifecycleCase",
            StringComparison.Ordinal);
        int securityCaseStart = source.IndexOf(
            "static void RunMarkdownSecurityPolicyLifecycleCase",
            resourceCaseStart,
            StringComparison.Ordinal);
        Assert.True(resourceCaseStart >= 0 && securityCaseStart > resourceCaseStart);
        string resourceCase = source[resourceCaseStart..securityCaseStart];
        Assert.Contains("NativeMethods.GetWorkArea()", resourceCase, StringComparison.Ordinal);
        Assert.Contains("Math.Min(1366, workArea.Width)", resourceCase, StringComparison.Ordinal);
        Assert.Contains("Math.Min(900, workArea.Height)", resourceCase, StringComparison.Ordinal);

        string appSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "App.xaml.cs"));
        Assert.DoesNotContain("MarkdownLifecycleFixturePage", appSource, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "MarkdownLifecycleFixturePage.cs")));

        string[] realPageSources =
        [
            "RepoIssuePage.xaml",
            "RepoPullRequestPage.xaml",
            "RepoCommitsPage.xaml",
            "MyIssuesPage.xaml",
            "MyPullRequestsPage.xaml",
            "ProfilePage.xaml",
        ];
        foreach (string pageSource in realPageSources)
        {
            string pagePath = Directory.GetFiles(
                Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views"),
                pageSource,
                SearchOption.AllDirectories).Single();
            string xaml = File.ReadAllText(pagePath);
            if (string.Equals(pageSource, "RepoIssuePage.xaml", StringComparison.Ordinal))
            {
                xaml += File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(),
                    "JitHub.WinUI",
                    "Views",
                    "Controls",
                    "Issue",
                    "RepoIssueDetailPane.xaml"));
            }
            Assert.True(
                xaml.Contains("MarkdownViewer", StringComparison.Ordinal) ||
                xaml.Contains("MarkdownForm", StringComparison.Ordinal),
                $"{pageSource} must instantiate a production Markdown host.");
        }

        string repoCodePage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages", "RepoCodePage.xaml"));
        string filePreviewHost = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "CodeViewer", "FilePreviewHost.xaml"));
        string filePreviewHostCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "CodeViewer", "FilePreviewHost.xaml.cs"));
        string markdownPreview = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "MarkdownPreview.xaml"));
        Assert.Contains("FilePreviewHost", repoCodePage, StringComparison.Ordinal);
        Assert.Contains("RendererHost", filePreviewHost, StringComparison.Ordinal);
        Assert.Contains("CachedCodeRendererHost", filePreviewHost, StringComparison.Ordinal);
        Assert.Contains("RepoFilePreviewKind.Markdown => new MarkdownPreview()", filePreviewHostCode, StringComparison.Ordinal);
        Assert.Contains("GetOrCreateCodeRenderer", filePreviewHostCode, StringComparison.Ordinal);
        Assert.Contains("MarkdownViewer", markdownPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycleRetainsNativeExitEvidenceForAttachedProcesses()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("NativeMethods.OpenProcessExitHandle(processId)", source, StringComparison.Ordinal);
        Assert.Contains("lifecycle.GetExitCode()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycle.Process.ExitCode", source, StringComparison.Ordinal);
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
            ReadRendererSource("Controls", "MarkdownRendererControl.cs"),
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
        int recognizedGestureStart = pointerSource.IndexOf("internal bool DispatchRecognizedGesture(", StringComparison.Ordinal);
        int pointerDispatchStart = pointerSource.IndexOf("internal void DispatchPointerInput(", recognizedGestureStart, StringComparison.Ordinal);
        Assert.True(recognizedGestureStart >= 0 && pointerDispatchStart > recognizedGestureStart);
        string recognizedGesture = pointerSource[recognizedGestureStart..pointerDispatchStart];
        int endInteraction = recognizedGesture.IndexOf("snapshot.EndInteraction();", StringComparison.Ordinal);
        int postInteractionMenu = recognizedGesture.IndexOf("_contextMenuCompletion.Complete(", endInteraction, StringComparison.Ordinal);
        Assert.True(endInteraction >= 0 && postInteractionMenu > endInteraction);
        Assert.Contains("if (input.ShowContextMenuAfterInteraction)", recognizedGesture, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration,\n                layoutRevision);", recognizedGesture[postInteractionMenu..], StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycleOrchestratorRequiresDebugReleaseAndStableSource()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "Run-MarkdownLifecycleMatrix.ps1"));

        Assert.Contains("@(\"Debug\", \"Release\")", source, StringComparison.Ordinal);
        Assert.Contains("($expectedHosts * 3 * 3) * 3", source, StringComparison.Ordinal);
        Assert.Contains("$expectedHostNames", source, StringComparison.Ordinal);
        Assert.Contains("pointerDragSelection", source, StringComparison.Ordinal);
        Assert.Contains("retainedMemoryBudget", source, StringComparison.Ordinal);
        Assert.Contains("internalRepositoryRoute", source, StringComparison.Ordinal);
        Assert.Contains("externalBrowserRoute", source, StringComparison.Ordinal);
        Assert.Contains("Get-SourceSnapshotHash", source, StringComparison.Ordinal);
        Assert.Contains("Source changed while the lifecycle matrix was running", source, StringComparison.Ordinal);
        Assert.Contains("resourceMapAbsentCase", source, StringComparison.Ordinal);
        Assert.Contains("securityPolicyCase", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-OneLifecycleCase", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_MARKDOWN_MAX_CASES = \"1\"", source, StringComparison.Ordinal);
        Assert.Contains("$runner.WaitForExit($CaseTimeoutSeconds * 1000)", source, StringComparison.Ordinal);
        Assert.Contains("Stop-OwnedLifecycleProcesses", source, StringComparison.Ordinal);
        Assert.Contains("Assert-DesktopLifecycleProcessesClosed", source, StringComparison.Ordinal);
        Assert.Contains("oneRunnerProcessPerCase = $true", source, StringComparison.Ordinal);
        Assert.Contains("$manifest.version -ne 5", source, StringComparison.Ordinal);
        Assert.Contains("$manifest.runScope -ne \"full-matrix\"", source, StringComparison.Ordinal);
        Assert.Contains("-not $manifest.requiresSupplementalCases", source, StringComparison.Ordinal);
        Assert.Contains("version = 5", source, StringComparison.Ordinal);
        Assert.Contains("appAssemblySha256 = $manifest.appAssemblySha256", source, StringComparison.Ordinal);
        Assert.Contains("markdown-lifecycle-combined-manifest.json", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoCodeAutomationRequiresDeterministicSourceAndLivePerformanceGates()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("repo-code-high-contrast", source, StringComparison.Ordinal);
        Assert.Contains("--high-contrast", source, StringComparison.Ordinal);
        Assert.Contains("High contrast editor colors active", source, StringComparison.Ordinal);
        Assert.Contains("repo-code-high-contrast-1366x900.png", source, StringComparison.Ordinal);
        Assert.Contains("repo-code-high-contrast-760x650.png", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoIssuesProbeAssertsCurrentDeterministicPreviewMarkdown()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        string probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        string probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
    public void MarkdownLifecycleApplicationDisposalIsIdempotentAndReleasesEveryResource()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        int classStart = source.IndexOf("internal sealed class MarkdownLifecycleApplication", StringComparison.Ordinal);
        int classEnd = source.IndexOf("internal sealed class MarkdownLifecycleManifest", classStart, StringComparison.Ordinal);
        Assert.True(classStart >= 0 && classEnd > classStart);
        string lifecycle = source[classStart..classEnd];

        Assert.Contains("Interlocked.Exchange(ref _disposed, 1)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DisposeResource(Application, ref firstException);", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DisposeResource(Process, ref firstException);", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DisposeResource(Launcher, ref firstException);", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _processExitHandle, IntPtr.Zero)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ExceptionDispatchInfo.Capture(firstException).Throw();", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void FilteredMarkdownLifecycleRunsDeclareAndCompleteTheirOwnScope()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("RunScope = runScope", source, StringComparison.Ordinal);
        Assert.Contains("RequestedTarget = requestedTarget", source, StringComparison.Ordinal);
        Assert.Contains("RequiresSupplementalCases = requiresSupplementalCases", source, StringComparison.Ordinal);
        Assert.Contains("selectedTargets.Sum", source, StringComparison.Ordinal);
        Assert.Contains("!requiresSupplementalCases ||", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GracefulCloseRequiresExactOwnershipAndNormalExitWithoutForceFallback()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

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
        Assert.Contains("catch (ArgumentException)\n    {", wait.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("Absence is the successful state this cleanup wait is proving.", wait, StringComparison.Ordinal);
        Assert.Contains("catch (InvalidOperationException)\n    {\n        return true;", wait.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsExportPickerUsesOwnedWindowAndLocalizationIndependentDialogContracts()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        int methodStart = source.IndexOf("static void AssertSettingsExportPicker(", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("static void RunStarsLibraryProbe", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);

        string method = source[methodStart..methodEnd];
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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        int methodStart = source.IndexOf("static void RunShellResponsiveProbe", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("static void RunShellNavClicksProbe", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);

        string method = source[methodStart..methodEnd];
        Assert.Contains("user collapses wide shell rail", method, StringComparison.Ordinal);
        Assert.Contains("user-collapsed shell rail remains collapsed after resize", method, StringComparison.Ordinal);
        Assert.Contains("user expands wide shell rail", method, StringComparison.Ordinal);
        Assert.Contains("Wide shell did not expose the persistent navigation toggle", method, StringComparison.Ordinal);
        Assert.DoesNotContain("redundant navigation drawer button", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryActionsUseLiveSharedStarsStateAndGracefulRelaunchBoundaries()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        int probeStart = source.IndexOf("static void RunRepositoryActionsProbe", StringComparison.Ordinal);
        int probeEnd = source.IndexOf("static void RunCommitsResponsiveWorkspaceProbe", probeStart, StringComparison.Ordinal);
        Assert.True(probeStart >= 0 && probeEnd > probeStart);
        string probe = source[probeStart..probeEnd];

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
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        int switchStart = source.IndexOf("static void RunThemeSwitchProbe", StringComparison.Ordinal);
        int paletteStart = source.IndexOf("static void RunThemePaletteProbe", switchStart, StringComparison.Ordinal);
        int paletteEnd = source.IndexOf("static void RunThemePaletteHomeMatrix", paletteStart, StringComparison.Ordinal);
        int captureStart = source.IndexOf("static void CaptureWindow(Window window, string path)", StringComparison.Ordinal);
        int captureEnd = source.IndexOf("static void WaitForScreenshotRegionToStabilize", captureStart, StringComparison.Ordinal);
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
