using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Immutable, thread-safe vector output emitted by a feature pack. It contains
/// no UI, graphics-device, or native-lifetime object.
/// </summary>
public sealed class MarkdownVectorScene
{
    private readonly MarkdownVectorLinkAction?[] _linkBySemanticIndex;
    internal readonly IReadOnlyList<MarkdownVectorLinkAction?> SemanticLinks;
    private readonly int[] _semanticDepths;

    /// <summary>
    /// Creates an immutable vector scene with no reference font size, so the renderer
    /// applies its documented fallback; see <see cref="ReferenceFontSize"/>.
    /// </summary>
    public MarkdownVectorScene(
        float width,
        float height,
        float baseline,
        IReadOnlyList<MarkdownVectorCommand> commands)
        : this(width, height, baseline, commands, referenceFontSize: 0)
    {
    }

    /// <summary>Creates an immutable vector scene.</summary>
    public MarkdownVectorScene(
        float width,
        float height,
        float baseline,
        IReadOnlyList<MarkdownVectorCommand> commands,
        float referenceFontSize)
        : this(
            width,
            height,
            baseline,
            commands,
            referenceFontSize,
            new MarkdownVectorRectangle(0, 0, width, height),
            Array.Empty<MarkdownVectorSemanticItem>(),
            Array.Empty<MarkdownVectorLinkAction>())
    {
    }

