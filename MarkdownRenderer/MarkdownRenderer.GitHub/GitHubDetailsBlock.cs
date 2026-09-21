using System.Text;
using Markdig.Parsers;
using Markdig.Syntax;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// A GitHub HTML details container whose body remains ordinary Markdown blocks.
/// </summary>
internal sealed class GitHubDetailsBlock(BlockParser parser) : ContainerBlock(parser)
{
    private readonly StringBuilder _summaryMarkup = new();
    private bool _hasSummaryLine;

    internal string SummaryMarkup => _summaryMarkup.ToString();

    internal int SummarySourceStart { get; set; } = -1;

    internal bool HasSummary { get; set; }

    internal bool IsReadingSummary { get; set; }

    internal bool IsOpenByDefault { get; set; }

    internal void AppendSummaryLine(string value)
    {
        if (_hasSummaryLine)
            _summaryMarkup.Append('\n');
        _summaryMarkup.Append(value);
        _hasSummaryLine = true;
    }
}
