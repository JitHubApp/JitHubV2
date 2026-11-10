
using MarkdownRenderer.Svg.Resvg.Internal.Sources;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>Explicit source materialization helpers for assertions only.</summary>
internal static class SvgSourceBufferTestExtensions
{
    internal static byte[] ReadAllBytes(this SvgSourceBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new byte[source.Length];
        source.CopyTo(copy);
        return copy;
    }

    internal static SvgSourceBuffer ToSourceBuffer(this byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SvgSourceBuffer.CopyFrom(source);
    }
}
