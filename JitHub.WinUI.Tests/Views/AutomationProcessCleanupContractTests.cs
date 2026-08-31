using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class AutomationProcessCleanupContractTests
{
    [Fact]
    public void AutomationHarness_UsesEffectiveViewportSizingAndPhysicalPixelCapture()
    {
        string project = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI.Automation",
            "JitHub.WinUI.Automation.csproj"));
        string manifest = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI.Automation",
            "app.manifest"));
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project, StringComparison.Ordinal);
        Assert.Contains(">PerMonitorV2,PerMonitor</dpiAwareness>", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("EnablePerMonitorDpiAwareness", source, StringComparison.Ordinal);
        Assert.Contains("DwmGetWindowAttribute(", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window))", source, StringComparison.Ordinal);
        Assert.Contains("physicalWindowBounds.Width / (double)Math.Max(1, automationWindowBounds.Width)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyExitedOwnedProcess_IsSuccessfulCleanup()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI.Automation",
            "Program.cs"));
        int methodStart = source.IndexOf("static bool WaitForProcessExit", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("static bool TryTerminateOwnedProcess", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string method = source[methodStart..methodEnd];
        Assert.Contains("catch (ArgumentException)", method, StringComparison.Ordinal);
        Assert.Contains("catch (InvalidOperationException)", method, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(method, "return true;"));
        Assert.DoesNotContain("catch (ArgumentException)\n    {\n        return false;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycle_PrefersAReadyPreviewHostBeforeOperatingModeChrome()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("AutomationElement? activePreview", source, StringComparison.Ordinal);
        Assert.Contains("element.Patterns.Text.IsSupported", source, StringComparison.Ordinal);
        Assert.Contains("if (activePreview is null)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLifecycle_CapturesHostPreparationFailures()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("CaptureMarkdownLifecycleFailureState(", source, StringComparison.Ordinal);
        Assert.Contains("\"host preparation failed\"", source, StringComparison.Ordinal);
        Assert.Contains("PrintVisibleAutomationIds(window", source, StringComparison.Ordinal);
        Assert.Contains("diagnostic screenshot failed without replacing the product failure", source, StringComparison.Ordinal);
        Assert.Contains("AutomationWindowHandleCache.TryGet(window", source, StringComparison.Ordinal);
        Assert.Contains("window = ReacquireJitHubWindow(", source, StringComparison.Ordinal);
        Assert.Contains("FindExpectedJitHubWindow(application, automation, processId)", source, StringComparison.Ordinal);
        Assert.Contains("bool alreadySized", source, StringComparison.Ordinal);
    }

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

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
