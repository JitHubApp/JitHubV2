using System.Collections.ObjectModel;
using MarkdownRenderer.Mermaid.Internal.Rendering;

namespace MarkdownRenderer.Mermaid.Internal.Scenes;

/// <summary>An immutable flattened Mermaid scene. It contains no Win2D, Merman, or native lifetime objects.</summary>
internal sealed class MermaidScene
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
