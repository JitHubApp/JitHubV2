using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Independent oracle for the snapshot constructor's fused index traversal: the
/// former per-index preorder walks over block ordinals, image owners, horizontal
/// overflow owners and the keyboard focus plan.
/// </summary>
internal sealed class LegacySnapshotIndexes
{
    internal readonly Dictionary<BlockBox, int> BlockOrdinals = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<int, int> TopLevelOrdinalByBlockIndex = new();
    internal readonly Dictionary<ImageBox, int> ImageTopLevelOrdinalByIdentity = new(ReferenceEqualityComparer.Instance);
    internal readonly HashSet<IHorizontalOverflowBox> HorizontalOverflowBoxes = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<int, IHorizontalOverflowBox> HorizontalOverflowByBlockIndex = new();
    internal readonly List<LegacyFocusableEntry> FocusablePlan = new();

    internal static LegacySnapshotIndexes Build(IReadOnlyList<BlockBox> roots)
    {
        var indexes = new LegacySnapshotIndexes();
        for (int ordinal = 0; ordinal < roots.Count; ordinal++)
        {
            BlockBox root = roots[ordinal];
            indexes.BlockOrdinals[root] = ordinal;
            IndexBlockTree(root, ordinal, indexes.TopLevelOrdinalByBlockIndex);
            IndexImageTree(root, ordinal, indexes.ImageTopLevelOrdinalByIdentity);
            BuildLegacyFocusablePlan(root, indexes.FocusablePlan);
            CollectLegacyHorizontalOverflow(
                root,
                indexes.HorizontalOverflowBoxes,
                indexes.HorizontalOverflowByBlockIndex,
                ancestor: null);
        }
        return indexes;
    }

    private static void IndexBlockTree(BlockBox box, int ordinal, Dictionary<int, int> byBlockIndex)
    {
        byBlockIndex.TryAdd(box.BlockIndex, ordinal);
        switch (box)
        {
            case ListItemBox item:
                IndexBlockTree(item.Marker, ordinal, byBlockIndex);
                IndexBlockTree(item.Content, ordinal, byBlockIndex);
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    IndexBlockTree(child, ordinal, byBlockIndex);
                break;
            case CodeBlockBox code:
                foreach (InlineContainerBox chunk in code.Chunks)
                    IndexBlockTree(chunk, ordinal, byBlockIndex);
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    IndexBlockTree(cell, ordinal, byBlockIndex);
                break;
        }
    }

    private static void IndexImageTree(
        BlockBox box,
        int ordinal,
        Dictionary<ImageBox, int> byImageIdentity)
    {
        switch (box)
        {
            case ImageBox image:
                AddImageOwner(image, ordinal, byImageIdentity);
                break;
            case InlineContainerBox inline:
                foreach ((InlineImageRun run, _) in inline.InlineImageRuns)
                    AddImageOwner(run.Image, ordinal, byImageIdentity);
                break;
            case ListItemBox item:
                IndexImageTree(item.Marker, ordinal, byImageIdentity);
                IndexImageTree(item.Content, ordinal, byImageIdentity);
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    IndexImageTree(child, ordinal, byImageIdentity);
                break;
            case CodeBlockBox code:
                foreach (InlineContainerBox chunk in code.Chunks)
                    IndexImageTree(chunk, ordinal, byImageIdentity);
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    IndexImageTree(cell, ordinal, byImageIdentity);
                break;
        }
    }

    private static void AddImageOwner(
        ImageBox image,
        int ordinal,
        Dictionary<ImageBox, int> byImageIdentity)
    {
        if (byImageIdentity.TryGetValue(image, out int existing) && existing != ordinal)
            byImageIdentity[image] = -1;
        else
            byImageIdentity[image] = ordinal;
    }

    private static void CollectLegacyHorizontalOverflow(
        BlockBox box,
        HashSet<IHorizontalOverflowBox> result,
        Dictionary<int, IHorizontalOverflowBox> byBlockIndex,
        IHorizontalOverflowBox? ancestor)
    {
        if (box is IHorizontalOverflowBox overflow)
        {
            result.Add(overflow);
            ancestor = overflow;
        }
        if (ancestor is not null)
            byBlockIndex[box.BlockIndex] = ancestor;

        switch (box)
        {
            case ListItemBox item:
                CollectLegacyHorizontalOverflow(item.Marker, result, byBlockIndex, ancestor);
                CollectLegacyHorizontalOverflow(item.Content, result, byBlockIndex, ancestor);
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    CollectLegacyHorizontalOverflow(child, result, byBlockIndex, ancestor);
                break;
            case CodeBlockBox code:
                foreach (InlineContainerBox chunk in code.Chunks)
                    CollectLegacyHorizontalOverflow(chunk, result, byBlockIndex, ancestor);
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    CollectLegacyHorizontalOverflow(cell, result, byBlockIndex, ancestor);
                break;
        }
    }

    private static void BuildLegacyFocusablePlan(BlockBox box, List<LegacyFocusableEntry> plan)
    {
        int descendantStart = plan.Count;
        switch (box)
        {
            case InlineContainerBox inline:
                foreach (InlineRun run in inline.Runs)
                {
                    if (run is LinkRun or InlineImageRun { IsLinked: true } or
                        InlineVectorSceneRun { IsLinked: true })
                    {
                        plan.Add(new LegacyFocusableEntry(
                            new FocusableItem(inline.BlockIndex, run.InlineIndex, FocusableItemKind.Link),
                            null));
                    }
                    else if (run is InlineEmbedRun embed && LayoutSnapshot.IsInlineEmbedKeyboardFocusable(embed))
                    {
                        plan.Add(new LegacyFocusableEntry(
                            new FocusableItem(inline.BlockIndex, run.InlineIndex, FocusableItemKind.InlineEmbed),
                            null));
                    }
                }
                break;
            case EmbedBox embed:
                plan.Add(new LegacyFocusableEntry(
                    new FocusableItem(embed.BlockIndex, 0, FocusableItemKind.BlockEmbed),
                    null));
                break;
            case DeclarativeHostedElementBox hosted:
                plan.Add(new LegacyFocusableEntry(
                    new FocusableItem(hosted.BlockIndex, 0, FocusableItemKind.DeclarativeHostedElement),
                    null));
                break;
            case CodeBlockBox code when code.HasCopyButtonAction:
                plan.Add(new LegacyFocusableEntry(
                    new FocusableItem(code.BlockIndex, 0, FocusableItemKind.CodeBlockCopy),
                    null));
                break;
            case VectorSceneBox vector:
                for (int semanticIndex = 0; semanticIndex < vector.SemanticItems.Count; semanticIndex++)
                {
                    if (MarkdownVectorSemanticPolicy.IsKeyboardFocusable(vector.SemanticItems[semanticIndex].Flags))
                    {
                        plan.Add(new LegacyFocusableEntry(
                            new FocusableItem(vector.BlockIndex, semanticIndex, FocusableItemKind.VectorSemantic),
                            null));
                    }
                }
                break;
            case ListItemBox item:
                BuildLegacyFocusablePlan(item.Marker, plan);
                BuildLegacyFocusablePlan(item.Content, plan);
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    BuildLegacyFocusablePlan(cell, plan);
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    BuildLegacyFocusablePlan(child, plan);
                break;
        }

        if (plan.Count == descendantStart && box is IHorizontalOverflowBox overflow)
        {
            plan.Add(new LegacyFocusableEntry(
                new FocusableItem(box.BlockIndex, 0, FocusableItemKind.HorizontalOverflow),
                overflow));
        }
    }
}
