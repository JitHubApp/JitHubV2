using MarkdownRenderer.Extensions;
using MarkdownRenderer.Mermaid.Internal.Scenes;

namespace MarkdownRenderer.Mermaid.Internal.VectorScenes;

/// <summary>
/// Derives each draw command's renderer paint style from its authored MMIR
/// style: restricted to what the opcode paints, and tagged with the High
/// Contrast roles (surface or foreground) that the host recolors.
/// </summary>
internal static class MermaidVectorPaintStyles
{
    internal static MarkdownVectorPaintStyle GetCommandStyle(
        MermaidScene scene,
        int commandIndex,
        MermaidDrawCommand command,
        Dictionary<int, MarkdownVectorPaintStyle> authoredStyleCache,
        Dictionary<MermaidVectorStyleCacheKey, MarkdownVectorPaintStyle> commandStyleCache)
    {
        if (!authoredStyleCache.TryGetValue(command.StyleIndex, out MarkdownVectorPaintStyle? authored) ||
            authored is null)
        {
            authored = CreateAuthoredStyle(scene, command.StyleIndex);
            authoredStyleCache.Add(command.StyleIndex, authored);
        }

        (MarkdownVectorPaintRole fillRole, MarkdownVectorPaintRole strokeRole) =
            GetHighContrastRoles(scene, commandIndex, command, authored);
        var key = new MermaidVectorStyleCacheKey(
            command.StyleIndex,
            command.Opcode,
            fillRole,
            strokeRole);
        if (commandStyleCache.TryGetValue(key, out MarkdownVectorPaintStyle? cached) &&
            cached is not null)
            return cached;

        MarkdownVectorPaintStyle restricted = command.Opcode switch
        {
            MermaidDrawOpcode.FillPath => new MarkdownVectorPaintStyle(
                fillArgb: authored.FillArgb,
                opacity: authored.Opacity,
                fillRule: authored.FillRule),
            MermaidDrawOpcode.StrokePath => new MarkdownVectorPaintStyle(
                // Null is the legacy semantic-foreground sentinel in the public
                // vector contract. Use an explicit transparent fill so a stroked
                // open path is never implicitly closed and painted as a polygon.
                fillArgb: 0x00000000,
                strokeArgb: authored.StrokeArgb,
                strokeWidth: authored.StrokeWidth,
                opacity: authored.Opacity,
                lineCap: authored.LineCap,
                lineJoin: authored.LineJoin,
                nonScalingStroke: authored.NonScalingStroke,
                dashArray: authored.DashArray,
                dashOffset: authored.DashOffset),
            _ => authored,
        };
        MarkdownVectorPaintStyle result = restricted.WithHighContrastRoles(fillRole, strokeRole);
        commandStyleCache.Add(key, result);
        return result;
    }

    private static (MarkdownVectorPaintRole Fill, MarkdownVectorPaintRole Stroke) GetHighContrastRoles(
        MermaidScene scene,
        int commandIndex,
        MermaidDrawCommand command,
        MarkdownVectorPaintStyle style)
    {
        MarkdownVectorPaintRole fillRole = command.Opcode switch
        {
            MermaidDrawOpcode.Rectangle or MermaidDrawOpcode.Ellipse => MarkdownVectorPaintRole.Surface,
            MermaidDrawOpcode.Text => MarkdownVectorPaintRole.Foreground,
            MermaidDrawOpcode.FillPath when IsSurfacePath(scene, commandIndex, command, style) => MarkdownVectorPaintRole.Surface,
            MermaidDrawOpcode.FillPath => MarkdownVectorPaintRole.Foreground,
            _ => MarkdownVectorPaintRole.Foreground,
        };
        MarkdownVectorPaintRole strokeRole = command.Opcode switch
        {
            MermaidDrawOpcode.StrokePath or MermaidDrawOpcode.Line or
            MermaidDrawOpcode.Rectangle or MermaidDrawOpcode.Ellipse or
            MermaidDrawOpcode.Text => MarkdownVectorPaintRole.Foreground,
            _ => MarkdownVectorPaintRole.Foreground,
        };
        return (fillRole, strokeRole);
    }

