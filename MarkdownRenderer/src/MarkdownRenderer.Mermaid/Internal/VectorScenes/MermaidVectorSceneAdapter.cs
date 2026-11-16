using System.Text;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Mermaid.Internal.Rendering;
using MarkdownRenderer.Mermaid.Internal.Scenes;

namespace MarkdownRenderer.Mermaid.Internal.VectorScenes;

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
        var semanticBounds = new MermaidBoundsAccumulator[source.Semantics.Count];
        List<MarkdownVectorCommand> commands = ConvertCommands(source, semanticBounds, cancellationToken);

        AccumulateChildBounds(source.Semantics, semanticBounds, cancellationToken);
        MarkdownVectorSemanticItem[] semantics = ConvertSemantics(
            source,
            syntaxNode,
            sourceCode,
            contentOffset,
            semanticBounds,
            cancellationToken);
        MarkdownVectorLinkAction[] links = ConvertLinks(source, cancellationToken);

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

    /// <summary>
    /// Converts every draw command in paint order and accumulates each command's
    /// bounds into its semantic owner.
    /// </summary>
    private static List<MarkdownVectorCommand> ConvertCommands(
        MermaidScene source,
        MermaidBoundsAccumulator[] semanticBounds,
        CancellationToken cancellationToken)
    {
        var commands = new List<MarkdownVectorCommand>(source.Commands.Count);
        var authoredStyleCache = new Dictionary<int, MarkdownVectorPaintStyle>();
        var commandStyleCache = new Dictionary<MermaidVectorStyleCacheKey, MarkdownVectorPaintStyle>();
        for (int i = 0; i < source.Commands.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidDrawCommand command = source.Commands[i];
            MarkdownVectorPaintStyle style = MermaidVectorPaintStyles.GetCommandStyle(
                source,
                i,
                command,
                authoredStyleCache,
                commandStyleCache);
            IReadOnlyList<MarkdownVectorPathOperation> path = ToPath(source, command);
            MarkdownVectorCommand? converted = ConvertCommand(source, command, style, path);
            if (converted is null)
                throw new ArgumentException("The validated Mermaid scene contains an unsupported draw command.", nameof(source));
            commands.Add(converted);
            AccumulateBounds(source, command, semanticBounds);
        }

        return commands;
    }

    private static MarkdownVectorCommand? ConvertCommand(
        MermaidScene source,
        MermaidDrawCommand command,
        MarkdownVectorPaintStyle style,
        IReadOnlyList<MarkdownVectorPathOperation> path) => command.Opcode switch
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
                CreateTextStyle(source, command),
                style,
                command.SemanticIndex),
        _ => null,
    };

    private static MarkdownVectorTextStyle CreateTextStyle(MermaidScene source, MermaidDrawCommand command) => new(
        command.FontFamilyIndex >= 0 ? source.Strings[command.FontFamilyIndex] : "Segoe UI",
        GetTextSize(source, command),
        GetTextWeight(source, command),
        (command.TextFlags & MermaidTextFlags.Italic) != 0,
        (command.TextFlags & MermaidTextFlags.RightToLeft) != 0,
        (command.TextFlags & MermaidTextFlags.AnchorMiddle) != 0
            ? MarkdownVectorTextAlignment.Center
            : (command.TextFlags & MermaidTextFlags.AnchorEnd) != 0
                ? MarkdownVectorTextAlignment.End
                : MarkdownVectorTextAlignment.Start,
        MarkdownVectorFontRole.Authored);

    /// <summary>
    /// Converts the semantic table, mapping each item's source range into the
    /// Markdown document and falling back to the viewport for unpainted items.
    /// </summary>
    private static MarkdownVectorSemanticItem[] ConvertSemantics(
        MermaidScene source,
        MarkdownSyntaxNode syntaxNode,
        string sourceCode,
        int contentOffset,
        MermaidBoundsAccumulator[] semanticBounds,
        CancellationToken cancellationToken)
    {
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
            MermaidBoundsAccumulator bounds = semanticBounds[i];
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

        return semantics;
    }

    private static MarkdownVectorLinkAction[] ConvertLinks(
        MermaidScene source,
        CancellationToken cancellationToken)
    {
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

        return links;
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
        var emittedNamedOwner = new bool[scene.Semantics.Count];

        // Walk the rendered commands in paint order. A named semantic item's
        // first text command owns its complete label; later commands for that
        // item may be shaping fragments and are not separate body text. The
        // semantic table is accessibility metadata and is not guaranteed to
        // have the same ordering as the rendered scene.
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            MermaidDrawCommand command = scene.Commands[i];
            if (command.Opcode != MermaidDrawOpcode.Text ||
                GetString(scene, command.TextIndex) is not { Length: > 0 } rawText)
            {
                continue;
            }

            if ((uint)command.SemanticIndex < (uint)scene.Semantics.Count)
            {
                MermaidSemanticItem owner = scene.Semantics[command.SemanticIndex];
                if (owner.Role != MermaidSemanticRole.Diagram &&
                    GetString(scene, owner.NameIndex) is { Length: > 0 } semanticName)
                {
                    if (emittedNamedOwner[command.SemanticIndex])
                        continue;

                    // Explicit semantic names are the complete accessible
                    // label. Emit one at the first rendered occurrence, not
                    // in semantic-table order, and suppress subsequent owned
                    // shaping chunks for this same semantic identity.
                    emittedNamedOwner[command.SemanticIndex] = true;
                    if (builder.Length > 0)
                        builder.AppendLine();
                    builder.Append(semanticName);
                    continue;
                }
            }

            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(rawText);
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
                (uint)command.SemanticIndex >= (uint)scene.Semantics.Count ||
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
        MermaidBoundsAccumulator[] bounds)
    {
        if (command.SemanticIndex < 0 || command.GeometryCount < 2)
            return;
        ref MermaidBoundsAccumulator target = ref bounds[command.SemanticIndex];
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
        MermaidBoundsAccumulator[] bounds,
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
}
