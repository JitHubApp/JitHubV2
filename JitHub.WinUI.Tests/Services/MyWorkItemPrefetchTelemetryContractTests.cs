using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MyWorkItemPrefetchTelemetryContractTests
{
    [Theory]
    [InlineData("ScheduleSelectedIssueDwellPrefetch", "IssueTelemetry.TrackPrefetchStarted", "IssueTelemetry.TrackPrefetchCompleted")]
    [InlineData("ScheduleSelectedPullRequestDwellPrefetch", "PullRequestTelemetry.TrackPrefetchStarted", "PullRequestTelemetry.TrackPrefetchCompleted")]
    public void ScheduledDwellPrefetch_OwnsStartedAndTerminalTelemetry(
        string methodName,
        string startedCall,
        string completedCall)
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "MePageModels.cs"));
        string method = ExtractMethod(source, methodName);

        Assert.Contains(startedCall, method, StringComparison.Ordinal);
        Assert.Contains(completedCall, method, StringComparison.Ordinal);
        Assert.Contains("\"my\"", method, StringComparison.Ordinal);
        Assert.Contains("(result, duration) =>", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PrefetchPullRequestForNavigationAsync")]
    [InlineData("PrefetchIssueForNavigationAsync")]
    public void DirectPrefetch_NormalizesThrownFailuresToFailed(string methodName)
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "MePageModels.cs"));
        string method = ExtractMethod(source, methodName);

        Assert.Contains("TelemetryTaxonomy.Results.Failed", method, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TelemetryTaxonomy.Results.Error", method, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string methodName)
    {
        string[] declarationPrefixes = ["private void ", "private async Task ", "private Task "];
        int methodStart = declarationPrefixes
            .Select(prefix => source.IndexOf(prefix + methodName, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        Assert.True(methodStart >= 0, $"Could not find {methodName}.");
        int bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0, $"Could not find the body for {methodName}.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
            if (depth == 0)
            {
                return source[methodStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not parse the body for {methodName}.");
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
