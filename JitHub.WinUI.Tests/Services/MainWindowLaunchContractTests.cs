using System;
using System.IO;
using System.Xml.Linq;
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
    public void ShellDetachesItsNestedFrameAndWaitsBoundedForItToUnload()
    {
        // The close path's order (nested shell content, a renderer drain, the
        // window's page, another drain, then the providers only when no
        // renderer is alive, and the window's close last) is covered by
        // MarkdownViewShutdownTests and MarkdownWindowCloseCoordinatorTests in
        // JitHub.Markdown.Tests, and renderer retirement by
        // MarkdownRendererSlotTests, MarkdownRendererLifetimeRegistryTests and
        // MarkdownRendererDisposalCommitTests. Removing the shell's frame
        // content and observing its Unloaded event need a live XAML runtime.
        string shellPage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs"));
        string detacher = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Helpers", "FrameContentDetacher.cs"));

        Assert.Contains("FrameContentDetacher.DetachAsync(ShellContentFrame,", shellPage, StringComparison.Ordinal);
        Assert.Contains("frame.Content = null", detacher, StringComparison.Ordinal);
        Assert.Contains("await unloaded.Task.WaitAsync(unloadTimeout)", detacher, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRendererTeardownCannotReattachOnSubsequentLoaded()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        // The slot latches the fault before it reports teardown failure; that
        // ordering is covered by MarkdownRendererSlotTests. Refusing to reattach
        // or recreate after the latch, and showing the notice instead, is
        // covered by MarkdownRendererAttachmentTests. This pins the viewer's
        // wiring of the slot's failure report and the attachment to the notice.
        int failureReport = source.IndexOf(
            "void IMarkdownRendererSlotHost<MarkdownRendererControl>.OnRendererTeardownFaulted(",
            StringComparison.Ordinal);
        int showFailure = source.IndexOf("ShowRendererDisposalFailure();", failureReport, StringComparison.Ordinal);

        Assert.True(failureReport >= 0 && showFailure > failureReport);
        Assert.Contains(
            "void IMarkdownRendererAttachmentHost<MarkdownRendererControl>.ShowRendererDisposalFailure() =>\n        ShowRendererDisposalFailure();",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_attachment.EnsureRenderer();", source, StringComparison.Ordinal);
        Assert.Contains("RetryRenderButton.Visibility = Visibility.Collapsed;", source, StringComparison.Ordinal);
        // Keeping the disposal failure notice open across document resets is
        // covered by MarkdownViewerFeedbackTests.
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
