using System.Text;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid;

/// <summary>Converts validated private MMIR data to the stable renderer-neutral scene contract.</summary>
internal static class MermaidVectorSceneAdapter
{
    internal static MarkdownVectorScene Convert(
        MermaidScene source,
        MarkdownSyntaxNode syntaxNode,
        string sourceCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(syntaxNode);
        ArgumentNullException.ThrowIfNull(sourceCode);
        cancellationToken.ThrowIfCancellationRequested();

        int contentOffset = GetIntAttribute(syntaxNode, "contentOffset", 0);
        var commands = new List<MarkdownVectorCommand>(source.Commands.Count);
        var semanticBounds = new BoundsAccumulator[source.Semantics.Count];
        var authoredStyleCache = new Dictionary<int, MarkdownVectorPaintStyle>();
        var commandStyleCache = new Dictionary<StyleCacheKey, MarkdownVectorPaintStyle>();
        for (int i = 0; i < source.Commands.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidDrawCommand command = source.Commands[i];
            MarkdownVectorPaintStyle style = GetCommandStyle(
                source,
                i,
                command,
                authoredStyleCache,
                commandStyleCache);
            IReadOnlyList<MarkdownVectorPathOperation> path = ToPath(source, command);
            MarkdownVectorCommand? converted = command.Opcode switch
            {
                MermaidDrawOpcode.GroupBegin => MarkdownVectorCommand.BeginGroup(MarkdownVectorTransform.Identity, command.SemanticIndex),
                MermaidDrawOpcode.GroupEnd => MarkdownVectorCommand.EndGroup(),
                MermaidDrawOpcode.ClipBegin when path.Count > 0 => MarkdownVectorCommand.BeginClip(path),
                MermaidDrawOpcode.ClipEnd => MarkdownVectorCommand.EndClip(),
                MermaidDrawOpcode.FillPath or MermaidDrawOpcode.StrokePath when path.Count > 0 =>
                    MarkdownVectorCommand.DrawPath(
                        path,
                        style,
                        (command.Flags & MermaidDrawFlags.ClosedGeometry) != 0,
                        command.SemanticIndex),
                MermaidDrawOpcode.Line when TryLine(source, command, out MarkdownVectorPoint lineStart, out MarkdownVectorPoint lineEnd) =>
                    MarkdownVectorCommand.DrawLine(
                        lineStart,
                        lineEnd,
                        style,
                        command.SemanticIndex),
                MermaidDrawOpcode.Rectangle when TryRectangle(source, command, out MarkdownVectorRectangle rectangle) =>
                    MarkdownVectorCommand.DrawRectangle(rectangle, style, command.SemanticIndex),
                MermaidDrawOpcode.Ellipse when TryRectangle(source, command, out MarkdownVectorRectangle ellipse) =>
                    MarkdownVectorCommand.DrawEllipse(ellipse, style, command.SemanticIndex),
                MermaidDrawOpcode.Text when TryPoint(source, command, out MarkdownVectorPoint baseline) && command.TextIndex >= 0 =>
                    MarkdownVectorCommand.DrawText(
                        source.Strings[command.TextIndex],
                        baseline,
                        new MarkdownVectorTextStyle(
                            MarkdownVectorFontRole.Host,
                            command.FontFamilyIndex >= 0 ? source.Strings[command.FontFamilyIndex] : "Segoe UI",
                            GetTextSize(source, command),
                            GetTextWeight(source, command),
                            (command.TextFlags & MermaidTextFlags.Italic) != 0,
                            (command.TextFlags & MermaidTextFlags.RightToLeft) != 0,
                            (command.TextFlags & MermaidTextFlags.AnchorMiddle) != 0
                                ? MarkdownVectorTextAlignment.Center
                                : (command.TextFlags & MermaidTextFlags.AnchorEnd) != 0
                                    ? MarkdownVectorTextAlignment.End
                                    : MarkdownVectorTextAlignment.Start),
                        style,
                        command.SemanticIndex),
                _ => null,
            };
            if (converted is null)
                throw new ArgumentException("The validated Mermaid scene contains an unsupported draw command.", nameof(source));
            commands.Add(converted);
            AccumulateBounds(source, command, semanticBounds);
        }

        AccumulateChildBounds(source.Semantics, semanticBounds, cancellationToken);
        string?[] semanticNames = ResolveSemanticNames(source, cancellationToken);
        var semantics = new MarkdownVectorSemanticItem[source.Semantics.Count];
        for (int i = 0; i < semantics.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidSemanticItem item = source.Semantics[i];
            MermaidSceneSourceMapping? mapping = item.SourceMappingIndex >= 0
                ? source.SourceMappings[item.SourceMappingIndex]
                : null;
            SourceSpan mapped = mapping is { } value
                ? MapSourceSpan(syntaxNode.SourceSpan, contentOffset, value.SourceRange, sourceCode.Length)
                : syntaxNode.SourceSpan;
            BoundsAccumulator bounds = semanticBounds[i];
            semantics[i] = new MarkdownVectorSemanticItem(
                item.SourceId,
                ToSemanticRole(item.Role),
                semanticNames[i],
                GetString(source, item.DescriptionIndex),
                item.ParentIndex,
                mapped,
                bounds.HasValue
                    ? new MarkdownVectorRectangle(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
                    : new MarkdownVectorRectangle(
                        source.Viewport.X,
                        source.Viewport.Y,
                        source.Viewport.Width,
                        source.Viewport.Height),
                ToSemanticFlags(item.Flags));
        }

        var links = new MarkdownVectorLinkAction[source.Links.Count];
        for (int i = 0; i < links.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidSceneLink link = source.Links[i];
            links[i] = new MarkdownVectorLinkAction(
                link.SemanticIndex,
                GetString(source, link.TargetIndex),
                GetString(source, link.ActionIndex),
                (link.Flags & MermaidLinkFlags.External) != 0);
        }

        var viewport = new MarkdownVectorRectangle(
            source.Viewport.X,
            source.Viewport.Y,
            source.Viewport.Width,
            source.Viewport.Height);
        return new MarkdownVectorScene(
            source.Viewport.Width,
            source.Viewport.Height,
            source.Viewport.Height,
            commands,
            referenceFontSize: 0,
            viewport,
            semantics,
            links);
    }

    internal static string? GetAccessibleName(
        MermaidScene scene,
        CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < scene.Semantics.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (scene.Semantics[i].Role == MermaidSemanticRole.Diagram &&
                GetString(scene, scene.Semantics[i].NameIndex) is { Length: > 0 } name)
            {
                return name;
            }
        }
        return null;
    }

