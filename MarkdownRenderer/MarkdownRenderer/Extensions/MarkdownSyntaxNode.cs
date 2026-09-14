using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// A parser-independent, immutable syntax node presented to an extension renderer.
/// </summary>
public sealed class MarkdownSyntaxNode
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private static readonly IReadOnlyList<MarkdownSyntaxNode> EmptyChildren =
        Array.Empty<MarkdownSyntaxNode>();

    /// <summary>Initializes an immutable syntax node.</summary>
    public MarkdownSyntaxNode(
        string kind,
        SourceSpan sourceSpan,
        string? literal = null,
        IEnumerable<KeyValuePair<string, string>>? attributes = null,
        IEnumerable<MarkdownSyntaxNode>? children = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ValidateSourceSpan(sourceSpan);

        Kind = kind.Trim();
        SourceSpan = sourceSpan;
        Literal = literal;
        Attributes = CopyAttributes(attributes);
        Children = CopyChildren(children);
    }

    /// <summary>Gets the stable syntax kind used for exact renderer dispatch.</summary>
    public string Kind { get; }
    /// <summary>Gets the half-open UTF-16 source range.</summary>
    public SourceSpan SourceSpan { get; }
    /// <summary>Gets the node's literal value, when one exists.</summary>
    public string? Literal { get; }
    /// <summary>Gets parser-normalized string attributes.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
    /// <summary>Gets immutable child nodes in source order.</summary>
    public IReadOnlyList<MarkdownSyntaxNode> Children { get; }

    private static IReadOnlyDictionary<string, string> CopyAttributes(
        IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null)
            return EmptyAttributes;

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
            copy.Add(attribute.Key, attribute.Value ?? string.Empty);
        }

        return copy.Count == 0
            ? EmptyAttributes
            : new ReadOnlyDictionary<string, string>(copy);
    }

    private static IReadOnlyList<MarkdownSyntaxNode> CopyChildren(
        IEnumerable<MarkdownSyntaxNode>? children)
    {
        if (children is null)
            return EmptyChildren;

        var copy = new List<MarkdownSyntaxNode>();
        foreach (var child in children)
            copy.Add(child ?? throw new ArgumentException("Syntax nodes cannot contain null children.", nameof(children)));

        return copy.Count == 0
            ? EmptyChildren
            : new ReadOnlyCollection<MarkdownSyntaxNode>(copy.ToArray());
    }

    internal static void ValidateSourceSpan(SourceSpan sourceSpan)
    {
        if (sourceSpan.Start < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSpan), "Source offsets cannot be negative.");
        if (sourceSpan.Length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSpan), "Source lengths cannot be negative.");
    }
}

/// <summary>Context supplied to a declarative extension renderer.</summary>
public sealed class MarkdownExtensionContext
{
    private readonly Action<MarkdownDiagnostic>? _reportDiagnostic;
    private int _diagnosticCount;

    /// <summary>Initializes a renderer context.</summary>
    public MarkdownExtensionContext(MarkdownSyntaxNode node, CancellationToken cancellationToken = default)
        : this(node, cancellationToken, reportDiagnostic: null)
    {
    }

    internal MarkdownExtensionContext(
        MarkdownSyntaxNode node,
        CancellationToken cancellationToken,
        Action<MarkdownDiagnostic>? reportDiagnostic)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        CancellationToken = cancellationToken;
        _reportDiagnostic = reportDiagnostic;
    }

    /// <summary>Gets the parser-independent syntax node.</summary>
    public MarkdownSyntaxNode Node { get; }
    /// <summary>Gets the render-pass cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    internal int DiagnosticCount => _diagnosticCount;

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

/// <summary>
/// Produces declarative content for one exact syntax kind.
/// </summary>
/// <remarks>
/// An engine can invoke a renderer concurrently for independent documents.
/// Implementations and captured services must therefore be thread-safe.
/// </remarks>
public delegate void MarkdownNodeRenderer(
    MarkdownExtensionContext context,
    MarkdownContentBuilder content);

/// <summary>
/// Asynchronously produces declarative content for one exact syntax kind.
/// </summary>
/// <remarks>
/// An engine can invoke a renderer concurrently for independent documents.
/// Implementations and captured services must therefore be thread-safe. The
/// returned operation must observe <see cref="MarkdownExtensionContext.CancellationToken"/>.
/// </remarks>
public delegate ValueTask MarkdownAsyncNodeRenderer(
    MarkdownExtensionContext context,
    MarkdownContentBuilder content);
