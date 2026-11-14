using System;

namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>A declarative host-approved link or action associated with scene semantics.</summary>
public sealed class MarkdownVectorLinkAction
{
    /// <summary>Creates a declarative vector link/action.</summary>
    public MarkdownVectorLinkAction(int semanticIndex, string? target, string? action, bool external = false)
    {
        if (semanticIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(semanticIndex));
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("A vector link requires a target or action.");
        SemanticIndex = semanticIndex;
        Target = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        Action = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
        External = external;
    }

    /// <summary>Gets the associated semantic index.</summary>
    public int SemanticIndex { get; }
    /// <summary>Gets the host-approved URI target.</summary>
    public string? Target { get; }
    /// <summary>Gets the host-defined declarative action name.</summary>
    public string? Action { get; }
    /// <summary>Gets whether the native syntax requested external disposition.</summary>
    public bool External { get; }
}
