using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MainWindowLaunchContractTests
{
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
        Assert.Contains("AppWindow.Closing += AppWindow_Closing", source, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("QueueDiagnosticsCloseProbeIfRequested", source, StringComparison.Ordinal);
        Assert.Contains("ShutdownDiagnosticsAsync(TimeSpan.FromSeconds(5))", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
