namespace MarkdownRenderer.Mermaid;

/// <summary>A packed, non-premultiplied RGBA color.</summary>
internal readonly record struct MermaidRgba32(byte Red, byte Green, byte Blue, byte Alpha)
{
    internal static MermaidRgba32 FromPacked(uint value) => new(
        (byte)(value & 0xff),
        (byte)((value >> 8) & 0xff),
        (byte)((value >> 16) & 0xff),
        (byte)((value >> 24) & 0xff));
}
