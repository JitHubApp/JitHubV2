using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Constructor-only accumulator for the layout indexes which can be collected
/// while the semantic document performs its required block-tree walk.
/// Standalone semantic-document builds never receive this collector.
/// </summary>
internal sealed class LayoutSnapshotIndexBuilder
{
    private readonly BlockOrdinalIndex _topLevelOrdinalByBlockIndex;
    private readonly Dictionary<ImageBox, int> _imageTopLevelOrdinalByIdentity;
    private readonly HashSet<IHorizontalOverflowBox> _horizontalOverflowBoxes;
    private readonly Dictionary<int, IHorizontalOverflowBox> _horizontalOverflowByBlockIndex;
    private readonly List<LayoutSnapshot.FocusablePlanEntry> _focusablePlan = new();
    private readonly List<LayoutPlanCandidate> _layoutPlanCandidates = new();
    private int _topLevelOrdinal = -1;
    private IHorizontalOverflowBox? _ancestor;

    internal LayoutSnapshotIndexBuilder(
        BlockOrdinalIndex topLevelOrdinalByBlockIndex,
        Dictionary<ImageBox, int> imageTopLevelOrdinalByIdentity,
        HashSet<IHorizontalOverflowBox> horizontalOverflowBoxes,
        Dictionary<int, IHorizontalOverflowBox> horizontalOverflowByBlockIndex)
    {
        _topLevelOrdinalByBlockIndex = topLevelOrdinalByBlockIndex ??
            throw new ArgumentNullException(nameof(topLevelOrdinalByBlockIndex));
        _imageTopLevelOrdinalByIdentity = imageTopLevelOrdinalByIdentity ??
            throw new ArgumentNullException(nameof(imageTopLevelOrdinalByIdentity));
        _horizontalOverflowBoxes = horizontalOverflowBoxes ??
            throw new ArgumentNullException(nameof(horizontalOverflowBoxes));
        _horizontalOverflowByBlockIndex = horizontalOverflowByBlockIndex ??
            throw new ArgumentNullException(nameof(horizontalOverflowByBlockIndex));
    }

    internal void BeginTopLevel(int ordinal)
    {
        _topLevelOrdinal = ordinal;
        _ancestor = null;
    }

    internal void EndTopLevel()
    {
        _ancestor = null;
        _topLevelOrdinal = -1;
    }

    internal Scope EnterBlock(BlockBox box)
        => EnterBlock(box, addLayoutPlanCandidate: true);

    private Scope EnterBlock(BlockBox box, bool addLayoutPlanCandidate)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (_topLevelOrdinal < 0)
            throw new InvalidOperationException("A top-level ordinal must be set before indexing a block.");

        if (addLayoutPlanCandidate && IsLayoutPlanCandidate(box))
        {
            if (_layoutPlanCandidates.Count > 0 &&
                _layoutPlanCandidates[^1].TopLevelOrdinal > _topLevelOrdinal)
            {
                throw new InvalidOperationException(
                    "Layout-plan candidates must be indexed in top-level document order.");
            }

            _layoutPlanCandidates.Add(new LayoutPlanCandidate(box, _topLevelOrdinal));
        }

        int focusableStart = _focusablePlan.Count;
        IHorizontalOverflowBox? previousAncestor = _ancestor;
        _topLevelOrdinalByBlockIndex.TryAdd(box.BlockIndex, _topLevelOrdinal);

        if (box is IHorizontalOverflowBox overflow)
        {
            _horizontalOverflowBoxes.Add(overflow);
            _ancestor = overflow;
        }

        if (_ancestor is not null)
            _horizontalOverflowByBlockIndex[box.BlockIndex] = _ancestor;

        switch (box)
        {
            case ImageBox image:
                AddImageTopLevelOrdinal(image);
                break;
            case EmbedBox embed:
                AddFocusable(new FocusableItem(embed.BlockIndex, 0, FocusableItemKind.BlockEmbed));
                break;
            case DeclarativeHostedElementBox hosted:
                AddFocusable(new FocusableItem(
                    hosted.BlockIndex,
                    0,
                    FocusableItemKind.DeclarativeHostedElement));
                break;
            case CodeBlockBox codeBlock when codeBlock.HasCopyButtonAction:
                AddFocusable(new FocusableItem(codeBlock.BlockIndex, 0, FocusableItemKind.CodeBlockCopy));
                break;
        }

