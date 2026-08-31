using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public static partial class CommitDiffParser
{
    private static readonly SemaphoreSlim ParseLane = new(1, 1);

    public static CommitDiffDocument Parse(IEnumerable<GitHubCommitFile>? files)
        => Parse(files, CancellationToken.None);

    public static CommitDiffDocument Parse(
        IEnumerable<GitHubCommitFile>? files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (files is null)
        {
            return CommitDiffDocument.Empty;
        }

        List<CommitDiffFile> parsedFiles = [];
        foreach (GitHubCommitFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parsedFiles.Add(ParseFile(file, cancellationToken));
        }

        return parsedFiles.Count == 0 ? CommitDiffDocument.Empty : new CommitDiffDocument(parsedFiles);
    }

    public static async Task<CommitDiffDocument> ParseAsync(
        IEnumerable<GitHubCommitFile>? files,
        CancellationToken cancellationToken = default)
    {
        await ParseLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => Parse(files, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ParseLane.Release();
        }
    }

    public static CommitDiffFile ParseFile(GitHubCommitFile file)
        => ParseFile(file, CancellationToken.None);

    public static CommitDiffFile ParseFile(
        GitHubCommitFile file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string status = string.IsNullOrWhiteSpace(file.Status) ? "modified" : file.Status!;
        if (string.IsNullOrWhiteSpace(file.Patch))
        {
            return new CommitDiffFile(
                file.Filename,
                file.PreviousFilename,
                status,
                file.Additions,
                file.Deletions,
                file.Changes,
                (CommitDiffLine[])[new CommitDiffLine(CommitDiffLineKind.Binary, null, null, "Binary file or diff unavailable for this file.")],
                isBinaryOrUnavailable: true);
        }

        List<CommitDiffLine> lines = [];
        int? oldLine = null;
        int? newLine = null;
        int diffPosition = 0;

        using StringReader reader = new(file.Patch);
        while (reader.ReadLine() is { } rawLine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Match hunkMatch = HunkHeaderRegex().Match(rawLine);
            if (hunkMatch.Success)
            {
                oldLine = ParseLineNumber(hunkMatch.Groups["old"].Value);
                newLine = ParseLineNumber(hunkMatch.Groups["new"].Value);
                lines.Add(new CommitDiffLine(CommitDiffLineKind.Hunk, null, null, rawLine, null));
                continue;
            }

            if (rawLine.StartsWith("\\", StringComparison.Ordinal))
            {
                lines.Add(new CommitDiffLine(CommitDiffLineKind.NoNewline, null, null, rawLine, null));
                continue;
            }

            diffPosition++;
            if (rawLine.StartsWith("+", StringComparison.Ordinal))
            {
                lines.Add(new CommitDiffLine(CommitDiffLineKind.Addition, null, newLine, TrimDiffMarker(rawLine), diffPosition));
                newLine++;
            }
            else if (rawLine.StartsWith("-", StringComparison.Ordinal))
            {
                lines.Add(new CommitDiffLine(CommitDiffLineKind.Deletion, oldLine, null, TrimDiffMarker(rawLine), diffPosition));
                oldLine++;
            }
            else
            {
                lines.Add(new CommitDiffLine(CommitDiffLineKind.Context, oldLine, newLine, TrimDiffMarker(rawLine), diffPosition));
                oldLine++;
                newLine++;
            }
        }

        return new CommitDiffFile(
            file.Filename,
            file.PreviousFilename,
            status,
            file.Additions,
            file.Deletions,
            file.Changes,
            lines);
    }

    private static int ParseLineNumber(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

    private static string TrimDiffMarker(string value) =>
        value.Length == 0 ? string.Empty : value[1..];

    [GeneratedRegex(@"^@@\s+-(?<old>\d+)(?:,\d+)?\s+\+(?<new>\d+)(?:,\d+)?\s+@@")]
    private static partial Regex HunkHeaderRegex();
}
