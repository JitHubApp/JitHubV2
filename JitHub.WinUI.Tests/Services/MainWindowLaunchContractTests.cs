using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MainWindowLaunchContractTests
{
    [Fact]
    public void RootHidesGlobalKeyboardAcceleratorKeyTips()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = Assert.Single(document.Descendants(), element =>
            element.Attribute(xaml + "Name")?.Value == "RootLayout");

        Assert.Equal("Hidden", root.Attribute("KeyboardAcceleratorPlacementMode")?.Value);
    }

    [Fact]
    public void MaterialPolicyTracksWindowsAccessibilityPreferences()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml"));

        Assert.Contains("_uiSettings.AnimationsEnabledChanged += OnVisualEffectsChanged", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.AdvancedEffectsEnabledChanged += OnVisualEffectsChanged", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.AnimationsEnabledChanged -= OnVisualEffectsChanged", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.AdvancedEffectsEnabledChanged -= OnVisualEffectsChanged", source, StringComparison.Ordinal);
        Assert.Contains("MicaController.IsSupported()", source, StringComparison.Ordinal);
        Assert.Contains("AppMaterialPolicy.Evaluate(", source, StringComparison.Ordinal);
        Assert.Contains("ThemePaletteRuntime.SetMaterialEffectsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AppWindowBackgroundBrush}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MicaBackdrop", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ResolvesRequiredNamesBeforeThemeOrActivationAndDrainsBeforeClose()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"RootLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_rootLayout = ResolveRequiredElement(RootLayout", source, StringComparison.Ordinal);
        Assert.Contains("Content is FrameworkElement contentRoot", source, StringComparison.Ordinal);
        Assert.Contains("_rootLayout.RequestedTheme", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RootLayout.RequestedTheme", source, StringComparison.Ordinal);
        int refreshThemeIndex = source.IndexOf("_rootLayout.RequestedTheme = refreshTheme;", StringComparison.Ordinal);
        int resolvedThemeIndex = source.IndexOf("_rootLayout.RequestedTheme = resolvedTheme;", StringComparison.Ordinal);
        Assert.True(refreshThemeIndex >= 0 && resolvedThemeIndex > refreshThemeIndex);
        Assert.Contains("QueueTitleBarColorUpdate", source, StringComparison.Ordinal);
        Assert.Contains("titleBar.ButtonForegroundColor = foreground", source, StringComparison.Ordinal);
        Assert.Contains("titleBar.ButtonHoverBackgroundColor = hoverBackground", source, StringComparison.Ordinal);
        Assert.Contains("titleBar.ButtonPressedBackgroundColor = pressedBackground", source, StringComparison.Ordinal);
        Assert.Contains("TitleBarForegroundTokenProbe", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource AppInkBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource AppRowHoverBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource AppRowPressedBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Closing += AppWindow_Closing", source, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("QueueDiagnosticsCloseProbeIfRequested", source, StringComparison.Ordinal);
        Assert.Contains("ShutdownDiagnosticsAsync(TimeSpan.FromSeconds(5))", source, StringComparison.Ordinal);
        Assert.Contains("StatusDisplayDuration = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("_activationStatusTimer.IsRepeating = false", source, StringComparison.Ordinal);
        Assert.Contains("_activationStatusTimer.Stop();", source, StringComparison.Ordinal);
        Assert.Contains("_activationStatusTimer.Start();", source, StringComparison.Ordinal);
        Assert.Contains("_activationStatusHost.Visibility = Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("Program.CurrentLaunchOptions.WebsiteShowcase", source, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(Program.CurrentLaunchOptions.Page, \"profile\"", source, StringComparison.Ordinal);
        Assert.Contains("_rootLayout.LayoutUpdated += RootLayout_LayoutUpdated", source, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(owner, null)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownShutdownDrainsNestedShellContentAndRendererBorrowersBeforeProviderRetirement()
    {
        string root = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml.cs"));
        string shellPage = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs"));
        string markdownViewer = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"));
        string disposalCommit = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Services", "Markdown", "MarkdownRendererDisposalCommit.cs"));
        int closeFlowStart = mainWindow.IndexOf("private async Task DrainDiagnosticsAndCloseAsync()", StringComparison.Ordinal);
        int shutdownStart = mainWindow.IndexOf(
            "private async Task UnloadPageContentBeforeMarkdownShutdownAsync()",
            StringComparison.Ordinal);
        int shutdownEnd = mainWindow.IndexOf(
            "private async Task DismissActiveContentDialogBeforeCloseAsync()",
            StringComparison.Ordinal);
        Assert.True(closeFlowStart >= 0 && shutdownStart > closeFlowStart && shutdownEnd > shutdownStart);
        string closeFlow = mainWindow[closeFlowStart..shutdownStart];
        string shutdownPath = mainWindow[shutdownStart..shutdownEnd];

        int nestedFrameDetach = shutdownPath.IndexOf(
            "DetachNestedContentForMarkdownShutdownAsync",
            StringComparison.Ordinal);
        int rendererDrain = shutdownPath.IndexOf(
            "DrainMarkdownViewersBeforeProviderShutdownAsync",
            StringComparison.Ordinal);
        int outerFrameDetach = shutdownPath.IndexOf(
            "ContentFrameHost.Content = null",
            StringComparison.Ordinal);
        Assert.True(shutdownStart >= 0 && shutdownEnd > shutdownStart);
        Assert.True(nestedFrameDetach >= 0 && rendererDrain > nestedFrameDetach && outerFrameDetach > rendererDrain);
        Assert.Contains("ShellContentFrame.Content = null", shellPage, StringComparison.Ordinal);
        Assert.Contains("await unloaded.Task.WaitAsync(timeout)", shellPage, StringComparison.Ordinal);

        int rendererWait = shutdownPath.IndexOf(
            "MarkdownViewer.WaitForRendererDisposalAsync(",
            StringComparison.Ordinal);
        Assert.True(rendererWait >= 0);
        int forcedDisposal = shutdownPath.IndexOf(
            "MarkdownViewer.PrepareAllForApplicationShutdown()",
            rendererWait,
            StringComparison.Ordinal);
        int drainCall = closeFlow.IndexOf("await UnloadPageContentBeforeMarkdownShutdownAsync()", StringComparison.Ordinal);
        int providerRetirement = closeFlow.IndexOf(
            "await JitHubMarkdownRuntime.ShutdownAsync()",
            StringComparison.Ordinal);
        Assert.True(forcedDisposal > rendererWait);
        Assert.True(drainCall >= 0 && providerRetirement > drainCall);
        Assert.Contains("if (_markdownRendererDisposalConfirmed)", closeFlow, StringComparison.Ordinal);
        Assert.True(shutdownPath.LastIndexOf("await DrainMarkdownViewersBeforeProviderShutdownAsync()", StringComparison.Ordinal) > outerFrameDetach);
        Assert.Contains("WeakReference<MarkdownViewer>", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(timeout)", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("viewer.PrepareForApplicationShutdown()", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("renderer.Dispose()", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("renderer.DisposalCompleted += OnRendererDisposalCompleted", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("_currentRendererDisposalCompleted", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("retirementBegan: () => _renderer = null", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("drainRetiredResources: renderer.DrainRetiredSnapshotsAsync", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("Microsoft.UI.Dispatching.DispatcherQueue? retirementDispatcher = DispatcherQueue", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("ReleaseRendererTrackingOnDispatcherAsync(renderer, retirementDispatcher)", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("UiTaskGuard.Observe(", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("ObserveRendererRetirementAsync(renderer, retirementCompletion, retirementDispatcher)", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("if (_rendererRetirementPending is not null)", markdownViewer, StringComparison.Ordinal);
        Assert.Contains("_rendererRetirementPending = null;", markdownViewer, StringComparison.Ordinal);
        Assert.True(
            disposalCommit.IndexOf("detachAndDispose();", StringComparison.Ordinal)
                < disposalCommit.IndexOf("disposalCompletedSuccessfully()", StringComparison.Ordinal));
        Assert.True(
            disposalCommit.IndexOf("disposalCompletedSuccessfully()", StringComparison.Ordinal)
                < disposalCommit.IndexOf("releaseTracking();", StringComparison.Ordinal));
        Assert.True(
            disposalCommit.IndexOf("await drain.ConfigureAwait(false)", StringComparison.Ordinal)
                < disposalCommit.IndexOf("await release.ConfigureAwait(false)", StringComparison.Ordinal));
        Assert.Contains("dispatcher.TryEnqueue", markdownViewer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendererTrackingRemainsActiveUntilSnapshotRetirementDrains()
    {
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeRenderers = 1;
        int providerShutdowns = 0;
        bool rendererTracked = true;

        Task completion = MarkdownRendererDisposalCommit.Execute(
            detachAndDispose: static () => { },
            disposalCompletedSuccessfully: static () => true,
            retirementBegan: static () => { },
            drainRetiredResources: () => retirement.Task,
            releaseTracking: () =>
            {
                rendererTracked = false;
                activeRenderers--;
                if (activeRenderers == 0)
                    providerShutdowns++;
                return Task.CompletedTask;
            });

        Assert.True(rendererTracked);
        Assert.Equal(1, activeRenderers);
        Assert.Equal(0, providerShutdowns);
        Assert.False(completion.IsCompleted);

        retirement.SetResult(true);
        await completion;

        Assert.False(rendererTracked);
        Assert.Equal(0, activeRenderers);
        Assert.Equal(1, providerShutdowns);
    }

    [Fact]
    public void RendererWithoutDisposeCompletionDoesNotStartRetirementOrReleaseTracking()
    {
        int activeRenderers = 1;
        bool retirementStarted = false;
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = MarkdownRendererDisposalCommit.Execute(
                detachAndDispose: static () => { },
                disposalCompletedSuccessfully: static () => false,
                retirementBegan: () => retirementStarted = true,
                drainRetiredResources: () =>
                {
                    retirementStarted = true;
                    return Task.CompletedTask;
                },
                releaseTracking: () =>
                {
                    activeRenderers--;
                    return Task.CompletedTask;
                });
        });

        Assert.False(retirementStarted);
        Assert.Equal(1, activeRenderers);
    }

    [Fact]
    public async Task FaultedSnapshotRetirementKeepsRendererTrackingUnresolved()
    {
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Simulated snapshot retirement failure.");
        int activeRenderers = 1;
        bool rendererTracked = true;
        Task completion = MarkdownRendererDisposalCommit.Execute(
            detachAndDispose: static () => { },
            disposalCompletedSuccessfully: static () => true,
            retirementBegan: static () => { },
            drainRetiredResources: () => retirement.Task,
            releaseTracking: () =>
            {
                rendererTracked = false;
                activeRenderers--;
                return Task.CompletedTask;
            });

        retirement.SetException(failure);
        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await completion);

        Assert.Same(failure, observed);
        Assert.True(rendererTracked);
        Assert.Equal(1, activeRenderers);
    }

    [Fact]
    public void IncompleteSnapshotRetirementKeepsRendererTrackingUnresolved()
    {
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeRenderers = 1;
        bool rendererTracked = true;
        Task completion = MarkdownRendererDisposalCommit.Execute(
            detachAndDispose: static () => { },
            disposalCompletedSuccessfully: static () => true,
            retirementBegan: static () => { },
            drainRetiredResources: () => retirement.Task,
            releaseTracking: () =>
            {
                rendererTracked = false;
                activeRenderers--;
                return Task.CompletedTask;
            });

        Assert.False(retirement.Task.IsCompleted);
        Assert.False(completion.IsCompleted);
        Assert.True(rendererTracked);
        Assert.Equal(1, activeRenderers);
    }

    [Fact]
    public void MarkdownRendererDisposeFailureRetainsTrackingUntilSuccessfulRetry()
    {
        int disposeAttempts = 0;
        int activeRendererCount = 1;
        bool rendererTracked = true;
        bool disposalCompleted = false;
        Action detachAndDispose = () =>
        {
            if (++disposeAttempts == 1)
                throw new InvalidOperationException("Simulated renderer disposal failure.");
            disposalCompleted = true;
        };
        Action releaseTracking = () =>
        {
            rendererTracked = false;
            activeRendererCount--;
        };

        Assert.Throws<InvalidOperationException>(() =>
            MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking));
        Assert.True(rendererTracked);
        Assert.Equal(1, activeRendererCount);

        MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking);
        Assert.False(rendererTracked);
        Assert.Equal(0, activeRendererCount);
    }

    [Fact]
    public void MarkdownRendererNoOpRetryCannotConfirmIncompleteDisposal()
    {
        int activeRendererCount = 1;
        bool disposedLatch = false;
        bool disposalCompleted = false;
        Action detachAndDispose = () =>
        {
            if (disposedLatch)
                return;

            disposedLatch = true;
            throw new InvalidOperationException("Teardown failed after latching Dispose.");
        };
        Action releaseTracking = () => activeRendererCount--;

        Assert.Throws<InvalidOperationException>(() =>
            MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking));
        Assert.Throws<InvalidOperationException>(() =>
            MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking));
        Assert.Equal(1, activeRendererCount);
    }

    [Fact]
    public void FailedRendererTeardownCannotReattachOnSubsequentLoaded()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int failureLatch = source.IndexOf("_rendererDisposalFaulted = true;", StringComparison.Ordinal);
        int failureReport = source.IndexOf("ShowRendererDisposalFailure();", failureLatch, StringComparison.Ordinal);
        int ensureRenderer = source.IndexOf("private void EnsureRenderer()", StringComparison.Ordinal);
        int rejectFailedRenderer = source.IndexOf("if (_rendererDisposalFaulted)", ensureRenderer, StringComparison.Ordinal);
        int reattachRenderer = source.IndexOf("RendererHost.Children.Add(_renderer);", ensureRenderer, StringComparison.Ordinal);

        Assert.True(failureLatch >= 0 && failureReport > failureLatch);
        Assert.True(ensureRenderer >= 0 && rejectFailedRenderer > ensureRenderer && reattachRenderer > rejectFailedRenderer);
        Assert.Contains("RetryRenderButton.Visibility = Visibility.Collapsed;", source, StringComparison.Ordinal);
        Assert.Contains("if (!_rendererDisposalFaulted && RenderErrorInfoBar is not null)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
