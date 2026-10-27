namespace MarkdownRenderer.Extensions;

/// <summary>
/// Stable parser capability markers understood by the immutable engine.
/// Feature packs register only the narrow parser capabilities they require.
/// </summary>
public static class MarkdownExtensionFeatures
{
    /// <summary>Enables <c>$...$</c> and <c>$$...$$</c> mathematics syntax.</summary>
    public const string DollarMath = "markdown.math.dollar";
}
