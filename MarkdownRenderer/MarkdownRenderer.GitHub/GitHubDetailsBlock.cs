using System;
using System.Collections.Generic;
using System.Text;
using Markdig.Parsers;
using Markdig.Syntax;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// A GitHub HTML details container whose body remains ordinary Markdown blocks.
/// </summary>
internal sealed class GitHubDetailsBlock(BlockParser parser) :
    ContainerBlock(parser),
    Gfm029Compatibility.IPreparedSourceRemappable
{
    private StringBuilder _summaryMarkup = new();
    private MarkdownSourceProjectionBuilder _summaryProjection = new();
    private readonly List<SummaryLine> _summaryLines = [];
    private bool _hasSummaryLine;

    internal string SummaryMarkup => _summaryMarkup.ToString();

    internal MarkdownSourceProjection CreateSummarySourceProjection() => _summaryProjection.Build();

    internal int SummarySourceStart { get; set; } = -1;

    internal bool HasSummary { get; set; }

    internal bool IsReadingSummary { get; set; }

    internal bool IsOpenByDefault { get; set; }

    /// <summary>
    /// Number of open details descendants. Markdig asks open block parsers to
    /// continue from outermost to innermost, so an ancestor must defer a close
    /// line until the innermost details block has had the chance to consume it.
    /// </summary>
    internal int OpenDetailsDescendantCount { get; private set; }

    internal void AddOpenDetailsDescendant() => OpenDetailsDescendantCount++;

    internal void RemoveOpenDetailsDescendant()
    {
        if (OpenDetailsDescendantCount <= 0)
            throw new InvalidOperationException("Details descendant tracking became unbalanced.");

        OpenDetailsDescendantCount--;
    }

    internal void AppendSummaryLine(string value, int sourceStart, SourceSpan precedingLineBreak)
    {
        _summaryLines.Add(new SummaryLine(value.Length, sourceStart, precedingLineBreak));
        if (_hasSummaryLine)
        {
            _summaryMarkup.Append('\n');
            _summaryProjection.AppendMapped("\n", precedingLineBreak);
        }

        _summaryMarkup.Append(value);
        _summaryProjection.AppendDirect(value, sourceStart);
        _hasSummaryLine = true;
    }

    void Gfm029Compatibility.IPreparedSourceRemappable.RemapPreparedSource(
        Gfm029Compatibility.PreparedSource prepared,
        string source)
    {
        if (_summaryLines.Count == 0)
            return;

        var remappedMarkup = new StringBuilder();
        var remappedProjection = new MarkdownSourceProjectionBuilder();
        int remappedSummaryStart = -1;
        for (int index = 0; index < _summaryLines.Count; index++)
        {
            SummaryLine line = _summaryLines[index];
            if (index > 0)
            {
                if (line.PrecedingLineBreak.Length <= 0 ||
                    !prepared.TryGetOriginalTextRange(
                        line.PrecedingLineBreak.Start,
                        line.PrecedingLineBreak.Length,
                        source,
                        out int lineBreakStart,
                        out string lineBreakText) ||
                    !IsLineBreak(lineBreakText))
                {
                    throw new InvalidOperationException(
                        "A prepared details summary line break could not be mapped to its authored source.");
                }

                remappedMarkup.Append('\n');
                remappedProjection.AppendMapped("\n", new SourceSpan(lineBreakStart, lineBreakText.Length));
            }

            if (!prepared.TryGetOriginalTextRange(
                    line.SourceStart,
                    line.Length,
                    source,
                    out int lineSourceStart,
                    out string lineText))
            {
                throw new InvalidOperationException(
                    "A prepared details summary line could not be mapped to its authored source.");
            }

            remappedSummaryStart = remappedSummaryStart < 0 ? lineSourceStart : remappedSummaryStart;
            remappedMarkup.Append(lineText);
            remappedProjection.AppendMapped(lineText, new SourceSpan(lineSourceStart, lineText.Length));
        }

        _summaryMarkup = remappedMarkup;
        _summaryProjection = remappedProjection;
        SummarySourceStart = remappedSummaryStart;
    }

    private static bool IsLineBreak(string value)
        => value is "\r" or "\n" or "\r\n";

    private readonly record struct SummaryLine(
        int Length,
        int SourceStart,
        SourceSpan PrecedingLineBreak);
}