    private static bool IsSurfacePath(
        MermaidScene scene,
        int commandIndex,
        MermaidDrawCommand command,
        MarkdownVectorPaintStyle style)
    {
        // Filled edge paths are arrowheads. Treat their edge semantic as
        // authoritative before the stroke-width fallback so High Contrast does
        // not recolor the arrowhead to the canvas/background color.
        if ((uint)command.SemanticIndex < (uint)scene.Semantics.Count &&
            scene.Semantics[command.SemanticIndex].Role == MermaidSemanticRole.Edge)
        {
            return false;
        }

        // Sequence messages currently inherit the root Diagram semantic from
        // Merman. Recover their filled SVG marker relationship from the native
        // paint order so the marker follows the preceding message line instead
        // of being mistaken for a diagram surface in Windows High Contrast.
        if (IsFilledLineMarker(scene, commandIndex, command))
            return false;

        if (style.StrokeWidth > 0)
            return true;
        if ((uint)command.SemanticIndex >= (uint)scene.Semantics.Count)
            return false;

        return scene.Semantics[command.SemanticIndex].Role is
            MermaidSemanticRole.Diagram or
            MermaidSemanticRole.Group or
            MermaidSemanticRole.Node or
            MermaidSemanticRole.Legend;
    }

    private static bool IsFilledLineMarker(
        MermaidScene scene,
        int commandIndex,
        MermaidDrawCommand command)
    {
        if (commandIndex <= 0 ||
            (command.Flags & MermaidDrawFlags.ClosedGeometry) == 0 ||
            command.GeometryCount is < 6 or > 12 ||
            (command.GeometryCount & 1) != 0)
        {
            return false;
        }

        MermaidDrawCommand line = scene.Commands[commandIndex - 1];
        if (line.Opcode != MermaidDrawOpcode.Line ||
            line.GeometryCount != 4 ||
            line.SemanticIndex != command.SemanticIndex ||
            (uint)command.StyleIndex >= (uint)scene.Styles.Count ||
            (uint)line.StyleIndex >= (uint)scene.Styles.Count)
        {
            return false;
        }

        MermaidSceneStyle markerStyle = scene.Styles[command.StyleIndex];
        MermaidSceneStyle lineStyle = scene.Styles[line.StyleIndex];
        if (markerStyle.Fill.Alpha == 0 ||
            lineStyle.Stroke.Alpha == 0)
        {
            return false;
        }

        int lineOffset = line.GeometryStart;
        float endX = scene.Geometry[lineOffset + 2];
        float endY = scene.Geometry[lineOffset + 3];
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        int markerEnd = command.GeometryStart + command.GeometryCount;
        for (int i = command.GeometryStart; i < markerEnd; i += 2)
        {
            float x = scene.Geometry[i];
            float y = scene.Geometry[i + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        const float MaximumMarkerExtent = 32f;
        const float EndpointTolerance = 3f;
        return maxX - minX <= MaximumMarkerExtent &&
               maxY - minY <= MaximumMarkerExtent &&
               endX >= minX - EndpointTolerance &&
               endX <= maxX + EndpointTolerance &&
               endY >= minY - EndpointTolerance &&
               endY <= maxY + EndpointTolerance;
    }

    private static MarkdownVectorPaintStyle CreateAuthoredStyle(MermaidScene scene, int index)
    {
        if (index < 0)
            return new MarkdownVectorPaintStyle(fillArgb: null, strokeArgb: null, strokeWidth: 1);
        MermaidSceneStyle style = scene.Styles[index];
        var dashes = new float[checked((int)style.DashGeometryCount)];
        for (int i = 0; i < dashes.Length; i++)
            dashes[i] = scene.Geometry[checked((int)style.DashGeometryStart + i)];
        return new MarkdownVectorPaintStyle(
            ToArgb(style.Fill),
            ToArgb(style.Stroke),
            style.StrokeWidth,
            style.Opacity,
            MarkdownVectorFillRule.Nonzero,
            (style.Flags & MermaidStyleFlags.RoundLineCap) != 0 ? MarkdownVectorLineCap.Round : MarkdownVectorLineCap.Flat,
            (style.Flags & MermaidStyleFlags.RoundLineJoin) != 0 ? MarkdownVectorLineJoin.Round : MarkdownVectorLineJoin.Miter,
            (style.Flags & MermaidStyleFlags.NonScalingStroke) != 0,
            dashes);
    }

    private static uint ToArgb(MermaidRgba32 color) =>
        ((uint)color.Alpha << 24) | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
}
