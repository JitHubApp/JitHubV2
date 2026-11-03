using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// Immutable producer description for one deferred scene marker: the
/// syntax node, the extension callback and its lifetime, and the readable
/// source fallback used when the producer fails.
/// </summary>
internal sealed class DeferredSceneWork(
    MarkdownSyntaxNode Node,
    MarkdownAsyncNodeRenderer Renderer,
    MarkdownExtensionCallbackLifetime? Lifetime,
    string FallbackSource,
    string? FallbackLanguage,
    bool IsInline)
{
    internal MarkdownSyntaxNode Node { get; } = Node;
    internal MarkdownAsyncNodeRenderer Renderer { get; } = Renderer;
    internal MarkdownExtensionCallbackLifetime? Lifetime { get; } = Lifetime;
    internal string FallbackSource { get; } = FallbackSource;
    internal string? FallbackLanguage { get; } = FallbackLanguage;
    internal bool IsInline { get; } = IsInline;

    internal async ValueTask<MarkdownDeferredSceneResult> RenderAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<MarkdownDiagnostic>();
        var context = new MarkdownExtensionContext(Node, cancellationToken, diagnostics.Add);
        var content = new MarkdownContentBuilder();
        MarkdownExtensionCallbackLifetime.Lease? producerLease = Lifetime?.Enter();
        try
        {
            await Renderer(context, content).ConfigureAwait(false);
        }
        finally
        {
            producerLease?.Dispose();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MarkdownDeferredSceneResult(content.Build(), diagnostics.ToArray());
    }

    internal MarkdownDeferredSceneResult CreateFailureResult(
        string code,
        string message)
    {
        var fallback = new MarkdownContentBuilder();
        if (IsInline)
        {
            fallback.AddText(
                FallbackSource,
                Node.SourceSpan,
                Theming.MarkdownStyleRole.InlineCode,
                MarkdownAccessibilityRole.Code);
        }
        else
        {
            fallback.AddCodeBlock(
                FallbackSource,
                FallbackLanguage,
                Node.SourceSpan,
                Theming.MarkdownStyleRole.CodeBlock);
        }
        return new MarkdownDeferredSceneResult(
            fallback.Build(),
            [new MarkdownDiagnostic(code, MarkdownDiagnosticSeverity.Error, message, Node.SourceSpan)]);
    }
}
