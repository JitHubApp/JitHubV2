namespace MarkdownRenderer.Mermaid;

/// <summary>Version of the private MMIR scene transport understood by this package.</summary>
internal readonly record struct MermaidSceneVersion(ushort Major, ushort Minor)
{
    public static MermaidSceneVersion Current => new(1, 1);
}
