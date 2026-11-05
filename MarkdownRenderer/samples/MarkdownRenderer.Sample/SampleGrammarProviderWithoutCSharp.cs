using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

namespace MarkdownRenderer.Sample;

/// <summary>
/// The common grammar set without C#: the Providers page swaps it in to show
/// a live document falling back for a language its new provider lacks.
/// </summary>
internal sealed class SampleGrammarProviderWithoutCSharp : ITextMateGrammarProvider, IDisposable
{
    private readonly CommonTextMateGrammarProvider _inner = new();

    public int Revision => 1;

    public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsCSharp(request.LanguageId)
            ? ValueTask.FromResult<TextMateGrammarHighlightResult?>(TextMateGrammarHighlightResult.Empty)
            : _inner.HighlightAsync(request, cancellationToken);
    }

    public void Dispose() => _inner.Dispose();

    private static bool IsCSharp(string languageId)
        => string.Equals(languageId, "csharp", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(languageId, "cs", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(languageId, "c#", StringComparison.OrdinalIgnoreCase);
}
