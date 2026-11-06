using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Bounded descriptor capture and private clone construction for the eager
/// Body/Heading/List/Table and completed standalone-image raster-placement slice. The source tree is read only
/// while its owning LayoutSnapshot gate is held; this type never measures or
/// arranges source boxes.
/// </summary>
internal static class PrivateRasterPlacementTree
{
    internal const int MaximumRoots = 64;
    internal const int MaximumNodes = 256;
    internal const int MaximumDepth = 32;

    internal static bool TryAccumulateStyleKeyCopyEntries(ref long aggregate, long count)
    {
        if (count < 0)
            return false;
        aggregate = aggregate > long.MaxValue - count ? long.MaxValue : aggregate + count;
        return aggregate <= InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries;
    }

    internal readonly struct RootCounts(
        int NodeCount,
        int RunCount,
        int ImageCount,
        int TextLength,
        long StyleKeyCopyEntries,
        int IdleImageCount)
    {
        public int NodeCount { get; init; } = NodeCount;
        public int RunCount { get; init; } = RunCount;
        public int ImageCount { get; init; } = ImageCount;
        public int TextLength { get; init; } = TextLength;
        public long StyleKeyCopyEntries { get; init; } = StyleKeyCopyEntries;
        public int IdleImageCount { get; init; } = IdleImageCount;
    }

    /// <summary>
    /// Shared all-roots preflight state. A failed visit poisons the capture so
    /// callers cannot accidentally use partial identity/count results.
    /// </summary>
    internal sealed class CaptureState
    {
        private readonly bool _allowUnmeasuredTextOnly;
        private readonly bool _allowIdleImagePlacements;
        private readonly bool _allowCodePlacements;
        private readonly bool _allowMeasuredVectorPlacement;
        private readonly HashSet<BlockBox> _boxes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<int> _selectableBlockIndices = new();
        private readonly HashSet<ImageBox> _images = new(ReferenceEqualityComparer.Instance);
        private bool _failed;
        private int _rootCount;
        private long _styleKeyCopyEntries;

        internal CaptureState(bool allowUnmeasuredTextOnly = false, bool allowIdleImagePlacements = false,
            bool allowCodePlacements = false, bool allowMeasuredVectorPlacement = false)
        {
            _allowUnmeasuredTextOnly = allowUnmeasuredTextOnly;
            _allowIdleImagePlacements = allowIdleImagePlacements;
            _allowCodePlacements = allowCodePlacements;
            _allowMeasuredVectorPlacement = allowMeasuredVectorPlacement;
            _failed = allowMeasuredVectorPlacement &&
                (allowUnmeasuredTextOnly || allowIdleImagePlacements || allowCodePlacements);
        }

        internal int RootCount => _rootCount;
        internal int NodeCount { get; private set; }
        internal int RunCount { get; private set; }
        internal int ImageCount { get; private set; }
        internal int IdleImageCount { get; private set; }
        internal int TextLength { get; private set; }
        internal long StyleKeyCopyEntries => _styleKeyCopyEntries;

        internal bool TryAddRoot(
            BlockBox root,
            MarkdownLayoutContext sourceContext,
            MarkdownLayoutContext targetContext,
            float availableWidth,
            out RootCounts counts)
        {
            counts = default;
            if (_failed || _rootCount >= MaximumRoots || root is null ||
                sourceContext is null || targetContext is null ||
                !float.IsFinite(availableWidth) || availableWidth <= 0)
            {
                _failed = true;
                return false;
            }

            int nodesBefore = NodeCount;
            int runsBefore = RunCount;
            int imagesBefore = ImageCount;
            int idleImagesBefore = IdleImageCount;
            int textBefore = TextLength;
            long styleKeysBefore = _styleKeyCopyEntries;
            try
            {
                if (!TryVisit(root, depth: 0, sourceContext, targetContext))
                {
                    _failed = true;
                    return false;
                }
            }
            catch
            {
                // A canceled or exceptional partial walk has already changed
                // identity sets and counters. Never let callers reuse it.
                _failed = true;
                throw;
            }

            _rootCount++;
            counts = new RootCounts(
                NodeCount - nodesBefore,
                RunCount - runsBefore,
                ImageCount - imagesBefore,
                TextLength - textBefore,
                _styleKeyCopyEntries - styleKeysBefore,
                IdleImageCount - idleImagesBefore);
            return true;
        }

