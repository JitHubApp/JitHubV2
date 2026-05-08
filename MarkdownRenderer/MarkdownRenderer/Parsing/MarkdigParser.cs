using System;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;

namespace MarkdownRenderer.Parsing;

public sealed class MarkdigParser
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdigParser(MarkdownPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task<ParsedMarkdown> ParseAsync(string source, CancellationToken ct = default)
    {
        // Fast-path early cancellation without throwing inside Task.Run — that
        // path produced noisy first-chance OperationCanceledExceptions on the
        // thread-pool stack (visible in the debugger's exception window even
        // though RebuildAsync correctly catches them downstream).
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<ParsedMarkdown>(ct);

        return Task.Run(() =>
        {
            // Markdown.Parse is synchronous and not cancellation-aware; we just
            // observe the token cooperatively before paying the parse cost.
            // Use IsCancellationRequested + return rather than ThrowIfCancellationRequested
            // so we don't generate an exception object on the cancelled path.
            if (ct.IsCancellationRequested)
                return null!;
            var document = Markdown.Parse(source ?? string.Empty, _pipeline);
            return new ParsedMarkdown(source ?? string.Empty, document);
        }, ct);
    }

    public ParsedMarkdown Parse(string source)
    {
        var document = Markdown.Parse(source ?? string.Empty, _pipeline);
        return new ParsedMarkdown(source ?? string.Empty, document);
    }
}

public sealed record ParsedMarkdown(string SourceText, MarkdownDocument Document);
