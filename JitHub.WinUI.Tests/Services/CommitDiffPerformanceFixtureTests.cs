using System.Diagnostics;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitDiffPerformanceFixtureTests
{
    [Fact]
    public void LargeFixture_ExercisesFullVirtualizedRowAndSearchProjection()
    {
        GitHubCommitFile[] files = CommitDiffPerformanceFixture.CreateFiles("abc1234");

        Stopwatch parseTime = Stopwatch.StartNew();
        CommitDiffDocument document = CommitDiffParser.Parse(files);
        parseTime.Stop();
        Stopwatch searchTime = Stopwatch.StartNew();
        CommitDiffRowProjection projection = CommitDiffRowProjection.Create(
            document,
            "file-035.cs",
            "PERF_TARGET_35_119");
        searchTime.Stop();

        Assert.Equal(36, files.Length);
        Assert.True(document.Rows.Count > 12_000);
        Assert.Single(projection.Matches);
        Assert.Contains(projection.Rows, static row => row.FileName.Contains("file-035.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(projection.Rows, static row => row.FileName.Contains("file-034.cs", StringComparison.Ordinal));
        Assert.True(parseTime.Elapsed < TimeSpan.FromSeconds(2), $"Large fixture parse took {parseTime.Elapsed}.");
        Assert.True(searchTime.Elapsed < TimeSpan.FromSeconds(1), $"Large fixture projection took {searchTime.Elapsed}.");
    }

    [Fact]
    public void LargeFixture_StatsMatchGeneratedFiles()
    {
        GitHubCommitFile[] files = CommitDiffPerformanceFixture.CreateFiles("def5678");

        GitHubCommitStats stats = CommitDiffPerformanceFixture.CreateStats(files);

        Assert.Equal(files.Sum(static file => file.Additions), stats.Additions);
        Assert.Equal(files.Sum(static file => file.Deletions), stats.Deletions);
        Assert.Equal(files.Sum(static file => file.Changes), stats.Total);
    }
}
