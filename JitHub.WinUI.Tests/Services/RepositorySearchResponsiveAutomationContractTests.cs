using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositorySearchResponsiveAutomationContractTests
{
    private static readonly string ProgramPath = FindRepositoryFile(
        "JitHub.WinUI.Automation",
        "Program.cs");

    [Fact]
    public void ProbeRetainsCanonicalWidthsAndBehavioralAssertions()
    {
        string source = File.ReadAllText(ProgramPath);

        Assert.Contains("repo-search-responsive", source, StringComparison.Ordinal);
        Assert.Contains("RunRepositorySearchResponsiveProbe", source, StringComparison.Ordinal);
        Assert.Contains("(1366, 900)", source, StringComparison.Ordinal);
        Assert.Contains("(1180, 800)", source, StringComparison.Ordinal);
        Assert.Contains("(900, 700)", source, StringComparison.Ordinal);
        Assert.Contains("(760, 650)", source, StringComparison.Ordinal);
        Assert.Contains("(640, 600)", source, StringComparison.Ordinal);
        Assert.Contains("content changed page width", source, StringComparison.Ordinal);
        Assert.Contains("was not keyboard reachable", source, StringComparison.Ordinal);
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
