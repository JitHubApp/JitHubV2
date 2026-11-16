using System.Diagnostics;
using MarkdownRenderer.Parsing.Internal;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>The parsed document a rebuild lays out, with the parse stage's timestamps.</summary>
/// <param name="Parsed">The Markdig document and its source text.</param>
/// <param name="SemanticDocument">The semantic document the snapshot exposes.</param>
/// <param name="Started">When the parse stage started.</param>
/// <param name="Ended">When the parse stage ended.</param>
internal sealed record RebuildParseResult(
    ParsedMarkdown Parsed,
    MarkdownRenderer.Document.MarkdownDocument SemanticDocument,
    long Started,
    long Ended)
{
    internal double Milliseconds => Stopwatch.GetElapsedTime(Started, Ended).TotalMilliseconds;
}
