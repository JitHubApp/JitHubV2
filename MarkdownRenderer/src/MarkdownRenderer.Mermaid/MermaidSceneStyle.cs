namespace MarkdownRenderer.Mermaid;

/// <summary>Backend-neutral paint values referenced by flattened draw commands.</summary>
internal readonly record struct MermaidSceneStyle(
    MermaidRgba32 Stroke,
    MermaidRgba32 Fill,
    float StrokeWidth,
    float Opacity,
    MermaidStyleFlags Flags,
    uint DashGeometryStart,
    uint DashGeometryCount);
