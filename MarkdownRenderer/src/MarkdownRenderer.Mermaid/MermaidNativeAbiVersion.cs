namespace MarkdownRenderer.Mermaid;

/// <summary>A version of the fixed-width C ABI used to exchange MMIR buffers.</summary>
public readonly record struct MermaidNativeAbiVersion(ushort Major, ushort Minor);