        private bool TryVisit(
            BlockBox block,
            int depth,
            MarkdownLayoutContext sourceContext,
            MarkdownLayoutContext targetContext)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (depth > MaximumDepth || NodeCount >= MaximumNodes ||
                block.BlockIndex < 0 || !_boxes.Add(block))
                return false;
            NodeCount++;

            switch (block)
            {
                case ImageBox image:
                    if (image.BlockIndex <= 0 ||
                        !ReferenceEquals(image.Context, sourceContext) ||
                        !_selectableBlockIndices.Add(image.BlockIndex) ||
                        ImageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
                        !_images.Add(image) ||
                        !image.TryClassifyPrivateImagePlacement(
                            targetContext, _allowIdleImagePlacements,
                            out bool isIdleImage, out _))
                    {
                        return false;
                    }
                    ImageCount++;
                    if (isIdleImage)
                        IdleImageCount++;
                    return true;

                case InlineContainerBox inline:
                    return TryVisitInline(inline, sourceContext, targetContext);

                case CodeBlockBox code when _allowCodePlacements:
                    if (code.BlockIndex <= 0 || code.SafeHtmlInlineFlowScopeId is not null ||
                        !ReferenceEquals(code.Context, sourceContext) ||
                        !_selectableBlockIndices.Add(code.BlockIndex) ||
                        !code.TryGetPrivatePlacementCounts(targetContext, _allowUnmeasuredTextOnly,
                            out int lineCount, out long codeStyleKeys) ||
                        lineCount > MaximumNodes - NodeCount ||
                        !TryAccumulateStyleKeyCopyEntries(ref _styleKeyCopyEntries, codeStyleKeys))
                        return false;
                    NodeCount += lineCount;
                    foreach (InlineContainerBox chunk in code.Chunks)
                        if (!TryVisit(chunk, depth + 1, sourceContext, targetContext))
                            return false;
                    return true;

                case StackBox stack when stack.GetType() == typeof(StackBox) &&
                                         stack.IsPrivateRasterPlacementCloneEligible &&
                                         stack.SafeHtmlInlineFlowScopeId is null:
                    for (int index = 0; index < stack.Children.Count; index++)
                    {
                        targetContext.CancellationToken.ThrowIfCancellationRequested();
                        if (!TryVisit(stack.Children[index], depth + 1, sourceContext, targetContext))
                            return false;
                    }
                    return true;

                case ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                               listItem.SafeHtmlInlineFlowScopeId is null:
                    return TryVisit(listItem.Marker, depth + 1, sourceContext, targetContext) &&
                           TryVisit(listItem.Content, depth + 1, sourceContext, targetContext);

                case TableBox table when table.GetType() == typeof(TableBox) &&
                                         table.SafeHtmlInlineFlowScopeId is null &&
                                         ReferenceEquals(table.Context, sourceContext):
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                    {
                        targetContext.CancellationToken.ThrowIfCancellationRequested();
                        if (cell is null || !TryVisit(cell, depth + 1, sourceContext, targetContext))
                            return false;
                    }
                    return true;

                default:
                    return false;
            }
        }

