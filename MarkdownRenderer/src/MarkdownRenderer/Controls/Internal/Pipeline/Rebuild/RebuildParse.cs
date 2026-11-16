using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Parsing.Internal;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>
/// The parse stage of a rebuild. A performance session prepares the document
/// when one is attached; otherwise the engine parses, and without an engine
/// the control's legacy parse cache does.
/// </summary>
internal static class RebuildParse
{
    /// <summary>
    /// Parses <paramref name="inputs"/>. Returns null when no document was
    /// produced; the caller checks cancellation and generation currency.
    /// </summary>
    internal static async Task<RebuildParseResult?> ParseAsync(
        RebuildSourceInputs inputs,
        ParsedMarkdownCache cache,
        object documentOwner,
        CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var reusableDocument = inputs.ReusableDocument;
        string source = inputs.Source;
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument = reusableDocument;
        MarkdownRenderer.Document.MarkdownDocument? reusableParsedDocument =
            reusableDocument?.ParsedDocument is not null ? reusableDocument : null;
        if (inputs.PerformanceSession is { } performanceSession)
        {
            semanticDocument = await performanceSession.ParseAndPrepareDocumentAsync(
                inputs.Engine,
                reusableParsedDocument,
                source,
                inputs.LegacyRegistry,
                documentOwner,
                ct).ConfigureAwait(true);
        }

        ParsedMarkdown? parsed;
        if (semanticDocument?.ParsedDocument is { } semanticAst)
        {
            parsed = new ParsedMarkdown(semanticDocument.Source, semanticAst);
        }
        else if (inputs.Engine is { } engine)
        {
            semanticDocument = await engine.ParseAsync(source, ct).ConfigureAwait(true);
            parsed = semanticDocument.ParsedDocument is { } engineAst
                ? new ParsedMarkdown(semanticDocument.Source, engineAst)
                : null;
        }
        else
        {
            parsed = await cache.GetAsync(source, inputs.LegacyRegistry, ct).ConfigureAwait(true);
            if (parsed is not null)
            {
                semanticDocument = await Task.Run(
                    () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                        parsed.SourceText,
                        parsed.Document,
                        diagnostics: [],
                        ct),
                    CancellationToken.None).ConfigureAwait(true);
            }
        }

        return parsed is null || semanticDocument is null
            ? null
            : new RebuildParseResult(parsed, semanticDocument, started, Stopwatch.GetTimestamp());
    }
}
