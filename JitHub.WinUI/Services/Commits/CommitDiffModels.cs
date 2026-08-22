using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace JitHub.Services;

public enum CommitDiffViewMode
{
    Unified,
    Split
}

public enum CommitDiffLineKind
{
    Hunk,
    Context,
    Addition,
    Deletion,
    NoNewline,
    Binary
}

public enum CommitDiffRowKind
{
    FileHeader,
    HunkHeader,
    DiffLine,
    UnavailableDiff,
    SearchNoResults
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffDocument
{
    public static CommitDiffDocument Empty { get; } = new([]);

    public CommitDiffDocument(IReadOnlyList<CommitDiffFile> files)
        : this(files, BuildRows(files))
    {
    }

    private CommitDiffDocument(IReadOnlyList<CommitDiffFile> files, IReadOnlyList<CommitDiffRow> rows)
    {
        Files = files;
        Rows = rows;
    }

    public IReadOnlyList<CommitDiffFile> Files { get; }

    public IReadOnlyList<CommitDiffRow> Rows { get; }

    public bool HasFiles => Files.Count > 0;

    public bool HasRows => Rows.Count > 0;

    public int FileCount => Files.Count;

    private static IReadOnlyList<CommitDiffRow> BuildRows(IReadOnlyList<CommitDiffFile> files)
    {
        if (files.Count == 0)
        {
            return [];
        }

        List<CommitDiffRow> rows = [];
        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            CommitDiffFile file = files[fileIndex];
            rows.Add(CommitDiffRow.CreateFileHeader(file, fileIndex));

            if (file.Lines.Count == 0)
            {
                rows.Add(CommitDiffRow.CreateUnavailable(file, fileIndex, 0, "No diff is available for this file."));
                continue;
            }

            for (int lineIndex = 0; lineIndex < file.Lines.Count; lineIndex++)
            {
                CommitDiffLine line = file.Lines[lineIndex];
                rows.Add(line.Kind switch
                {
                    CommitDiffLineKind.Hunk => CommitDiffRow.CreateHunk(file, line, fileIndex, lineIndex),
                    CommitDiffLineKind.Binary => CommitDiffRow.CreateUnavailable(file, fileIndex, lineIndex, line.Text),
                    _ => CommitDiffRow.CreateDiffLine(file, line, fileIndex, lineIndex)
                });
            }
        }

        return rows;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffFile
{
    public CommitDiffFile(
        string filename,
        string? previousFilename,
        string status,
        int additions,
        int deletions,
        int changes,
        IReadOnlyList<CommitDiffLine> lines,
        bool isBinaryOrUnavailable = false,
        bool isLargeDiff = false)
    {
        Filename = filename;
        PreviousFilename = previousFilename;
        Status = status;
        Additions = additions;
        Deletions = deletions;
        Changes = changes;
        Lines = lines;
        IsBinaryOrUnavailable = isBinaryOrUnavailable;
        IsLargeDiff = isLargeDiff;
    }

    public string Filename { get; }

    public string? PreviousFilename { get; }

    public string Status { get; }

    public int Additions { get; }

    public int Deletions { get; }

    public int Changes { get; }

    public IReadOnlyList<CommitDiffLine> Lines { get; }

    public bool IsBinaryOrUnavailable { get; }

    public bool IsLargeDiff { get; }

    public string HeaderText => string.IsNullOrWhiteSpace(PreviousFilename)
        ? Filename
        : $"{Filename} from {PreviousFilename}";

    public string SummaryText => $"+{Additions.ToString(CultureInfo.CurrentCulture)} -{Deletions.ToString(CultureInfo.CurrentCulture)}";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffLine
{
    public CommitDiffLine(
        CommitDiffLineKind kind,
        int? oldLineNumber,
        int? newLineNumber,
        string text,
        int? diffPosition = null)
    {
        Kind = kind;
        OldLineNumber = oldLineNumber;
        NewLineNumber = newLineNumber;
        Text = text;
        DiffPosition = diffPosition;
    }

    public CommitDiffLineKind Kind { get; }

    public int? OldLineNumber { get; }

    public int? NewLineNumber { get; }

    public string Text { get; }

    public int? DiffPosition { get; }

    public string OldLineDisplay => OldLineNumber?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public string NewLineDisplay => NewLineNumber?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public string Marker => Kind switch
    {
        CommitDiffLineKind.Addition => "+",
        CommitDiffLineKind.Deletion => "-",
        CommitDiffLineKind.Hunk => "@",
        CommitDiffLineKind.NoNewline => "\\",
        _ => string.Empty
    };

    public bool IsHunk => Kind == CommitDiffLineKind.Hunk;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffSearchMatch
{
    public CommitDiffSearchMatch(
        int matchIndex,
        string rowKey,
        int rowIndex,
        int startIndex,
        int length)
    {
        MatchIndex = matchIndex;
        RowKey = rowKey;
        RowIndex = rowIndex;
        StartIndex = startIndex;
        Length = length;
    }

    public int MatchIndex { get; }

    public string RowKey { get; }

    public int RowIndex { get; }

    public int StartIndex { get; }

    public int Length { get; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffRow
{
    private CommitDiffRow(
        string key,
        CommitDiffRowKind kind,
        CommitDiffFile? file,
        CommitDiffLine? line,
        string fileName,
        string headerText,
        string status,
        int additions,
        int deletions,
        int changes,
        string oldLineDisplay,
        string newLineDisplay,
        string marker,
        string text,
        CommitDiffLineKind? lineKind,
        IReadOnlyList<CommitDiffSearchMatch>? searchMatches = null)
    {
        Key = key;
        Kind = kind;
        File = file;
        Line = line;
        FileName = fileName;
        HeaderText = headerText;
        Status = status;
        Additions = additions;
        Deletions = deletions;
        Changes = changes;
        OldLineDisplay = oldLineDisplay;
        NewLineDisplay = newLineDisplay;
        Marker = marker;
        Text = text;
        LineKind = lineKind;
        SearchMatches = searchMatches ?? [];
    }

    public string Key { get; }

    public CommitDiffRowKind Kind { get; }

    public CommitDiffFile? File { get; }

    public CommitDiffLine? Line { get; }

    public string FileName { get; }

    public string HeaderText { get; }

    public string Status { get; }

    public int Additions { get; }

    public int Deletions { get; }

    public int Changes { get; }

    public string SummaryText => $"+{Additions.ToString(CultureInfo.CurrentCulture)} -{Deletions.ToString(CultureInfo.CurrentCulture)}";

    public string OldLineDisplay { get; }

    public string NewLineDisplay { get; }

    public string LineNumberGutterText => $"{OldLineDisplay.PadLeft(6)} {NewLineDisplay.PadLeft(6)}";

    public string Marker { get; }

    public string GutterText => $"{LineNumberGutterText} {Marker}";

    public string Text { get; }

    public CommitDiffLineKind? LineKind { get; }

    public IReadOnlyList<CommitDiffSearchMatch> SearchMatches { get; }

    public string AutomationId => $"CommitDiffRow_{SanitizeAutomationValue(Key)}";

    public string CopyFileAutomationId => $"CommitDiffCopyFile_{SanitizeAutomationValue(Key)}";

    public string AutomationName => Kind switch
    {
        CommitDiffRowKind.FileHeader => $"Changed file {FileName}",
        CommitDiffRowKind.HunkHeader => $"Diff hunk {Text}",
        CommitDiffRowKind.DiffLine => $"{OldLineDisplay} {NewLineDisplay} {Marker} {Text}".Trim(),
        _ => Text
    };

    public bool HasSearchMatches => SearchMatches.Count > 0;

    public bool IsFileHeader => Kind == CommitDiffRowKind.FileHeader;

    public bool IsHunkHeader => Kind == CommitDiffRowKind.HunkHeader;

    public bool IsDiffLine => Kind == CommitDiffRowKind.DiffLine;

    public bool IsUnavailableDiff => Kind == CommitDiffRowKind.UnavailableDiff;

    public bool IsSearchNoResults => Kind == CommitDiffRowKind.SearchNoResults;

    private static string SanitizeAutomationValue(string value) =>
        new(value.Where(char.IsLetterOrDigit).Take(80).ToArray());

    public static CommitDiffRow CreateFileHeader(CommitDiffFile file, int fileIndex) =>
        new(
            $"file:{fileIndex}:{file.Filename}",
            CommitDiffRowKind.FileHeader,
            file,
            null,
            file.Filename,
            file.HeaderText,
            file.Status,
            file.Additions,
            file.Deletions,
            file.Changes,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null);

    public static CommitDiffRow CreateHunk(CommitDiffFile file, CommitDiffLine line, int fileIndex, int lineIndex) =>
        new(
            $"hunk:{fileIndex}:{lineIndex}",
            CommitDiffRowKind.HunkHeader,
            file,
            line,
            file.Filename,
            file.HeaderText,
            file.Status,
            file.Additions,
            file.Deletions,
            file.Changes,
            string.Empty,
            string.Empty,
            line.Marker,
            line.Text,
            line.Kind);

    public static CommitDiffRow CreateDiffLine(CommitDiffFile file, CommitDiffLine line, int fileIndex, int lineIndex) =>
        new(
            $"line:{fileIndex}:{lineIndex}:{line.DiffPosition?.ToString(CultureInfo.InvariantCulture) ?? "meta"}",
            CommitDiffRowKind.DiffLine,
            file,
            line,
            file.Filename,
            file.HeaderText,
            file.Status,
            file.Additions,
            file.Deletions,
            file.Changes,
            line.OldLineDisplay,
            line.NewLineDisplay,
            line.Marker,
            line.Text,
            line.Kind);

    public static CommitDiffRow CreateUnavailable(CommitDiffFile file, int fileIndex, int lineIndex, string text) =>
        new(
            $"unavailable:{fileIndex}:{lineIndex}",
            CommitDiffRowKind.UnavailableDiff,
            file,
            null,
            file.Filename,
            file.HeaderText,
            file.Status,
            file.Additions,
            file.Deletions,
            file.Changes,
            string.Empty,
            string.Empty,
            string.Empty,
            text,
            CommitDiffLineKind.Binary);

    public static CommitDiffRow CreateSearchNoResults(string text) =>
        new(
            "search:no-results",
            CommitDiffRowKind.SearchNoResults,
            null,
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            text,
            null);

    public CommitDiffRow WithSearchMatches(IReadOnlyList<CommitDiffSearchMatch> matches) =>
        new(
            Key,
            Kind,
            File,
            Line,
            FileName,
            HeaderText,
            Status,
            Additions,
            Deletions,
            Changes,
            OldLineDisplay,
            NewLineDisplay,
            Marker,
            Text,
            LineKind,
            matches);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffRowProjection
{
    private readonly Dictionary<string, CommitDiffRow> _rowsByKey;
    private readonly Dictionary<string, int> _rowIndexesByKey;

    public static CommitDiffRowProjection Empty { get; } = new([], [], string.Empty, string.Empty);

    public CommitDiffRowProjection(
        IReadOnlyList<CommitDiffRow> rows,
        IReadOnlyList<CommitDiffSearchMatch> matches,
        string fileFilterText,
        string searchText)
    {
        Rows = new ObservableCollection<CommitDiffRow>(rows);
        Matches = matches;
        FileFilterText = fileFilterText;
        SearchText = searchText;
        _rowsByKey = new Dictionary<string, CommitDiffRow>(rows.Count, StringComparer.Ordinal);
        _rowIndexesByKey = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            CommitDiffRow row = rows[index];
            _rowsByKey[row.Key] = row;
            _rowIndexesByKey[row.Key] = index;
        }
    }

    public ObservableCollection<CommitDiffRow> Rows { get; }

    public IReadOnlyList<CommitDiffSearchMatch> Matches { get; }

    public string FileFilterText { get; }

    public string SearchText { get; }

    public int RowCount => Rows.Count;

    public int MatchCount => Matches.Count;

    public bool HasRows => Rows.Count > 0;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasMatches => Matches.Count > 0;

    public string MatchCountText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return string.Empty;
            }

            return Matches.Count == 1 ? "1 match" : $"{Matches.Count.ToString(CultureInfo.CurrentCulture)} matches";
        }
    }

    internal bool TryGetRow(string key, out CommitDiffRow row) =>
        _rowsByKey.TryGetValue(key, out row!);

    internal bool TryGetRowIndex(string key, out int index) =>
        _rowIndexesByKey.TryGetValue(key, out index);

    public static CommitDiffRowProjection Create(CommitDiffDocument? document, string? fileFilterText, string? searchText)
    {
        CommitDiffDocument source = document ?? CommitDiffDocument.Empty;
        string fileFilter = fileFilterText?.Trim() ?? string.Empty;
        string search = searchText?.Trim() ?? string.Empty;

        IReadOnlyList<CommitDiffRow> filteredRows = FilterRowsByFile(source, fileFilter);
        if (filteredRows.Count == 0)
        {
            string message = string.IsNullOrWhiteSpace(fileFilter)
                ? "No diff is available for this commit."
                : "No files match the current filter.";
            return new CommitDiffRowProjection([CommitDiffRow.CreateSearchNoResults(message)], [], fileFilter, search);
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            return new CommitDiffRowProjection(filteredRows, [], fileFilter, search);
        }

        List<CommitDiffSearchMatch> matches = [];
        CommitDiffRow[] projectedRows = new CommitDiffRow[filteredRows.Count];
        for (int rowIndex = 0; rowIndex < filteredRows.Count; rowIndex++)
        {
            CommitDiffRow row = filteredRows[rowIndex];
            IReadOnlyList<CommitDiffSearchMatch> rowMatches = FindMatches(row, rowIndex, search, matches.Count);
            if (rowMatches.Count > 0)
            {
                matches.AddRange(rowMatches);
                projectedRows[rowIndex] = row.WithSearchMatches(rowMatches);
            }
            else
            {
                projectedRows[rowIndex] = row;
            }
        }

        return new CommitDiffRowProjection(projectedRows, matches, fileFilter, search);
    }

    private static IReadOnlyList<CommitDiffRow> FilterRowsByFile(CommitDiffDocument document, string fileFilter)
    {
        if (document.Rows.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(fileFilter))
        {
            return document.Rows;
        }

        HashSet<string> matchingFiles = document.Files
            .Where(file =>
                file.Filename.Contains(fileFilter, StringComparison.OrdinalIgnoreCase) ||
                (file.PreviousFilename?.Contains(fileFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(static file => file.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (matchingFiles.Count == 0)
        {
            return [];
        }

        return [.. document.Rows.Where(row =>
            !string.IsNullOrWhiteSpace(row.FileName) &&
            matchingFiles.Contains(row.FileName))];
    }

    private static IReadOnlyList<CommitDiffSearchMatch> FindMatches(
        CommitDiffRow row,
        int rowIndex,
        string searchText,
        int startingMatchIndex)
    {
        if (row.Kind is CommitDiffRowKind.FileHeader or CommitDiffRowKind.SearchNoResults ||
            string.IsNullOrEmpty(row.Text))
        {
            return [];
        }

        List<CommitDiffSearchMatch> matches = [];
        int searchStart = 0;
        while (searchStart < row.Text.Length)
        {
            int matchStart = row.Text.IndexOf(searchText, searchStart, StringComparison.OrdinalIgnoreCase);
            if (matchStart < 0)
            {
                break;
            }

            matches.Add(new CommitDiffSearchMatch(
                startingMatchIndex + matches.Count,
                row.Key,
                rowIndex,
                matchStart,
                searchText.Length));
            searchStart = matchStart + Math.Max(1, searchText.Length);
        }

        return matches;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffViewportState
{
    public CommitDiffViewportState(
        double verticalOffset,
        string? anchorRowKey,
        int selectedSearchMatchIndex)
    {
        VerticalOffset = verticalOffset;
        AnchorRowKey = anchorRowKey;
        SelectedSearchMatchIndex = selectedSearchMatchIndex;
    }

    public double VerticalOffset { get; }

    public string? AnchorRowKey { get; }

    public int SelectedSearchMatchIndex { get; }
}
