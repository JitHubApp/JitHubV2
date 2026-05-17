using System;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;

namespace MarkdownRenderer.Parsing;

internal sealed class MarkdigParser
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdigParser(MarkdownPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task<ParsedMarkdown?> ParseAsync(string source, CancellationToken ct = default)
    {
        // Fast-path early cancellation without producing a canceled Task. The
        // renderer treats null as cooperative cancellation, avoiding noisy
        // first-chance OperationCanceledException/TaskCanceledException output.
        if (ct.IsCancellationRequested)
            return Task.FromResult<ParsedMarkdown?>(null);

        return Task.Run(() =>
        {
            // Markdown.Parse is synchronous and not cancellation-aware; we just
            // observe the token cooperatively before paying the parse cost.
            // Use IsCancellationRequested + return rather than ThrowIfCancellationRequested
            // so we don't generate an exception object on the cancelled path.
            if (ct.IsCancellationRequested)
                return null;
            var fixedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
            var document = Markdown.Parse(fixedSource, _pipeline);
            return (ParsedMarkdown?)new ParsedMarkdown(fixedSource, document);
        }, CancellationToken.None);
    }

    public ParsedMarkdown Parse(string source)
    {
        var fixedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
        var document = Markdown.Parse(fixedSource, _pipeline);
        return new ParsedMarkdown(fixedSource, document);
    }
}

internal sealed record ParsedMarkdown(string SourceText, MarkdownDocument Document);
