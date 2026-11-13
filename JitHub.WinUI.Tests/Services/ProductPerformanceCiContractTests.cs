using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceCiContractTests
{
    [Fact]
    public void CiWorkflow_UsesCanonicalScriptAndPreservesMachineReadableReport()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "product-performance-gate.yml"));
        string script = File.ReadAllText(Path.Combine(root, "eng", "Invoke-ProductPerformanceGate.ps1"));

        Assert.Contains("Invoke-ProductPerformanceGate.ps1", workflow);
        Assert.Contains("-PlanOnly", workflow);
        Assert.Contains("FullyQualifiedName~ProductPerformance", workflow);
        Assert.Contains("JitHub.WinUI.Automation\\JitHub.WinUI.Automation.csproj", workflow);
        Assert.True(File.Exists(Path.Combine(root, "JitHub.WinUI.Automation", "ProductPerformanceReport.cs")));
        Assert.Contains("runs-on: windows-latest", workflow);
        Assert.Contains("push:", workflow);
        Assert.Contains("pull_request:", workflow);
        Assert.DoesNotContain("push:\n    branches:", workflow);
        Assert.DoesNotContain("paths:", workflow);
        Assert.DoesNotContain("github.event_name == 'schedule'", workflow);
        Assert.DoesNotContain("inputs.run_live", workflow);
        // The live benchmark needs an interactive desktop and runs locally;
        // a job waiting for a self-hosted runner never runs and is cancelled.
        Assert.DoesNotContain("self-hosted", workflow);
        Assert.DoesNotContain("product-performance-report", workflow);
        Assert.Contains("[Environment]::UserInteractive", script);
        Assert.Contains("bin\\x64\\$Configuration", script);
        Assert.Contains("exit $LASTEXITCODE", script);
        Assert.DoesNotContain("--no-build", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
