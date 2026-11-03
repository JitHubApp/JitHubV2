using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// A raster placement prepared off the UI thread: every piece of geometry
/// and membership the publisher swaps in at once. Discard releases the
/// strong candidate; lists adopted by the control are never cleared by the
/// former owner.
/// </summary>
internal sealed class RasterPlacementPlan : IDisposable
{
    internal readonly SingleRasterLayoutCandidate Owner;
    internal List<ImageBox>? Images;
    internal HashSet<ImageBox>? Subscriptions;
    internal ImagePlanPublication? Publication;
    internal IReadOnlyList<FocusableItem>? Focus;
    internal List<CodeBlockActionPlan>? CodeActionPlans;
    internal List<WeakReference<CodeBlockBox>>? CodeActionSources;
    internal int[]? CodeActionPlacementIndices;
    internal CodeBlockPlacementIdentity[]? CodePlacements;
    internal ViewportBandIndex? CodeActionIndex;
    internal List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)>? CodeActionRects;
    internal List<int>? ActiveCodeActionOrdinals;
    internal bool HasMeasuredCodeBlocks;
    internal List<(CodeBlockActionPlan OldPlan, CodeBlockActionPlan NewPlan, int NewOrdinal)>? CodeActionAdoptions;
    internal Layout.FocusableItem? ResumeFocusIdentity;
    internal readonly double? AnchorOffset;
    internal readonly double? CapturedScrollOffset;

    internal RasterPlacementPlan(SingleRasterLayoutCandidate owner, long generation,
        (int BlockIndex, double OffsetFromTop)? anchor, double? scrollOffset)
    {
        Owner = owner;
        LayoutSnapshot snapshot = owner.Snapshot;
        // Estimated roots carry only never-started descriptors. They must
        // not become subscriptions or start work before normal lazy-band
        // realization discovers them in the newly published snapshot.
        var imagePlan = new RasterPlacementImagePlan(
            snapshot, snapshot.GetMeasuredTopLevelBlocks(), owner.SourceSubscribedImageCount);
        Images = imagePlan.Images;
        Subscriptions = imagePlan.Subscriptions;
        Publication = new ImagePlanPublication(snapshot, generation, snapshot.LayoutRevision,
            imagePlan.PublicationImages, imagePlan.ImageBounds, imagePlan.Index);
        Focus = snapshot.CollectFocusableItems();

        IReadOnlyList<CodeBlockPlacementIdentity> placements = owner.CodePlacements;
        CodePlacements = new CodeBlockPlacementIdentity[placements.Count];
        var actions = new List<CodeBlockActionPlan>(placements.Count);
        var sources = new List<WeakReference<CodeBlockBox>>(placements.Count);
        var placementIndices = new List<int>(placements.Count);
        for (int index = 0; index < placements.Count; index++)
        {
            CodeBlockPlacementIdentity identity = placements[index];
            CodePlacements[index] = identity;
            CodeBlockBox placement = identity.Placement;
            if (identity.WasMeasured)
                HasMeasuredCodeBlocks = true;
            if (!identity.WasMeasured || !placement.HasCopyButtonAction ||
                placement.CopyButtonBounds.Width <= 0 || placement.CopyButtonBounds.Height <= 0)
                continue;

            actions.Add(new CodeBlockActionPlan
            {
                Box = placement,
                FocusIdentity = new FocusableItem(
                    placement.BlockIndex, 0, FocusableItemKind.CodeBlockCopy),
                Rect = snapshot.ToLogicalRectForBlockIndex(
                    placement.BlockIndex, placement.CopyButtonBounds),
                Kind = CodeBlockHostedElementKind.Copy,
            });
            sources.Add(identity.Source);
            placementIndices.Add(index);
        }
        CodeActionPlans = actions;
        CodeActionSources = sources;
        CodeActionPlacementIndices = placementIndices.ToArray();
        CodeActionIndex = EmbedRealization.BuildPlanIndex(actions);
        CodeActionRects = new List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)>(actions.Count);
        ActiveCodeActionOrdinals = new List<int>(actions.Count);
        CodeActionAdoptions = new List<(CodeBlockActionPlan OldPlan, CodeBlockActionPlan NewPlan, int NewOrdinal)>(actions.Count);
        AnchorOffset = LazyLayoutScrollAnchorPolicy.GetTargetOffset(snapshot, anchor);
        CapturedScrollOffset = scrollOffset;
    }

    public void Dispose()
    {
        Owner.Dispose();
        Images = null;
        Subscriptions = null;
        Publication = null;
        Focus = null;
        CodeActionPlans = null;
        CodeActionSources = null;
        CodeActionPlacementIndices = null;
        CodePlacements = null;
        CodeActionIndex = null;
        CodeActionRects = null;
        ActiveCodeActionOrdinals = null;
        CodeActionAdoptions = null;
    }
}