        private bool TryVisitInline(
            InlineContainerBox inline,
            MarkdownLayoutContext sourceContext,
            MarkdownLayoutContext targetContext)
        {
            if (!IsSupportedInlineElement(inline.ElementKey) ||
                inline.BlockIndex <= 0 || inline.SafeHtmlInlineFlowScopeId is not null ||
                !ReferenceEquals(inline.Context, sourceContext) ||
                !_selectableBlockIndices.Add(inline.BlockIndex) ||
                !TryGetInlineCounts(inline, targetContext,
                    out int runCount, out int imageCount, out long styleKeyCopyEntries))
            {
                return false;
            }

            int spanCount = inline.PrivateRasterPlacementForegroundSpanCount;
            if (spanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount ||
                runCount + spanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - RunCount ||
                imageCount > InlineContainerBox.MaximumPrivateRasterPlacementImages - ImageCount ||
                inline.PrivateRasterPlacementTextLength >
                    InlineContainerBox.MaximumPrivateRasterPlacementTextLength - TextLength ||
                !TryAccumulateStyleKeyCopyEntries(ref _styleKeyCopyEntries, styleKeyCopyEntries))
                return false;

            IReadOnlyList<(InlineImageRun Run, int Offset)> images = inline.InlineImageRuns;
            if (images.Count != imageCount)
                return false;
            for (int index = 0; index < images.Count; index++)
            {
                targetContext.CancellationToken.ThrowIfCancellationRequested();
                if (!_images.Add(images[index].Run.Image) ||
                    !images[index].Run.TryClassifyPrivateImagePlacement(
                        targetContext, _allowIdleImagePlacements,
                        out bool isIdleImage, out _))
                    return false;
                if (isIdleImage)
                    IdleImageCount++;
            }

            RunCount += runCount + spanCount;
            ImageCount += imageCount;
            TextLength += inline.PrivateRasterPlacementTextLength;
            return true;
        }

        private bool TryGetInlineCounts(
            InlineContainerBox inline,
            MarkdownLayoutContext targetContext,
            out int runCount,
            out int imageCount,
            out long styleKeyCopyEntries)
        {
            float width = Math.Max(1f, (float)inline.Bounds.Width);
            if (_allowMeasuredVectorPlacement)
            {
                runCount = 0;
                imageCount = 0;
                styleKeyCopyEntries = 0;
                return !_allowUnmeasuredTextOnly && !_allowIdleImagePlacements && !_allowCodePlacements &&
                    inline.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                        targetContext, width, out runCount, out imageCount, out styleKeyCopyEntries);
            }

