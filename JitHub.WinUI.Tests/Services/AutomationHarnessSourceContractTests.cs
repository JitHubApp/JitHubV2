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
        string rendererSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));
        Assert.Contains("case VirtualKey.Application when _selection.IsActive", rendererSource, StringComparison.Ordinal);
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
        Assert.Contains("Lifecycle oversized SVG", File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs")), StringComparison.Ordinal);
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
    public void MarkdownImageTerminalFailureRelayoutsOnlyUnconstrainedInlineFallbacks()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "Boxes",
            "ImageBox.cs"));
        int publishFailure = source.IndexOf("private void PublishFailure", StringComparison.Ordinal);

        Assert.True(publishFailure >= 0);
        string failureImplementation = source[publishFailure..];
        Assert.Contains("UpdatePlaceholder(maxWidth, _imageHeight)", failureImplementation, StringComparison.Ordinal);
        Assert.Contains("layoutInvalidated: ShouldExpandInlineFailure", failureImplementation, StringComparison.Ordinal);
        Assert.Contains("_requestedWidth is null", source, StringComparison.Ordinal);
        Assert.Contains("_requestedHeight is null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownImagePlaceholderUsesThemeTypographyAndPaintsInlineAltText()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "Boxes",
            "ImageBox.cs"));

        Assert.Contains("PaintPlaceholder(ds, destination);", source, StringComparison.Ordinal);
        Assert.Contains("EnsureLoading();", source, StringComparison.Ordinal);
        Assert.Contains("ImageResolverDeadline.RunAsync", source, StringComparison.Ordinal);
        Assert.Contains("ImageResolverTimeout", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(45)", source, StringComparison.Ordinal);
        Assert.Contains("compactInlineFailure", source, StringComparison.Ordinal);
        Assert.Contains("MeasureInlineFailureWidth", source, StringComparison.Ordinal);
        Assert.Contains("GetInlineFailureText()", source, StringComparison.Ordinal);
        Assert.Contains("CanvasWordWrapping.NoWrap", source, StringComparison.Ordinal);
        Assert.Contains("GetStyle(MarkdownElementKeys.ImageCaption)", source, StringComparison.Ordinal);
        string placeholder = Regex.Match(source,
            @"private void UpdatePlaceholder\([\s\S]*?private bool ShouldExpandInlineFailure").Value;
        Assert.NotEmpty(placeholder);
        Assert.Matches(
            @"SharedCanvasTextFormatCache\.Acquire\(\s*style,\s*compactInlineFailure \? CanvasWordWrapping\.NoWrap : CanvasWordWrapping\.Wrap,\s*CanvasHorizontalAlignment\.Center,\s*Context\.FlowDirection,\s*Context\.Language,\s*true,\s*CanvasVerticalAlignment\.Center\)",
            placeholder);
        string formatSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer", "Layout",
            "SharedCanvasTextFormatCache.cs"));
        Assert.Contains("NormalizeFontFamilyForCanvas(style.FontFamily)", formatSource, StringComparison.Ordinal);
        Assert.Contains("style.FontSize,", formatSource, StringComparison.Ordinal);
        Assert.Contains("format.FontFamily = key.FontFamily", formatSource, StringComparison.Ordinal);
        Assert.Contains("format.FontSize = key.FontSize", formatSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FontFamily = \"Segoe UI Variable\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownInlineImagesSubscribeBeforeViewportGeometryIsAvailable()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));

        Assert.Contains("foreach (var (imageRun, _) in icb.InlineImageRuns)", source, StringComparison.Ordinal);
        Assert.Contains("AddImagePlan(imageRun.Image);", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var _ in icb.EnumerateInlineImageRects())", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("AddImagePlan(imageRun.Image);", StringComparison.Ordinal) <
            source.IndexOf("foreach (var _ in icb.EnumerateInlineImageRects())", StringComparison.Ordinal));
        Assert.Contains("if (!RegisterImage(image))", source, StringComparison.Ordinal);
        Assert.Contains("image.LoadCompleted += OnImageLoadCompleted;", source, StringComparison.Ordinal);
        Assert.Contains("_subscribedImages.Contains(completedImage)", source, StringComparison.Ordinal);
        Assert.Contains("UnsubscribeAllImages();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownImageCompletionRelayoutsTheCommittedSnapshotWithoutResolverLoop()
    {
        string controlSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string snapshotSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "LayoutSnapshot.cs"));

        Assert.Contains("QueueImageRelayout(completedImage.BlockIndex);", controlSource, StringComparison.Ordinal);
        int queueStart = controlSource.IndexOf("private void QueueImageRelayout(", StringComparison.Ordinal);
        int recoveryStart = controlSource.IndexOf("private void StartImageRelayoutRecovery(", queueStart, StringComparison.Ordinal);
        Assert.True(queueStart >= 0 && recoveryStart > queueStart);
        string queue = controlSource[queueStart..recoveryStart];
        Assert.Contains("_pendingImageRelayoutBlockIndices.Add(blockIndex);", queue, StringComparison.Ordinal);
        Assert.Contains("if (_imageRelayoutQueued || _imageRelayoutActive)", queue, StringComparison.Ordinal);
        Assert.Contains("StartImageRelayoutRecovery();", queue, StringComparison.Ordinal);
        int queuedFlag = queue.IndexOf("_imageRelayoutQueued = true;", StringComparison.Ordinal);
        Assert.True(queuedFlag >= 0);
        int recoveryStartAfterQueue = queue.IndexOf("StartImageRelayoutRecovery();", queuedFlag, StringComparison.Ordinal);
        Assert.True(recoveryStartAfterQueue > queuedFlag);
        int missingRecoveryClock = queue.IndexOf("if (!HasImageRelayoutRecoveryClock)", recoveryStartAfterQueue, StringComparison.Ordinal);
        Assert.True(missingRecoveryClock > recoveryStartAfterQueue);
        int dispatcherCapture = queue.IndexOf("Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;", missingRecoveryClock, StringComparison.Ordinal);
        Assert.True(dispatcherCapture > missingRecoveryClock);
        string missingRecovery = queue[missingRecoveryClock..dispatcherCapture];
        Assert.Contains("_imageRelayoutQueued = false;", missingRecovery, StringComparison.Ordinal);
        Assert.Contains("RequestRebuild(RebuildReason.Full);", missingRecovery, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingImageRelayoutBlockIndices.Clear();", missingRecovery, StringComparison.Ordinal);
        int enqueueAfterRecovery = queue.IndexOf("dispatcher?.TryEnqueue(", dispatcherCapture, StringComparison.Ordinal);
        Assert.True(enqueueAfterRecovery > dispatcherCapture);
        string dispatch = queue[dispatcherCapture..];
        Assert.Contains("DispatcherQueuePriority.Low", dispatch, StringComparison.Ordinal);
        Assert.Contains("var receiver = new WeakReference<MarkdownRendererControl>(this);", dispatch, StringComparison.Ordinal);
        Assert.Contains("if (receiver.TryGetTarget(out var control))", dispatch, StringComparison.Ordinal);
        Assert.Contains("control.ProcessQueuedImageRelayout(ticket);", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingImageRelayoutBlockIndices.Clear();", dispatch, StringComparison.Ordinal);

        Assert.Contains(
            "private bool HasImageRelayoutRecoveryClock => _imageRelayoutRecoveryTimer is not null ||\n        _managedImageRelayoutRecoveryTimer is not null;",
            controlSource,
            StringComparison.Ordinal);

        int recoveryMethodEnd = controlSource.IndexOf("private static void ProcessImageRelayoutRecovery(", recoveryStart, StringComparison.Ordinal);
        Assert.True(recoveryMethodEnd > recoveryStart);
        string recovery = controlSource[recoveryStart..recoveryMethodEnd];
        Assert.Contains("if (HasImageRelayoutRecoveryClock || DispatcherQueue is not { } dispatcher)", recovery, StringComparison.Ordinal);
        Assert.Contains("var receiver = new WeakReference<MarkdownRendererControl>(this);", recovery, StringComparison.Ordinal);
        Assert.Contains("dispatcher.CreateTimer()", recovery, StringComparison.Ordinal);
        Assert.Contains("timer.Tick +=", recovery, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(sender, control._imageRelayoutRecoveryTimer)", recovery, StringComparison.Ordinal);
        Assert.Contains("ProcessImageRelayoutRecovery(control);", recovery, StringComparison.Ordinal);
        Assert.Matches(
            "_managedImageRelayoutRecoveryTimer\\s*=\\s*new\\(\\s*this,\\s*callback\\s*=>\\s*dispatcher\\.TryEnqueue\\(callback\\.Invoke\\),\\s*ProcessImageRelayoutRecovery,\\s*TimeSpan\\.FromMilliseconds\\(100\\)\\);",
            recovery);
        Assert.DoesNotContain("ProcessQueuedImageRelayout(", recovery, StringComparison.Ordinal);

        string weakRecoveryTimer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "WeakRecoveryTimer.cs"));
        Assert.Contains("_target = new WeakReference<T>(target);", weakRecoveryTimer, StringComparison.Ordinal);
        Assert.Contains("_queuedCallback = RunQueuedCallback;", weakRecoveryTimer, StringComparison.Ordinal);
        Assert.Contains("if (!TryGetLiveTarget(out _))", weakRecoveryTimer, StringComparison.Ordinal);
        Assert.Contains("private bool TryGetLiveTarget(out T? target) => _target.TryGetTarget(out target);", weakRecoveryTimer, StringComparison.Ordinal);

        int recoveryTickEnd = controlSource.IndexOf("private void StopImageRelayoutRecovery(", recoveryMethodEnd, StringComparison.Ordinal);
        Assert.True(recoveryTickEnd > recoveryMethodEnd);
        string recoveryTick = controlSource[recoveryMethodEnd..recoveryTickEnd];
        Assert.Contains("private static void ProcessImageRelayoutRecovery(MarkdownRendererControl control)", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessBitmapOutputRecovery();", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessQueuedImageRelayout(control._lastImageRelayoutDispatchTicket);", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control._imageRelayoutPayload?.PublishAfterWorkDrains();", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.PublishPendingLayoutCommit();", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessQueuedLazyLayoutRealization(control._lastLazyLayoutDispatchTicket);", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.TryPublishLazyLayoutCompletion(control._lazyLayoutGeneration);", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessPendingHorizontalOverflowVisualRetry();", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("control.ProcessPendingFocusGeometry();", recoveryTick, StringComparison.Ordinal);
        Assert.Matches(
            "if\\s*\\(control\\._isDisposed \\|\\| control\\._isUnloaded\\)\\s*control\\.StopImageRelayoutRecovery\\(force: true\\);",
            recoveryTick);
        Assert.Contains("control.StopImageRelayoutRecoveryIfIdle();", recoveryTick, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", recoveryTick, StringComparison.Ordinal);

        int processStart = controlSource.IndexOf("private void ProcessQueuedImageRelayout(", StringComparison.Ordinal);
        int automationStart = controlSource.IndexOf("private void InvalidateAutomationLayout(", processStart, StringComparison.Ordinal);
        Assert.True(processStart >= 0 && automationStart > processStart);
        string process = controlSource[processStart..automationStart];
        Assert.Contains("_rebuildCoordinator.HasPendingOrBuilding", process, StringComparison.Ordinal);
        Assert.Contains("snapshot.TopLevelBlockCount is > 0 and <= LayoutSnapshot.MaximumPrivateRasterPlacementRoots", process, StringComparison.Ordinal);
        Assert.Contains("_embedPlans.Count == 0 &&", process, StringComparison.Ordinal);
        Assert.DoesNotContain("_codeBlockActionPlans.Count == 0", process, StringComparison.Ordinal);
        Assert.Contains("ImageRelayoutBatch.CapturePendingIndices(", process, StringComparison.Ordinal);
        Assert.Contains("private readonly SortedSet<int> _pendingImageRelayoutBlockIndices = [];", controlSource, StringComparison.Ordinal);
        Assert.Contains("optedIn: session is not null", process, StringComparison.Ordinal);
        int taskLaunch = process.IndexOf("_imageRelayoutTask = RunImageRelayoutAsync(", StringComparison.Ordinal);
        int acceptedRemoval = process.IndexOf("_pendingImageRelayoutBlockIndices.Remove(completedBlockIndex)", StringComparison.Ordinal);
        Assert.True(taskLaunch >= 0 && acceptedRemoval > taskLaunch);
        Assert.DoesNotContain("_pendingImageRelayoutBlockIndices.Clear()", process, StringComparison.Ordinal);
        string rasterPublication = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer", "Controls",
            "MarkdownRendererControl.ImageCandidate.cs"));
        Assert.Contains("TryPrepareCodeBlockActionPublication(plan", rasterPublication, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(newPlan.Box, identity.Placement)", rasterPublication, StringComparison.Ordinal);
        Assert.Contains("_imagePlans.Count is > 0 and <= InlineContainerBox.MaximumPrivateRasterPlacementImages", process, StringComparison.Ordinal);
        Assert.Contains("new RasterPlacementInputs(receiver, _snapshotGeneration,", process, StringComparison.Ordinal);
        Assert.Contains("RequestRebuild(RebuildReason.Full);", process, StringComparison.Ordinal);

        int runStart = controlSource.IndexOf("private static async Task RunImageRelayoutAsync(", StringComparison.Ordinal);
        int publicationStart = controlSource.IndexOf("private static WeakSnapshotPublication<LayoutSnapshot> CreateImageRelayoutPublication(", runStart, StringComparison.Ordinal);
        Assert.True(runStart >= 0 && publicationStart > runStart);
        string run = controlSource[runStart..publicationStart];
        int privateCandidate = run.IndexOf("payload.Snapshot.CreateSingleRasterCandidate(", StringComparison.Ordinal);
        int fallbackRelayout = run.IndexOf("payload.Snapshot.TryRelayoutChangedBlocks(", StringComparison.Ordinal);
        int constructionDrain = run.IndexOf("candidateContext.DrainOwnedRetirementsAsync(failure)", StringComparison.Ordinal);
        int candidateBind = run.IndexOf("rasterPlacement!.Bind(candidate.Snapshot)", StringComparison.Ordinal);
        Assert.True(privateCandidate >= 0 && fallbackRelayout > privateCandidate);
        Assert.True(constructionDrain > fallbackRelayout && candidateBind > constructionDrain);
        int rootPreparation = run.IndexOf("payload.Snapshot.TryRelayoutCompletedImageRoots(", StringComparison.Ordinal);
        Assert.True(rootPreparation >= 0 && rootPreparation < privateCandidate && rootPreparation < fallbackRelayout);
        Assert.Contains("else if (result == PreparedImageRelayoutResult.Unavailable)", run, StringComparison.Ordinal);
        Assert.Contains("expectedPointerInputEpoch: pointerInputEpoch", run, StringComparison.Ordinal);
        Assert.Contains("if (result == PreparedImageRelayoutResult.Retry)", run, StringComparison.Ordinal);
        Assert.Contains("retryImageBlockIndex = payload.Changed[0];", run, StringComparison.Ordinal);
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

        int completionStart = controlSource.IndexOf("private void CompleteImageRelayout(", publicationStart, StringComparison.Ordinal);
        int failureReporter = controlSource.IndexOf("private static void ReportImageRelayoutFailure", completionStart, StringComparison.Ordinal);
        Assert.True(completionStart >= 0 && failureReporter > completionStart);
        string publicationFactory = controlSource[publicationStart..completionStart];
        Assert.Contains("int? retryImageBlockIndex", publicationFactory, StringComparison.Ordinal);
        Assert.Contains("bool retryCurrentImages", publicationFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("int[]", publicationFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("retryImageBlockIndices", publicationFactory, StringComparison.Ordinal);
        string completion = controlSource[completionStart..failureReporter];
        Assert.Contains("current && snapshot is not null && !_isDisposed && !_isUnloaded &&\n                (!canceled || rootMeasureCommitted)", completion, StringComparison.Ordinal);
        Assert.Contains("// A private whole-tree candidate has not committed and is excluded.", completion, StringComparison.Ordinal);
        Assert.Contains("generation == _snapshotGeneration && ReferenceEquals(snapshot, _snapshot)", completion, StringComparison.Ordinal);
        Assert.Contains("TryPublishRasterPlacement(snapshot, candidate, generation)", completion, StringComparison.Ordinal);
        Assert.Contains("RequestRebuild(RebuildReason.Full);", completion, StringComparison.Ordinal);
        Assert.Contains("else if (retryImageBlockIndex.HasValue || retryCurrentImages)", completion, StringComparison.Ordinal);
        Assert.Contains("if (retryImageBlockIndex is { } retryIndex)", completion, StringComparison.Ordinal);
        Assert.Contains("if (retryCurrentImages)\n                        foreach (var image in _subscribedImages)", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("retryImageBlockIndices", completion, StringComparison.Ordinal);
        Assert.Contains("rootMeasureCommitted &&\n                        (canceled || capturedScrollOffset != _scroll?.VerticalOffset) ? null : anchor", completion, StringComparison.Ordinal);

        Assert.Contains("internal SingleRasterLayoutCandidate? CreateSingleRasterCandidate(", snapshotSource, StringComparison.Ordinal);
        Assert.Contains("internal void RelayoutChangedBlocks(", snapshotSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Initial load / intrinsic-size change", controlSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownSelectionContextMenuSupportsKeyboardInvocation()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string pointerSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.PointerInput.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("case VirtualKey.F10 when shift && _selection.IsActive", source, StringComparison.Ordinal);
        Assert.Contains("ShowSelectionContextMenu(GetKeyboardContextMenuPoint())", source, StringComparison.Ordinal);

        int rightTappedStart = source.IndexOf("private void ProcessRightTapped(ref PointerInput e)", StringComparison.Ordinal);
        int menuStart = source.IndexOf("private bool ShowSelectionContextMenu(", rightTappedStart, StringComparison.Ordinal);
        Assert.True(rightTappedStart >= 0 && menuStart > rightTappedStart);
        string rightTapped = source[rightTappedStart..menuStart];
        Assert.Contains("long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);", rightTapped, StringComparison.Ordinal);
        Assert.Contains("if (touchOrPen && (!_selection.IsActive || !IsPointInsideSelection(snapshot, pt)))", rightTapped, StringComparison.Ordinal);
        Assert.Contains("TrySelectWordForTouchContext(\n                pt,\n                snapshot,\n                snapshotGeneration,\n                layoutRevision,", rightTapped, StringComparison.Ordinal);
        Assert.Contains("if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))", rightTapped, StringComparison.Ordinal);
        Assert.Contains("e.ShowContextMenuAfterInteraction = true;", rightTapped, StringComparison.Ordinal);
        Assert.Contains("e.TouchSelectionContextMenu = touchOrPen;", rightTapped, StringComparison.Ordinal);

        int keyDownStart = source.IndexOf("private void OnKeyDown(", StringComparison.Ordinal);
        int keyDownEnd = source.IndexOf("private bool HandleHorizontalArrowKey(", keyDownStart, StringComparison.Ordinal);
        Assert.True(keyDownStart >= 0 && keyDownEnd > keyDownStart);
        string keyDown = source[keyDownStart..keyDownEnd];
        Assert.Contains("case VirtualKey.F10 when shift && _selection.IsActive:", keyDown, StringComparison.Ordinal);
        Assert.Contains("case VirtualKey.Application when _selection.IsActive:", keyDown, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(keyDown, "ShowSelectionContextMenu\\(GetKeyboardContextMenuPoint\\(\\)\\)").Count);

        int menuClosedStart = source.IndexOf("private void OnSelectionContextMenuClosed(", menuStart, StringComparison.Ordinal);
        Assert.True(menuClosedStart > menuStart);
        string menu = source[menuStart..menuClosedStart];
        Assert.Contains("requiredSnapshotGeneration", menu, StringComparison.Ordinal);
        Assert.Contains("requiredLayoutRevision", menu, StringComparison.Ordinal);
        Assert.Contains("IsCurrentContextMenuSnapshot(", menu, StringComparison.Ordinal);
        Assert.Contains("CloseSelectionContextMenu();", menu, StringComparison.Ordinal);
        Assert.Contains("expectedLayoutRevision = requiredSnapshot is null", menu, StringComparison.Ordinal);
        Assert.Contains(": requiredLayoutRevision;", menu, StringComparison.Ordinal);
        Assert.Contains("expectedSnapshotGeneration,\n                    expectedLayoutRevision,\n                    out target))", menu, StringComparison.Ordinal);
        Assert.Contains("menu.ShowAt(canvas, placementPoint);", menu, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(menu, "DisposeContextMenuTarget\\(target\\);").Count);
        Assert.Contains("!ReferenceEquals(_selectionContextMenu, menu) ||\n                !IsCurrentContextMenuSnapshot(", menu, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)\n        {\n            DisposeContextMenuTarget(target);", menu, StringComparison.Ordinal);

        int recognizedGestureStart = pointerSource.IndexOf("private bool DispatchRecognizedGesture(", StringComparison.Ordinal);
        int pointerDispatchStart = pointerSource.IndexOf("private void DispatchPointerInput(", recognizedGestureStart, StringComparison.Ordinal);
        Assert.True(recognizedGestureStart >= 0 && pointerDispatchStart > recognizedGestureStart);
        string recognizedGesture = pointerSource[recognizedGestureStart..pointerDispatchStart];
        int endInteraction = recognizedGesture.IndexOf("snapshot.EndInteraction();", StringComparison.Ordinal);
        int postInteractionMenu = recognizedGesture.IndexOf("CompleteGestureAfterSnapshotInteraction(", endInteraction, StringComparison.Ordinal);
        Assert.True(endInteraction >= 0 && postInteractionMenu > endInteraction);
        Assert.Contains("if (input.ShowContextMenuAfterInteraction)", recognizedGesture, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration,\n                    layoutRevision);", recognizedGesture[postInteractionMenu..], StringComparison.Ordinal);

        int completeGestureStart = pointerSource.IndexOf("private bool CompleteGestureAfterSnapshotInteraction(", StringComparison.Ordinal);
        int postInteractionStart = pointerSource.IndexOf("private bool CompletePointerPostInteraction(", completeGestureStart, StringComparison.Ordinal);
        Assert.True(completeGestureStart >= 0 && postInteractionStart > completeGestureStart);
        string completion = pointerSource[completeGestureStart..postInteractionStart];
        Assert.Contains("!ReferenceEquals(_snapshot, snapshot)", completion, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration != Volatile.Read(ref _snapshotGeneration)", completion, StringComparison.Ordinal);
        Assert.Contains("input.GestureLayoutRevision != layoutRevision", completion, StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision != layoutRevision", completion, StringComparison.Ordinal);
        Assert.Contains("ShowSelectionContextMenu(", completion, StringComparison.Ordinal);
        Assert.Contains("touchSelection: input.TouchSelectionContextMenu", completion, StringComparison.Ordinal);
        Assert.Contains("requiredSnapshotGeneration: snapshotGeneration", completion, StringComparison.Ordinal);
        Assert.Contains("requiredLayoutRevision: layoutRevision", completion, StringComparison.Ordinal);
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
