using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ResponsiveViewportAutomationContractTests
{
    [Fact]
    public void ResponsiveProbes_RecordActualNativeBounds()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("static Rectangle ResizeWindow", source, StringComparison.Ordinal);
        Assert.Contains("GetResponsiveViewportLabel", source, StringComparison.Ordinal);
        Assert.Contains("actual={settledBounds.Width}x{settledBounds.Height}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"profile-responsive-{width}x{height}.png\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileResponsiveProbe_NavigatesThroughProductionShell()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));
        int start = source.IndexOf("static void RunProfileResponsiveProbe", StringComparison.Ordinal);
        int end = source.IndexOf("static void RunProfileAvatarRoutingProbe", start, StringComparison.Ordinal);
        string probe = source[start..end];

        Assert.Contains("LaunchApplication(options.AppPath, \"--page=shell\"", probe, StringComparison.Ordinal);
        Assert.Contains("Rectangle shellBounds = ResizeWindow(window, 760, 650);", probe, StringComparison.Ordinal);
        Assert.Contains("ShellProfileTopButton", probe, StringComparison.Ordinal);
        Assert.Contains("ShellProfileTopButton for compact profile navigation", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellUserFooterButton", probe, StringComparison.Ordinal);
        Assert.Contains("string editButtonId = editViewport.Width >= 900", probe, StringComparison.Ordinal);
        Assert.Contains("ProfilePageRoot through shell navigation", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("--page=profile", probe, StringComparison.Ordinal);
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
