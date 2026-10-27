namespace MarkdownRenderer.Mermaid;

internal static class MmirFormat
{
    internal const uint Magic = 0x52494D4D; // "MMIR" as little-endian bytes.
    internal const int HeaderSize = 32;
    internal const int SectionDescriptorSize = 20;
    internal const ushort SupportedMajor = 1;
    internal const ushort SupportedMinor = 1;
    internal const int MaximumSectionCount = 64;
    internal const uint NoIndex = uint.MaxValue;

    internal enum SectionKind : uint
    {
        Viewport = 1,
        Strings = 2,
        Styles = 3,
        Geometry = 4,
        DrawCommands = 5,
        Semantics = 6,
        Links = 7,
        Diagnostics = 8,
        SourceMappings = 9,
    }

    internal readonly record struct Section(
        SectionKind Kind,
        int Offset,
        int Length,
        int Count,
        int Stride);
}
