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
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
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
