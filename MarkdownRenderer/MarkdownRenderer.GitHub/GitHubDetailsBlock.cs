using Markdig.Parsers;
using Markdig.Syntax;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// A GitHub HTML details container whose body remains ordinary Markdown blocks.
/// </summary>
internal sealed class GitHubDetailsBlock(BlockParser parser) : ContainerBlock(parser)
{
    internal string SummaryMarkup { get; set; } = string.Empty;

    internal bool IsOpenByDefault { get; set; }
}
