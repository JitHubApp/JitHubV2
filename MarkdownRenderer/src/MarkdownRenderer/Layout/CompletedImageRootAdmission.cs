using System.Collections.Generic;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Admission budget for one completed-image relayout batch. Each changed root
/// is checked against the shared text, run, image, node and style-key limits,
/// and every image and inline owner may appear in the batch only once.
/// Unsupported or duplicate roots are rejected before any bitmap lease.
/// </summary>
internal sealed class CompletedImageRootAdmission
{
    // Both descriptor capture and native preparation stay bounded
    // by changed content, not the complete document/sibling tree.
    private readonly HashSet<ImageBox> _imageIdentities = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<InlineContainerBox> _inlineIdentities = new(ReferenceEqualityComparer.Instance);

    // These clones can only transfer measured placement back to
    // retained source owners; they are never publishable snapshots.
    // General whole-tree COW capture remains vector-ineligible.
    private readonly PrivateRasterPlacementTree.CaptureState _tableCapture = new(
        allowMeasuredVectorPlacement: true);

    private readonly MarkdownLayoutContext _sourceContext;
    private readonly MarkdownLayoutContext _context;
    private readonly float _contentWidth;
    private int _textLength;
    private int _runCount;
    private int _imageCount;
    private int _nodeCount;
    private long _styleKeyCopyEntries;

    internal CompletedImageRootAdmission(
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext context,
        float contentWidth,
        int rootCount)
    {
        _sourceContext = sourceContext;
        _context = context;
        _contentWidth = contentWidth;
        _nodeCount = rootCount;
    }

    /// <summary>Images admitted so far, inline and standalone.</summary>
    internal int ImageCount => _imageCount;

    internal bool TryAdmit(BlockBox root)
    {
        switch (root)
        {
            case ImageBox image:
                return TryAdmitImage(image);
            case InlineContainerBox inline:
                return TryAdmitInline(inline);
            case TableBox table:
                return TryAdmitTable(table);
            case StackBox or ListItemBox:
                return TryAdmitContainer(root);
            default:
                return false;
        }
    }

    private bool TryAdmitImage(ImageBox image)
    {
        if (image.Alt.Length > LayoutSnapshot.MaximumPrivateRasterPlacementTextLength - _textLength ||
            ++_imageCount > ImageBox.MaximumMeasuredPlacementTransferBatchCount ||
            !_imageIdentities.Add(image) ||
            !image.TryClassifyPrivateImagePlacement(_context, false, out _, out _))
            return false;
        _textLength += image.Alt.Length;
        return true;
    }

    private bool TryAdmitInline(InlineContainerBox inline)
    {
        if (!_inlineIdentities.Add(inline) ||
            !inline.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(_context, _contentWidth,
                out int rootRuns, out int rootImages, out long rootStyleKeys) ||
            rootImages <= 0 ||
            rootRuns > InlineContainerBox.MaximumPrivateRasterPlacementRuns - _runCount ||
            rootImages > ImageBox.MaximumMeasuredPlacementTransferBatchCount - _imageCount ||
            inline.PrivateRasterPlacementTextLength >
                LayoutSnapshot.MaximumPrivateRasterPlacementTextLength - _textLength ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                ref _styleKeyCopyEntries, rootStyleKeys))
            return false;
        _runCount += rootRuns;
        _imageCount += rootImages;
        _textLength += inline.PrivateRasterPlacementTextLength;
        foreach (var entry in inline.InlineImageRuns)
            if (!_imageIdentities.Add(entry.Run.Image))
                return false;
        return true;
    }

    private bool TryAdmitTable(TableBox table)
    {
        // Empty/sparse malicious row arrays must not turn a
        // bounded cell budget into an unbounded preflight walk.
        if (table.RowCount is <= 0 or > PrivateRasterPlacementTree.MaximumNodes ||
            table.ColumnCount is <= 0 or > PrivateRasterPlacementTree.MaximumNodes ||
            !_tableCapture.TryAddRoot(table, _sourceContext, _context, _contentWidth,
                out PrivateRasterPlacementTree.RootCounts tableCounts) ||
            !TryAddTreeCounts(tableCounts))
            return false;
        foreach (InlineContainerBox cell in table.GetCellBoxes())
        {
            if (!_inlineIdentities.Add(cell))
                return false;
            foreach (var entry in cell.InlineImageRuns)
                if (!_imageIdentities.Add(entry.Run.Image))
                    return false;
        }
        return true;
    }

    private bool TryAdmitContainer(BlockBox root) =>
        TryCaptureContainerImageIdentities(root, _inlineIdentities, _imageIdentities) &&
        _tableCapture.TryAddRoot(root, _sourceContext, _context, _contentWidth,
            out PrivateRasterPlacementTree.RootCounts containerCounts) &&
        TryAddTreeCounts(containerCounts);

    private bool TryAddTreeCounts(PrivateRasterPlacementTree.RootCounts counts)
    {
        if (counts.ImageCount <= 0 ||
            counts.NodeCount - 1 > PrivateRasterPlacementTree.MaximumNodes - _nodeCount ||
            counts.RunCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - _runCount ||
            counts.ImageCount > ImageBox.MaximumMeasuredPlacementTransferBatchCount - _imageCount ||
            counts.TextLength > LayoutSnapshot.MaximumPrivateRasterPlacementTextLength - _textLength ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                ref _styleKeyCopyEntries, counts.StyleKeyCopyEntries))
            return false;
        _nodeCount += counts.NodeCount - 1;
        _runCount += counts.RunCount;
        _imageCount += counts.ImageCount;
        _textLength += counts.TextLength;
        return true;
    }

    // Reject unsupported nested shapes and cross-root identities before the
    // shared descriptor walk (a sparse nested table is outside this slice) and
    // acquiring any bitmap lease. Table roots have their own variable-width
    // publisher; ordinary container trees deliberately retain fixed leaf widths.
    private static bool TryCaptureContainerImageIdentities(
        BlockBox root, HashSet<InlineContainerBox> inlineIdentities, HashSet<ImageBox> imageIdentities)
    {
        int visited = 0;
        return Visit(root, 0);

        bool Visit(BlockBox box, int depth)
        {
            if (depth > PrivateRasterPlacementTree.MaximumDepth ||
                ++visited > PrivateRasterPlacementTree.MaximumNodes)
                return false;
            switch (box)
            {
                case ImageBox image:
                    return imageIdentities.Add(image);
                case InlineContainerBox inline:
                    if (inline.ElementKey is not (MarkdownElementKeys.Body or MarkdownElementKeys.Heading1 or
                            MarkdownElementKeys.Heading2 or MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
                            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or MarkdownElementKeys.ListMarker) ||
                        inline.Runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns ||
                        inline.InlineImageRuns.Count > ImageBox.MaximumMeasuredPlacementTransferBatchCount ||
                        !inlineIdentities.Add(inline))
                        return false;
                    foreach (var entry in inline.InlineImageRuns)
                        if (!imageIdentities.Add(entry.Run.Image))
                            return false;
                    return true;
                case StackBox stack when stack.GetType() == typeof(StackBox) && stack.IsPrivateRasterPlacementCloneEligible:
                    if (stack.Children.Count > PrivateRasterPlacementTree.MaximumNodes - visited)
                        return false;
                    foreach (BlockBox child in stack.Children)
                        if (!Visit(child, depth + 1))
                            return false;
                    return true;
                case ListItemBox list when list.SafeHtmlInlineFlowScopeId is null:
                    return Visit(list.Marker, depth + 1) && Visit(list.Content, depth + 1);
                default:
                    return false;
            }
        }
    }
}
