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

    internal LegacyCodeHighlighterAdapter(ICodeBlockSyntaxHighlighter inner)
        => Inner = inner ?? throw new ArgumentNullException(nameof(inner));

    internal readonly ICodeBlockSyntaxHighlighter Inner;

    public int Revision => Inner.Revision;

    public async ValueTask<CodeBlockHighlightResult?> HighlightAsync(
        CodeBlockHighlightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CancellationToken == cancellationToken)
            return await Inner.HighlightAsync(request).ConfigureAwait(false);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken,
            cancellationToken);
        var forwarded = new CodeBlockHighlightRequest(
            request.Language,
            request.Code,
            request.ThemeVariant,
            linked.Token);
        return await Inner.HighlightAsync(forwarded).ConfigureAwait(false);
    }
#pragma warning restore CS0618
}
