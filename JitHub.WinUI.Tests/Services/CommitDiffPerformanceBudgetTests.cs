using System.Diagnostics;
using System.Text;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitDiffPerformanceBudgetTests
{
    [Theory]
    [InlineData(16, 16, 0)]
    [InlineData(10, 16, 0)]
    [InlineData(52.8, 16, 36.8)]
    public void DispatcherLateness_ExcludesExpectedTimerCadence(
        double elapsedMilliseconds,
        double intervalMilliseconds,
        double expectedLatenessMilliseconds)
    {
        double result = CommitDiffPerformanceBudget.CalculateDispatcherLateness(
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            TimeSpan.FromMilliseconds(intervalMilliseconds));

        Assert.Equal(expectedLatenessMilliseconds, result, precision: 3);
    }

    [Fact]
    public void LargeDiff_ParseAndSearch_StayInsideBackgroundWorkBudgets()
    {
        GitHubCommitFile[] files = CreateLargeCommitFixture(fileCount: 36, changedLinePairs: 120);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch parse = Stopwatch.StartNew();

        CommitDiffDocument document = CommitDiffParser.Parse(files);

        parse.Stop();
        long parseAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(36, document.Files.Count);
        Assert.True(document.Rows.Count > 13_000);
        Assert.True(
            parse.Elapsed <= TimeSpan.FromSeconds(1),
            $"Large diff parse took {parse.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.True(
            parseAllocatedBytes <= 128L * 1024 * 1024,
            $"Large diff parse allocated {parseAllocatedBytes / (1024d * 1024d):F1} MiB.");

        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch search = Stopwatch.StartNew();

        CommitDiffRowProjection projection = CommitDiffRowProjection.Create(
            document,
            fileFilterText: null,
            searchText: "PERF_TARGET_35_119");

        search.Stop();
        long searchAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Single(projection.Matches);
        Assert.Equal(document.Rows.Count, projection.Rows.Count);
        Assert.True(
            search.Elapsed <= TimeSpan.FromMilliseconds(500),
            $"Large diff search took {search.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.True(
            searchAllocatedBytes <= 64L * 1024 * 1024,
            $"Large diff search allocated {searchAllocatedBytes / (1024d * 1024d):F1} MiB.");
    }

    private static GitHubCommitFile[] CreateLargeCommitFixture(int fileCount, int changedLinePairs)
    {
        GitHubCommitFile[] files = new GitHubCommitFile[fileCount];
        for (int fileIndex = 0; fileIndex < fileCount; fileIndex++)
        {
            StringBuilder patch = new();
            patch.Append("@@ -1,").Append(changedLinePairs * 3).Append(" +1,").Append(changedLinePairs * 3)
                .AppendLine(" @@ performance_fixture()");
            for (int lineIndex = 0; lineIndex < changedLinePairs; lineIndex++)
            {
                patch.Append(' ').Append("context_line_").Append(fileIndex).Append('_').Append(lineIndex)
                    .AppendLine(" = keep_the_commit_diff_responsive_while_rendering_wrapped_content;");
                patch.Append('-').Append("old_line_").Append(fileIndex).Append('_').Append(lineIndex)
                    .AppendLine(" = a_long_removed_value_that_must_wrap_without_horizontal_scroll;");
                patch.Append('+').Append("new_line_").Append(fileIndex).Append('_').Append(lineIndex)
                    .Append(" = PERF_TARGET_").Append(fileIndex).Append('_').Append(lineIndex)
                    .AppendLine("_a_long_added_value_that_remains_searchable_in_the_virtualized_surface;");
            }

            files[fileIndex] = new GitHubCommitFile
            {
                Filename = $"performance/fixture/file-{fileIndex:D3}.cs",
                Status = "modified",
                Additions = changedLinePairs,
                Deletions = changedLinePairs,
                Changes = changedLinePairs * 2,
                Patch = patch.ToString()
            };
        }

        return files;
    }
}
