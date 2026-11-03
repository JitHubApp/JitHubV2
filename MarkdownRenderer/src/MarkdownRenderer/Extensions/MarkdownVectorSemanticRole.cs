namespace MarkdownRenderer.Extensions;

/// <summary>Semantic role for a child within an atomic vector scene.</summary>
public enum MarkdownVectorSemanticRole
{
    /// <summary>The root diagram.</summary>
    Diagram,
    /// <summary>A semantic grouping.</summary>
    Group,
    /// <summary>A diagram node.</summary>
    Node,
    /// <summary>A relationship or edge.</summary>
    Edge,
    /// <summary>A standalone label.</summary>
    Label,
    /// <summary>A diagram legend.</summary>
    Legend,
}