    internal static string? GetSemanticText(
        MermaidScene scene,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string?[] semanticNames = ResolveSemanticNames(scene, cancellationToken);
        for (int i = 0; i < scene.Semantics.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (semanticNames[i] is not { Length: > 0 } name || !seen.Add(name))
                continue;
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(name);
        }
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidDrawCommand command = scene.Commands[i];
            if (command.Opcode != MermaidDrawOpcode.Text ||
                GetString(scene, command.TextIndex) is not { Length: > 0 } text ||
                !seen.Add(text))
            {
                continue;
            }
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(text);
        }
        return builder.Length == 0 ? GetAccessibleName(scene, cancellationToken) : builder.ToString();
    }

    private static string?[] ResolveSemanticNames(
        MermaidScene scene,
        CancellationToken cancellationToken)
    {
        var names = new string?[scene.Semantics.Count];
        for (int i = 0; i < names.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            names[i] = GetString(scene, scene.Semantics[i].NameIndex);
        }

        // Merman sometimes places a visual label under a semantic SVG group
        // without copying that label onto the group itself. Preserve the text
        // as the native node/edge name, while leaving an untitled diagram root
        // unnamed so the host's localized DiagramName provider remains in use.
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidDrawCommand command = scene.Commands[i];
            if (command.Opcode != MermaidDrawOpcode.Text ||
                command.SemanticIndex < 0 ||
                scene.Semantics[command.SemanticIndex].Role == MermaidSemanticRole.Diagram ||
                names[command.SemanticIndex] is { Length: > 0 } ||
                GetString(scene, command.TextIndex) is not { Length: > 0 } text)
            {
                continue;
            }
            names[command.SemanticIndex] = text;
        }
        return names;
    }

    private static MarkdownVectorPaintStyle GetCommandStyle(
        MermaidScene scene,
        int commandIndex,
        MermaidDrawCommand command,
        Dictionary<int, MarkdownVectorPaintStyle> authoredStyleCache,
        Dictionary<StyleCacheKey, MarkdownVectorPaintStyle> commandStyleCache)
    {
        if (!authoredStyleCache.TryGetValue(command.StyleIndex, out MarkdownVectorPaintStyle? authored) ||
            authored is null)
        {
            authored = CreateAuthoredStyle(scene, command.StyleIndex);
            authoredStyleCache.Add(command.StyleIndex, authored);
        }

        (MarkdownVectorPaintRole fillRole, MarkdownVectorPaintRole strokeRole) =
            GetHighContrastRoles(scene, commandIndex, command, authored);
        var key = new StyleCacheKey(
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

    private static IReadOnlyList<MarkdownVectorPathOperation> ToPath(MermaidScene scene, MermaidDrawCommand command)
    {
        if (command.GeometryCount < 2)
            return Array.Empty<MarkdownVectorPathOperation>();
        var path = new List<MarkdownVectorPathOperation>(command.GeometryCount / 2 + 1);
        for (int i = 0; i + 1 < command.GeometryCount; i += 2)
        {
            int offset = command.GeometryStart + i;
            var point = new MarkdownVectorPoint(scene.Geometry[offset], scene.Geometry[offset + 1]);
            path.Add(i == 0 ? MarkdownVectorPathOperation.MoveTo(point) : MarkdownVectorPathOperation.LineTo(point));
        }
        if ((command.Flags & MermaidDrawFlags.ClosedGeometry) != 0)
            path.Add(MarkdownVectorPathOperation.Close());
        return path;
    }

    private static bool TryRectangle(MermaidScene scene, MermaidDrawCommand command, out MarkdownVectorRectangle rectangle)
    {
        if (command.GeometryCount != 4)
        {
            rectangle = default;
            return false;
        }
        int i = command.GeometryStart;
        rectangle = new MarkdownVectorRectangle(scene.Geometry[i], scene.Geometry[i + 1], scene.Geometry[i + 2], scene.Geometry[i + 3]);
        return true;
    }

    private static bool TryPoint(MermaidScene scene, MermaidDrawCommand command, out MarkdownVectorPoint point)
    {
        if (command.GeometryCount < 2)
        {
            point = default;
            return false;
        }
        int i = command.GeometryStart;
        point = new MarkdownVectorPoint(scene.Geometry[i], scene.Geometry[i + 1]);
        return true;
    }

    private static bool TryLine(
        MermaidScene scene,
        MermaidDrawCommand command,
        out MarkdownVectorPoint start,
        out MarkdownVectorPoint end)
    {
        if (command.GeometryCount != 4)
        {
            start = default;
            end = default;
            return false;
        }
        int i = command.GeometryStart;
        start = new MarkdownVectorPoint(scene.Geometry[i], scene.Geometry[i + 1]);
        end = new MarkdownVectorPoint(scene.Geometry[i + 2], scene.Geometry[i + 3]);
        return true;
    }

    private static float GetTextSize(MermaidScene scene, MermaidDrawCommand command) =>
        command.GeometryCount >= 3 ? Math.Max(1, scene.Geometry[command.GeometryStart + 2]) : 16;

    private static ushort GetTextWeight(MermaidScene scene, MermaidDrawCommand command) =>
        command.GeometryCount >= 4
            ? (ushort)Math.Clamp((int)Math.Round(scene.Geometry[command.GeometryStart + 3]), 1, 999)
            : (ushort)400;

    private static void AccumulateBounds(
        MermaidScene scene,
        MermaidDrawCommand command,
        BoundsAccumulator[] bounds)
    {
        if (command.SemanticIndex < 0 || command.GeometryCount < 2)
            return;
        ref BoundsAccumulator target = ref bounds[command.SemanticIndex];
        if ((command.Opcode is MermaidDrawOpcode.Rectangle or MermaidDrawOpcode.Ellipse) && command.GeometryCount == 4)
        {
            int i = command.GeometryStart;
            target.Add(scene.Geometry[i], scene.Geometry[i + 1]);
            target.Add(scene.Geometry[i] + scene.Geometry[i + 2], scene.Geometry[i + 1] + scene.Geometry[i + 3]);
            return;
        }
        if (command.Opcode == MermaidDrawOpcode.Text && command.TextIndex >= 0)
        {
            int offset = command.GeometryStart;
            float baselineX = scene.Geometry[offset];
            float baselineY = scene.Geometry[offset + 1];
            float size = GetTextSize(scene, command);
            float width = scene.Strings[command.TextIndex].Length * size * 0.55f;
            float left = (command.TextFlags & MermaidTextFlags.AnchorMiddle) != 0
                ? baselineX - width / 2f
                : (command.TextFlags & MermaidTextFlags.AnchorEnd) != 0
                    ? baselineX - width
                    : baselineX;
            target.Add(left, baselineY - size);
            target.Add(left + width, baselineY + size * 0.25f);
            return;
        }
        for (int i = 0; i + 1 < command.GeometryCount; i += 2)
        {
            int offset = command.GeometryStart + i;
            target.Add(scene.Geometry[offset], scene.Geometry[offset + 1]);
        }
    }

    private static void AccumulateChildBounds(
        IReadOnlyList<MermaidSemanticItem> semantics,
        BoundsAccumulator[] bounds,
        CancellationToken cancellationToken)
    {
        // Native semantics are validated as an acyclic parent graph, but their
        // records are not required to be parent-first. Process leaves upward so
        // every descendant bound is propagated exactly once in O(n) time.
        var remainingChildren = new int[semantics.Count];
        var leaves = new Queue<int>(semantics.Count);
        for (int i = 0; i < semantics.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int parent = semantics[i].ParentIndex;
            if (parent < -1 || parent >= semantics.Count || parent == i)
                throw new ArgumentException("The Mermaid semantic parent graph is invalid.", nameof(semantics));
            if (parent >= 0)
                remainingChildren[parent]++;
        }

        for (int i = 0; i < remainingChildren.Length; i++)
        {
            if (remainingChildren[i] == 0)
                leaves.Enqueue(i);
        }

        int processed = 0;
        while (leaves.TryDequeue(out int child))
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, processed++);
            int parent = semantics[child].ParentIndex;
            if (parent < 0)
                continue;

            bounds[parent].Add(bounds[child]);
            if (--remainingChildren[parent] == 0)
                leaves.Enqueue(parent);
        }

        if (processed != semantics.Count)
            throw new ArgumentException("The Mermaid semantic parent graph contains a cycle.", nameof(semantics));
    }

    private readonly record struct StyleCacheKey(
        int StyleIndex,
        MermaidDrawOpcode Opcode,
        MarkdownVectorPaintRole HighContrastFillRole,
        MarkdownVectorPaintRole HighContrastStrokeRole);

    private static void ThrowIfCancellationRequestedPeriodically(
        CancellationToken cancellationToken,
        int index)
    {
        if ((index & 0xff) == 0)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static SourceSpan MapSourceSpan(
        SourceSpan fencedSpan,
        int contentOffset,
        MermaidSourceRange range,
        int sourceLength)
    {
        int start = Math.Clamp(range.Start, 0, sourceLength);
        int length = Math.Clamp(range.Length, 0, sourceLength - start);
        int absoluteStart = checked(fencedSpan.Start + Math.Max(0, contentOffset) + start);
        int maximumEnd = fencedSpan.End;
        if (absoluteStart > maximumEnd)
            return fencedSpan;
        return new SourceSpan(absoluteStart, Math.Min(length, maximumEnd - absoluteStart));
    }

    private static MarkdownVectorSemanticRole ToSemanticRole(MermaidSemanticRole role) => role switch
    {
        MermaidSemanticRole.Diagram => MarkdownVectorSemanticRole.Diagram,
        MermaidSemanticRole.Group => MarkdownVectorSemanticRole.Group,
        MermaidSemanticRole.Node => MarkdownVectorSemanticRole.Node,
        MermaidSemanticRole.Edge => MarkdownVectorSemanticRole.Edge,
        MermaidSemanticRole.Label => MarkdownVectorSemanticRole.Label,
        MermaidSemanticRole.Legend => MarkdownVectorSemanticRole.Legend,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static MarkdownVectorSemanticFlags ToSemanticFlags(MermaidSemanticFlags flags) =>
        (MarkdownVectorSemanticFlags)(int)flags;

    private static string? GetString(MermaidScene scene, int index) =>
        index >= 0 ? scene.Strings[index] : null;

    private static int GetIntAttribute(MarkdownSyntaxNode node, string key, int fallback) =>
        node.Attributes.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : fallback;

    private struct BoundsAccumulator
    {
        internal bool HasValue;
        internal float Left;
        internal float Top;
        internal float Right;
        internal float Bottom;

        internal void Add(float x, float y)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y))
                return;
            if (!HasValue)
            {
                HasValue = true;
                Left = Right = x;
                Top = Bottom = y;
                return;
            }
            Left = Math.Min(Left, x);
            Top = Math.Min(Top, y);
            Right = Math.Max(Right, x);
            Bottom = Math.Max(Bottom, y);
        }

        internal void Add(BoundsAccumulator other)
        {
            if (!other.HasValue)
                return;
            Add(other.Left, other.Top);
            Add(other.Right, other.Bottom);
        }
    }
}
