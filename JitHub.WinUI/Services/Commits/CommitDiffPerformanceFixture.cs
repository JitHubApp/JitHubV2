using System;
using System.Linq;
using System.Text;
using JitHub.Models.GitHub;

namespace JitHub.Services;

internal static class CommitDiffPerformanceFixture
{
    internal const string EnvironmentVariable = "JITHUB_AUTOMATION_LARGE_COMMIT";

    internal static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        "1",
        StringComparison.Ordinal);

    internal static GitHubCommitFile[] CreateFiles(string sha)
    {
        const int fileCount = 36;
        const int changedLinePairs = 120;
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
                    .AppendLine(" = a_long_removed_value_that_must_wrap_without_creating_horizontal_scroll_or_blocking_input;");
                patch.Append('+').Append("new_line_").Append(fileIndex).Append('_').Append(lineIndex)
                    .Append(" = PERF_TARGET_").Append(fileIndex).Append('_').Append(lineIndex)
                    .AppendLine("_a_long_added_value_that_is_searchable_while_virtualized_rows_remain_snappy;");
            }

            files[fileIndex] = new GitHubCommitFile
            {
                Filename = $"performance/{sha}/file-{fileIndex:D3}.cs",
                Status = "modified",
                Additions = changedLinePairs,
                Deletions = changedLinePairs,
                Changes = changedLinePairs * 2,
                Patch = patch.ToString()
            };
        }

        return files;
    }

    internal static GitHubCommitStats CreateStats(GitHubCommitFile[] files) => new()
    {
        Additions = files.Sum(static file => file.Additions),
        Deletions = files.Sum(static file => file.Deletions),
        Total = files.Sum(static file => file.Changes)
    };
}