            return inline.TryGetPrivateRasterPlacementCounts(
                targetContext,
                width,
                out runCount,
                out imageCount,
                out styleKeyCopyEntries,
                _allowUnmeasuredTextOnly, _allowIdleImagePlacements,
                allowCodeForegroundSpans: _allowCodePlacements && inline.ElementKey == MarkdownElementKeys.CodeBlock);
        }

        internal static bool IsSupportedInlineElement(string elementKey) =>
            elementKey is MarkdownElementKeys.Body or
                MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
                MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
                MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or
                MarkdownElementKeys.ListMarker or MarkdownElementKeys.TableHeader or
                MarkdownElementKeys.TableCell or MarkdownElementKeys.CodeBlock;
    }

    /// <summary>
    /// Finds a source context without trusting an arbitrary first root cast.
    /// Traversal is identity-checked and bounded before the caller creates a
    /// fresh construction context from the returned supported placement leaf.
    /// </summary>
    internal static bool TryGetFirstInlineContext(
        IReadOnlyList<BlockBox> roots,
        out MarkdownLayoutContext? sourceContext)
    {
        ArgumentNullException.ThrowIfNull(roots);
        sourceContext = null;
        if (roots.Count is 0 or > MaximumNodes)
            return false;

        var seen = new HashSet<BlockBox>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(BlockBox Box, int Depth)>(Math.Min(roots.Count, MaximumNodes));
        for (int index = roots.Count - 1; index >= 0; index--)
        {
            if (roots[index] is null)
                return false;
            pending.Push((roots[index], 0));
        }

        int visited = 0;
        while (pending.Count > 0)
        {
            (BlockBox box, int depth) = pending.Pop();
            if (depth > MaximumDepth || ++visited > MaximumNodes ||
                box.BlockIndex < 0 || !seen.Add(box))
                return false;

            switch (box)
            {
                case InlineContainerBox inline:
                    if (!CaptureState.IsSupportedInlineElement(inline.ElementKey) ||
                        inline.SafeHtmlInlineFlowScopeId is not null)
                        return false;
                    sourceContext = inline.Context;
                    return sourceContext is not null;

                case ImageBox image:
                    sourceContext = image.Context;
                    return sourceContext is not null;

                case CodeBlockBox code:
                    sourceContext = code.Context;
                    return sourceContext is not null;

                case StackBox stack when stack.GetType() == typeof(StackBox) &&
                                         stack.IsPrivateRasterPlacementCloneEligible:
                    if (stack.Children.Count > MaximumNodes - visited - pending.Count)
                        return false;
                    for (int index = stack.Children.Count - 1; index >= 0; index--)
                        pending.Push((stack.Children[index], depth + 1));
                    break;

                case ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                               listItem.SafeHtmlInlineFlowScopeId is null:
                    if (pending.Count > MaximumNodes - visited - 2)
                        return false;
                    pending.Push((listItem.Content, depth + 1));
                    pending.Push((listItem.Marker, depth + 1));
                    break;

                case TableBox table when table.GetType() == typeof(TableBox) &&
                                         table.SafeHtmlInlineFlowScopeId is null:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                    {
                        if (cell is null)
                            return false;
                        if (pending.Count >= MaximumNodes - visited)
                            return false;
                        pending.Push((cell, depth + 1));
                    }
                    break;

                default:
                    return false;
            }
        }

        return false;
    }

    internal static bool TryGetFirstInlineContext(
        BlockBox root,
        out MarkdownLayoutContext? sourceContext)
    {
        ArgumentNullException.ThrowIfNull(root);
        return TryGetFirstInlineContext(new[] { root }, out sourceContext);
    }

    /// <summary>
    /// Creates one fully owning private clone after the caller has completed
    /// preflight for every document root. Every partial wrapper is disposed
    /// with its descendants if cancellation or construction fails.
    /// </summary>
    internal static bool TryCreateClone(
        BlockBox source,
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out BlockBox? clone,
        out RasterPlacementSourceFence[]? sourceFences,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false,
        List<CodeBlockPlacementSourceFence>? codeSources = null)
        => TryCreateCloneCore(source, sourceContext, targetContext, availableWidth,
            out clone, out sourceFences, allowUnmeasuredTextOnly, allowIdleImagePlacements,
            codeSources, allowMeasuredVectorPlacement: false);

    /// <summary>
    /// Creates a private placement-only clone for image-completion relayout.
    /// Inline vector runs in this clone are measurement-only borrowers and must
    /// never be published as document boxes.
    /// </summary>
    internal static bool TryCreateCloneWithMeasuredVectorPlacement(
        BlockBox source,
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out BlockBox? clone,
        out RasterPlacementSourceFence[]? sourceFences)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceContext);
        ArgumentNullException.ThrowIfNull(targetContext);
        if (source is not (StackBox or ListItemBox or TableBox))
        {
            clone = null;
            sourceFences = null;
            return false;
        }

        return TryCreateCloneCore(source, sourceContext, targetContext, availableWidth,
            out clone, out sourceFences, allowUnmeasuredTextOnly: false,
            allowIdleImagePlacements: false, codeSources: null,
            allowMeasuredVectorPlacement: true);
    }

    private static bool TryCreateCloneCore(
        BlockBox source,
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out BlockBox? clone,
        out RasterPlacementSourceFence[]? sourceFences,
        bool allowUnmeasuredTextOnly,
        bool allowIdleImagePlacements,
        List<CodeBlockPlacementSourceFence>? codeSources,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceContext);
        ArgumentNullException.ThrowIfNull(targetContext);
        clone = null;
        sourceFences = null;
        if (allowMeasuredVectorPlacement &&
            (allowUnmeasuredTextOnly || allowIdleImagePlacements || codeSources is not null ||
             source is not (StackBox or ListItemBox or TableBox)))
            return false;
        if (!float.IsFinite(availableWidth) || availableWidth <= 0 ||
            !ReferenceEquals(targetContext.SourceMap, sourceContext.SourceMap))
            return false;

        var fences = new List<RasterPlacementSourceFence>();
        BlockBox? candidate = null;
        var cloner = new PrivateRasterPlacementCloner(sourceContext, targetContext, fences, codeSources,
            allowUnmeasuredTextOnly, allowIdleImagePlacements, allowMeasuredVectorPlacement);
        try
        {
            if (!cloner.TryCloneNode(source, depth: 0, out candidate) ||
                candidate is null)
                return false;

            RasterPlacementSourceFence[] completedFences = fences.ToArray();
            clone = candidate;
            candidate = null;
            sourceFences = completedFences;
            return true;
        }
        finally
        {
            if (candidate is not null)
                targetContext.DiscardConstructionBlock(candidate);
        }
    }
}
