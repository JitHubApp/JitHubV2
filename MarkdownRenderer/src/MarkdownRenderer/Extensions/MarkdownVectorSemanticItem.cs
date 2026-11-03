using System;

namespace MarkdownRenderer.Extensions;

/// <summary>Immutable, stable semantic identity and bounds within a vector scene.</summary>
public sealed class MarkdownVectorSemanticItem
{
    /// <summary>Creates an immutable vector semantic item.</summary>
    public MarkdownVectorSemanticItem(
        uint sourceId,
        MarkdownVectorSemanticRole role,
        string? name,
        string? description,
        int parentIndex,
        SourceSpan sourceSpan,
        MarkdownVectorRectangle bounds,
        MarkdownVectorSemanticFlags flags = MarkdownVectorSemanticFlags.None)
    {
        if (sourceId == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (parentIndex < -1)
            throw new ArgumentOutOfRangeException(nameof(parentIndex));
        if ((flags & ~(MarkdownVectorSemanticFlags.Focusable |
                       MarkdownVectorSemanticFlags.Selectable |
                       MarkdownVectorSemanticFlags.Linked |
                       MarkdownVectorSemanticFlags.Decorative)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        MarkdownSyntaxNode.ValidateSourceSpan(sourceSpan);
        SourceId = sourceId;
        Role = role;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ParentIndex = parentIndex;
        SourceSpan = sourceSpan;
        Bounds = bounds;
        Flags = flags;
    }

    /// <summary>Gets the stable source-derived identity.</summary>
    public uint SourceId { get; }
    /// <summary>Gets the semantic role.</summary>
    public MarkdownVectorSemanticRole Role { get; }
    /// <summary>Gets the accessible name.</summary>
    public string? Name { get; }
    /// <summary>Gets the accessible description.</summary>
    public string? Description { get; }
    /// <summary>Gets the parent semantic index, or -1 for a root.</summary>
    public int ParentIndex { get; }
    /// <summary>Gets the half-open UTF-16 markdown source range.</summary>
    public SourceSpan SourceSpan { get; }
    /// <summary>Gets bounds in scene coordinates.</summary>
    public MarkdownVectorRectangle Bounds { get; }
    /// <summary>Gets behavioral flags.</summary>
    public MarkdownVectorSemanticFlags Flags { get; }
}