    /// <summary>Creates a rich immutable vector scene with semantics and link actions.</summary>
    public MarkdownVectorScene(
        float width,
        float height,
        float baseline,
        IReadOnlyList<MarkdownVectorCommand> commands,
        float referenceFontSize,
        MarkdownVectorRectangle viewport,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        IReadOnlyList<MarkdownVectorLinkAction> links)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            !float.IsFinite(baseline) || !float.IsFinite(referenceFontSize) ||
            width < 0 || height < 0 || baseline < 0 || baseline > height || referenceFontSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Scene dimensions must be finite and non-negative.");
        }

        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(links);
        MarkdownVectorCommand[] copy = CopyNestedCommands(commands);
        MarkdownVectorSemanticItem[] semanticCopy = CopySemantics(semantics);
        int[] semanticDepths = ValidateSemanticHierarchy(semanticCopy);
        ValidateCommandSemantics(copy, semanticCopy.Length);
        MarkdownVectorLinkAction[] linkCopy = CopyLinks(
            links,
            semanticCopy,
            out MarkdownVectorLinkAction?[] linkBySemanticIndex);

        Width = width;
        Height = height;
        Baseline = baseline;
        ReferenceFontSize = referenceFontSize;
        Commands = new ReadOnlyCollection<MarkdownVectorCommand>(copy);
        Viewport = viewport;
        Semantics = new ReadOnlyCollection<MarkdownVectorSemanticItem>(semanticCopy);
        Links = new ReadOnlyCollection<MarkdownVectorLinkAction>(linkCopy);
        _linkBySemanticIndex = linkBySemanticIndex;
        SemanticLinks = new ReadOnlyCollection<MarkdownVectorLinkAction?>(linkBySemanticIndex);
        _semanticDepths = semanticDepths;
    }

    private static MarkdownVectorCommand[] CopyNestedCommands(IReadOnlyList<MarkdownVectorCommand> commands)
    {
        var copy = new MarkdownVectorCommand[commands.Count];
        var scopeStack = new MarkdownVectorCommandKind[commands.Count];
        int scopeDepth = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            MarkdownVectorCommand command = commands[i] ??
                throw new ArgumentException("Scene commands cannot contain null.", nameof(commands));
            switch (command.Kind)
            {
                case MarkdownVectorCommandKind.BeginGroup:
                case MarkdownVectorCommandKind.BeginClip:
                    scopeStack[scopeDepth++] = command.Kind;
                    break;
                case MarkdownVectorCommandKind.EndGroup:
                    if (scopeDepth == 0 || scopeStack[--scopeDepth] != MarkdownVectorCommandKind.BeginGroup)
                        throw new ArgumentException("Vector groups must be properly nested.", nameof(commands));
                    break;
                case MarkdownVectorCommandKind.EndClip:
                    if (scopeDepth == 0 || scopeStack[--scopeDepth] != MarkdownVectorCommandKind.BeginClip)
                        throw new ArgumentException("Vector clips must be properly nested.", nameof(commands));
                    break;
            }
            copy[i] = command;
        }
        if (scopeDepth != 0)
            throw new ArgumentException("Vector groups and clips must be closed.", nameof(commands));

        return copy;
    }

    private static MarkdownVectorSemanticItem[] CopySemantics(IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        var semanticCopy = new MarkdownVectorSemanticItem[semantics.Count];
        var sourceIds = new HashSet<uint>();
        for (int i = 0; i < semanticCopy.Length; i++)
        {
            MarkdownVectorSemanticItem item = semantics[i] ?? throw new ArgumentException("Scene semantics cannot contain null.", nameof(semantics));
            if (!sourceIds.Add(item.SourceId) || item.ParentIndex >= semanticCopy.Length || item.ParentIndex == i)
                throw new ArgumentException("Scene semantic identities and parent references must be valid.", nameof(semantics));
            semanticCopy[i] = item;
        }

        return semanticCopy;
    }

    private static void ValidateCommandSemantics(MarkdownVectorCommand[] commands, int semanticCount)
    {
        for (int i = 0; i < commands.Length; i++)
        {
            if (commands[i].SemanticIndex < -1 || commands[i].SemanticIndex >= semanticCount)
                throw new ArgumentException("Scene commands must reference scene semantics.", nameof(commands));
        }
    }

    private static MarkdownVectorLinkAction[] CopyLinks(
        IReadOnlyList<MarkdownVectorLinkAction> links,
        MarkdownVectorSemanticItem[] semanticCopy,
        out MarkdownVectorLinkAction?[] linkBySemanticIndex)
    {
        var linkCopy = new MarkdownVectorLinkAction[links.Count];
        linkBySemanticIndex = new MarkdownVectorLinkAction?[semanticCopy.Length];
        for (int i = 0; i < linkCopy.Length; i++)
        {
            MarkdownVectorLinkAction link = links[i] ?? throw new ArgumentException("Scene links cannot contain null.", nameof(links));
            if (link.SemanticIndex >= semanticCopy.Length)
                throw new ArgumentException("Scene links must reference scene semantics.", nameof(links));
            if ((semanticCopy[link.SemanticIndex].Flags & MarkdownVectorSemanticFlags.Linked) == 0)
                throw new ArgumentException("Scene link actions require the referenced semantic to carry the Linked flag.", nameof(links));
            if (linkBySemanticIndex[link.SemanticIndex] is not null)
                throw new ArgumentException("A scene semantic can declare at most one link action.", nameof(links));
            linkCopy[i] = link;
            linkBySemanticIndex[link.SemanticIndex] = link;
        }

        for (int i = 0; i < semanticCopy.Length; i++)
        {
            if ((semanticCopy[i].Flags & MarkdownVectorSemanticFlags.Linked) != 0 &&
                linkBySemanticIndex[i] is null)
            {
                throw new ArgumentException("Every semantic carrying the Linked flag requires one link action.", nameof(links));
            }
        }

        return linkCopy;
    }

    /// <summary>Gets the scene width in device-independent units.</summary>
    public float Width { get; }
    /// <summary>Gets the scene height in device-independent units.</summary>
    public float Height { get; }
    /// <summary>Gets the baseline measured from the top.</summary>
    public float Baseline { get; }
    /// <summary>
    /// Gets the scene font size that maps to the containing markdown style, or
    /// zero to let the block/inline renderer apply its documented fallback.
    /// </summary>
    public float ReferenceFontSize { get; }
    /// <summary>Gets immutable commands in painter order.</summary>
    public IReadOnlyList<MarkdownVectorCommand> Commands { get; }
    /// <summary>Gets the source-space viewport mapped into the scene bounds.</summary>
    public MarkdownVectorRectangle Viewport { get; }
    /// <summary>Gets stable semantic children used for hit testing and UI Automation.</summary>
    public IReadOnlyList<MarkdownVectorSemanticItem> Semantics { get; }
    /// <summary>Gets declarative link/action records associated with semantic children.</summary>
    public IReadOnlyList<MarkdownVectorLinkAction> Links { get; }

    // Scene-independent immutable lookup retained by semantic projections;
    // it contains no command-tree or scene back-reference.

    internal MarkdownVectorLinkAction? GetLinkAction(int semanticIndex) =>
        (uint)semanticIndex < (uint)_linkBySemanticIndex.Length
            ? _linkBySemanticIndex[semanticIndex]
            : null;

    // Boxes may retain semantic projection data after releasing the command
    // graph. ReadOnlyMemory borrows only this immutable scalar array, not the
    // scene or its commands.
    internal ReadOnlyMemory<int> SemanticDepths => _semanticDepths;

    internal int GetSemanticDepth(int semanticIndex) =>
        (uint)semanticIndex < (uint)_semanticDepths.Length
            ? _semanticDepths[semanticIndex]
            : -1;

    private static int[] ValidateSemanticHierarchy(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        // 0 = unseen, 1 = in the current parent chain, 2 = completely checked.
        var states = new byte[semantics.Count];
        var path = new int[semantics.Count];
        var semanticDepths = new int[semantics.Count];
        for (int start = 0; start < semantics.Count; start++)
        {
            if (states[start] == 2)
                continue;

            int depth = 0;
            int current = start;
            while (current >= 0 && states[current] == 0)
            {
                states[current] = 1;
                path[depth++] = current;
                current = semantics[current].ParentIndex;
            }
            if (current >= 0 && states[current] == 1)
                throw new ArgumentException("Scene semantic parents cannot contain cycles.", nameof(semantics));
            int resolvedDepth = current >= 0 ? semanticDepths[current] + 1 : 0;
            while (depth > 0)
            {
                int semanticIndex = path[--depth];
                states[semanticIndex] = 2;
                semanticDepths[semanticIndex] = resolvedDepth++;
            }
        }

        return semanticDepths;
    }
}
