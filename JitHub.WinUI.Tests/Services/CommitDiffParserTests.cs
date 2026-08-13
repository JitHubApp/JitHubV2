using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitDiffParserTests
{
    [Fact]
    public void ParsedDocumentCache_UsesCaseInsensitiveLruWithoutDuplicateRecencyEntries()
    {
        CommitDiffDocumentCache cache = new(capacity: 2, maxBytes: 4 * 1024 * 1024);
        CommitDiffDocument first = CreateDocument("first.txt", "+first");
        CommitDiffDocument second = CreateDocument("second.txt", "+second");
        CommitDiffDocument third = CreateDocument("third.txt", "+third");

        Assert.True(cache.TryStore("ABCDEF", first));
        Assert.True(cache.TryStore("second", second));
        Assert.True(cache.TryGet("abcdef", out CommitDiffDocument selected));
        Assert.Same(first, selected);
        Assert.True(cache.TryStore("third", third));

        Assert.True(cache.TryGet("ABCDEF", out _));
        Assert.False(cache.TryGet("second", out _));
        Assert.True(cache.TryGet("third", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ParsedDocumentCache_RejectsSingleDocumentBeyondByteBudget()
    {
        CommitDiffDocument document = CreateDocument("large.txt", $"+{new string('x', 4096)}");
        long estimatedBytes = CommitDiffDocumentCache.EstimateSizeBytes(document);
        CommitDiffDocumentCache cache = new(capacity: 4, maxBytes: estimatedBytes - 1);

        Assert.False(cache.TryStore("large", document));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void ParsedDocumentCache_EvictsLeastRecentDocumentsToHonorByteBudget()
    {
        CommitDiffDocument first = CreateDocument("first.txt", $"+{new string('a', 512)}");
        CommitDiffDocument second = CreateDocument("second.txt", $"+{new string('b', 512)}");
        long budget = CommitDiffDocumentCache.EstimateSizeBytes(first) +
            CommitDiffDocumentCache.EstimateSizeBytes(second) - 1;
        CommitDiffDocumentCache cache = new(capacity: 4, maxBytes: budget);

        Assert.True(cache.TryStore("first", first));
        Assert.True(cache.TryStore("second", second));

        Assert.False(cache.TryGet("first", out _));
        Assert.True(cache.TryGet("second", out _));
        Assert.InRange(cache.CurrentBytes, 1, budget);
    }

    [Fact]
    public async Task ParseAsync_CancelsSupersededLargePatch()
    {
        string patch = "@@ -1 +1 @@\n" + string.Join(
            '\n',
            Enumerable.Range(0, 500_000).Select(static index => $"+generated line {index}"));
        GitHubCommitFile file = new()
        {
            Filename = "generated.txt",
            Status = "modified",
            Additions = 500_000,
            Changes = 500_000,
            Patch = patch
        };
        using CancellationTokenSource cancellation = new();

        Task<CommitDiffDocument> parse = CommitDiffParser.ParseAsync([file], cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parse);
    }

    private static CommitDiffDocument CreateDocument(string fileName, string body) =>
        CommitDiffParser.Parse(
        [
            new GitHubCommitFile
            {
                Filename = fileName,
                Status = "modified",
                Additions = 1,
                Changes = 1,
                Patch = $"@@ -0,0 +1 @@\n{body}"
            }
        ]);

    [Fact]
    public void ParseFile_ParsesHunkLineNumbersAndLineKinds()
    {
        GitHubCommitFile file = new()
        {
            Filename = "src/app.cs",
            Status = "modified",
            Additions = 2,
            Deletions = 1,
            Changes = 3,
            Patch = """
                @@ -10,3 +10,4 @@ public void Run()
                 var before = true;
                -Console.WriteLine("old");
                +Console.WriteLine("new");
                +Console.WriteLine("again");
                \ No newline at end of file
                """
        };

        CommitDiffFile parsed = CommitDiffParser.ParseFile(file);

        Assert.Equal("src/app.cs", parsed.Filename);
        Assert.Equal([CommitDiffLineKind.Hunk, CommitDiffLineKind.Context, CommitDiffLineKind.Deletion, CommitDiffLineKind.Addition, CommitDiffLineKind.Addition, CommitDiffLineKind.NoNewline], parsed.Lines.Select(static line => line.Kind).ToArray());
        Assert.Equal(10, parsed.Lines[1].OldLineNumber);
        Assert.Equal(10, parsed.Lines[1].NewLineNumber);
        Assert.Equal(11, parsed.Lines[2].OldLineNumber);
        Assert.Null(parsed.Lines[2].NewLineNumber);
        Assert.Null(parsed.Lines[3].OldLineNumber);
        Assert.Equal(11, parsed.Lines[3].NewLineNumber);
        Assert.Equal("Console.WriteLine(\"new\");", parsed.Lines[3].Text);
    }

    [Fact]
    public void ParseFile_PreservesRenameMetadata()
    {
        GitHubCommitFile file = new()
        {
            Filename = "src/new-name.cs",
            PreviousFilename = "src/old-name.cs",
            Status = "renamed",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
            Patch = """
                @@ -1 +1 @@
                +namespace NewName;
                """
        };

        CommitDiffFile parsed = CommitDiffParser.ParseFile(file);

        Assert.Equal("src/new-name.cs", parsed.Filename);
        Assert.Equal("src/old-name.cs", parsed.PreviousFilename);
        Assert.Equal("src/new-name.cs from src/old-name.cs", parsed.HeaderText);
    }

    [Fact]
    public void ParseFile_UsesBinaryFallbackWhenPatchIsMissing()
    {
        GitHubCommitFile file = new()
        {
            Filename = "assets/logo.png",
            Status = "modified",
            Changes = 1
        };

        CommitDiffFile parsed = CommitDiffParser.ParseFile(file);

        Assert.True(parsed.IsBinaryOrUnavailable);
        Assert.Single(parsed.Lines);
        Assert.Equal(CommitDiffLineKind.Binary, parsed.Lines[0].Kind);
    }

    [Fact]
    public void Parse_ParsesLargeProvidedPatches()
    {
        string patch = "@@ -1 +1 @@\n" + string.Join('\n', Enumerable.Range(0, 2_600).Select(static index => $"+line {index}"));
        GitHubCommitFile file = new()
        {
            Filename = "generated.txt",
            Status = "modified",
            Additions = 2_600,
            Changes = 2_600,
            Patch = patch
        };

        CommitDiffDocument document = CommitDiffParser.Parse([file]);

        Assert.Single(document.Files);
        Assert.False(document.Files[0].IsLargeDiff);
        Assert.Equal(2_601, document.Files[0].Lines.Count);
        Assert.Contains(document.Files[0].Lines, static line => line.Text == "line 2599");
        Assert.Equal(2_602, document.Rows.Count);
        Assert.Equal(CommitDiffRowKind.FileHeader, document.Rows[0].Kind);
        Assert.Equal(CommitDiffRowKind.HunkHeader, document.Rows[1].Kind);
    }

    [Fact]
    public void Parse_EmitsStableFlattenedRows()
    {
        GitHubCommitFile file = new()
        {
            Filename = "src/app.cs",
            Status = "modified",
            Additions = 1,
            Deletions = 1,
            Changes = 2,
            Patch = """
                @@ -4,2 +4,2 @@
                -old();
                +new();
                """
        };

        CommitDiffDocument document = CommitDiffParser.Parse([file]);

        Assert.Equal(
            [CommitDiffRowKind.FileHeader, CommitDiffRowKind.HunkHeader, CommitDiffRowKind.DiffLine, CommitDiffRowKind.DiffLine],
            document.Rows.Select(static row => row.Kind).ToArray());
        Assert.Equal("file:0:src/app.cs", document.Rows[0].Key);
        Assert.Equal("hunk:0:0", document.Rows[1].Key);
        Assert.Equal("line:0:1:1", document.Rows[2].Key);
        Assert.Equal("line:0:2:2", document.Rows[3].Key);
        Assert.Equal(13, document.Rows[2].LineNumberGutterText.Length);
        Assert.StartsWith("     4", document.Rows[2].LineNumberGutterText, StringComparison.Ordinal);
        Assert.EndsWith("     4", document.Rows[3].LineNumberGutterText, StringComparison.Ordinal);
        Assert.EndsWith(" -", document.Rows[2].GutterText, StringComparison.Ordinal);
        Assert.EndsWith(" +", document.Rows[3].GutterText, StringComparison.Ordinal);
    }

    [Fact]
    public void RowProjection_FiltersByFileAndPreservesFileHeader()
    {
        CommitDiffDocument document = CommitDiffParser.Parse(
        [
            new GitHubCommitFile
            {
                Filename = "src/app.cs",
                Status = "modified",
                Patch = """
                    @@ -1 +1 @@
                    +app
                    """
            },
            new GitHubCommitFile
            {
                Filename = "tests/app.tests.cs",
                Status = "modified",
                Patch = """
                    @@ -1 +1 @@
                    +test
                    """
            }
        ]);

        CommitDiffRowProjection projection = CommitDiffRowProjection.Create(document, "tests", string.Empty);

        Assert.Equal(3, projection.Rows.Count);
        Assert.All(projection.Rows, row => Assert.Equal("tests/app.tests.cs", row.FileName));
        Assert.Equal(CommitDiffRowKind.FileHeader, projection.Rows[0].Kind);
    }

    [Fact]
    public void RowProjection_SearchesFilteredRowsAndMarksMatches()
    {
        CommitDiffDocument document = CommitDiffParser.Parse(
        [
            new GitHubCommitFile
            {
                Filename = "src/app.cs",
                Status = "modified",
                Patch = """
                    @@ -1 +1 @@
                    +needle here
                    +needle again
                    """
            },
            new GitHubCommitFile
            {
                Filename = "docs/readme.md",
                Status = "modified",
                Patch = """
                    @@ -1 +1 @@
                    +needle outside filter
                    """
            }
        ]);

        CommitDiffRowProjection projection = CommitDiffRowProjection.Create(document, "src", "needle");

        Assert.Equal(2, projection.MatchCount);
        Assert.Equal([0, 1], projection.Matches.Select(static match => match.MatchIndex).ToArray());
        Assert.All(projection.Matches, match => Assert.True(match.RowIndex >= 0));
        Assert.Equal(2, projection.Rows.Count(row => row.HasSearchMatches));
    }

    [Fact]
    public void RowProjection_PreindexesStableRowsForConstantTimeViewportLookup()
    {
        CommitDiffDocument document = CommitDiffParser.Parse(
        [
            new GitHubCommitFile
            {
                Filename = "src/app.cs",
                Status = "modified",
                Patch = "@@ -1 +1 @@\n-old\n+new"
            }
        ]);

        CommitDiffRowProjection projection = CommitDiffRowProjection.Create(document, string.Empty, string.Empty);
        CommitDiffRow expected = projection.Rows[2];

        Assert.True(projection.TryGetRow(expected.Key, out CommitDiffRow actual));
        Assert.Same(expected, actual);
        Assert.True(projection.TryGetRowIndex(expected.Key, out int index));
        Assert.Equal(2, index);
        Assert.False(projection.TryGetRow("missing", out _));
        Assert.False(projection.TryGetRowIndex("missing", out _));
    }
}
