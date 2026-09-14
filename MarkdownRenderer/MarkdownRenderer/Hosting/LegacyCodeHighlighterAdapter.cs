using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Bridges the preview highlighter contract to the stable cancellation-aware
/// service without blocking or taking ownership of the supplied provider.
/// </summary>
internal sealed class LegacyCodeHighlighterAdapter : ICodeHighlighter
{
#pragma warning disable CS0618 // Compatibility adapter owns the obsolete surface.
    private readonly ICodeBlockSyntaxHighlighter _inner;

    internal LegacyCodeHighlighterAdapter(ICodeBlockSyntaxHighlighter inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    internal ICodeBlockSyntaxHighlighter Inner => _inner;

    public int Revision => _inner.Revision;

    public async ValueTask<CodeBlockHighlightResult?> HighlightAsync(
        CodeBlockHighlightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CancellationToken == cancellationToken)
            return await _inner.HighlightAsync(request).ConfigureAwait(false);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken,
            cancellationToken);
        var forwarded = new CodeBlockHighlightRequest(
            request.Language,
            request.Code,
            request.ThemeVariant,
            linked.Token);
        return await _inner.HighlightAsync(forwarded).ConfigureAwait(false);
    }
#pragma warning restore CS0618
}
