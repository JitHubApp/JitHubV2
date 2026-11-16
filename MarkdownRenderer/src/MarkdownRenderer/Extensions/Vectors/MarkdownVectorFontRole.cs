namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>
/// Controls whether vector text keeps its authored font family or resolves
/// the family from the host's semantic style at layout time.
/// </summary>
public enum MarkdownVectorFontRole
{
    /// <summary>Uses the font family carried by the vector scene.</summary>
    Authored,
    /// <summary>Uses the font family resolved for the containing markdown style role.</summary>
    Host,
}
