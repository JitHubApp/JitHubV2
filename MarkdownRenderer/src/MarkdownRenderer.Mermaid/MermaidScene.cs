using System.Collections.ObjectModel;

namespace MarkdownRenderer.Mermaid;

/// <summary>Version of the private MMIR scene transport understood by this package.</summary>
public readonly record struct MermaidSceneVersion(ushort Major, ushort Minor)
{
    public static MermaidSceneVersion Current => new(1, 1);
}

/// <summary>A device-independent viewport in Mermaid scene coordinates.</summary>
public readonly record struct MermaidViewport(float X, float Y, float Width, float Height);

/// <summary>A packed, non-premultiplied RGBA color.</summary>
public readonly record struct MermaidRgba32(byte Red, byte Green, byte Blue, byte Alpha)
{
    internal static MermaidRgba32 FromPacked(uint value) => new(
        (byte)(value & 0xff),
        (byte)((value >> 8) & 0xff),
        (byte)((value >> 16) & 0xff),
        (byte)((value >> 24) & 0xff));
}

[Flags]
public enum MermaidStyleFlags : uint
{
    None = 0,
    NonScalingStroke = 1 << 0,
    RoundLineCap = 1 << 1,
    RoundLineJoin = 1 << 2,
}

/// <summary>Backend-neutral paint values referenced by flattened draw commands.</summary>
public readonly record struct MermaidSceneStyle(
    MermaidRgba32 Stroke,
    MermaidRgba32 Fill,
    float StrokeWidth,
    float Opacity,
    MermaidStyleFlags Flags,
    uint DashGeometryStart,
    uint DashGeometryCount);

public enum MermaidDrawOpcode : ushort
{
    GroupBegin = 1,
    GroupEnd = 2,
    ClipBegin = 3,
    ClipEnd = 4,
    FillPath = 5,
    StrokePath = 6,
    Line = 7,
    Rectangle = 8,
    Ellipse = 9,
    Text = 10,
}

[Flags]
public enum MermaidDrawFlags : ushort
{
    None = 0,
    EvenOddFill = 1 << 0,
    ClosedGeometry = 1 << 1,
    Decorative = 1 << 2,
}

[Flags]
public enum MermaidTextFlags : uint
{
    None = 0,
    Italic = 1 << 0,
    RightToLeft = 1 << 1,
    AnchorMiddle = 1 << 2,
    AnchorEnd = 1 << 3,
}

/// <summary>A flattened draw operation. Indices use -1 when a referenced item is absent.</summary>
public readonly record struct MermaidDrawCommand(
    MermaidDrawOpcode Opcode,
    MermaidDrawFlags Flags,
    int StyleIndex,
    int SemanticIndex,
    int TextIndex,
    int GeometryStart,
    int GeometryCount,
    int FontFamilyIndex,
    MermaidTextFlags TextFlags);

public enum MermaidSemanticRole : uint
{
    Diagram = 1,
    Group = 2,
    Node = 3,
    Edge = 4,
    Label = 5,
    Legend = 6,
}

[Flags]
public enum MermaidSemanticFlags : uint
{
    None = 0,
    Focusable = 1 << 0,
    Selectable = 1 << 1,
    Linked = 1 << 2,
    Decorative = 1 << 3,
}

/// <summary>Accessible diagram semantics independent of UI Automation or any drawing backend.</summary>
public readonly record struct MermaidSemanticItem(
    uint SourceId,
    MermaidSemanticRole Role,
    int NameIndex,
    int DescriptionIndex,
    int ParentIndex,
    int SourceMappingIndex,
    MermaidSemanticFlags Flags);

[Flags]
public enum MermaidLinkFlags : uint
{
    None = 0,
    External = 1 << 0,
    Action = 1 << 1,
}

/// <summary>A declarative host-approved link or action. No executable callback crosses the native boundary.</summary>
public readonly record struct MermaidSceneLink(
    int SemanticIndex,
    int TargetIndex,
    int ActionIndex,
    MermaidLinkFlags Flags);

/// <summary>A mapping from a native source ID to a public UTF-16 source range.</summary>
public readonly record struct MermaidSceneSourceMapping(
    uint SourceId,
    MermaidSourceRange SourceRange,
    int SemanticIndex);

/// <summary>An immutable flattened Mermaid scene. It contains no Win2D, Merman, or native lifetime objects.</summary>
public sealed class MermaidScene
{
    internal MermaidScene(
        MermaidSceneVersion version,
        MermaidViewport viewport,
        string[] strings,
        MermaidSceneStyle[] styles,
        float[] geometry,
        MermaidDrawCommand[] commands,
        MermaidSemanticItem[] semantics,
        MermaidSceneLink[] links,
        MermaidSceneSourceMapping[] sourceMappings,
        MermaidDiagnostic[] diagnostics)
    {
        Version = version;
        Viewport = viewport;
        Strings = Array.AsReadOnly(strings);
        Styles = Array.AsReadOnly(styles);
        Geometry = Array.AsReadOnly(geometry);
        Commands = Array.AsReadOnly(commands);
        Semantics = Array.AsReadOnly(semantics);
        Links = Array.AsReadOnly(links);
        SourceMappings = Array.AsReadOnly(sourceMappings);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    private MermaidScene(
        MermaidSceneVersion version,
        MermaidViewport viewport,
        ReadOnlyCollection<string> strings,
        ReadOnlyCollection<MermaidSceneStyle> styles,
        ReadOnlyCollection<float> geometry,
        ReadOnlyCollection<MermaidDrawCommand> commands,
        ReadOnlyCollection<MermaidSemanticItem> semantics,
        ReadOnlyCollection<MermaidSceneLink> links,
        ReadOnlyCollection<MermaidSceneSourceMapping> sourceMappings,
        ReadOnlyCollection<MermaidDiagnostic> diagnostics)
    {
        Version = version;
        Viewport = viewport;
        Strings = strings;
        Styles = styles;
        Geometry = geometry;
        Commands = commands;
        Semantics = semantics;
        Links = links;
        SourceMappings = sourceMappings;
        Diagnostics = diagnostics;
    }

    internal MermaidScene WithDiagnostics(MermaidDiagnostic[] diagnostics) => new(
        Version,
        Viewport,
        Strings,
        Styles,
        Geometry,
        Commands,
        Semantics,
        Links,
        SourceMappings,
        Array.AsReadOnly(diagnostics));

    public MermaidSceneVersion Version { get; }

    public MermaidViewport Viewport { get; }

    public ReadOnlyCollection<string> Strings { get; }

    public ReadOnlyCollection<MermaidSceneStyle> Styles { get; }

    public ReadOnlyCollection<float> Geometry { get; }

    public ReadOnlyCollection<MermaidDrawCommand> Commands { get; }

    public ReadOnlyCollection<MermaidSemanticItem> Semantics { get; }

    public ReadOnlyCollection<MermaidSceneLink> Links { get; }

    public ReadOnlyCollection<MermaidSceneSourceMapping> SourceMappings { get; }

    public ReadOnlyCollection<MermaidDiagnostic> Diagnostics { get; }
}
