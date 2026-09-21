using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NativeAotSourceContractTests
{
    private const string GeneratedBindableAttribute = "[WinRT.GeneratedBindableCustomProperty]";

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ReleaseAndAotDebug_EnableTheStrictNativeAotContract()
    {
        string root = FindRepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "eng", "NativeAot.props"));
        string directoryProps = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string launchSettings = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Properties", "launchSettings.json"));

        foreach (string property in new[]
        {
            "<PublishAot>true</PublishAot>",
            "<PublishTrimmed>true</PublishTrimmed>",
            "<SelfContained>true</SelfContained>",
            "<IsAotCompatible>true</IsAotCompatible>",
            "<EnableAotAnalyzer>true</EnableAotAnalyzer>",
            "<EnableTrimAnalyzer>true</EnableTrimAnalyzer>",
            "<TrimmerSingleWarn>false</TrimmerSingleWarn>",
            "<IlcTreatWarningsAsErrors>true</IlcTreatWarningsAsErrors>",
            "<PublishReadyToRun>false</PublishReadyToRun>",
        })
        {
            Assert.Contains(property, props, StringComparison.Ordinal);
        }

        Assert.Contains("'$(Configuration)' == 'Release' or '$(Configuration)' == 'AotDebug'", props, StringComparison.Ordinal);
        Assert.Contains("<Optimize>true</Optimize>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<Optimize>false</Optimize>", props, StringComparison.Ordinal);
        Assert.Contains("<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>", project, StringComparison.Ordinal);
        Assert.Contains("<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>", project, StringComparison.Ordinal);
        Assert.Contains("<CsWinRTAotWarningLevel>2</CsWinRTAotWarningLevel>", project, StringComparison.Ordinal);
        Assert.Contains("CsWinRT1032", directoryProps, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>", project, StringComparison.Ordinal);
        Assert.Contains("MarkdownRenderer.Math\\MarkdownRenderer.Math.csproj", project, StringComparison.Ordinal);
        Assert.Contains("MarkdownRenderer.Mermaid\\MarkdownRenderer.Mermaid.csproj", project, StringComparison.Ordinal);
        Assert.Contains("MarkdownRenderer.Svg.Resvg\\MarkdownRenderer.Svg.Resvg.csproj", project, StringComparison.Ordinal);
        Assert.Contains("JitHub.WinUI (AotDebug)", launchSettings, StringComparison.Ordinal);
        Assert.Contains("\"nativeDebugging\": true", launchSettings, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void RuntimeGraph_UsesTheReviewedAotReplacements()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string ledger = File.ReadAllText(Path.Combine(root, "eng", "native-aot-dependencies.json"));
        string verifier = File.ReadAllText(Path.Combine(root, "eng", "Verify-NativeAotArtifact.ps1"));
        string graph = project + Environment.NewLine + ledger;

        Assert.Contains("WinUIEdit" + "\" Version=\"0.0.5-prerelease", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MarkdownRenderer.Svg.Resvg\\MarkdownRenderer.Svg.Resvg.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.Services.Store.Engagement\" Version=\"10.2307.3001", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'$(RuntimeIdentifier)' == 'win-arm64'", project, StringComparison.Ordinal);

        foreach (string removedPackage in new[]
        {
            "CsvHelper",
            "CommunityToolkit.WinUI.Controls.DataGrid",
            "MarkdownRenderer.Svg.ThorVG",
            "Svg.Skia",
            "SkiaSharp",
            "SkiaSharp.Views.WinUI",
            "Jint",
        })
        {
            Assert.DoesNotContain(removedPackage, graph, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("libHarfBuzzSharp.dll", verifier, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void LockedNativeAotRestore_KeepsProjectReferenceLocksOrdinary()
    {
        string root = FindRepositoryRoot();
        string restoreScript = File.ReadAllText(Path.Combine(root, "eng", "Restore-NativeAot.ps1"));
        string nativeAotProps = File.ReadAllText(Path.Combine(root, "eng", "NativeAot.props"));

        Assert.Contains("$arguments += '--locked-mode'", restoreScript, StringComparison.Ordinal);
        Assert.Contains("$arguments += '--force'", restoreScript, StringComparison.Ordinal);
        Assert.DoesNotContain("'-p:PublishAot=true'", restoreScript, StringComparison.Ordinal);
        Assert.Contains("<PublishAot>true</PublishAot>", nativeAotProps, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void DependencyLedgerGenerator_SupportsWindowsPowerShellAndPowerShellSeven()
    {
        string root = FindRepositoryRoot();
        string ledgerScript = File.ReadAllText(Path.Combine(root, "eng", "Update-NativeAotDependencyLedger.ps1"));

        Assert.Contains("Parameters.ContainsKey('AsHashtable')", ledgerScript, StringComparison.Ordinal);
        Assert.Contains("System.Web.Script.Serialization.JavaScriptSerializer", ledgerScript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void DebugWorkflow_DoesNotRequireRetiredWebEditorAssets()
    {
        string root = FindRepositoryRoot();
        string launcher = File.ReadAllText(Path.Combine(root, "eng", "Start-JitHubWinUIDebug.ps1"));
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        string workflow = File.ReadAllText(Path.Combine(root, "docs", "windows-cli-workflow.md"));
        string combinedWorkflow = launcher + Environment.NewLine + readme + Environment.NewLine + workflow;

        Assert.DoesNotContain("EditorAssets", combinedWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync-vscode-assets", combinedWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jithub-vs-code", combinedWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WinUIEdit", readme, StringComparison.Ordinal);
        Assert.Contains("resources.pri", launcher, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $resourceIndexPath", launcher, StringComparison.Ordinal);
        Assert.Contains("finally {", launcher, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void NativeAotUiMatrix_WaitsForRenderedSvgAndObservableEditorResults()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AppSvgViewport.xaml.cs"));
        string viewportPeer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AppSvgViewportAutomationPeer.cs"));
        string preview = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "Renderers",
            "SvgPreview.xaml.cs"));
        string matrix = File.ReadAllText(Path.Combine(root, "eng", "Invoke-NativeAotUiMatrix.ps1"));

        Assert.Contains("SvgPreviewRenderedImage", viewport, StringComparison.Ordinal);
        Assert.Contains("AccessibilityView.Content", viewport, StringComparison.Ordinal);
        Assert.Contains("CanvasBitmap.CreateFromBytes", viewport, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", viewport, StringComparison.Ordinal);
        Assert.Contains("() => UploadTile(device, rendered)", viewport, StringComparison.Ordinal);
        Assert.Contains("RepositorySvgGpuCache.TryReserve", viewport, StringComparison.Ordinal);
        Assert.Contains("DetachPublishedPlan(plan)", viewport, StringComparison.Ordinal);
        Assert.Contains("device.ForceSoftwareRenderer", viewport, StringComparison.Ordinal);
        Assert.Contains("device.IsPixelFormatSupported", viewport, StringComparison.Ordinal);
        Assert.Contains("DirectXPixelFormat.R8G8B8A8UIntNormalized", viewport, StringComparison.Ordinal);
        Assert.Contains("DirectXPixelFormat.B8G8R8A8UIntNormalized", viewport, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteableBitmap", viewport, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelBuffer", viewport, StringComparison.Ordinal);
        Assert.Contains("AutomationControlType.Image", viewportPeer, StringComparison.Ordinal);
        Assert.Contains("_svgRenderer.CacheInvalidated += OnSvgCacheInvalidated", preview, StringComparison.Ordinal);
        Assert.Contains("_documentHasText", preview, StringComparison.Ordinal);
        Assert.Contains("exception.Reason == MarkdownSvgFailureReason.Canceled", preview, StringComparison.Ordinal);
        Assert.Contains("request.CancellationToken.IsCancellationRequested", preview, StringComparison.Ordinal);
        Assert.Contains("MarkdownSvgFailureReason.Timeout", preview, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElement -AppProcessId $appProcessId -AutomationId 'SvgPreviewRenderedImage'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_recent_activity'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_overview'", matrix, StringComparison.Ordinal);
        Assert.Contains("ResizeProcessWindow", matrix, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow", matrix, StringComparison.Ordinal);
        Assert.Contains("Math.Ceiling(width * dpi / DefaultDpi)", matrix, StringComparison.Ordinal);
        Assert.Contains("SetThreadDpiAwarenessContext", matrix, StringComparison.Ordinal);
        Assert.Contains("GetWindowRect", matrix, StringComparison.Ordinal);
        Assert.Contains("actualLogicalWidth", matrix, StringComparison.Ordinal);
        Assert.Contains("int minimumUsableHeight = Math.Min(height, 760);", matrix, StringComparison.Ordinal);
        Assert.Contains("Math.Abs(actualLogicalWidth - width) > 4", matrix, StringComparison.Ordinal);
        Assert.Contains("actualLogicalHeight < minimumUsableHeight", matrix, StringComparison.Ordinal);
        Assert.Contains("EnableHighContrast", matrix, StringComparison.Ordinal);
        Assert.Contains("RestoreHighContrast", matrix, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(1250);", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep(1_250);", matrix, StringComparison.Ordinal);
        Assert.Contains("Add-Type -AssemblyName System.Drawing -ErrorAction Stop", matrix, StringComparison.Ordinal);
        Assert.Contains("finally {", matrix, StringComparison.Ordinal);
        Assert.Contains("Invoke-CompactSectionMatrix", matrix, StringComparison.Ordinal);
        Assert.Contains("-AutomationId $PickerAutomationId -Interaction 'invoke'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForAnyVisibleElement", matrix, StringComparison.Ordinal);
        Assert.Contains("for ($settingsCycle = 0; $settingsCycle -lt 2; $settingsCycle++)", matrix, StringComparison.Ordinal);
        Assert.Contains("Test-VisibleElement", matrix, StringComparison.Ordinal);
        Assert.Contains("$routeUsesCompactLayout", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("$isCompactViewport", matrix, StringComparison.Ordinal);
        Assert.Contains("Action = 'SettingsSection_about'; Target = 'SettingsViewSourceButton'; Interaction = 'invoke'", matrix, StringComparison.Ordinal);
        Assert.Contains("DashboardSideDrawerCloseButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCodeOpenFileTreeButton", matrix, StringComparison.Ordinal);
        Assert.Contains("StarsOpenCategories", matrix, StringComparison.Ordinal);
        Assert.Contains("GistsLeadingPaneButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsSectionComboBox", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsCompactSection_Timeline", matrix, StringComparison.Ordinal);
        Assert.Contains("CommentActionsButton_IssueComment_1000_6E2934754F8C", matrix, StringComparison.Ordinal);
        Assert.Contains("CommentReactionButton_IssueComment_1000_6E2934754F8C_1", matrix, StringComparison.Ordinal);
        Assert.Contains("Assert-MinimumInteractiveSize", matrix, StringComparison.Ordinal);
        Assert.Contains("Show-InteractiveElementThroughScrollHost", matrix, StringComparison.Ordinal);
        Assert.Contains("-ScrollHostAutomationId 'RepoPullRequestsCommentsList'", matrix, StringComparison.Ordinal);
        Assert.Contains("Remove-GeneratedLayoutDirectory", matrix, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Directory]::Delete($resolvedLayout, $true)", matrix, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = 'Continue'", matrix, StringComparison.Ordinal);
        Assert.Contains("$output = @(& winapp @Arguments 2>&1) | ForEach-Object { $_.ToString() }", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("$output = & winapp ui get-property", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName 'repo-pull-requests-comment-actions'", matrix, StringComparison.Ordinal);
        Assert.Contains("ProfileOrganization_JitHubApp", matrix, StringComparison.Ordinal);
        Assert.Contains("ProfileCompactIdentityDetailsButton", matrix, StringComparison.Ordinal);
        Assert.Contains("ProfileCompactIdentityDetailsContent", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName 'profile-organization'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-NativeUiaBooleanProperty", matrix, StringComparison.Ordinal);
        Assert.Contains("SettingsStoreTelemetryToggle", matrix, StringComparison.Ordinal);
        Assert.Contains("$storeTelemetryExpected = $true", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName 'settings-store-telemetry'", matrix, StringComparison.Ordinal);
        Assert.Contains("-Property 'IsKeyboardFocusable' -ExpectedValue $true", matrix, StringComparison.Ordinal);
        Assert.Contains("-Property 'IsPassword' -ExpectedValue $false", matrix, StringComparison.Ordinal);
        Assert.Contains("-Property 'HasKeyboardFocus' -ExpectedValue $true", matrix, StringComparison.Ordinal);
        Assert.Contains("Assert-ElementValueContains", matrix, StringComparison.Ordinal);
        Assert.Contains("-ExpectedText 'public const string Experience'", matrix, StringComparison.Ordinal);
        Assert.Contains("-Property 'HelpText'", matrix, StringComparison.Ordinal);
        Assert.Contains("-Value 'High contrast editor colors active'", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsDiffFilesButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsDiffFileFilterBox", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsDiffFileTree", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName 'repo-commits-diff-files'", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsDiffSearchButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsDiffSearchBox", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName 'repo-commits-diff-search'", matrix, StringComparison.Ordinal);
        Assert.Contains("function Wait-ForElementHidden", matrix, StringComparison.Ordinal);
        Assert.Contains("'ui', 'send-keys', 'esc'", matrix, StringComparison.Ordinal);
        Assert.Contains("--target', 'RepoCommitsDiffSearchBox'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElementHidden -AppProcessId $appProcessId -AutomationId 'RepoCommitsDiffSearchBox'", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsCompareSearchButton", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("RepoCommitsCompareSearchToggleButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsCompareButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsCompareDiffViewer", matrix, StringComparison.Ordinal);
        Assert.Contains("-AutomationId 'RepoCommitsCompareSearchButton' -Property 'IsEnabled' -ExpectedValue $true", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCommitsCompareDiffSearchBox", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName 'repo-commits-compare-search'", matrix, StringComparison.Ordinal);
        Assert.Contains("--scenario=$Scenario", matrix, StringComparison.Ordinal);
        Assert.Contains("--palette=$Palette", matrix, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_DATA_ROOT", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElementProperty", matrix, StringComparison.Ordinal);
        Assert.Contains("-Property 'Name' -Value '5' -Contains", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("-Property 'ItemStatus'", matrix, StringComparison.Ordinal);
        Assert.Contains("PrepareProcessWindowForCapture", matrix, StringComparison.Ordinal);
        Assert.Contains("Measure-ScreenshotComposition", matrix, StringComparison.Ordinal);
        Assert.Contains("BlackSampleRatio", matrix, StringComparison.Ordinal);
        Assert.Contains("$blackRatio -lt 0.85", matrix, StringComparison.Ordinal);
        Assert.Contains("$visibleSampleCount -ge 24", matrix, StringComparison.Ordinal);
        Assert.Contains("HorizontalSignalRatio", matrix, StringComparison.Ordinal);
        Assert.Contains("VerticalSignalRatio", matrix, StringComparison.Ordinal);
        Assert.Contains("-RouteName \"$($route.Name)-failure\"", matrix, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void SvgRuntime_UsesStrictGlobalCacheAndCompletesAsyncShutdownBeforeClose()
    {
        string root = FindRepositoryRoot();
        string cache = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "CodeViewer",
            "RepositorySvgGpuCache.cs"));
        string runtime = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "JitHubMarkdownRuntime.cs"));
        string window = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml.cs"));
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));

        Assert.Contains("internal static partial class RepositorySvgGpuCache", cache, StringComparison.Ordinal);
        Assert.Contains("32L * 1024 * 1024", cache, StringComparison.Ordinal);
        Assert.Contains("64L * 1024 * 1024", cache, StringComparison.Ordinal);
        Assert.Contains("StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap>", cache, StringComparison.Ordinal);
        Assert.Contains("AppMemoryUsageIncreased += OnAppMemoryUsageIncreased", cache, StringComparison.Ordinal);
        Assert.Contains("Cache.Trim();", cache, StringComparison.Ordinal);
        Assert.Contains("RepositorySvgGpuCache.Shutdown();", runtime, StringComparison.Ordinal);
        Assert.Contains("RUST-DEPENDENCY-LICENSES.txt", project, StringComparison.Ordinal);

        int drain = window.IndexOf("private async Task DrainDiagnosticsAndCloseAsync()", StringComparison.Ordinal);
        int shutdown = window.IndexOf("await JitHubMarkdownRuntime.ShutdownAsync();", drain, StringComparison.Ordinal);
        int allowClose = window.IndexOf("_allowCloseAfterDiagnostics = true;", shutdown, StringComparison.Ordinal);
        int close = window.IndexOf("Close();", allowClose, StringComparison.Ordinal);
        Assert.True(drain >= 0);
        Assert.True(shutdown > drain);
        Assert.True(allowClose > shutdown);
        Assert.True(close > allowClose);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void StoreRelease_UsesPrValidatedNativeAotWithoutHardwareDependency()
    {
        string root = FindRepositoryRoot();
        string nativeAotWorkflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "native-aot.yml"));
        string hardwareWorkflowPath = Path.Combine(
            root,
            ".github",
            "workflows",
            "native-aot-hardware-validation.yml");
        string storeWorkflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "jithub-store-release.yml"));

        Assert.False(File.Exists(hardwareWorkflowPath));
        Assert.Contains("pull_request:", nativeAotWorkflow, StringComparison.Ordinal);
        Assert.Contains("- architecture: x86", nativeAotWorkflow, StringComparison.Ordinal);
        Assert.Contains("- architecture: x64", nativeAotWorkflow, StringComparison.Ordinal);
        Assert.Contains("- architecture: arm64", nativeAotWorkflow, StringComparison.Ordinal);
        Assert.Contains("Verify architecture MSIX", nativeAotWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("native_aot_validation_run_id", storeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Require matching hardware validation", storeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions: read", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("'x86|x64|ARM64'", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("microsoft/microsoft-store-apppublisher@v1.4", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("STORE_CLI_VERSION: v0.4.1", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("version: ${{ env.STORE_CLI_VERSION }}", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("STORE_UPLOAD_TIMEOUT_SECONDS: 900", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("'--uploadTimeout'", storeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("msstore --verbose", storeWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void RuntimeBindings_UseGeneratedCustomPropertyProviders()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "JitHub.WinUI");
        string[] sourcePaths = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();
        string allSource = string.Join(Environment.NewLine, sourcePaths.Select(File.ReadAllText));

        Assert.DoesNotContain("[Bindable]", allSource, StringComparison.Ordinal);

        HashSet<string> typedRuntimeBindingSources = FindTypedRuntimeBindingSources(appRoot);
        typedRuntimeBindingSources.UnionWith(new[]
        {
            "RepoPullRequestPageViewModel",
            "RepoCommitsPageViewModel",
            "DashboardPageViewModel",
            "StarLibraryPageViewModel",
        });

        string[] frameworkOrInterfaceSources = ["IPullRequestReviewThreadItem"];
        typedRuntimeBindingSources.ExceptWith(frameworkOrInterfaceSources);

        List<string> violations = [];
        foreach (string typeName in typedRuntimeBindingSources.Order(StringComparer.Ordinal))
        {
            string declarationPattern =
                $@"{Regex.Escape(GeneratedBindableAttribute)}\s*(?:public\s+)?(?:sealed\s+)?partial\s+(?:class|record|struct)\s+{Regex.Escape(typeName)}\b";
            if (!Regex.IsMatch(allSource, declarationPattern, RegexOptions.CultureInvariant))
            {
                violations.Add(typeName);
            }
        }

        Assert.True(
            violations.Count == 0,
            "Runtime Binding sources must be partial and use GeneratedBindableCustomProperty: " +
            string.Join(", ", violations));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void CommentReactionFlyout_PopulatesItsItemsSourceWithoutCompiledBinding()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "CommentInteractionBar.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "CommentInteractionBar.xaml.cs"));

        Assert.DoesNotContain("ItemsSource=\"{x:Bind ReactionOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReactionOptionsItems\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReactionOptionsItems.ItemsSource", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "ReactionOptionsItems.Items.Add(option);",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void CommitDiffRepeater_UsesAnAotProjectableObservableVector()
    {
        string root = FindRepositoryRoot();
        string models = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Commits",
            "CommitDiffModels.cs"));
        string typeRoots = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "NativeAotWinRtTypeRoots.cs"));
        int projectionStart = models.IndexOf(
            "public sealed partial class CommitDiffRowProjection",
            StringComparison.Ordinal);
        Assert.True(projectionStart >= 0, "CommitDiffRowProjection declaration was not found.");
        string projection = models[projectionStart..];

        Assert.Contains(
            "public ObservableCollection<CommitDiffRow> Rows { get; }",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Rows = new ObservableCollection<CommitDiffRow>(rows);",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "GeneratedWinRTExposedExternalType(typeof(ObservableCollection<CommitDiffRow>))",
            typeRoots,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void SettingsSections_PopulateControlItemCollectionsWithoutGenericVectorProjection()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "SettingsPageViewModel.cs"));
        string view = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml.cs"));

        Assert.Contains(
            "public IReadOnlyList<SettingsSectionItem> SettingsSections { get; }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsSections = new SettingsSectionItem[]",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial class SettingsSectionItem",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=", view, StringComparison.Ordinal);
        Assert.Contains("SettingsSectionList.Items.Add(section);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CompactSectionPicker.Items.Add(section);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ThemePaletteRepeater.ItemsSource = ViewModel.PaletteOptions;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PopulateItems(SettingsCacheOwnersList, ViewModel.CacheOwners);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PopulateContributors(SettingsDevelopersList, Developers);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PopulateContributors(SettingsDesignersList, Designers);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("panel.Children.Add(new AppContributorCard(contributor));", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "SynchronizeSectionSelection(ViewModel.SelectedSection);",
            codeBehind,
            StringComparison.Ordinal);
    }

    private static HashSet<string> FindTypedRuntimeBindingSources(string appRoot)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> sources = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.None);
            Visit(document.Root, null);
        }

        return sources;

        void Visit(XElement? element, string? inheritedType)
        {
            if (element is null)
            {
                return;
            }

            string? currentType = element.Attribute(xaml + "DataType")?.Value ?? inheritedType;
            bool hasRuntimeBinding = element.Attributes().Any(attribute =>
                attribute.Value.Contains("{Binding", StringComparison.Ordinal));
            if (hasRuntimeBinding && currentType is not null)
            {
                string typeName = currentType[(currentType.LastIndexOf(':') + 1)..];
                if (!string.Equals(typeName, "TreeViewNode", StringComparison.Ordinal))
                {
                    sources.Add(typeName);
                }
            }

            foreach (XElement child in element.Elements())
            {
                Visit(child, currentType);
            }
        }
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
