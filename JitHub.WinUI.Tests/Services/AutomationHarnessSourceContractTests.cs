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
        Assert.Contains("AuditedMarkdownAssemblyNames", source, StringComparison.Ordinal);
        Assert.Contains(
            "AppArtifactFreshness.GetNewestMarkdownAssemblySourceWriteTimeUtc(repositoryRoot, name)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsAppBuildFresh(candidate, baseDirectory)", source, StringComparison.Ordinal);
        Assert.Contains("GetFreshnessArtifact(appPath)", source, StringComparison.Ordinal);
        Assert.Contains("GetStaleAppArtifact(appPath, repositoryRoot)", source, StringComparison.Ordinal);
        Assert.Contains("AppArtifactFreshness.IsNativeAotTimestampFresh(", source, StringComparison.Ordinal);
        Assert.Contains("GetNewestSourceWriteTimeUtc(repositoryRoot, includeDependencies: true)", source, StringComparison.Ordinal);
        Assert.Contains("hasVerifiedCanonicalProvenance))", source, StringComparison.Ordinal);
        Assert.Contains("File.GetLastWriteTimeUtc(appAssembly) < GetNewestSourceWriteTimeUtc(", source, StringComparison.Ordinal);
        Assert.Contains("AppArtifactFreshness.GetStaleJitHubAppContentArtifact(", source, StringComparison.Ordinal);
        Assert.Contains("AppArtifactFreshness.GetStaleResvgWorkerArtifact(appPath, repositoryRoot)", source, StringComparison.Ordinal);
        Assert.Contains("firstSegment.StartsWith(\"obj\"", source, StringComparison.Ordinal);
        Assert.Contains("firstSegment.StartsWith(\"bin\"", source, StringComparison.Ordinal);
        Assert.Contains("No fresh JitHub executable was found for UI automation", source, StringComparison.Ordinal);
        Assert.Contains("\"publish\", \"JitHub.WinUI.exe\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "EnsureAppBinaryIsFresh(appPath, hasVerifiedCanonicalProvenance);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("VerifyCanonicalNativeAotProvenance(", source, StringComparison.Ordinal);
        Assert.Contains("GetCanonicalWorkflowIdentity()", source, StringComparison.Ordinal);
        Assert.Contains("IsNativeAotExecutable(appPath)", source, StringComparison.Ordinal);
        Assert.Contains("using PEReader reader", source, StringComparison.Ordinal);
        Assert.Contains("reader.PEHeaders.CorHeader is null", source, StringComparison.Ordinal);
        Assert.Contains("Refusing stale JitHub automation binary", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SameByteViewerUsesPinnedV4ProfileWithoutChangingOrdinaryDocuments()
    {
        string viewer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));

        Assert.Contains("if (sameByteCorpusPath is not null)", viewer, StringComparison.Ordinal);
        Assert.Contains("_sameByteVisualProfile = GitHubReadmeVisualStyleProfiles.PrimerLightV4;", viewer, StringComparison.Ordinal);
        Assert.Contains("_renderer.Theme = _sameByteVisualTheme ?? _theme;", viewer, StringComparison.Ordinal);
        Assert.Contains("_renderer.StyleSheet = _sameByteVisualProfile?.StyleSheet ?? new MarkdownStyleSheet(", viewer, StringComparison.Ordinal);
        Assert.Contains("_renderer.CodeBlockChromeMode = _sameByteVisualProfile?.CodeBlockChromeMode ?? CodeBlockChromeMode.Enhanced;", viewer, StringComparison.Ordinal);
        Assert.Contains("_renderer.CodeBlockLineNumberMode = _sameByteVisualProfile?.CodeBlockLineNumberMode ?? CodeBlockLineNumberMode.AutoMultiline;", viewer, StringComparison.Ordinal);
        Assert.Contains("_renderer.IsCodeBlockCopyEnabled = _sameByteVisualProfile?.IsCodeBlockCopyEnabled ?? IsCodeBlockCopyEnabled;", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetContextMenuSharesCommandRoutingAndReleasesDocumentTargetsOnClose()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = source.IndexOf("private bool AddTargetContextMenuItems", StringComparison.Ordinal);
        int end = source.IndexOf("private void AttachTargetCommand", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        string builder = source[start..end];
        Assert.Contains("MarkdownCommandKind.CopyLink", builder, StringComparison.Ordinal);
        Assert.Contains("MarkdownCommandKind.CopyImage", builder, StringComparison.Ordinal);
        Assert.Contains("MarkdownCommandKind.CopyCode", builder, StringComparison.Ordinal);
        Assert.Contains("MarkdownCommandKind.CopyTable", builder, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(builder, "AttachTargetCommand\\(item, action, target\\)").Count);

        Assert.Contains("item.Tag = (action, target);", source, StringComparison.Ordinal);
        Assert.Contains("item.Click += OnTargetContextMenuItemClick;", source, StringComparison.Ordinal);
        int handlerStart = source.IndexOf("private async void OnTargetContextMenuItemClick", StringComparison.Ordinal);
        int handlerEnd = source.IndexOf("private bool CanExecuteTargetCommandOrDefault", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];
        int cloneExecutionTarget = handler.IndexOf(
            "using ContextMenuTarget executionTarget = command.Item2.CloneForExecution();",
            StringComparison.Ordinal);
        Assert.True(cloneExecutionTarget >= 0);
        int awaitExecution = handler.IndexOf(
            "await ExecuteTargetCommandOrDefaultAsync(command.Item1, executionTarget).ConfigureAwait(true);",
            cloneExecutionTarget,
            StringComparison.Ordinal);
        Assert.True(cloneExecutionTarget >= 0 && awaitExecution > cloneExecutionTarget);
        Assert.Contains("catch (Exception ex)", handler, StringComparison.Ordinal);
        Assert.Contains("MarkdownDiagnostics.WriteLine(", handler, StringComparison.Ordinal);
        Assert.Contains("item.Click -= OnTargetContextMenuItemClick;", source, StringComparison.Ordinal);
        Assert.Contains("item.Tag = null;", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseTargetContextMenuCommands(menu);", source, StringComparison.Ordinal);

        int targetCaptureStart = source.IndexOf("private bool FindContextMenuTarget(", StringComparison.Ordinal);
        int populateStart = source.IndexOf("private static bool PopulateContextMenuTarget(", targetCaptureStart, StringComparison.Ordinal);
        int populateInlineStart = source.IndexOf("private static void PopulateInlineContextMenuTarget(", populateStart, StringComparison.Ordinal);
        int targetOwnerStart = source.IndexOf("private sealed class ContextMenuTarget : IDisposable", populateInlineStart, StringComparison.Ordinal);
        int tableSnapshotStart = source.IndexOf("private sealed class MarkdownTableClipboardSnapshot", targetOwnerStart, StringComparison.Ordinal);
        Assert.True(targetCaptureStart >= 0 && populateStart > targetCaptureStart &&
            populateInlineStart > populateStart && targetOwnerStart > populateInlineStart &&
            tableSnapshotStart > targetOwnerStart);
        string targetCapture = source[targetCaptureStart..populateStart];
        Assert.Contains("snapshot.TryBeginInteraction()", targetCapture, StringComparison.Ordinal);
        Assert.Contains("PopulateContextMenuTarget(block, physicalPoint, target)", targetCapture, StringComparison.Ordinal);
        Assert.Contains("snapshot.EndInteraction();", targetCapture, StringComparison.Ordinal);
        Assert.Contains("!captured || !IsCurrentContextMenuSnapshot(", targetCapture, StringComparison.Ordinal);
        Assert.Contains("DisposeContextMenuTarget(target);", targetCapture, StringComparison.Ordinal);
        string targetPopulation = source[populateStart..targetOwnerStart];
        Assert.Contains("target.SetImageBitmapLease(image.AcquireBitmapLease());", targetPopulation, StringComparison.Ordinal);
        Assert.Contains("target.SetImageBitmapLease(image.Image.AcquireBitmapLease());", targetPopulation, StringComparison.Ordinal);
        string targetLifetime = source[targetOwnerStart..tableSnapshotStart];
        Assert.Contains("_imageBitmapLease = ImageBitmapLease?.TryClone(),", targetLifetime, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _imageBitmapLease, lease)", targetLifetime, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _imageBitmapLease, null)?.Dispose()", targetLifetime, StringComparison.Ordinal);

        string bitmapCache = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "Boxes",
            "SharedCanvasBitmapCache.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int leaseStart = bitmapCache.IndexOf("internal sealed class Lease : IDisposable", StringComparison.Ordinal);
        int cloneStart = bitmapCache.IndexOf("internal Lease? TryClone()", leaseStart, StringComparison.Ordinal);
        int leaseDisposeStart = bitmapCache.IndexOf("public void Dispose()", cloneStart, StringComparison.Ordinal);
        Assert.True(leaseStart >= 0 && cloneStart > leaseStart && leaseDisposeStart > cloneStart);
        string cloneLease = bitmapCache[cloneStart..leaseDisposeStart];
        Assert.Contains("Entry? entry = Volatile.Read(ref _entry);", cloneLease, StringComparison.Ordinal);
        Assert.Contains("if (!entry.TryAcquire())\n                return null;", cloneLease, StringComparison.Ordinal);
        Assert.Contains("clone.Attach(entry);\n            return clone;", cloneLease, StringComparison.Ordinal);
        string leaseDisposal = bitmapCache[leaseDisposeStart..];
        Assert.Contains("Interlocked.Exchange(ref _entry, null)?.Release();", leaseDisposal, StringComparison.Ordinal);

        int showStart = source.IndexOf("private bool ShowSelectionContextMenu", StringComparison.Ordinal);
        int closeStart = source.IndexOf("private void CloseSelectionContextMenu", showStart, StringComparison.Ordinal);
        Assert.True(showStart >= 0 && closeStart > showStart);
        string menuLifecycle = source[showStart..closeStart];
        int closeExisting = menuLifecycle.IndexOf("CloseSelectionContextMenu();", StringComparison.Ordinal);
        int currentCheckAfterClose = menuLifecycle.IndexOf("!IsCurrentContextMenuSnapshot(", closeExisting, StringComparison.Ordinal);
        int createMenu = menuLifecycle.IndexOf("var menu = new MenuFlyout();", closeExisting, StringComparison.Ordinal);
        Assert.True(closeExisting >= 0 && currentCheckAfterClose > closeExisting && createMenu > currentCheckAfterClose);
        Assert.Contains("ContextMenuTarget target = new();", menuLifecycle, StringComparison.Ordinal);
        Assert.Matches(
            "if \\(!ReferenceEquals\\(_selectionContextMenu, menu\\)\\)\\s+return;",
            menuLifecycle);
        Assert.Matches(
            "if\\s*\\(touchSelection(?:\\s*&&[^)]*)?\\)\\s*_touchSelection\\.OpenContextMenu\\(\\);",
            menuLifecycle);

        int executeStart = source.IndexOf("private async Task<bool> ExecuteTargetCommandOrDefaultAsync(", StringComparison.Ordinal);
        int executeEnd = source.IndexOf("private static bool CopyTextTargetToClipboard", executeStart, StringComparison.Ordinal);
        Assert.True(executeStart >= 0 && executeEnd > executeStart);
        string execution = source[executeStart..executeEnd];
        int hostDispatch = execution.IndexOf("MarkdownTargetCommandDispatcher.Execute(CommandProvider, action)", StringComparison.Ordinal);
        int fallback = execution.IndexOf("return action.Kind switch", StringComparison.Ordinal);
        Assert.True(hostDispatch >= 0 && fallback > hostDispatch);
        Assert.Contains("if (dispatch == MarkdownTargetCommandDispatchResult.Executed)\n                return true;", execution, StringComparison.Ordinal);
        Assert.Contains("if (dispatch == MarkdownTargetCommandDispatchResult.Disabled)\n                return false;", execution, StringComparison.Ordinal);
        Assert.Contains("MarkdownCommandKind.CopyImage when target.ImageUrl is not null ||\n                                               target.ImageBitmapLease is not null", execution, StringComparison.Ordinal);
        int imageCommandBranch = execution.IndexOf("MarkdownCommandKind.CopyImage when target.ImageUrl", StringComparison.Ordinal);
        Assert.True(imageCommandBranch >= 0);
        int imageCopyCall = execution.IndexOf("await CopyImageTargetToClipboardAsync(", imageCommandBranch, StringComparison.Ordinal);
        Assert.True(imageCopyCall > imageCommandBranch);
        int leasedBitmapArgument = execution.IndexOf("target.ImageBitmapLease?.Bitmap", imageCopyCall, StringComparison.Ordinal);
        Assert.True(imageCommandBranch >= 0 && imageCopyCall > imageCommandBranch && leasedBitmapArgument > imageCopyCall);
        Assert.Contains("target.TableSnapshot is { } tableSnapshot", execution, StringComparison.Ordinal);
        Assert.Contains("_ => false,", execution, StringComparison.Ordinal);

        int retire = source.IndexOf("ctx.RegisterRetirement(RetireSnapshot(old));", StringComparison.Ordinal);
        int closeBeforeRetire = source.LastIndexOf("CloseSelectionContextMenu();", retire, StringComparison.Ordinal);
        Assert.True(closeBeforeRetire >= 0 && closeBeforeRetire < retire);

        int imageCopyStart = source.IndexOf("private static async Task<bool> CopyImageTargetToClipboardAsync", StringComparison.Ordinal);
        int tableCopyStart = source.IndexOf("private static bool CopyTableTargetToClipboard", imageCopyStart, StringComparison.Ordinal);
        Assert.True(imageCopyStart >= 0 && tableCopyStart > imageCopyStart);
        string imageCopy = source[imageCopyStart..tableCopyStart];
        int borrowedBitmap = imageCopy.IndexOf("if (bitmap is { } sourceBitmap)", StringComparison.Ordinal);
        int saveBitmap = imageCopy.IndexOf("await sourceBitmap.SaveAsync(", borrowedBitmap, StringComparison.Ordinal);
        Assert.True(borrowedBitmap >= 0 && saveBitmap > borrowedBitmap);
        Assert.Contains("using var stream = new InMemoryRandomAccessStream();", imageCopy, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceBitmap.Dispose()", imageCopy, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", imageCopy, StringComparison.Ordinal);
    }

    [Fact]
    public void TopReadmeAuditBuildsAndRunsTheSameHarnessPlatform()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "Invoke-TopReadmeAudit.ps1"));

        Assert.Contains("""$automationBuildArguments += @("-r", "win-x64")""", script, StringComparison.Ordinal);
        Assert.Contains("# Build the contract-enforcing harness even when the shipping NativeAOT app is",
            script, StringComparison.Ordinal);
        Assert.Contains(
            "net10.0-windows10.0.19041.0$runnerRidSegment",
            script,
            StringComparison.Ordinal);
        Assert.Contains("[string]$Configuration = \"Release\"", script, StringComparison.Ordinal);
        Assert.Contains("[string]$AppExecutablePath = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("-not $SkipBuild", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Path]::GetFullPath($AppExecutablePath)", script, StringComparison.Ordinal);
        Assert.Contains("(Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash.ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.Contains("if ($provenanceVerified)", script, StringComparison.Ordinal);
        Assert.Contains("--verified-native-aot-provenance=$provenanceSidecarPath", script, StringComparison.Ordinal);
        Assert.Contains("--verified-native-aot-provenance-sha256=$nativeAotProvenanceSha256", script, StringComparison.Ordinal);
        Assert.Contains("$dependencyAssemblySha256 = [ordered]@{}", script, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $appDirectory -File -Filter '*.dll' | Sort-Object Name", script, StringComparison.Ordinal);
        Assert.Contains("dependencyAssemblySha256 = $dependencyAssemblySha256", script, StringComparison.Ordinal);
        Assert.Contains("foreach ($assemblyName in @('JitHub.WinUI.dll', 'MarkdownRenderer.dll'))", script, StringComparison.Ordinal);
        Assert.Contains("if (-not $dependencyAssemblySha256.Contains($assemblyName))", script, StringComparison.Ordinal);
        Assert.Contains("if ($NativeAotArtifact)", script, StringComparison.Ordinal);
        Assert.Contains("Verify-NativeAotArtifact.ps1", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFlavor = if ($NativeAotArtifact) { 'native-aot' } else { 'managed' }", script, StringComparison.Ordinal);
        Assert.DoesNotContain("managedAssemblySha256", script, StringComparison.Ordinal);
        Assert.Contains("app-binary-ranks-{0}-{1}.json", script, StringComparison.Ordinal);
        Assert.Contains("--app=$appPath", script, StringComparison.Ordinal);
        Assert.Contains("-p:SkipReleaseSecurityGate=true", script, StringComparison.Ordinal);
        Assert.Contains("--reuse-browser-evidence", script, StringComparison.Ordinal);
        Assert.Contains("$nativeTraversalInactivityWatchdog = [TimeSpan]::FromSeconds(90)", script, StringComparison.Ordinal);
        Assert.Contains("^README native audit stage: (.+)\\.$", script, StringComparison.Ordinal);
        Assert.Contains("$lastNativeProgressStage = \"native traversal complete\"", script, StringComparison.Ordinal);
        Assert.Contains("^README native audit complete\\.$", script, StringComparison.Ordinal);
        Assert.Contains("^README audit .+: (?:passed|failed);", script, StringComparison.Ordinal);
        Assert.Contains("lastProgress = $Progress", script, StringComparison.Ordinal);
        Assert.Contains("-Progress $lastNativeProgressStage", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ReadmeAuditProcessTree -Process $Process", script, StringComparison.Ordinal);
        Assert.Contains("$live.CreationDate -eq $entry.CreationDate", script, StringComparison.Ordinal);
        Assert.Contains("$live.CreationDate -eq $_.CreationDate", script, StringComparison.Ordinal);
        Assert.Contains("-TimeoutKind \"post-exit-pipe-drain\"", script, StringComparison.Ordinal);
        Assert.Contains("processTreeTerminated = $ProcessTreeTerminated", script, StringComparison.Ordinal);
        Assert.Contains("failureCategory = \"infrastructure-timeout\"", script, StringComparison.Ordinal);
        Assert.Contains("performanceGateEvaluated = $false", script, StringComparison.Ordinal);
        Assert.Contains("watchdog-failure-$timestamp-$suffix.json", script, StringComparison.Ordinal);

        string nativeProbe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));
        Assert.Contains("WriteNativeAuditStage(\"UIA ItemStatus read starting\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"acquiring TextPattern\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"TextPatternRange.GetText first chunk\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"waiting for app-ready signal\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"attaching UI Automation application\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"waiting for app window\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"waiting for Markdown host\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"waiting for render-complete signal\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(\"README native audit complete.\")", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WriteNativeAuditStage(\"checking host ScrollPattern support\")", nativeProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void TopReadmeAuditShutdownAvoidsBlockingUiaWindowClose()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));

        Assert.Contains("NativeMethods.TryRequestGracefulClose(appProcess.Id, out _)", source, StringComparison.Ordinal);
        Assert.Contains("appProcess.WaitForExit(12_000)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Close();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TopReadmeWorkflowRequiresACompleteConsolidatedCorpus()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "markdown-readme-top500.yml"));
        string merger = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "Merge-TopReadmeAudit.ps1"));
        string manifestGenerator = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "New-TopReadmeManifest.ps1"));
        string runtimeInstaller = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "Install-PinnedWindowsAppRuntime.ps1"));

        Assert.Contains("Qualify same-byte top-500 release gate", workflow, StringComparison.Ordinal);
        Assert.Contains("Consolidate live top-500 diagnostics (not release qualified)", workflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("merge-multiple: true", workflow, StringComparison.Ordinal);
        Assert.Contains("-ExpectedCount 500", workflow, StringComparison.Ordinal);
        Assert.Contains("$shardCount = ${{ matrix.end }} - ${{ matrix.start }} + 1", workflow, StringComparison.Ordinal);
        Assert.Contains("Count = $shardCount", workflow, StringComparison.Ordinal);
        Assert.Contains(".\\eng\\Invoke-TopReadmeAudit.ps1 @arguments", workflow, StringComparison.Ordinal);
        // GitHub's Windows checkout may use CRLF even when the local checkout
        // uses LF. Validate both representations, including exact rank coverage.
        string lfWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string candidate in new[] { lfWorkflow, lfWorkflow.Replace("\n", "\r\n", StringComparison.Ordinal) })
        {
            MatchCollection shardRanges = Regex.Matches(
                candidate,
                @"(?m)^[ \t]*- \{ start: (?<start>\d+), end: (?<end>\d+) \}[ \t]*\r?$");
            Assert.NotEmpty(shardRanges);
            int nextRank = 1;
            foreach (Match shard in shardRanges)
            {
                int start = int.Parse(shard.Groups["start"].Value);
                int end = int.Parse(shard.Groups["end"].Value);
                Assert.Equal(nextRank, start);
                Assert.InRange(end - start + 1, 1, 25);
                nextRank = end + 1;
            }
            Assert.Equal(501, nextRank);
        }
        Assert.Contains("$cases.Count -ne $ExpectedCount", merger, StringComparison.Ordinal);
        Assert.Contains("Native first-render p95", merger, StringComparison.Ordinal);
        Assert.Contains("Native full-page p95", merger, StringComparison.Ordinal);
        Assert.Contains("enforceAggregateGates", File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs")), StringComparison.Ordinal);
        Assert.Contains(".\\eng\\readme-audit\\Install-PinnedWindowsAppRuntime.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("B8CDA840267AB72797F654F801F9A064AB6D9E508CEDEE3DF79F772F104DB6D6", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8'", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name != 'workflow_dispatch' || inputs.diagnostic_rank == 0", workflow, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name == 'workflow_dispatch' && inputs.diagnostic_rank != 0", workflow, StringComparison.Ordinal);
        Assert.Contains("$rank -lt 1 -or $rank -gt 500", workflow, StringComparison.Ordinal);
        Assert.Contains("diagnostic_prior_cases:", workflow, StringComparison.Ordinal);
        Assert.Contains("$priorCases -lt 0 -or $priorCases -gt 4 -or $priorCases -ge $rank", workflow, StringComparison.Ordinal);
        Assert.Contains("StartRank = [int]$env:AUDIT_VALIDATED_RANK - [int]$env:AUDIT_VALIDATED_PRIOR_CASES", workflow, StringComparison.Ordinal);
        Assert.Contains("Count = [int]$env:AUDIT_VALIDATED_PRIOR_CASES + 1", workflow, StringComparison.Ordinal);
        Assert.Contains("-ExpectedCount 500 -MaximumAgeDays 14", workflow, StringComparison.Ordinal);
        Assert.Contains("AUDIT_REQUIRE_CURRENT_CORPUS: ${{ (github.event_name == 'workflow_dispatch' || github.event_name == 'workflow_call') && inputs.same_byte_release }}", workflow, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name == 'workflow_call' && inputs.same_byte_release != true", workflow, StringComparison.Ordinal);
        Assert.Contains("summary_sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($env:AUDIT_REQUIRE_CURRENT_CORPUS -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("$attempt -le 4", manifestGenerator, StringComparison.Ordinal);
        Assert.Contains("GitHub repository search rate limit exceeded for page", manifestGenerator, StringComparison.Ordinal);
        Assert.Contains("$allowNotFound -and $text -match 'HTTP 404'", manifestGenerator, StringComparison.Ordinal);
        Assert.Contains("$text -match 'IP allow list enabled'", manifestGenerator, StringComparison.Ordinal);
        Assert.Contains("Invoke-PublicGitHubJson $route $ref $allowNotFound", manifestGenerator, StringComparison.Ordinal);
        Assert.Contains("Could not pin a commit", manifestGenerator, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not pin a commit and README", manifestGenerator, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalMarkdownReleaseRequiresPassedSameRunTop500AndCleanSourceProvenance()
    {
        string root = FindRepositoryRoot();
        string physicalWorkflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "markdown-renderer-physical-release.yml"));
        string top500Workflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "markdown-readme-top500.yml"));
        string provenanceScript = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "eng",
            "New-MarkdownRendererSourceProvenance.ps1"));
        string referenceValidator = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "eng",
            "Test-MarkdownRendererReleaseReference.ps1"));
        string counterbalancedScript = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "eng",
            "Invoke-MarkdownWinUICounterbalancedPerformance.ps1"));
        string joinScript = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "eng",
            "Test-MarkdownRendererReleaseEvidence.ps1"));
        string top500OutputPublisher = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "eng",
            "Publish-MarkdownRendererTop500WorkflowOutputs.ps1"));

        physicalWorkflow = physicalWorkflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("needs: [validate_release_input, same_byte_top500]", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("MARKDOWN_RENDERER_RELEASE_REFERENCE_SHA", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("SchemaVersion\\s*=\\s*12", referenceValidator, StringComparison.Ordinal);
        Assert.Contains("reference_commit_sha: ${{ steps.validate.outputs.reference_commit_sha }}", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("needs: [validate_release_input]\n    if: needs.validate_release_input.result == 'success'", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("needs.same_byte_top500.result == 'success'", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("needs.same_byte_top500.outputs.passed == 'true'", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("needs.same_byte_top500.outputs.consolidated_artifact_name != ''", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("if: always()\n    needs: [validate_release_input, physical, same_byte_top500]", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("if: needs.physical.result == 'success' && needs.same_byte_top500.result == 'success'", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: [self-hosted, Windows, X64, markdown-renderer-physical]", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("Hardware identity assurance is operator-configured", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("bare metal is not independently attested", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("Microsoft Windows 11(?:\\s|$)", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("$sessionId -eq 0", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("markdown-renderer-release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("Refusing to reuse pre-existing release evidence directory", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("MARKDOWN_RENDERER_RELEASE_ROOT=$directory", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("markdown-renderer-counterbalanced-$env:GITHUB_RUN_ATTEMPT", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("artifact_name: ${{ steps.physical_artifact_name.outputs.name }}", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: ${{ steps.physical_artifact_name.outputs.name }}", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: ${{ needs.physical.outputs.artifact_name }}", physicalWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("name: markdown-renderer-counterbalanced-${{ github.run_attempt }}", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("markdown-renderer-release-${{ github.run_id }}-${{ github.run_attempt }}/**", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: Test the joined-evidence validator against release mutations\n        working-directory: markdown-renderer-candidate", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("-RequireSourceProvenance", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/markdown-readme-top500.yml", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("same_byte_release: true", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("workflow_call:", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("readme-top500-corpus-attempt-$corpusRunAttempt", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("name: readme-native-aot-app${{ (github.event_name == 'workflow_call'", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("github.event_name == 'workflow_dispatch' && inputs.same_byte_release", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("readme-audit-evidence-*${{ (github.event_name == 'workflow_call'", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("diagnostic_corpus_run_attempt:", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("AUDIT_CORPUS_RUN_ATTEMPT", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("name: ${{ needs.same_byte_top500.outputs.consolidated_artifact_name }}", physicalWorkflow, StringComparison.Ordinal);
        Assert.Contains("value: ${{ jobs.consolidate.outputs.consolidated_artifact_name }}", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("readme-audit-visual-${{ matrix.start }}-${{ matrix.end }}${{ (github.event_name == 'workflow_call'", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("Publish-MarkdownRendererTop500WorkflowOutputs.ps1", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("if ($summaryPassed -isnot [bool])", top500OutputPublisher, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredJsonProperty $summary 'sameByteRequired'", top500OutputPublisher, StringComparison.Ordinal);
        Assert.Contains("-isnot [bool]", top500OutputPublisher, StringComparison.Ordinal);
        Assert.Contains("required: true\n        type: boolean", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("AUDIT_REQUIRE_CURRENT_CORPUS:", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("same_byte_cases:", top500Workflow, StringComparison.Ordinal);
        Assert.Contains("'status', '--porcelain=v1', '--untracked-files=all'", provenanceScript, StringComparison.Ordinal);
        Assert.Contains("workingTreeClean = $true", provenanceScript, StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", provenanceScript, StringComparison.Ordinal);
        Assert.Contains("Get-VerifiedSourceProvenance", counterbalancedScript, StringComparison.Ordinal);
        Assert.Contains("sourceProvenance = $sourceProvenance", counterbalancedScript, StringComparison.Ordinal);
        Assert.Contains("$Top500JobResult -cne 'success'", joinScript, StringComparison.Ordinal);
        Assert.Contains("Assert-JsonTrue -Value $physical.complete", joinScript, StringComparison.Ordinal);
        Assert.Contains("Assert-JsonTrue -Value $physical.passed", joinScript, StringComparison.Ordinal);
        Assert.Contains("Assert-JsonTrue -Value $top.passed", joinScript, StringComparison.Ordinal);
        Assert.Contains("Assert-JsonTrue -Value $build.provenanceVerified", joinScript, StringComparison.Ordinal);
        Assert.Contains("physicalRunnerAttestation = 'operator-configured'", joinScript, StringComparison.Ordinal);
        Assert.Contains("bareMetalIndependentlyAttested = $false", joinScript, StringComparison.Ordinal);
        Assert.Contains("[int]$top.totalCases -ne 500", joinScript, StringComparison.Ordinal);
        Assert.Contains("[int]$top.passedCases -ne 500", joinScript, StringComparison.Ordinal);
        Assert.Contains("[int]$top.sameByteCases -ne 500", joinScript, StringComparison.Ordinal);
        Assert.Contains("[int]$top.sameByteExpectedCases -ne 500", joinScript, StringComparison.Ordinal);
        Assert.Contains("[string]$build.sourceCommitSha -ine $candidateSha", joinScript, StringComparison.Ordinal);
        Assert.Contains("$topEvidence.Sha256 -cne $Top500WorkflowSummarySha256.ToLowerInvariant()", joinScript, StringComparison.Ordinal);
    }

    [Fact]
    public void TopReadmeAuditComparesEquivalentAccessibleImageTextAndWaitsForLinkedImages()
    {
        string root = FindRepositoryRoot();
        string browserOracle = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "browser-oracle.mjs"));
        string nativeProbe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));

        Assert.Contains("hasExplicitAlt: image.hasAttribute(\"alt\")", browserOracle, StringComparison.Ordinal);
        Assert.Contains("!image.hasExplicitAlt ? \"Image\" : \"\"", browserOracle, StringComparison.Ordinal);
        Assert.Contains("text: accessibleText", browserOracle, StringComparison.Ordinal);
        Assert.Contains("visibleText: clean(article.innerText)", browserOracle, StringComparison.Ordinal);
        Assert.Contains("visibleMermaidSources", browserOracle, StringComparison.Ordinal);
        Assert.Contains("schemaVersion: 5", browserOracle, StringComparison.Ordinal);
        Assert.Contains("data-type=\"mermaid\"", browserOracle, StringComparison.Ordinal);
        Assert.Contains("isImageSelfLink", browserOracle, StringComparison.Ordinal);
        Assert.Contains("data-canonical-src", browserOracle, StringComparison.Ordinal);
        Assert.Contains("github-asset://", browserOracle, StringComparison.Ordinal);
        Assert.Contains("WaitForVisibleImages(host", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("IsVisibleRenderedBrowserImage", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("ReadAutomationString", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("browserDistinctLinks", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MarkdownLinkedImage", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("automation-mermaid-sources.json", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MatchEquivalentMermaidSources", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MatchedMermaidTransformations", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("Image-only anchors are links too", nativeProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("link => !string.IsNullOrWhiteSpace(link.Text)", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("double textFidelity = textCoverage", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("Rectangle.Intersect(", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("RequestRendererCapture", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("save: false", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WaitForVisibleImages(host", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("if (!repository.Readme.Available)", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("WaitForSourceEditorOrRenderedHost", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("RepoCodeFileTree", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("if (result.InfrastructureFailure)", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("PreserveStartupDiagnostics(dataRoot, output, launcher)", nativeProbe, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(
            nativeProbe,
            "PreserveShutdownExceptionDiagnostics\\(dataRoot, output\\)").Count);
        Assert.Contains("app-exit-timeout-12s", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("app-exit-code-0x", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("OpenProcessExitHandle(appProcess.Id)", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("GetProcessExitCode(appExitHandle)", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("CloseProcessExitHandle(appExitHandle)", nativeProbe, StringComparison.Ordinal);
        Assert.True(
            nativeProbe.IndexOf("OpenProcessExitHandle(appProcess.Id)", StringComparison.Ordinal) <
            nativeProbe.IndexOf("NativeMethods.TryRequestGracefulClose(appProcess.Id, out _)", StringComparison.Ordinal));
        Assert.DoesNotContain("appProcess.ExitCode", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("launcher-exit-code-0x", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("CloseFailure = close.Failure", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("PreserveEvidenceFile(shutdownStageEvidence", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_SVG_PREFLIGHT_EVIDENCE_PATH", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("PreserveEvidenceFile(svgPreflightEvidence", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("startup-process.txt", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("await navigateReadme(", browserOracle, StringComparison.Ordinal);
        string browserNavigation = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "browser-navigation.mjs"));
        Assert.Contains("attempt < 2", browserNavigation, StringComparison.Ordinal);
        Assert.Contains("previousTimeOrigin, 60_000", browserNavigation, StringComparison.Ordinal);
        Assert.Contains("await Promise.race([", browserOracle, StringComparison.Ordinal);
        Assert.Contains("cdp.send(\"Browser.close\")", browserOracle, StringComparison.Ordinal);
        Assert.DoesNotContain("cdp.once(\"Page.loadEventFired\"", browserOracle, StringComparison.Ordinal);
        Assert.Contains("process.WaitForExit(600_000)", nativeProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableImageEvidenceUsesHashedIdentityAndRetainsLegacyReadSupport()
    {
        string root = FindRepositoryRoot();
        string bridge = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLifecycleAutomationBridge.cs"));
        string probe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));

        int writerStart = bridge.IndexOf("public static void RecordImageUnavailable(", StringComparison.Ordinal);
        int writerEnd = bridge.IndexOf("public static void SignalShutdownStage(", writerStart, StringComparison.Ordinal);
        Assert.True(writerStart >= 0 && writerEnd > writerStart);
        string writer = bridge[writerStart..writerEnd];
        Assert.Contains("HashSourceIdentity(source)", writer, StringComparison.Ordinal);
        Assert.Contains("GetSourceKind(source)", writer, StringComparison.Ordinal);
        Assert.Contains("GetSourceMatchSha256(source)", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("new ImageUnavailableSignal(\n                        Environment.ProcessId,\n                        automationId,\n                        source,", writer, StringComparison.Ordinal);

        int signalStart = bridge.IndexOf("private sealed record ImageUnavailableSignal(", StringComparison.Ordinal);
        int signalEnd = bridge.IndexOf("private sealed record ImageResolutionSignal(", signalStart, StringComparison.Ordinal);
        Assert.True(signalStart >= 0 && signalEnd > signalStart);
        string signal = bridge[signalStart..signalEnd];
        Assert.Contains("string SourceSha256", signal, StringComparison.Ordinal);
        Assert.Contains("string[] SourceMatchSha256", signal, StringComparison.Ordinal);
        Assert.DoesNotContain("string Source,", signal, StringComparison.Ordinal);

        int countStart = probe.IndexOf("private static int CountRenderedUnavailableImages(", StringComparison.Ordinal);
        int countEnd = probe.IndexOf("private static IEnumerable<string> GetImageSourceMatchHashes(", countStart, StringComparison.Ordinal);
        Assert.True(countStart >= 0 && countEnd > countStart);
        string consumer = probe[countStart..countEnd];
        Assert.Contains("SourceSha256", consumer, StringComparison.Ordinal);
        Assert.Contains("SourceMatchSha256", consumer, StringComparison.Ordinal);
        Assert.Contains("sourceMatchHashes.Overlaps(visibleSourceMatchHashes)", consumer, StringComparison.Ordinal);
        Assert.Contains("legacySource", consumer, StringComparison.Ordinal);
        Assert.Contains("ImageSourcesReferToSameAsset(source, candidate)", consumer, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceBoundReplayFailuresPersistOnlyBoundedTypedDiagnostics()
    {
        string root = FindRepositoryRoot();
        string sourceReplay = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "same-byte-edge-source.mjs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string automationProbe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string offlineAutomationProbe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "OfflineSameByteReadmeAuditProbe.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        int cliStart = sourceReplay.IndexOf("async function runCli()", StringComparison.Ordinal);
        int cliEnd = sourceReplay.IndexOf("if (process.argv[1]", cliStart, StringComparison.Ordinal);
        Assert.True(cliStart >= 0 && cliEnd > cliStart);
        string cli = sourceReplay[cliStart..cliEnd];
        int failureStart = cli.IndexOf("} catch (error) {", StringComparison.Ordinal);
        Assert.True(failureStart >= 0);
        int failureEnd = cli.IndexOf("} finally {", failureStart, StringComparison.Ordinal);
        Assert.True(failureEnd > failureStart);
        string successPath = cli[..failureStart];
        string failurePath = cli[failureStart..failureEnd];
        Assert.Contains("createSourceReplayFailureReport(error)", cli, StringComparison.Ordinal);
        Assert.Contains("pendingReport = report;", successPath, StringComparison.Ordinal);
        Assert.DoesNotContain("writeFile(reportPath", successPath, StringComparison.Ordinal);
        Assert.DoesNotContain("process.stdout.write", successPath, StringComparison.Ordinal);
        Assert.DoesNotContain("process.stderr.write", successPath, StringComparison.Ordinal);
        Assert.DoesNotContain("error?.stack", failurePath, StringComparison.Ordinal);
        Assert.DoesNotContain("report: reportPath", failurePath, StringComparison.Ordinal);

        int finalReportWrite = cli.IndexOf("await writeFile(reportPath,", failureEnd, StringComparison.Ordinal);
        Assert.True(finalReportWrite > failureEnd);
        string finalization = cli[finalReportWrite..];
        Assert.Contains("{ encoding: \"utf8\", flag: \"wx\" }", finalization, StringComparison.Ordinal);
        Assert.Contains("ok: true, report: reportPath, status: report.status", finalization, StringComparison.Ordinal);
        Assert.Contains("process.stderr.write(`${JSON.stringify({", finalization, StringComparison.Ordinal);
        Assert.Contains("failureCategory: report.failureCategory", finalization, StringComparison.Ordinal);
        Assert.DoesNotContain("error?.stack", finalization, StringComparison.Ordinal);

        int diagnosticStart = sourceReplay.IndexOf("export function createSourceReplayFailureDiagnostic(error)", StringComparison.Ordinal);
        int reportStart = sourceReplay.IndexOf("export function createSourceReplayFailureReport(error)", diagnosticStart, StringComparison.Ordinal);
        Assert.True(diagnosticStart >= 0 && reportStart > diagnosticStart);
        string diagnostic = sourceReplay[diagnosticStart..reportStart];
        Assert.Contains("message: SOURCE_REPLAY_FAILURE_MESSAGES[category]", diagnostic, StringComparison.Ordinal);
        Assert.Contains("const message = String(error?.message || error || \"\")", diagnostic, StringComparison.Ordinal);
        Assert.Contains("errorType: SOURCE_REPLAY_ERROR_TYPES.has(error?.sourceReplayErrorType)", diagnostic, StringComparison.Ordinal);
        Assert.Contains("failurePredicate,", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("error?.stack", diagnostic, StringComparison.Ordinal);

        int reportEnd = sourceReplay.IndexOf("export function sanitizeSourceReplayNativeMovementEvidence(", reportStart, StringComparison.Ordinal);
        Assert.True(reportEnd > reportStart);
        string report = sourceReplay[reportStart..reportEnd];
        Assert.Contains("failureCategory: diagnostic.category", report, StringComparison.Ordinal);
        Assert.Contains("error: diagnostic.message", report, StringComparison.Ordinal);
        Assert.DoesNotContain("error?.stack", report, StringComparison.Ordinal);
        Assert.DoesNotContain("error?.message", report, StringComparison.Ordinal);

        int categoryReaderStart = automationProbe.IndexOf("private static string ReadSourceBoundReplayFailureCategory(", StringComparison.Ordinal);
        int categoryReaderEnd = automationProbe.IndexOf("private static string GetSourceBoundReplayFailureCategory(", categoryReaderStart, StringComparison.Ordinal);
        Assert.True(categoryReaderStart >= 0 && categoryReaderEnd > categoryReaderStart);
        string categoryReader = automationProbe[categoryReaderStart..categoryReaderEnd];
        Assert.Contains("reportInfo.Length is <= 0 or > 4_194_304", categoryReader, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", categoryReader, StringComparison.Ordinal);
        Assert.Contains("root.GetProperty(\"schemaVersion\").GetInt32() != 2", categoryReader, StringComparison.Ordinal);
        Assert.Contains("root.GetProperty(\"status\").GetString() != \"failed\"", categoryReader, StringComparison.Ordinal);
        Assert.Contains("_ => \"worker-failure\"", categoryReader, StringComparison.Ordinal);
        Assert.Contains("catch\n        {\n            return \"worker-failure\";", categoryReader, StringComparison.Ordinal);

        Assert.Contains("GetSourceBoundReplayFailureCategory(exception)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("ReadSourceBoundReplayFailureCategory(reportPath)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("GetSourceBoundReplayPreflightFailureCategory(", automationProbe, StringComparison.Ordinal);
        Assert.Contains("return \"semantic-incomplete\"", automationProbe, StringComparison.Ordinal);
        Assert.Contains("return \"tail-coverage\"", automationProbe, StringComparison.Ordinal);
        Assert.Contains("throw new SourceBoundEdgeReplayException(\"text-coverage\")", automationProbe, StringComparison.Ordinal);
        Assert.Contains("return \"digest-mismatch\"", automationProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Source-bound Edge replay failed: \" + exception", automationProbe, StringComparison.Ordinal);
        Assert.Contains("sourceReplay.Semantic is { Complete: true }", automationProbe, StringComparison.Ordinal);
        Assert.Contains("TokenCoverage(sourceSemantic, comparableNativeText)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("browser.Semantic);", automationProbe, StringComparison.Ordinal);
        Assert.Contains("capturedGitHubArticleSemantic!.VisibleText", automationProbe, StringComparison.Ordinal);
        Assert.Contains("TryGetBidirectionalSourceArticleTextCoverage", automationProbe, StringComparison.Ordinal);
        Assert.Contains("HashSemanticText(semanticDigestKey, token)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("sourceToGitHubCoverage >= 0.985", automationProbe, StringComparison.Ordinal);
        Assert.Contains("gitHubToSourceCoverage >= 0.985", automationProbe, StringComparison.Ordinal);
        Assert.Contains("SourceReplayToCapturedGitHubVisibleTextTokenCoverage = sourceToGitHubCoverage", automationProbe, StringComparison.Ordinal);
        Assert.Contains("CapturedGitHubToSourceReplayVisibleTextTokenCoverage = gitHubToSourceCoverage", automationProbe, StringComparison.Ordinal);
        Assert.Contains("CountFidelity(capturedHeadingCount, sourceSemantic.HeadingCount)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("CountFidelity(capturedGitHubArticleSemantic.Tables, sourceSemantic.TableCount)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("CountFidelity(capturedGitHubArticleSemantic.TaskCheckboxes, sourceSemantic.TaskCheckboxCount)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("CountFidelity(capturedGitHubArticleSemantic.Details, sourceSemantic.DetailsCount)", automationProbe, StringComparison.Ordinal);
        Assert.Contains("double normalizedWeight = domainWeights[index] / totalWeight", automationProbe, StringComparison.Ordinal);
        Assert.Contains("CapturedGitHubSourceStructureScore = ComputeCapturedGitHubSourceStructureScore(", automationProbe, StringComparison.Ordinal);
        Assert.Contains("sourceStructureScore < 0.95", automationProbe, StringComparison.Ordinal);
        Assert.Contains("FidelityReference = sourceSemantic is null ? \"live-github-diagnostic\" : \"same-byte-source\"", automationProbe, StringComparison.Ordinal);
        Assert.Contains("TileSsimReference = \"live-github-cross-style-diagnostic-absolute-offset\"", automationProbe, StringComparison.Ordinal);
        Assert.Contains("SameByteTileSsimReference = sameByteSsimReference", automationProbe, StringComparison.Ordinal);
        Assert.Contains("tile.ScrollTopViewportUnits", automationProbe, StringComparison.Ordinal);
        Assert.Contains("same-byte-source-matched-viewport", automationProbe, StringComparison.Ordinal);
        Assert.Contains("replay.SchemaVersion != 3", automationProbe, StringComparison.Ordinal);
        Assert.Contains("TokenizationVersion != \"rune-l-n-mn-mc-han-nfc-simple-lower-invariant-v1\"", automationProbe, StringComparison.Ordinal);
        Assert.Contains("SemanticDigestKeyHex = semanticDigestKey", automationProbe, StringComparison.Ordinal);
        Assert.Contains("RemoveOfflineGitHubCredentialEnvironment(startInfo);", automationProbe, StringComparison.Ordinal);
        int credentialRemovalStart = offlineAutomationProbe.IndexOf(
            "private static void RemoveOfflineGitHubCredentialEnvironment(ProcessStartInfo startInfo)",
            StringComparison.Ordinal);
        int credentialRemovalEnd = offlineAutomationProbe.IndexOf("private sealed record OfflineSameByteAggregate(", credentialRemovalStart, StringComparison.Ordinal);
        Assert.True(credentialRemovalStart >= 0 && credentialRemovalEnd > credentialRemovalStart);
        string credentialRemoval = offlineAutomationProbe[credentialRemovalStart..credentialRemovalEnd];
        Assert.Contains("OfflineGitHubCredentialEnvironmentNames", credentialRemoval, StringComparison.Ordinal);
        Assert.Contains("startInfo.Environment.Remove(name);", credentialRemoval, StringComparison.Ordinal);
        Assert.Contains("\"JITHUB_README_AUDIT_GITHUB_TOKEN\"", offlineAutomationProbe, StringComparison.Ordinal);
        Assert.Contains("\"JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID\"", offlineAutomationProbe, StringComparison.Ordinal);
        Assert.Contains("\"JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY\"", offlineAutomationProbe, StringComparison.Ordinal);
        string liveBrowserOracle = File.ReadAllText(Path.Combine(root, "eng", "readme-audit", "browser-oracle.mjs"));
        string browserEnvironment = File.ReadAllText(Path.Combine(root, "eng", "readme-audit", "browser-environment.mjs"));
        Assert.Contains("createCredentialFreeBrowserEnvironment(process.env)", liveBrowserOracle, StringComparison.Ordinal);
        Assert.Contains("GITHUB_CREDENTIAL_ENVIRONMENT_NAMES", browserEnvironment, StringComparison.Ordinal);
        Assert.Contains("delete browserEnvironment[name]", browserEnvironment, StringComparison.Ordinal);
        Assert.Contains("Text = string.Empty", automationProbe, StringComparison.Ordinal);
        Assert.Contains("VisibleText = string.Empty", automationProbe, StringComparison.Ordinal);
        Assert.Contains("VisibleMermaidSources = []", automationProbe, StringComparison.Ordinal);
        int replaySanitizerStart = automationProbe.IndexOf(
            "private static BrowserSameByteReplayEvidence? SanitizeSameByteReplayEvidence(",
            StringComparison.Ordinal);
        int replaySanitizerEnd = automationProbe.IndexOf(
            "private static NativeAuditResult? SanitizeNativeAuditResult(",
            replaySanitizerStart,
            StringComparison.Ordinal);
        Assert.True(replaySanitizerStart >= 0 && replaySanitizerEnd > replaySanitizerStart);
        string replaySanitizer = automationProbe[replaySanitizerStart..replaySanitizerEnd];
        Assert.DoesNotContain("SemanticDigestKeyHex", replaySanitizer, StringComparison.Ordinal);
        Assert.DoesNotContain("VisibleTextTokenDigests =", replaySanitizer, StringComparison.Ordinal);
        Assert.Contains("SourceReplayToCapturedGitHubVisibleTextTokenCoverage =", replaySanitizer, StringComparison.Ordinal);
        Assert.Contains("CapturedGitHubToSourceReplayVisibleTextTokenCoverage =", replaySanitizer, StringComparison.Ordinal);
        Assert.Contains("CapturedGitHubSourceStructureScore =", replaySanitizer, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadmeAuditSummaryGatesOnCompleteSameByteRatiosAndFailsClosedOnIncompleteCapture()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));

        int markdownStart = source.IndexOf("private static void WriteSummaryMarkdown(", StringComparison.Ordinal);
        int markdownEnd = source.IndexOf("private static void TryOpenReadme(", markdownStart, StringComparison.Ordinal);
        Assert.True(markdownStart >= 0 && markdownEnd > markdownStart);
        string markdownWriter = source[markdownStart..markdownEnd];
        Assert.Contains("summary.PerformanceRatioReference", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Native/live GitHub Edge first-render ratio (diagnostic)", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Native/live GitHub Edge full-page ratio (diagnostic)", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("FormatSourceBoundIncomplete", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Same-byte first ratio", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Same-byte full ratio", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Same-byte viewport SSIM", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Same-byte visual coverage", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Live GitHub cross-style SSIM (diagnostic)", markdownWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("Styled viewport SSIM", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Live GitHub first ratio (diagnostic)", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("Live GitHub full ratio (diagnostic)", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("NativeToSameByteFirstRenderRatio?.ToString", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("NativeToSameByteFullPageRatio?.ToString", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("NativeToBrowserFirstRenderRatio.ToString", markdownWriter, StringComparison.Ordinal);
        Assert.Contains("NativeToBrowserFullPageRatio.ToString", markdownWriter, StringComparison.Ordinal);

        int summaryStart = source.IndexOf("private static ReadmeAuditSummary BuildSummary(", StringComparison.Ordinal);
        int summaryEnd = source.IndexOf("private static void WriteSummaryMarkdown(", summaryStart, StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && summaryEnd > summaryStart);
        string gateCalculation = source[summaryStart..summaryEnd];
        Assert.Contains("result.Comparison!.NativeToBrowserFirstRenderRatio", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("result.Comparison!.NativeToBrowserFullPageRatio", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("NativeToSameByteFirstRenderRatio", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("NativeToSameByteFullPageRatio", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("native.FullTraversalMs / fullReplay.Timing.ChargedTraversalMs", source, StringComparison.Ordinal);
        Assert.Contains("FullTraversalMs - replay.Timing.ChargedTraversalMs", source, StringComparison.Ordinal);
        Assert.Contains("AuditOnlyFrameWaitCount is < 2", source, StringComparison.Ordinal);
        Assert.Contains("public double ChargedTraversalMs", source, StringComparison.Ordinal);
        Assert.Contains("public double AuditOnlyFrameWaitMs", source, StringComparison.Ordinal);
        Assert.Contains("public BrowserSameByteHtmlReplayTiming Timing", source, StringComparison.Ordinal);
        Assert.Contains("internal sealed class BrowserSameByteHtmlReplayTiming", source, StringComparison.Ordinal);
        Assert.Contains("raw traversal clock is not the source-bound performance denominator", source, StringComparison.Ordinal);
        Assert.Contains("bool sameByteRatiosRequired = auditCaptureSameByteCorpus || hasSameByteEvidence", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("sameByteFirstRatios.Length == comparisonResults.Length", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("sameByteFullRatios.Length == comparisonResults.Length", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("double[] firstRatios = sourceBoundRatiosIncomplete", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("double[] fullRatios = sourceBoundRatiosIncomplete", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("? []", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("useSameByteRatios ? sameByteFirstRatios : liveFirstRatios", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("live GitHub ratios were not substituted", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("!sourceBoundRatiosIncomplete && firstP95 > 1.10", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("!sourceBoundRatiosIncomplete && fullP95 > 1.10", gateCalculation, StringComparison.Ordinal);
        Assert.Contains("PerformanceRatioReference = performanceRatioReference", gateCalculation, StringComparison.Ordinal);
    }

    [Fact]
    public void SameByteFullTraversalUsesOneBoundedOverlappingViewportSchedule()
    {
        string root = FindRepositoryRoot();
        string nativeProbe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));
        string sharedSchedule = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "same-byte-traversal.mjs"));
        string capturedHtmlReplay = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "same-byte-edge-replay.mjs"));
        string sourceBoundReplay = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "readme-audit",
            "same-byte-edge-source.mjs"));
        int traversalStart = nativeProbe.IndexOf("private static NativeTraversalResult CaptureNativeTiles(", StringComparison.Ordinal);
        int traversalEnd = nativeProbe.IndexOf("private static double RevisitPendingImages(", traversalStart, StringComparison.Ordinal);

        Assert.True(traversalStart >= 0 && traversalEnd > traversalStart);
        string nativeTraversal = nativeProbe[traversalStart..traversalEnd];
        Assert.Contains("SameByteTraversalViewportStepRatio = 0.9", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MaximumNativeScrollViewportFraction = 0.95", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MaximumTraversalViewports = 512", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MaximumNativeScrollAttempts = 3", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("MaximumNativeViewportPositions", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("UiaProviderTransactionTimeout = TimeSpan.FromSeconds(8)", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("automation.TransactionTimeout = UiaProviderTransactionTimeout", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO = 0.9", sharedSchedule, StringComparison.Ordinal);
        Assert.Contains("MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS = 512", sharedSchedule, StringComparison.Ordinal);
        Assert.Contains("innerHeight * viewportStepRatio", capturedHtmlReplay, StringComparison.Ordinal);
        Assert.Contains("nativeViewportProfile.viewports", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("scrollTo(0, expectedTop * innerHeight)", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("movementOffsetsViewportUnits", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("captureOffsetsViewportUnits", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("auditOnlyFrameWaitMs += passFrameWaitMs", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("chargedTraversalMs = fullTraversalMs - auditOnlyFrameWaitMs", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("int index = tiles.Count", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("pendingViewportMovementOffsets.Add(scrollTopViewportUnits)", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("positionCorrectionCount++", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("RequestRendererScroll(", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("for (int attempt = 0; attempt < MaximumNativeScrollAttempts; attempt++)", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("scroll = host.Patterns.Scroll.Pattern;", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("scrollChange.ElapsedMs - scrollChange.ProbeOverheadMs", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("scroll.VerticalScrollPercent.ValueOrDefault", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("scroll.VerticalViewSize.ValueOrDefault", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("GetScrollTopViewportUnits(actual, currentViewSize)", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("automationProbe.Elapsed.TotalMilliseconds -", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("scrollResponse.ScrollOperationMilliseconds", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("correctionScrollResponse.ScrollOperationMilliseconds", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("NativeScrollTerminalContract.IsTerminalNoOpCandidate(", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("NativeScrollTerminalContract.IsStableTerminalConfirmation(", nativeTraversal, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Abs(settledViewSize - currentViewSize)", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("WriteNativeViewportMovementFailure(", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("native-viewport-movement-failure.json", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("movementSinceCaptureUnits", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("lastCorrectionDocumentTopDip", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("movementObserved = true;", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("after {MaximumNativeScrollAttempts} in-app scroll attempts.", nativeTraversal, StringComparison.Ordinal);
        Assert.DoesNotContain("scroll.Scroll(", nativeProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("SetScrollPercent", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("scrollViewportFraction: viewportFraction", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("double? ScrollViewportFraction = null", nativeProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollNativeHostPhysically", nativeProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.Scroll", nativeProbe, StringComparison.Ordinal);
        int requestStart = nativeProbe.IndexOf(
            "private static RendererCaptureResponse RequestRendererCapture(",
            StringComparison.Ordinal);
        int requestEnd = nativeProbe.IndexOf(
            "private static RendererCaptureResponse RequestRendererScroll(",
            requestStart,
            StringComparison.Ordinal);
        Assert.True(requestStart >= 0 && requestEnd > requestStart);
        string requestHelper = nativeProbe[requestStart..requestEnd];
        Assert.Contains("if (scrollViewportFraction is not null)", requestHelper, StringComparison.Ordinal);
        Assert.Contains("did not acknowledge the one-shot audit scroll request", requestHelper, StringComparison.Ordinal);
        Assert.Contains("sendAttempted = true", requestHelper, StringComparison.Ordinal);
        Assert.Contains("failed after its atomic send was attempted", requestHelper, StringComparison.Ordinal);
        Assert.Contains("ScrollOperationMilliseconds", nativeProbe, StringComparison.Ordinal);
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string lifecycleBridge = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLifecycleAutomationBridge.cs"));
        Assert.Contains("TryGetAuditScrollViewportFraction", viewer, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.HasThreadAccess", viewer, StringComparison.Ordinal);
        Assert.Contains("scrollViewer.ChangeView(", viewer, StringComparison.Ordinal);
        Assert.Contains("disableAnimation: true", viewer, StringComparison.Ordinal);
        Assert.Contains("FindAncestorVerticalScrollViewer", viewer, StringComparison.Ordinal);
        Assert.Contains("FindDescendantVerticalScrollViewer", viewer, StringComparison.Ordinal);
        Assert.Contains("MaximumAuditScrollViewportFraction = 0.95", lifecycleBridge, StringComparison.Ordinal);
        Assert.Contains("if (!_productionAuditEnabled)", lifecycleBridge, StringComparison.Ordinal);
        Assert.Contains("NativeScrollTerminalContract.IsTerminalNoOpCandidate(", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("NativeScrollTerminalContract.IsStableTerminalConfirmation(", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("WaitForVisibleImages(host", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("terminalCandidatePercent", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("loadingAfterTraversal > 0", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("captured movement profile is incomplete", nativeTraversal, StringComparison.Ordinal);
        Assert.Contains("heightAfterPaint !== heightBeforePaint || confirmedTop < confirmedMaxTop", capturedHtmlReplay, StringComparison.Ordinal);
        Assert.Contains("finalHeight === heightAfterImages &&", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("imageRealizationOverscanPx = 800", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("pendingImageSources.size !== 0", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("firstViewportRealizedImageCount", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("sourceTailViewportCount", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("sourceTailMovementCount", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("sourceTailCaptureOffsetsViewportUnits", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("traversalViewportCount + sourceTailViewportCount >= maximumSteps", sourceBoundReplay, StringComparison.Ordinal);
        Assert.Contains("replay.Timing.MovementCount != expectedMovementCount", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("SourceReplayTerminalClampContract.IsComplete(", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("replay.Timing.TerminalClampObservations", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("SourceTailMovementCount != tailOffsets.Count", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("step > SameByteTraversalViewportStepRatio + 2 * positionTolerance", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("timing.TraversalViewportCount + timing.SourceTailViewportCount > MaximumTraversalViewports", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("Math.Abs(previousOffset - expectedBottom) <= positionTolerance", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("FullTraversalViewportCount = traversal.Tiles.Count", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("TraversalViewportCount is < 1 or > MaximumTraversalViewports", nativeProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredImagePeersAreRevisitedWithinTheOriginalDeadlineBeforeTheGlobalZeroGate()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));
        int traversalStart = source.IndexOf(
            "private static NativeTraversalResult CaptureNativeTiles(",
            StringComparison.Ordinal);
        int revisitStart = source.IndexOf(
            "private static double RevisitPendingImages(",
            traversalStart,
            StringComparison.Ordinal);
        int revisitEnd = source.IndexOf(
            "private static void WriteNativeTraversalProgress(",
            revisitStart,
            StringComparison.Ordinal);
        Assert.True(traversalStart >= 0 && revisitStart > traversalStart && revisitEnd > revisitStart);

        string traversal = source[traversalStart..revisitStart];
        string revisit = source[revisitStart..revisitEnd];
        int pendingSnapshot = traversal.IndexOf(
            "AutomationElement[] pendingImagePeers = descendants.Where(IsLoadingImage).ToArray();",
            StringComparison.Ordinal);
        int conditionalRevisit = traversal.IndexOf(
            "if (pendingImagePeers.Length > 0)",
            pendingSnapshot,
            StringComparison.Ordinal);
        int revisitCall = traversal.IndexOf(
            "auditOverheadMs += RevisitPendingImages(",
            conditionalRevisit,
            StringComparison.Ordinal);
        int finalSnapshot = traversal.IndexOf(
            "descendants = host.FindAllDescendants();",
            revisitCall,
            StringComparison.Ordinal);
        int strictZeroGate = traversal.IndexOf(
            "if (loadingAfterTraversal > 0)",
            finalSnapshot,
            StringComparison.Ordinal);

        Assert.True(pendingSnapshot >= 0);
        Assert.True(conditionalRevisit > pendingSnapshot);
        Assert.True(revisitCall > conditionalRevisit);
        Assert.True(finalSnapshot > revisitCall);
        Assert.True(strictZeroGate > finalSnapshot);
        Assert.Contains("loadingAfterTraversal = descendants.Count(IsLoadingImage);", traversal[finalSnapshot..strictZeroGate], StringComparison.Ordinal);
        Assert.Contains("captured movement profile is incomplete", traversal[strictZeroGate..], StringComparison.Ordinal);
        Assert.Contains("EnsurePendingImagePeersRemainDocumented(", traversal, StringComparison.Ordinal);
        Assert.Contains("loadingAfterTraversal = descendants.Count(IsLoadingImage)", traversal, StringComparison.Ordinal);

        Assert.Contains("MaximumPendingImagePeersForRevisit = 16_384", source, StringComparison.Ordinal);
        Assert.Contains("peer.BoundingRectangle", source, StringComparison.Ordinal);
        Assert.Contains("peer.Properties.AutomationId.ValueOrDefault", source, StringComparison.Ordinal);
        Assert.Contains("if (matches != 1)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(3)", source, StringComparison.Ordinal);
        Assert.Contains("GetRemainingNativeTraversalTime(traversalWall", revisit, StringComparison.Ordinal);
        Assert.Contains("GetBoundedNativeTraversalWait(", revisit, StringComparison.Ordinal);
        Assert.Contains("RequestRendererScroll(", revisit, StringComparison.Ordinal);
        Assert.Contains("-SameByteTraversalViewportStepRatio", revisit, StringComparison.Ordinal);
        Assert.Contains("traversalWall);", revisit, StringComparison.Ordinal);
        Assert.Contains("RendererRequestResponseTimeout = TimeSpan.FromSeconds(30)", source, StringComparison.Ordinal);
        Assert.Contains("remainingTraversalTime < RendererRequestResponseTimeout", source, StringComparison.Ordinal);
        Assert.Contains("responseTimeout = remainingTraversalTime < RendererRequestResponseTimeout", source, StringComparison.Ordinal);
        Assert.Contains("responseTimeout ?? RendererRequestResponseTimeout", source, StringComparison.Ordinal);
        Assert.Contains("absoluteDeadlineClock: traversalWall", source, StringComparison.Ordinal);
        Assert.Contains("absoluteDeadline: traversalWall is null ? null : NativeTraversalTimeout", source, StringComparison.Ordinal);
        Assert.Contains("README image revisit exceeded the original", source, StringComparison.Ordinal);
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
    public void LinkRouteEvidenceRecordsDispositionWithoutPersistingTheDestinationUri()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLifecycleAutomationBridge.cs"));
        int writerStart = source.IndexOf("public static bool RecordLinkRoute(", StringComparison.Ordinal);
        Assert.True(writerStart >= 0);
        int writerEnd = source.IndexOf("public static void RecordImageUnavailable(", writerStart, StringComparison.Ordinal);
        Assert.True(writerEnd > writerStart);
        string writer = source[writerStart..writerEnd];
        Assert.Contains("new LinkRouteSignal(", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("AbsoluteUri", writer, StringComparison.Ordinal);

        int recordStart = source.IndexOf("private sealed record LinkRouteSignal(", StringComparison.Ordinal);
        Assert.True(recordStart >= 0);
        int recordEnd = source.IndexOf(");", recordStart, StringComparison.Ordinal);
        Assert.True(recordEnd > recordStart);
        string record = source[recordStart..recordEnd];
        Assert.Contains("string Disposition", record, StringComparison.Ordinal);
        Assert.DoesNotContain("string Uri", record, StringComparison.Ordinal);
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
        Assert.Contains("RequestRebuild();", missingRecovery, StringComparison.Ordinal);
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
        Assert.Contains("RequestRebuild();", process, StringComparison.Ordinal);

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
        Assert.Contains("RequestRebuild();", completion, StringComparison.Ordinal);
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
    public void MarkdownThemeSnapshotsUseFinitePointLookupsInsteadOfEnumeratingApplicationResources()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Theming",
            "ThemeResolver.cs"));

        Assert.Contains("applicationResources.TryGetValue(resourceKey, out value)", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyCollection<string>? additionalElementKeys", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureMarkdownResources()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCollectResourceRoleNames", source, StringComparison.Ordinal);
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
    public void MarkdownTouchHandleUpdatesAreFrameCoalescedAndOverlayOnly()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));

        int moveStart = source.IndexOf(
            "private void ProcessSelectionHandlePointerMoved",
            StringComparison.Ordinal);
        int releaseStart = source.IndexOf(
            "private void ProcessSelectionHandlePointerReleased",
            moveStart,
            StringComparison.Ordinal);
        int frameStart = source.IndexOf(
            "private void OnSelectionHandleFrame",
            releaseStart,
            StringComparison.Ordinal);
        int applyStart = source.IndexOf(
            "private bool ApplyPendingSelectionHandleMove",
            frameStart,
            StringComparison.Ordinal);
        int applyEnd = source.IndexOf(
            "private bool IsSelectionHandleInAutoScrollBand",
            applyStart,
            StringComparison.Ordinal);
        Assert.True(moveStart >= 0 && releaseStart > moveStart &&
            frameStart > releaseStart && applyStart > frameStart && applyEnd > applyStart);

        string pointerMove = source[moveStart..releaseStart];
        string frameUpdate = source[frameStart..applyEnd];
        Assert.Contains("_selectionHandleMovePending = true", pointerMove, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyPendingSelectionHandleMove", pointerMove, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering", source, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingSelectionHandleMove", frameUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestRebuild", frameUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateCanvas", frameUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("_canvas.Invalidate", frameUpdate, StringComparison.Ordinal);
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

    [Fact]
    public void FirstViewportImageWaitDiagnosticsKeepTheReadyClockAndPersistOnlyAggregateStates()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));

        int waitStart = source.IndexOf("private static VisibleImageWaitResult WaitForVisibleImages(", StringComparison.Ordinal);
        int waitEnd = source.IndexOf("private static bool IsLoadingImage", waitStart, StringComparison.Ordinal);
        int diagnosticStart = source.IndexOf("internal sealed class ReadmeAuditFirstViewportImageWait", StringComparison.Ordinal);
        int transitionStart = source.IndexOf("internal sealed class ReadmeAuditVisibleImageLoadingStateTransition", diagnosticStart, StringComparison.Ordinal);
        int sanitizerStart = source.IndexOf("private static NativeAuditResult? SanitizeNativeAuditResult", StringComparison.Ordinal);
        int sanitizerEnd = source.IndexOf("private static NativeAuditResult RunNativeAudit", sanitizerStart, StringComparison.Ordinal);
        Assert.True(waitStart >= 0 && waitEnd > waitStart);
        Assert.True(diagnosticStart >= 0 && transitionStart > diagnosticStart);
        Assert.True(sanitizerStart >= 0 && sanitizerEnd > sanitizerStart);

        string wait = source[waitStart..waitEnd];
        string diagnostic = source[diagnosticStart..transitionStart];
        string sanitizer = source[sanitizerStart..sanitizerEnd];
        Assert.Contains("captureLoadingStateTransitions: true", source, StringComparison.Ordinal);
        Assert.Contains("firstImagesReadySignal.Timestamp - hostReadySignal.Timestamp", source, StringComparison.Ordinal);
        Assert.Contains("firstImageWait.ProbeOverheadMs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.UtcNow - ReadSignalTimestamp(hostReady)", source, StringComparison.Ordinal);
        Assert.Contains("pollCount++", wait, StringComparison.Ordinal);
        Assert.Contains("ElapsedMilliseconds = elapsedMilliseconds", wait, StringComparison.Ordinal);
        Assert.Contains("HasLoadingVisibleImages = isLoading", wait, StringComparison.Ordinal);
        Assert.Contains("probeOverheadMs += probe.Elapsed.TotalMilliseconds", wait, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(10)", wait, StringComparison.Ordinal);
        Assert.Contains("public double ElapsedMilliseconds", diagnostic, StringComparison.Ordinal);
        Assert.Contains("public double ProbeOverheadMs", diagnostic, StringComparison.Ordinal);
        Assert.Contains("public int PollCount", diagnostic, StringComparison.Ordinal);
        Assert.Contains("public bool? InitialHasLoadingVisibleImages", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("string", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemStatus", diagnostic, StringComparison.Ordinal);
        Assert.Contains("public ReadmeAuditFirstViewportImageWait? FirstViewportImageWait { get; init; }", source, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImageWait = result.FirstViewportImageWait is { } firstViewportImageWait", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ElapsedMilliseconds = firstViewportImageWait.ElapsedMilliseconds", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ProbeOverheadMs = firstViewportImageWait.ProbeOverheadMs", sanitizer, StringComparison.Ordinal);
        Assert.Contains("PollCount = firstViewportImageWait.PollCount", sanitizer, StringComparison.Ordinal);
        Assert.Contains("InitialHasLoadingVisibleImages = firstViewportImageWait.InitialHasLoadingVisibleImages", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalGeneration = firstViewportImageWait.ApplicationSignalGeneration", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalViewportPaintGeneration = firstViewportImageWait.ApplicationSignalViewportPaintGeneration", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalPollCount = firstViewportImageWait.ApplicationSignalPollCount", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalProbeWorkMilliseconds = firstViewportImageWait.ApplicationSignalProbeWorkMilliseconds", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalViewportTop = firstViewportImageWait.ApplicationSignalViewportTop", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalViewportHeight = firstViewportImageWait.ApplicationSignalViewportHeight", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalViewportMeasured = firstViewportImageWait.ApplicationSignalViewportMeasured", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalAfterRenderCompleteMs = firstViewportImageWait.ApplicationSignalAfterRenderCompleteMs", sanitizer, StringComparison.Ordinal);
        Assert.Contains("firstViewportImageWait.LoadingStateTransitions", sanitizer, StringComparison.Ordinal);
        Assert.Contains("ElapsedMilliseconds = transition.ElapsedMilliseconds", sanitizer, StringComparison.Ordinal);
        Assert.Contains("HasLoadingVisibleImages = transition.HasLoadingVisibleImages", sanitizer, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstViewportImageWait = result.FirstViewportImageWait,", sanitizer, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadmeAuditSvgStageTraceIsExplicitlyOptInAndPreservedAsASeparateArtifact()
    {
        string probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));
        int start = probe.IndexOf("private static NativeAuditResult RunNativeAudit(", StringComparison.Ordinal);
        int end = probe.IndexOf("private static NativeSameByteReadmeSourceIdentity ReadSameByteSourceEvidence(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string nativeProbe = probe[start..end];

        Assert.Contains("JITHUB_README_AUDIT_SVG_STAGE_DIAGNOSTICS", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH\"] = null", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH\"] = svgStageEvidence", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(runtime, \"svg-work-stages.ndjson\")", nativeProbe, StringComparison.Ordinal);
        Assert.True(Regex.Matches(
            nativeProbe,
            "PreserveEvidenceFile\\(svgStageEvidence").Count >= 3);
        Assert.Contains("Path.Combine(output, \"svg-work-stages.ndjson\")", nativeProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstViewportReadyClockUsesGenerationBoundAppEvidenceAndRetainsUiAValidation()
    {
        string probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string viewer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string bridge = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLifecycleAutomationBridge.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string renderer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string imageBox = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "Boxes",
            "ImageBox.cs"));
        string rendererProbe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "FirstViewportImagesReadyRendererProbe.cs"));
        string evidenceWriter = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "FirstViewportImagesReadyEvidenceWriter.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string readinessDiagnostic = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "NativeReadinessProgressDiagnostic.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string contract = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "NativeFirstViewportImagesReadyContract.cs"));
        string edgeSourceReplay = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "readme-audit",
            "same-byte-edge-source.mjs"));

        int uiaWait = probe.IndexOf("VisibleImageWaitResult firstImageWait = WaitForVisibleImages(", StringComparison.Ordinal);
        int appSignalWait = probe.IndexOf("WaitForNativeFirstViewportImagesReadySignal(", uiaWait, StringComparison.Ordinal);
        Assert.True(uiaWait >= 0 && appSignalWait > uiaWait);
        Assert.Contains("ready.Generation != renderComplete.Generation", contract, StringComparison.Ordinal);
        Assert.Contains("ready.ViewportPaintGeneration != ready.Generation", contract, StringComparison.Ordinal);
        Assert.Contains("ready.ProcessId != processId", contract, StringComparison.Ordinal);
        Assert.Contains("ready.HasVisibleLoadingImages", contract, StringComparison.Ordinal);
        Assert.Contains("!ready.ViewportMeasured", contract, StringComparison.Ordinal);
        Assert.Contains("ready.ViewportHeight <= 0", contract, StringComparison.Ordinal);
        Assert.Contains("ready.Timestamp < renderComplete.Timestamp", contract, StringComparison.Ordinal);
        Assert.Contains("ValidateSameByteSourceTiming(", contract, StringComparison.Ordinal);
        Assert.Contains("admission.StopwatchTicks > renderComplete.StopwatchTicks", contract, StringComparison.Ordinal);
        Assert.Contains("admission.DecodedTextSha256", contract, StringComparison.Ordinal);
        Assert.Contains("SourceAvailableToFirstViewportImagesReadyMs = sourceAvailableToFirstViewportImagesReadyMs", probe, StringComparison.Ordinal);
        Assert.Contains("sourceAvailableReadyMs / replay.Timing.FirstViewportImagesReadyMs", probe, StringComparison.Ordinal);
        Assert.Contains("replay.Source.DecodedTextSha256", probe, StringComparison.Ordinal);
        Assert.Contains("MatchSameByteReadmeSource(", viewer, StringComparison.Ordinal);
        Assert.Contains("GetSameByteReadmeGitBlobSha1(", viewer, StringComparison.Ordinal);
        Assert.Contains("out sameByteSourceAdmissionFailure);", viewer, StringComparison.Ordinal);
        Assert.Contains("sourceAdmissionStopwatchTicks = sameByteSourceIdentity is null ? 0 : Stopwatch.GetTimestamp()", viewer, StringComparison.Ordinal);
        Assert.Contains("_renderer.Markdown = markdown", viewer, StringComparison.Ordinal);
        Assert.Contains("RecordSameByteReadmeSourceAdmission(", viewer, StringComparison.Ordinal);
        Assert.Contains("decodedTextSha256: replay.decodedTextSha256", edgeSourceReplay, StringComparison.Ordinal);
        Assert.Contains("new TextDecoder(\"utf-8\", { fatal: true, ignoreBOM: true })", edgeSourceReplay, StringComparison.Ordinal);
        Assert.Contains("const decodedTextSha256 = await hashText(markdown)", edgeSourceReplay, StringComparison.Ordinal);
        Assert.Contains("same source-available start through successful decode of every visible pinned image", edgeSourceReplay, StringComparison.Ordinal);
        Assert.Contains("firstImagesReadySignal.Timestamp - hostReadySignal.Timestamp", probe, StringComparison.Ordinal);
        Assert.Contains("ready.ReadmeGitBlobSha1,", contract, StringComparison.Ordinal);
        Assert.Contains("expectedReadmeGitBlobSha1", contract, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalPollCount", probe, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalProbeWorkMilliseconds", probe, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalViewportTop", probe, StringComparison.Ordinal);
        Assert.Contains("ApplicationSignalViewportHeight", probe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_EVIDENCE_PATH", probe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_PROGRESS_PATH", probe, StringComparison.Ordinal);
        Assert.Contains("first-viewport-images-ready-progress.ndjson", probe, StringComparison.Ordinal);
        Assert.Contains("firstViewportImagesReadyProgress", probe, StringComparison.Ordinal);

        int initialCapture = probe.IndexOf("NativeTraversalResult CaptureNativeTiles(", StringComparison.Ordinal);
        Assert.True(initialCapture >= 0);
        int initialCaptureWrite = probe.IndexOf("out double capturedDocumentTop", initialCapture, StringComparison.Ordinal);
        Assert.True(initialCaptureWrite > initialCapture);
        int initialCaptureValidation = probe.IndexOf(
            "NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(",
            initialCaptureWrite,
            StringComparison.Ordinal);
        Assert.True(initialCaptureValidation > initialCaptureWrite);
        Assert.Contains("out double documentTop", probe, StringComparison.Ordinal);
        Assert.Contains("documentTop = capture.DocumentTop", probe, StringComparison.Ordinal);
        Assert.Contains("InitialCaptureStartsAtDocumentTop", contract, StringComparison.Ordinal);
        Assert.Contains("ViewportTopMatchesInitialCapture", contract, StringComparison.Ordinal);

        Assert.Contains("DispatcherQueuePriority.Low", viewer, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(16)", viewer, StringComparison.Ordinal);
        Assert.Contains("renderer.AutomationPipelineGeneration != generation", viewer, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImagesReadyRendererProbe.HasVisibleLoadingImages(renderer)", viewer, StringComparison.Ordinal);
        Assert.Contains("probe.TryAcknowledgeAfterPaint(", viewer, StringComparison.Ordinal);
        Assert.Contains("paintedSnapshot,\n                    paintedLayoutRevision,\n                    region)", viewer, StringComparison.Ordinal);
        Assert.Contains("_firstViewportImagesReadyPaintGeneration = generation", viewer, StringComparison.Ordinal);
        Assert.Contains("long paintTimestamp = Stopwatch.GetTimestamp()", viewer, StringComparison.Ordinal);
        Assert.Contains("_firstViewportImagesReadyPaintTimestamp = paintTimestamp", viewer, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(viewportPaintTimestamp)", viewer, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImagesReadyProbeContract.IsViewportPaintAcknowledged(", viewer, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(sender, _firstViewportImagesReadyTimer)", viewer, StringComparison.Ordinal);
        Assert.Contains("RecordFirstViewportImagesReady(", viewer, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImagesReadyProbeContract.IsCurrentPublishedGeneration", viewer, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImagesReadyProbeContract.IsMeasuredViewport", viewer, StringComparison.Ordinal);
        Assert.Contains("StopFirstViewportImagesReadyProbe();", viewer, StringComparison.Ordinal);
        int probeArm = viewer.IndexOf("private void QueueFirstViewportImagesReadyProbe(", StringComparison.Ordinal);
        int targetGuard = viewer.IndexOf(
            "!MarkdownLifecycleAutomationBridge.TargetsHost(automationId)",
            probeArm,
            StringComparison.Ordinal);
        int armedProgress = viewer.IndexOf("\"armed\"", probeArm, StringComparison.Ordinal);
        Assert.True(probeArm >= 0 && targetGuard > probeArm && targetGuard < armedProgress);
        Assert.Contains("!MarkdownLifecycleAutomationBridge.TargetsHost(host)", viewer, StringComparison.Ordinal);
        Assert.Contains("IsFirstViewportImagesReadyEvidenceEnabled", bridge, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImagesReadyEvidenceWriter.TryQueueProgressWrite(", bridge, StringComparison.Ordinal);
        Assert.Contains("FirstViewportImagesReadyEvidenceWriter.TryQueueWrite(", bridge, StringComparison.Ordinal);
        Assert.Contains("IsFirstViewportImagesReadyEvidenceEnabled,", bridge, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => DrainQueuedWritesAsync(request))", evidenceWriter, StringComparison.Ordinal);
        Assert.Contains("await progressDrain.WaitAsync(generationCancellationToken).ConfigureAwait(false)", evidenceWriter, StringComparison.Ordinal);

        int recordReadyStart = bridge.IndexOf("public static Task<bool>? RecordFirstViewportImagesReady(", StringComparison.Ordinal);
        int captureRequestStart = bridge.IndexOf("public static bool TryReadCaptureRequest(", recordReadyStart, StringComparison.Ordinal);
        Assert.True(recordReadyStart >= 0 && captureRequestStart > recordReadyStart);
        string recordReady = bridge[recordReadyStart..captureRequestStart];
        Assert.Contains("if (!IsFirstViewportImagesReadyEvidenceEnabled ||", recordReady, StringComparison.Ordinal);
        Assert.Contains("!TargetsHost(automationId)", recordReady, StringComparison.Ordinal);
        Assert.Contains("generation <= 0", recordReady, StringComparison.Ordinal);
        Assert.Contains("viewportPaintGeneration != generation", recordReady, StringComparison.Ordinal);
        Assert.Contains("lock (SameByteReadmeSourceGate)", recordReady, StringComparison.Ordinal);
        Assert.Contains("admitted.Generation == generation", recordReady, StringComparison.Ordinal);
        Assert.Contains("publicationIdentity: publicationIdentity", recordReady, StringComparison.Ordinal);
        Assert.Contains("generationCancellationToken: generationCancellationToken", recordReady, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", recordReady, StringComparison.Ordinal);
        Assert.Contains("\"faulted\"", recordReady, StringComparison.Ordinal);
        Assert.Contains("return Task.FromResult(false);", recordReady, StringComparison.Ordinal);

        int signalQueueStart = evidenceWriter.IndexOf("private static Task<bool> QueueWriteOutcomeAfterCurrent(", StringComparison.Ordinal);
        int compatibilityWriterStart = evidenceWriter.IndexOf("internal static async Task<bool> WriteAfterProgressDrainAsync(", signalQueueStart, StringComparison.Ordinal);
        Assert.True(signalQueueStart >= 0 && compatibilityWriterStart > signalQueueStart);
        string signalQueue = evidenceWriter[signalQueueStart..compatibilityWriterStart];
        Assert.Contains("else if (_queuedWrite is { IsCompleted: false })", signalQueue, StringComparison.Ordinal);
        Assert.Contains("suppressed = _pendingWrite;", signalQueue, StringComparison.Ordinal);
        Assert.Contains("_pendingWrite = request;", signalQueue, StringComparison.Ordinal);
        Assert.Contains("suppressed?.Completion.TrySetResult(false);", signalQueue, StringComparison.Ordinal);
        Assert.Contains("if (canceled)\n                request.Completion.TrySetResult(false);", signalQueue, StringComparison.Ordinal);
        Assert.Contains("request.RecordOutcome(PublicationWriteOutcome.Canceled);", signalQueue, StringComparison.Ordinal);

        int boolWrapperStart = evidenceWriter.IndexOf("internal static Task<bool> QueueWriteAfterCurrent(", StringComparison.Ordinal);
        Assert.True(boolWrapperStart >= 0 && signalQueueStart > boolWrapperStart);
        string boolWrapper = evidenceWriter[boolWrapperStart..signalQueueStart];
        Assert.Contains("writeSignal() ? PublicationWriteOutcome.Published : PublicationWriteOutcome.WriteFailed", boolWrapper, StringComparison.Ordinal);

        int outcomeQueueStart = evidenceWriter.IndexOf("internal static bool TryQueuePublicationOutcome(", StringComparison.Ordinal);
        int progressQueueStart = evidenceWriter.IndexOf("internal static void TryQueueProgressWrite(", outcomeQueueStart, StringComparison.Ordinal);
        Assert.True(outcomeQueueStart >= 0 && progressQueueStart > outcomeQueueStart);
        string outcomeQueue = evidenceWriter[outcomeQueueStart..progressQueueStart];
        Assert.Contains("lock (PublicationWriteGate)", outcomeQueue, StringComparison.Ordinal);
        Assert.Contains("PendingPublicationOutcomes.Count >= MaximumPendingPublicationOutcomes", outcomeQueue, StringComparison.Ordinal);
        Assert.Contains("Task.Run(DrainPublicationWrites)", outcomeQueue, StringComparison.Ordinal);
        Assert.Contains("MaximumPublicationRecordBytes", evidenceWriter, StringComparison.Ordinal);

        int signalDrainStart = evidenceWriter.IndexOf("private static async Task<bool> DrainQueuedWritesAsync(", StringComparison.Ordinal);
        int typedSignalWriterStart = evidenceWriter.IndexOf("private static async Task<PublicationWriteOutcome> WriteOutcomeAfterProgressDrainAsync(", signalDrainStart, StringComparison.Ordinal);
        Assert.True(signalDrainStart >= 0 && typedSignalWriterStart > signalDrainStart);
        string signalDrain = evidenceWriter[signalDrainStart..typedSignalWriterStart];
        Assert.Contains("request.RecordOutcome(outcome);", signalDrain, StringComparison.Ordinal);
        Assert.Contains("request.Completion.TrySetResult(lastResult);", signalDrain, StringComparison.Ordinal);
        Assert.DoesNotContain("await QueuedPublicationWrite", signalDrain, StringComparison.Ordinal);
        int typedSignalWriterEnd = evidenceWriter.IndexOf("private enum PublicationWriteOutcome", typedSignalWriterStart, StringComparison.Ordinal);
        Assert.True(typedSignalWriterEnd > typedSignalWriterStart);
        string typedSignalWriter = evidenceWriter[typedSignalWriterStart..typedSignalWriterEnd];
        Assert.Contains("progressDrain.WaitAsync(cancellationToken)", typedSignalWriter, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.IsCancellationRequested\n            ? PublicationWriteOutcome.Canceled : writeSignal()", typedSignalWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("QueuedPublicationWrite", typedSignalWriter, StringComparison.Ordinal);

        int publicationDrainStart = evidenceWriter.IndexOf("private static void DrainPublicationWrites()", StringComparison.Ordinal);
        int publicationAppendStart = evidenceWriter.IndexOf("private static bool TryWritePublicationOutcome(", publicationDrainStart, StringComparison.Ordinal);
        Assert.True(publicationDrainStart >= 0 && publicationAppendStart > publicationDrainStart);
        string publicationDrain = evidenceWriter[publicationDrainStart..publicationAppendStart];
        Assert.Contains("lock (PublicationWriteGate)", publicationDrain, StringComparison.Ordinal);
        Assert.Contains("PendingPublicationOutcomes.Dequeue()", publicationDrain, StringComparison.Ordinal);
        Assert.DoesNotContain("lock (WriteGate)", publicationDrain, StringComparison.Ordinal);
        Assert.DoesNotContain("lock (ProgressWriteGate)", publicationDrain, StringComparison.Ordinal);

        Assert.Matches(
            "GetPublicationOutcomePath\\(\\s*firstViewportImagesReadyProgress\\)",
            probe);
        int auditStart = probe.IndexOf("private static NativeAuditResult RunNativeAudit(", StringComparison.Ordinal);
        int captureTilesStart = probe.IndexOf("private static NativeTraversalResult CaptureNativeTiles(", auditStart, StringComparison.Ordinal);
        Assert.True(auditStart >= 0 && captureTilesStart > auditStart);
        string nativeAudit = probe[auditStart..captureTilesStart];
        int staleStart = nativeAudit.IndexOf("foreach (string stale in new[]", StringComparison.Ordinal);
        int argumentsStart = nativeAudit.IndexOf("string[] arguments =", staleStart, StringComparison.Ordinal);
        Assert.True(staleStart >= 0 && argumentsStart > staleStart);
        string staleCleanup = nativeAudit[staleStart..argumentsStart];
        Assert.Contains("readinessPublicationOutcomes", staleCleanup, StringComparison.Ordinal);
        Assert.Contains("preservedReadinessPublicationOutcomes", staleCleanup, StringComparison.Ordinal);
        const string preserveOutcomes = "NativeReadinessProgressDiagnostic.TryPreservePublicationOutcomes(";
        Assert.Equal(2, Regex.Matches(nativeAudit, Regex.Escape(preserveOutcomes)).Count);
        Assert.Matches(
            "NativeReadinessProgressDiagnostic\\.TryPreservePublicationOutcomes\\(\\s*readinessPublicationOutcomes,\\s*preservedReadinessPublicationOutcomes\\);",
            nativeAudit);
        int successPreserve = nativeAudit.IndexOf(preserveOutcomes, StringComparison.Ordinal);
        int validateReady = nativeAudit.IndexOf("NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(", successPreserve, StringComparison.Ordinal);
        int failurePreserve = nativeAudit.LastIndexOf(preserveOutcomes, StringComparison.Ordinal);
        Assert.True(successPreserve >= 0 && validateReady > successPreserve && failurePreserve > validateReady);

        int optionalPreserveStart = readinessDiagnostic.IndexOf("internal static bool TryPreservePublicationOutcomes(", StringComparison.Ordinal);
        int optionalPreserveEnd = readinessDiagnostic.IndexOf("private static string ReadFileSummary(", optionalPreserveStart, StringComparison.Ordinal);
        Assert.True(optionalPreserveStart >= 0 && optionalPreserveEnd > optionalPreserveStart);
        string optionalPreserve = readinessDiagnostic[optionalPreserveStart..optionalPreserveEnd];
        Assert.Contains("EvidenceFileSnapshot.PreserveIfPresent(source, destination, TimeSpan.FromSeconds(2))", optionalPreserve, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", optionalPreserve, StringComparison.Ordinal);
        Assert.Contains("return false;", optionalPreserve, StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", optionalPreserve, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(probe, Regex.Escape("EvidenceFileSnapshot.PreserveIfPresent(source, destination, TimeSpan.FromSeconds(2));")));

        int waitStart = probe.IndexOf("private static NativeFirstViewportImagesReadySignal WaitForNativeFirstViewportImagesReadySignal(", StringComparison.Ordinal);
        int sameByteWaitStart = probe.IndexOf("private static (NativeRenderCompleteSignal RenderComplete, NativeFirstViewportImagesReadySignal Ready)", waitStart, StringComparison.Ordinal);
        Assert.True(waitStart >= 0 && sameByteWaitStart > waitStart);
        string readinessWait = probe[waitStart..sameByteWaitStart];
        int timeoutRead = readinessWait.IndexOf("ReadLastSummary(progressPath)", StringComparison.Ordinal);
        int timeoutThrow = readinessWait.IndexOf("throw new TimeoutException(", StringComparison.Ordinal);
        Assert.True(timeoutThrow >= 0 && timeoutRead > timeoutThrow);
        Assert.Contains("ReadImagesReadySignal(path)", readinessWait, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPublicationOutcomePath", readinessWait, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadinessPublication", contract, StringComparison.Ordinal);

        int summaryStart = readinessDiagnostic.IndexOf("internal static string ReadLastSummary(", StringComparison.Ordinal);
        int summaryEnd = readinessDiagnostic.IndexOf("private static string ReadFileSummary(", summaryStart, StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && summaryEnd > summaryStart);
        string lastSummary = readinessDiagnostic[summaryStart..summaryEnd];
        Assert.Contains("ReadFileSummary(path)", lastSummary, StringComparison.Ordinal);
        Assert.Contains("ReadFileSummary(GetPublicationOutcomePath(path))", lastSummary, StringComparison.Ordinal);
        Assert.Contains("publication.StartsWith(\"readinessPublication outcome=\"", lastSummary, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", readinessDiagnostic, StringComparison.Ordinal);

        Assert.Contains("PaintCallbackCount", evidenceWriter, StringComparison.Ordinal);
        Assert.Contains("CoveredArea", evidenceWriter, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Serialize(", evidenceWriter, StringComparison.Ordinal);
        Assert.Contains("internal long AutomationPipelineGeneration => _pipelineGeneration", renderer, StringComparison.Ordinal);
        Assert.Contains("GetAutomationPipelineCancellationToken(long generation)", renderer, StringComparison.Ordinal);
        Assert.Contains("generation == _activePipelineGeneration", renderer, StringComparison.Ordinal);
        Assert.Contains("internal List<Layout.Boxes.ImageBox> AutomationImagePlans => _imagePlans", renderer, StringComparison.Ordinal);
        Assert.Contains("internal ViewportBandIndex? AutomationImagePlanIndex => _imagePlanIndex", renderer, StringComparison.Ordinal);
        Assert.Contains("var automationPaintCallback = _automationFirstViewportImagesReadyPaintCallback", renderer, StringComparison.Ordinal);
        Assert.Contains("catch\n                    {\n                        // Audit-only callback failures must not escape Canvas paint.", renderer, StringComparison.Ordinal);
        Assert.Contains("internal async Task<(CanvasRenderTarget Target, int Width, int Height, double DocumentTop)> CaptureAuditViewportAsync()", renderer, StringComparison.Ordinal);
        Assert.Contains("var capture = await renderer.CaptureAuditViewportAsync()", viewer, StringComparison.Ordinal);
        Assert.Contains("SaveAuditViewportAsync(capture.Target, request.OutputPath)", viewer, StringComparison.Ordinal);
        Assert.Contains("File.WriteAllBytesAsync(fullPath, bytes)", viewer, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() =>", viewer, StringComparison.Ordinal);
        int captureStart = renderer.IndexOf("internal async Task<(CanvasRenderTarget Target, int Width, int Height, double DocumentTop)> CaptureAuditViewportAsync()", StringComparison.Ordinal);
        int captureEnd = renderer.IndexOf("private void HandleCanvasDeviceLost(", captureStart, StringComparison.Ordinal);
        string captureMethod = renderer[captureStart..captureEnd];
        Assert.DoesNotContain("SaveAsync(", captureMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllBytes", captureMethod, StringComparison.Ordinal);
        int beginPaintProbe = renderer.IndexOf("internal void BeginAutomationFirstViewportImagesReadyProbe(", StringComparison.Ordinal);
        int cancelPaintProbe = renderer.IndexOf("internal void CancelAutomationFirstViewportImagesReadyProbe(", beginPaintProbe, StringComparison.Ordinal);
        Assert.True(beginPaintProbe >= 0 && cancelPaintProbe > beginPaintProbe);
        string paintProbeStart = renderer[beginPaintProbe..cancelPaintProbe];
        Assert.Contains("paintCallback", paintProbeStart, StringComparison.Ordinal);
        Assert.True(
            paintProbeStart.IndexOf("_automationFirstViewportImagesReadyPaintCallback = paintCallback", StringComparison.Ordinal) <
            paintProbeStart.IndexOf("InvalidateCanvas();", StringComparison.Ordinal));
        Assert.Contains("Func<LayoutSnapshot, long, Windows.Foundation.Rect, bool> paintCallback", paintProbeStart, StringComparison.Ordinal);
        Assert.Contains("HasAllSvgTilesForAutomation", imageBox, StringComparison.Ordinal);
        Assert.Contains("if (!_svgTiles.ContainsKey(key))", imageBox, StringComparison.Ordinal);
        Assert.Contains("lastTileX", imageBox, StringComparison.Ordinal);
        Assert.Contains("lastTileY", imageBox, StringComparison.Ordinal);
        Assert.Contains("image.HasAllSvgTilesForAutomation(tileX, tileY, tileX, tileY)", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("!IsFiniteNonEmptyRect(destination)", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("_coverage.AddPaintedRegionExcluding(", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("FirstViewportPaintCoverage.TryGetPaintableViewport(", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("Size snapshotSize = snapshot?.Size ?? default", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("snapshotSize.Height", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("FirstViewportPaintCoverage.MaximumPendingMasks", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("pending-mask-count-budget-exceeded", rendererProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingRegions.Count >= 4096", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("snapshot.IsBandMeasured(LazyLayoutBand.FromViewport(", rendererProbe, StringComparison.Ordinal);
        Assert.Contains("_automationFirstViewportImagesReadyPaintCallback = null", renderer, StringComparison.Ordinal);
        Assert.Contains("CancelAutomationFirstViewportImagesReadyProbe();", renderer[renderer.IndexOf("private void ReleaseLoadedResources()", StringComparison.Ordinal)..], StringComparison.Ordinal);
        int snapshotPaint = renderer.IndexOf("snapshot.Paint(ds, region);", StringComparison.Ordinal);
        int callbackAfterPaint = renderer.IndexOf(
            "automationPaintCallback(snapshot, paintedLayoutRevision, region)",
            snapshotPaint,
            StringComparison.Ordinal);
        Assert.True(snapshotPaint >= 0 && callbackAfterPaint > snapshotPaint);
    }

    [Fact]
    public void FirstViewportSignalWaitKeepsDeadlineAndReportsBoundedProgressOrProcessExit()
    {
        string probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("TimeSpan.FromSeconds(2)", probe, StringComparison.Ordinal);
        Assert.Contains("EnsureAuditAppRunning(\n                    appProcess,\n                    appExitHandle,", probe, StringComparison.Ordinal);
        Assert.Contains("OpenProcessExitHandle(processId)", probe, StringComparison.Ordinal);
        Assert.Contains("if (!appProcess.HasExited)", probe, StringComparison.Ordinal);
        Assert.Contains("GetProcessExitCode(appExitHandle)", probe, StringComparison.Ordinal);
        Assert.Contains("source-admitted first-viewport readiness", probe, StringComparison.Ordinal);
        Assert.Contains("NativeReadinessProgressDiagnostic.ReadLastSummary(progressPath)", probe, StringComparison.Ordinal);
        Assert.Contains("firstViewportImagesReadyProgress", probe, StringComparison.Ordinal);
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
