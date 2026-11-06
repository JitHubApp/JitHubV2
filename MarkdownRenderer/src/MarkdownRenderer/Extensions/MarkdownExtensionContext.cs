using System;
using System.Threading;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Extensions;

/// <summary>Context supplied to a declarative extension renderer.</summary>
public sealed class MarkdownExtensionContext
{
    private readonly Action<MarkdownDiagnostic>? _reportDiagnostic;
    private readonly MarkdownExtensionSet? _deferredSceneMarkers;
    private int _diagnosticCount;

    /// <summary>Initializes a renderer context.</summary>
    public MarkdownExtensionContext(MarkdownSyntaxNode node, CancellationToken cancellationToken = default)
        : this(node, cancellationToken, reportDiagnostic: null)
    {
    }

    internal MarkdownExtensionContext(
        MarkdownSyntaxNode node,
        CancellationToken cancellationToken,
        Action<MarkdownDiagnostic>? reportDiagnostic,
        MarkdownExtensionSet? deferredSceneMarkers = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        CancellationToken = cancellationToken;
        _reportDiagnostic = reportDiagnostic;
        _deferredSceneMarkers = deferredSceneMarkers;
    }

    /// <summary>Gets the parser-independent syntax node.</summary>
    public MarkdownSyntaxNode Node { get; }
    /// <summary>Gets the render-pass cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    internal int DiagnosticCount => _diagnosticCount;

    /// <summary>
    /// Gets whether the host presents this document progressively. When
    /// <see langword="true"/>, an expensive renderer may add a cheap
    /// placeholder marked with <see cref="MarkdownContentAttributes.DeferredScene"/>
    /// instead of its full output. Before the placeholder becomes visible, the
    /// host calls the renderer again for the same node, with this property
    /// <see langword="false"/>, and shows that result instead.
    /// </summary>
    public bool CanDeferOffscreenScenes => _deferredSceneMarkers is not null;

    /// <summary>
    /// Creates the value for <see cref="MarkdownContentAttributes.DeferredScene"/>
    /// on this node's placeholder. Every call returns a new value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="CanDeferOffscreenScenes"/> is <see langword="false"/>.
    /// </exception>
    public string CreateDeferredSceneMarker()
        => _deferredSceneMarkers?.CreateDeferredSceneMarker(Node.Kind) ??
            throw new InvalidOperationException(
                "This render pass cannot defer scenes; add the node's full content instead.");

    /// <summary>
    /// Reports a localized diagnostic for this node. Diagnostics are captured
    /// in the immutable document returned by the engine.
    /// </summary>
    public void ReportDiagnostic(
        string code,
        MarkdownDiagnosticSeverity severity,
        string message,
        SourceSpan sourceSpan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        MarkdownSyntaxNode.ValidateSourceSpan(sourceSpan);
        if (sourceSpan.Start < Node.SourceSpan.Start || sourceSpan.End > Node.SourceSpan.End)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceSpan),
                "An extension diagnostic must be contained by its syntax node.");
        }

        _diagnosticCount++;
        _reportDiagnostic?.Invoke(new MarkdownDiagnostic(
            code.Trim(),
            severity,
            message,
            sourceSpan));
    }
}
