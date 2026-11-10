using Xunit;

namespace JitHub.WinUI.Tests.Automation;

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