        return new Scope(previousAncestor, focusableStart);
    }

    /// <summary>
    /// Code chunks are layout boxes but are not semantic child nodes and never
    /// contribute focus targets. The semantic builder already visits their
    /// runs for text, so index only their block/overflow identity and images.
    /// </summary>
    internal Scope EnterCodeChunk(InlineContainerBox chunk)
    {
        // The existing realization-plan traversal treats CodeBlockBox as an
        // atomic candidate and does not descend into its drawing chunks.
        // Preserve that contract while still indexing chunk images/focus data.
        Scope scope = EnterBlock(chunk, addLayoutPlanCandidate: false);
        foreach ((InlineImageRun imageRun, _) in chunk.InlineImageRuns)
            AddImageTopLevelOrdinal(imageRun.Image);
        return scope;
    }

    internal void ObserveInlineRun(InlineContainerBox owner, InlineRun run)
    {
        if (run is InlineImageRun imageRun)
            AddImageTopLevelOrdinal(imageRun.Image);

        if (run is LinkRun or InlineImageRun { IsLinked: true } or
            InlineVectorSceneRun { IsLinked: true })
        {
            AddFocusable(new FocusableItem(owner.BlockIndex, run.InlineIndex, FocusableItemKind.Link));
        }
        else if (run is InlineEmbedRun embed && LayoutSnapshot.IsInlineEmbedKeyboardFocusable(embed))
        {
            AddFocusable(new FocusableItem(owner.BlockIndex, run.InlineIndex, FocusableItemKind.InlineEmbed));
        }
    }

    internal void ObserveVectorSemantic(VectorSceneBox vector, int semanticIndex, MarkdownVectorSemanticFlags flags)
    {
        if (MarkdownVectorSemanticPolicy.IsKeyboardFocusable(flags))
        {
            AddFocusable(new FocusableItem(
                vector.BlockIndex,
                semanticIndex,
                FocusableItemKind.VectorSemantic));
        }
    }

    internal void ExitBlock(BlockBox box, Scope scope)
        => Exit(box, scope, addOverflowFallback: true);

    internal void ExitCodeChunk(InlineContainerBox chunk, Scope scope)
        => Exit(chunk, scope, addOverflowFallback: false);

    internal LayoutSnapshot.FocusablePlanEntry[] CompleteFocusablePlan()
        => _focusablePlan.ToArray();

    internal LayoutPlanCandidate[] CompleteLayoutPlanCandidates()
        => _layoutPlanCandidates.ToArray();

    private static bool IsLayoutPlanCandidate(BlockBox box)
        => box switch
        {
            InlineContainerBox inline => inline.HasInlineImages || inline.HasInlineEmbeds,
            EmbedBox => true,
            DeclarativeHostedElementBox => true,
            ImageBox => true,
            CodeBlockBox => true,
            _ => false,
        };

    private void Exit(BlockBox box, Scope scope, bool addOverflowFallback)
    {
        try
        {
            if (addOverflowFallback &&
                _focusablePlan.Count == scope.FocusableStart &&
                box is IHorizontalOverflowBox overflow)
            {
                _focusablePlan.Add(new LayoutSnapshot.FocusablePlanEntry(
                    new FocusableItem(box.BlockIndex, 0, FocusableItemKind.HorizontalOverflow),
                    overflow));
            }
        }
        finally
        {
            _ancestor = scope.PreviousAncestor;
        }
    }

    private void AddImageTopLevelOrdinal(ImageBox image)
    {
        // Inline images are not entered as BlockBox nodes by the semantic
        // traversal, but their geometry is still owned by the containing
        // top-level root and must share its immutable placement transform.
        _topLevelOrdinalByBlockIndex.TryAdd(image.BlockIndex, _topLevelOrdinal);

        if (_imageTopLevelOrdinalByIdentity.TryGetValue(image, out int existingOrdinal))
        {
            if (existingOrdinal != _topLevelOrdinal)
                _imageTopLevelOrdinalByIdentity[image] = -1;
        }
        else
        {
            _imageTopLevelOrdinalByIdentity.Add(image, _topLevelOrdinal);
        }
    }

    private void AddFocusable(FocusableItem item)
        => _focusablePlan.Add(new LayoutSnapshot.FocusablePlanEntry(item, null));

    internal readonly struct Scope
    {
        internal Scope(IHorizontalOverflowBox? previousAncestor, int focusableStart)
        {
            PreviousAncestor = previousAncestor;
            FocusableStart = focusableStart;
        }

        internal readonly IHorizontalOverflowBox? PreviousAncestor;
        internal readonly int FocusableStart;
    }
}
