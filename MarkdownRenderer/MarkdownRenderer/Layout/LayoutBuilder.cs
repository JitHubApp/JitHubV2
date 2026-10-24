using System;
using System.Collections.Generic;
using System.Threading.Tasks;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
#endif
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Footnotes;
using MarkdownSourceMap = MarkdownRenderer.Document.MarkdownSourceMap;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

internal sealed class LayoutBuilder
{
    private const string ListContainerStyleRole = MarkdownElementKeys.List;
    private const string DeferredSceneMarkerAttribute = "renderer.internal.deferred-scene";

    private const int MaxMonolithicTextLayoutLength = 32_768;
    private const int MaxCodeBlockLinesPerChunk = 128;

    private readonly MarkdownLayoutContext _context;
    private readonly IMarkdownEmbedFactory? _embedFactory;
    private readonly bool _enableDeclarativeHostedElements;
    private readonly MarkdownRenderer.Document.MarkdownDocument? _semanticDocument;
    private readonly IReadOnlySet<HostedElementFallbackKey> _hostedElementFallbacks;
    private readonly MarkdownEngine? _sharedLayoutEngine;
    private readonly SharedLayoutMetricsKey? _sharedLayoutMetricsKey;
    // A worker result may contain nested containers or several scene owners.
    // Scope its scene-free identity through construction, never its fragment.
    private string? _materializedSceneMarker;
    private int _materializedSceneOrdinal;
    private object? _materializedSceneProjectionIdentity;

    public LayoutBuilder(
        MarkdownLayoutContext context,
        IMarkdownEmbedFactory? embedFactory = null,
        bool enableDeclarativeHostedElements = false,
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument = null,
        IReadOnlySet<HostedElementFallbackKey>? hostedElementFallbacks = null,
        MarkdownEngine? sharedLayoutEngine = null,
        SharedLayoutMetricsKey? sharedLayoutMetricsKey = null)
    {
        _context = context;
        _embedFactory = embedFactory;
        _enableDeclarativeHostedElements = enableDeclarativeHostedElements;
        _semanticDocument = semanticDocument;
        _hostedElementFallbacks = hostedElementFallbacks ?? new HashSet<HostedElementFallbackKey>();
        _sharedLayoutEngine = sharedLayoutEngine;
        _sharedLayoutMetricsKey = sharedLayoutMetricsKey;
    }

    /// <summary>
    /// Builds a code child with the same parsing, source mapping, and chunking
    /// behavior as an ordinary layout block when an extension invokes its
    /// child builder outside an active <see cref="BuildBlocks"/> scope.
    /// </summary>
    internal static BlockBox? BuildCodeBlockForExtension(
        CodeBlock block,
        MarkdownLayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(context);
        return new LayoutBuilder(context).BuildBlock(block);
    }

    public LayoutSnapshot Build(MarkdownDocument document, float availableWidth)
        => Build(document, availableWidth, CancellationToken.None);

    public LayoutSnapshot Build(MarkdownDocument document, float availableWidth, CancellationToken cancellationToken)
    {
        // Build is deliberately eager even when the context borrows a
        // progressive performance session. Clear any stale builder hint before
        // constructing runs and leave the reusable context in its neutral state.
        _context.UseProgressiveNativeSceneResidency = false;
        try
        {
            return BuildEagerCore(document, availableWidth, cancellationToken);
        }
        finally
        {
            _context.UseProgressiveNativeSceneResidency = false;
        }
    }

    private LayoutSnapshot BuildEagerCore(
        MarkdownDocument document,
        float availableWidth,
        CancellationToken cancellationToken)
    {
        CancellationToken effectiveCancellationToken = cancellationToken.CanBeCanceled
            ? cancellationToken : _context.CancellationToken;
        using var cancellationScope = LayoutPassCancellation.Push(effectiveCancellationToken);
        effectiveCancellationToken.ThrowIfCancellationRequested();
        MarkdownLayoutConstructionScope ownership = BeginBlockBuildOwnership();
        LayoutSnapshot? snapshot = null;
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy = null;
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            MarkdownLayoutDiagnostics? diagnostics = _context.LayoutDiagnostics;
            if (diagnostics?.IsCaptured == true)
                diagnostics = null;
            long phaseStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
#endif
            var blocks = BuildBlocks(document, effectiveCancellationToken);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
            {
                diagnostics.RecordBlockConstruction(Stopwatch.GetTimestamp() - phaseStarted);
                diagnostics.SetTopLevelBlockCount(blocks.Count);
            }
#endif

            SharedLayoutMetrics? sharedMetrics = AcquireSharedMetrics(blocks.Count);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
                phaseStarted = Stopwatch.GetTimestamp();
#endif

            Thickness documentPadding = _context.ThemeSnapshot.DocumentPadding;
            float contentX = (float)documentPadding.Left;
            float contentWidth = (float)Math.Max(
                1,
                availableWidth - documentPadding.Left - documentPadding.Right);
            float blockSpacing = (float)_context.ThemeSnapshot.BlockSpacing;
            float y = (float)documentPadding.Top;
            for (int blockOrdinal = 0; blockOrdinal < blocks.Count; blockOrdinal++)
            {
                effectiveCancellationToken.ThrowIfCancellationRequested();
                BlockBox b = blocks[blockOrdinal];
                b.ThrowIfCancellationRequested();
                float h = b.Measure(contentWidth);
                b.Arrange(contentX, y, contentWidth);
                sharedMetrics?.RecordHeight(blockOrdinal, h);
                y += h;
                if (blockOrdinal < blocks.Count - 1 &&
                    !StackBox.SharesSafeHtmlInlineFlow(b, blocks[blockOrdinal + 1]))
                    y += blockSpacing;
            }
            effectiveCancellationToken.ThrowIfCancellationRequested();
            y += (float)documentPadding.Bottom;

#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
            {
                diagnostics.RecordMeasureArrange(Stopwatch.GetTimestamp() - phaseStarted);
                phaseStarted = Stopwatch.GetTimestamp();
            }
#endif

            var (defs, refs) = _context.SnapshotFootnoteRegistry();
            var fragments = _context.SnapshotFragmentTargets();
            vectorSceneResidencyPolicy =
                _context.PerformanceSession?.CreateVectorSceneResidencyPolicy(
                    blocks, _context.PerformanceDocumentOwner);
            snapshot = new LayoutSnapshot(
                blocks,
                _context.SourceMap,
                availableWidth,
                y,
                generatedBlockIndexUpperBound: _context.GeneratedBlockIndexUpperBound,
                footnoteDefBlocks: defs,
                footnoteRefBlocks: refs,
                fragmentTargetBlocks: fragments,
                sharedLayoutMetrics: sharedMetrics,
                documentPadding: documentPadding,
                blockSpacing: blockSpacing,
                vectorSceneResidencyPolicy: vectorSceneResidencyPolicy);
            vectorSceneResidencyPolicy = null;
            ownership.TransferToSnapshot(blocks);
            if (snapshot.VectorSceneResidencyPolicy is not null)
            {
                // The explicit eager API keeps its all-document measure/arrange
                // contract. When the caller has opted into native admission,
                // attach the policy and make the whole eagerly built document
                // its initial residency band so any capacity fallback remains
                // owned and can later be narrowed/refilled by a viewport host.
                LazyLayoutBand eagerDocumentBand = LazyLayoutBand.FromViewport(
                    0, snapshot.Size.Height, overscan: 0);
                snapshot.EnsureMeasuredBand(
                    eagerDocumentBand, eagerDocumentBand, effectiveCancellationToken);
            }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
                diagnostics.RecordSnapshotConstruction(Stopwatch.GetTimestamp() - phaseStarted);
#endif
            return snapshot;
        }
        catch
        {
            if (snapshot is null)
            {
                if (vectorSceneResidencyPolicy is not null)
                    DisposePreservingBuildFailure(vectorSceneResidencyPolicy);
                TrackRetirementTasks(ownership.DisposeRootsBestEffort());
            }
            else
                TrackRetirementTask(RetireSnapshotPreservingBuildFailure(snapshot));
            throw;
        }
        finally
        {
            EndBlockBuildOwnership(ownership);
        }
    }

    public LayoutSnapshot BuildLazy(
        MarkdownDocument document,
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
        => BuildLazyWithVisibleVectorBand(
            document,
            availableWidth,
            viewportTop,
            viewportHeight,
            overscan,
            initialVisibleVectorBand: null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Progressive renderer entry point: layout/image realization retains its
    /// look-ahead band while native vector residency starts from the actual
    /// viewport captured by the owning control.
    /// </summary>
    internal LayoutSnapshot BuildLazyForViewport(
        MarkdownDocument document,
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
        => BuildLazyWithVisibleVectorBand(
            document,
            availableWidth,
            viewportTop,
            viewportHeight,
            overscan,
            LazyLayoutBand.FromVisibleViewport(viewportTop, viewportHeight),
            cancellationToken);

    private LayoutSnapshot BuildLazyWithVisibleVectorBand(
        MarkdownDocument document,
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        LazyLayoutBand? initialVisibleVectorBand,
        CancellationToken cancellationToken)
    {
        IMarkdownPerformanceSessionInternal? session = _context.PerformanceSession;
        _context.UseProgressiveNativeSceneResidency = session is not null &&
            !session.IsDisposed &&
            session.Options.DeferOffscreenScenes;
        try
        {
            return BuildLazyCore(
                document,
                availableWidth,
                viewportTop,
                viewportHeight,
                overscan,
                cancellationToken,
                initialVisibleVectorBand);
        }
        finally
        {
            // Context instances can be reused by a later eager build. The
            // residency flag is a scoped construction hint, not a session-wide
            // behavior switch.
            _context.UseProgressiveNativeSceneResidency = false;
        }
    }

    private LayoutSnapshot BuildLazyCore(
        MarkdownDocument document,
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken,
        LazyLayoutBand? initialVisibleVectorBand)
    {
        CancellationToken effectiveCancellationToken = cancellationToken.CanBeCanceled
            ? cancellationToken : _context.CancellationToken;
        using var cancellationScope = LayoutPassCancellation.Push(effectiveCancellationToken);
        effectiveCancellationToken.ThrowIfCancellationRequested();
        MarkdownLayoutConstructionScope ownership = BeginBlockBuildOwnership();
        LayoutSnapshot? snapshot = null;
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy = null;
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            MarkdownLayoutDiagnostics? diagnostics = _context.LayoutDiagnostics;
            if (diagnostics?.IsCaptured == true)
                diagnostics = null;
            long phaseStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
#endif
            var blocks = BuildBlocks(document, effectiveCancellationToken);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
            {
                diagnostics.RecordBlockConstruction(Stopwatch.GetTimestamp() - phaseStarted);
                diagnostics.SetTopLevelBlockCount(blocks.Count);
            }
#endif

            effectiveCancellationToken.ThrowIfCancellationRequested();
            SharedLayoutMetrics? sharedMetrics = AcquireSharedMetrics(blocks.Count);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
                phaseStarted = Stopwatch.GetTimestamp();
#endif
            var (defs, refs) = _context.SnapshotFootnoteRegistry();
            var fragments = _context.SnapshotFragmentTargets();
            vectorSceneResidencyPolicy =
                _context.PerformanceSession?.CreateVectorSceneResidencyPolicy(
                    blocks, _context.PerformanceDocumentOwner);
            snapshot = new LayoutSnapshot(
                blocks,
                _context.SourceMap,
                availableWidth,
                0,
                generatedBlockIndexUpperBound: _context.GeneratedBlockIndexUpperBound,
                footnoteDefBlocks: defs,
                footnoteRefBlocks: refs,
                fragmentTargetBlocks: fragments,
                sharedLayoutMetrics: sharedMetrics,
                documentPadding: _context.ThemeSnapshot.DocumentPadding,
                blockSpacing: _context.ThemeSnapshot.BlockSpacing,
                vectorSceneResidencyPolicy: vectorSceneResidencyPolicy);
            vectorSceneResidencyPolicy = null;
            ownership.TransferToSnapshot(blocks);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
            {
                diagnostics.RecordSnapshotConstruction(Stopwatch.GetTimestamp() - phaseStarted);
                phaseStarted = Stopwatch.GetTimestamp();
            }
#endif
            snapshot.EnableLazyLayout(
                availableWidth,
                viewportTop,
                viewportHeight,
                overscan,
                effectiveCancellationToken,
                initialVisibleVectorBand);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (diagnostics is not null)
                diagnostics.RecordMeasureArrange(Stopwatch.GetTimestamp() - phaseStarted);
#endif
            return snapshot;
        }
        catch
        {
            if (snapshot is not null)
                TrackRetirementTask(RetireSnapshotPreservingBuildFailure(snapshot));
            else
            {
                if (vectorSceneResidencyPolicy is not null)
                    DisposePreservingBuildFailure(vectorSceneResidencyPolicy);
                TrackRetirementTasks(ownership.DisposeRootsBestEffort());
            }
            throw;
        }
        finally
        {
            EndBlockBuildOwnership(ownership);
        }
    }

    private SharedLayoutMetrics? AcquireSharedMetrics(int topLevelBlockCount)
    {
        if (_sharedLayoutMetricsKey is null)
            return null;

        return SharedLayoutMetricsCache.Acquire(
            _sharedLayoutEngine,
            _sharedLayoutMetricsKey,
            topLevelBlockCount);
    }

    private List<BlockBox> BuildBlocks(MarkdownDocument document, CancellationToken cancellationToken)
    {
        using var childBlockScope = _context.PushChildBlockBuilder(BuildBlock);
        using var childInlineScope = _context.PushChildInlineBuilder(AddExtensionContainerInlines);
        var blocks = new List<BlockBox>();
        SafeHtmlBlockScopeTracker? htmlScopes = _context.Registry.SafeHtmlPolicy is null
            ? null
            : new SafeHtmlBlockScopeTracker(_context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = false;
        foreach (var b in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            SafeHtmlAlignment alignmentAtBlockEntry = htmlScopes?.CurrentAlignment ?? SafeHtmlAlignment.Inherit;
            int? inlineFlowScopeIdAtBlockEntry = htmlScopes?.CurrentInlineFlowScopeId;
            bool scopeOnly = b is HtmlBlock htmlBlock && htmlScopes?.Process(
                    htmlBlock.Lines.ToString(),
                    htmlBlock.Span.Start,
                    _context.DisclosureStates,
                    cancellationToken) == true;
            int? inlineFlowScopeIdForBlock = htmlScopes?.CurrentInlineFlowScopeId;
            if (inlineFlowScopeIdAtBlockEntry is int entryScopeId &&
                inlineFlowScopeIdForBlock != entryScopeId)
            {
                // Fail closed when this HtmlBlock exits the flow that was open
                // at entry (for example `</div><br>outside`). A newly opened
                // scope may still qualify if the renderer confirms the block is
                // exactly that scope's inline-only content.
                inlineFlowScopeIdForBlock = null;
            }
            bool inlineFlowContinuesPrevious = inlineFlowScopeIdForBlock is int scopeId &&
                blocks.Count > 0 &&
                blocks[^1].SafeHtmlInlineFlowScopeId == scopeId;
            if (suppressedBeforeBlock && b is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    b,
                    _context.DisclosureStates,
                    cancellationToken);
            }

            if (htmlScopes?.BudgetExceeded == true)
            {
                if (!htmlBudgetNoticeAdded)
                {
                    var notice = new InlineContainerBox(_context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = _context.NextBlockIndex(),
                    };
                    TrackBlock(notice);
                    AddHtmlBudgetNotice(notice);
                    blocks.Add(notice);
                    htmlBudgetNoticeAdded = true;
                }
                if (b is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }
            if (scopeOnly)
            {
                continue;
            }

            if (suppressedBeforeBlock)
            {
                continue;
            }

            BlockBox? box;
            SafeHtmlBlockScopeTracker? nestedScopes = b is ContainerBlock or HtmlBlock
                ? htmlScopes?.CloneForNestedContent()
                : null;
            using (_context.PushSafeHtmlBlockContext(
                       alignmentAtBlockEntry,
                       nestedScopes,
                       inlineFlowScopeIdForBlock,
                       inlineFlowContinuesPrevious))
                box = BuildBlock(b);
            if (box is not null)
            {
                // The safe renderer has already claimed this HtmlBlock, so an
                // empty renderer-owned root must not reintroduce vertical gap
                // for comments/declarations. It cannot fall back to literal
                // core rendering because BuildBlock has already dispatched it.
                if (b is HtmlBlock && box is StackBox { Children.Count: 0 })
                {
                    DiscardUnpublishedBlock(box);
                    continue;
                }

                if (htmlScopes is not null &&
                    b is not HtmlBlock)
                    ApplyHtmlAlignment(box, alignmentAtBlockEntry);

                if (b is ParagraphBlock paragraph &&
                    box is InlineContainerBox inlineParagraph &&
                    inlineFlowScopeIdForBlock is int inlineFlowScopeId &&
                    IsBadgeOnlyHtmlFlowParagraph(paragraph))
                {
                    inlineParagraph.SafeHtmlInlineFlowScopeId = inlineFlowScopeId;
                    inlineParagraph.UseContainerMargin = false;
                    inlineParagraph.Margin = default;
                }

                blocks.Add(box);
            }

            if (!suppressedBeforeBlock && b is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    b,
                    _context.DisclosureStates,
                    cancellationToken);
                if (htmlScopes?.BudgetExceeded == true && !htmlBudgetNoticeAdded)
                {
                    var notice = new InlineContainerBox(_context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = _context.NextBlockIndex(),
                    };
                    TrackBlock(notice);
                    AddHtmlBudgetNotice(notice);
                    blocks.Add(notice);
                    htmlBudgetNoticeAdded = true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte layoutPolicy = _context.ThemeSnapshot.LayoutPolicy;
        if ((layoutPolicy & 4) != 0)
            TopLevelBlockMarginCollapse.ResetLeadingTopMargin(blocks);
        if ((layoutPolicy & 1) != 0)
            TopLevelBlockMarginCollapse.Apply(blocks);
        if ((layoutPolicy & 2) != 0)
            TopLevelBlockMarginCollapse.ResetTrailingBottomMargin(blocks);
        return blocks;
    }

    private static void ApplyHtmlAlignment(BlockBox box, SafeHtmlAlignment alignment)
    {
        if (box is StackBox { HasResolvedSafeHtmlAlignment: true })
            return;

        if (alignment == SafeHtmlAlignment.Inherit)
        {
            return;
        }

        var canvasAlignment = alignment switch
        {
            SafeHtmlAlignment.Center => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            SafeHtmlAlignment.Right => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right,
            _ => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
        };

        switch (box)
        {
            case InlineContainerBox inline:
                inline.TextAlignment = canvasAlignment;
                break;
            case ImageBox image:
                image.ContentAlignment = canvasAlignment;
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    ApplyHtmlAlignment(child, alignment);
                }
                break;
        }
    }

    private BlockBox? BuildBlock(Block block)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        using var attrScope = _context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        if (_semanticDocument is { } document &&
            DeclarativeBlockSelection.TryGetContent(
                document,
                block,
                _hostedElementFallbacks,
                out var extensionFragment))
        {
            box = TryBuildDeclarativeBlock(extensionFragment!);
        }

        if (box is null && _embedFactory is { } ef)
        {
            _context.ThrowIfEmbedLayoutCallbackIsOnUiThread(nameof(IMarkdownEmbedFactory.CanCreate));
            if (!ef.CanCreate(block))
                goto SkipEmbedFactory;

            var eb = new EmbedBox(block, ef, _context);
            TrackBlock(eb);
            eb.BlockIndex = _context.NextBlockIndex();
            // Register the source span so Ctrl+C across an embed copies the
            // original markdown that produced it.
            if (block.Span.Length > 0)
            {
                var span = new MarkdownRenderer.SourceSpan(block.Span.Start, block.Span.Length);
                _context.SourceMap.Add(eb.BlockIndex, 0, 1, span);
            }
            box = eb;
        }

    SkipEmbedFactory:

        if (box is null && _context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, _context);
            if (custom is not null)
            {
                if (custom.BlockIndex == 0) custom.BlockIndex = _context.NextBlockIndex();
                box = custom;
            }
        }

        box ??= block switch
        {
            HeadingBlock h => BuildHeading(h),
            ParagraphBlock p => BuildParagraphOrImage(p),
            FencedCodeBlock fc => BuildCodeBlock(fc, fc.Lines.ToString()),
            CodeBlock cb => BuildCodeBlock(cb, cb.Lines.ToString()),
            QuoteBlock qb => BuildQuote(qb),
            ListBlock lb => BuildList(lb),
            ThematicBreakBlock => MakeThematicBreak(),
            HtmlBlock html => BuildLiteralHtmlBlock(html),
            ContainerBlock cb => BuildGenericContainer(cb),
            _ => null
        };

        if (box is not null)
        {
            TrackBlock(box);
            _context.RegisterMarkdownAttributes(block, box.BlockIndex);
        }

        return box;
    }

    private InlineContainerBox BuildLiteralHtmlBlock(HtmlBlock block)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.Body)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        TrackBlock(box);
        string source = block.Lines.ToString();
        box.Add(new TextRun(source)
        {
            SourceSpan = new SourceSpan(block.Span.Start, Math.Max(0, block.Span.Length)),
        });
        return box;
    }

    private BlockBox? TryBuildDeclarativeBlock(MarkdownContentFragment fragment)
    {
        // Validate the complete fragment before allocating block indices or
        // recording source-map entries. Unsupported output must fall back to
        // the built-in renderer atomically, without leaving partial layout
        // state behind.
        if (fragment.Items.Count == 0)
            return null;
        foreach (MarkdownContent content in fragment.Items)
        {
            if (!CanBuildDeclarativeContent(content))
                return null;
        }

        var fallbackKey = new HostedElementFallbackKey(fragment);

        if (fragment.Items.Count == 1)
            return TryBuildDeclarativeContent(fragment.Items[0], fallbackKey);

        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
            BlockIndex = _context.NextBlockIndex(),
        };
        TrackBlock(stack);
        foreach (MarkdownContent content in fragment.Items)
        {
            var child = TryBuildDeclarativeContent(content, fallbackKey);
            if (child is null)
            {
                DiscardUnpublishedBlock(stack);
                return null;
            }
            AddStackChild(stack, child);
        }

        if (stack.Children.Count > 0)
            return stack;

        DiscardUnpublishedBlock(stack);
        return null;
    }

    private bool CanBuildDeclarativeContent(MarkdownContent content) => content.Kind switch
    {
        MarkdownContentKind.Text => content.Text is not null,
        MarkdownContentKind.CodeBlock => content.Text is not null,
        MarkdownContentKind.Image => !string.IsNullOrWhiteSpace(content.Destination),
        MarkdownContentKind.VectorScene => content.VectorScene is not null,
        MarkdownContentKind.HostedElement =>
            _enableDeclarativeHostedElements && !string.IsNullOrWhiteSpace(content.FactoryKey),
        MarkdownContentKind.Link =>
            !string.IsNullOrWhiteSpace(content.Destination) &&
            content.Children.Count > 0 &&
            AllChildren(content, CanBuildDeclarativeInlineContent),
        MarkdownContentKind.Container =>
            content.Children.Count > 0 &&
            (AllChildren(content, CanBuildDeclarativeInlineContent) ||
             AllChildren(content, CanBuildDeclarativeContent)),
        MarkdownContentKind.List =>
            content.Children.Count > 0 &&
            AllChildren(content, static child => child.Kind == MarkdownContentKind.ListItem) &&
            AllChildren(content, CanBuildDeclarativeListItem),
        MarkdownContentKind.ListItem => CanBuildDeclarativeListItem(content),
        MarkdownContentKind.Table => CanBuildDeclarativeTable(content),
        _ => false,
    };

    private bool CanBuildDeclarativeListItem(MarkdownContent content) =>
        content.Kind == MarkdownContentKind.ListItem &&
        content.Children.Count > 0 &&
        AllChildren(content, CanBuildDeclarativeContent);

    private bool CanBuildDeclarativeInlineContent(MarkdownContent content) => content.Kind switch
    {
        MarkdownContentKind.Text => content.Text is not null,
        MarkdownContentKind.Image => !string.IsNullOrWhiteSpace(content.Destination),
        MarkdownContentKind.VectorScene => content.VectorScene is not null,
        MarkdownContentKind.Link =>
            !string.IsNullOrWhiteSpace(content.Destination) &&
            content.Children.Count > 0 &&
            AllChildren(content, CanBuildDeclarativeInlineContent),
        MarkdownContentKind.Container =>
            content.Children.Count > 0 && AllChildren(content, CanBuildDeclarativeInlineContent),
        _ => false,
    };

    private bool CanBuildDeclarativeTable(MarkdownContent content)
    {
        if (content.Children.Count == 0 ||
            !AllChildren(content, static child => child.Kind == MarkdownContentKind.TableRow))
        {
            return false;
        }

        int columnCount = content.Children[0].Children.Count;
        if (columnCount == 0)
            return false;

        foreach (MarkdownContent row in content.Children)
        {
            if (row.Children.Count != columnCount ||
                !AllChildren(row, static child => child.Kind == MarkdownContentKind.TableCell))
            {
                return false;
            }

            foreach (MarkdownContent cell in row.Children)
            {
                if (!AllChildren(cell, CanBuildDeclarativeInlineContent))
                    return false;
            }
        }

        return true;
    }

    private static bool AllChildren(
        MarkdownContent content,
        Func<MarkdownContent, bool> predicate)
    {
        foreach (MarkdownContent child in content.Children)
        {
            if (!predicate(child))
                return false;
        }

        return true;
    }

    private BlockBox? TryBuildDeclarativeContent(
        MarkdownContent content,
        HostedElementFallbackKey fallbackKey)
    {
        if (content.Kind == MarkdownContentKind.HostedElement)
        {
            var box = new DeclarativeHostedElementBox(_context, content, fallbackKey)
            {
                BlockIndex = _context.NextBlockIndex(),
            };
            TrackBlock(box);
            if (!content.SourceSpan.IsEmpty)
                _context.SourceMap.Add(box.BlockIndex, 0, 1, content.SourceSpan);
            return box;
        }

        if (content.Kind == MarkdownContentKind.VectorScene && content.VectorScene is not null)
        {
            var vector = new VectorSceneBox(
                _context,
                content,
                GetElementKey(
                    content,
                    content.AccessibilityRole == MarkdownAccessibilityRole.Math
                        ? MarkdownElementKeys.Math
                        : MarkdownElementKeys.Diagram))
            {
                BlockIndex = _context.NextBlockIndex(),
                DeferredSceneBinding = NextMaterializedSceneBinding(),
            };
            TrackBlock(vector);
            if (!content.SourceSpan.IsEmpty)
                _context.SourceMap.Add(vector.BlockIndex, 0, 1, content.SourceSpan);
            return vector;
        }

        if (content.Kind == MarkdownContentKind.Text ||
            content.Kind == MarkdownContentKind.Link ||
            (content.Kind == MarkdownContentKind.Container &&
             AllChildren(content, CanBuildDeclarativeInlineContent)))
        {
            return BuildDeclarativeInlineBlock(content);
        }

        if (content.Kind == MarkdownContentKind.Image)
            return BuildDeclarativeImage(content);

        if (content.Kind == MarkdownContentKind.CodeBlock)
        {
            if (content.Attributes.TryGetValue("renderer.internal.deferred-scene", out string? deferredSceneMarker) &&
                !string.IsNullOrWhiteSpace(deferredSceneMarker) &&
                _context.PerformanceSession is IMarkdownPerformanceSessionInternal session &&
                _context.PerformanceDocumentOwner is { } documentOwner &&
                session.TryGetDeferredSceneResult(documentOwner, deferredSceneMarker,
                    out MarkdownContentFragment? result, out object? projectionIdentity) &&
                TryBuildMaterializedSceneBlock(deferredSceneMarker, result!, projectionIdentity) is { } materialized)
                return materialized;

            return BuildDeclarativeCodeBlock(content);
        }

        if (content.Kind == MarkdownContentKind.List)
            return BuildDeclarativeList(content, fallbackKey);

        if (content.Kind == MarkdownContentKind.Table)
            return BuildDeclarativeTable(content);

        if ((content.Kind == MarkdownContentKind.Container ||
             content.Kind == MarkdownContentKind.ListItem) &&
            content.Children.Count > 0)
        {
            var stack = new StackBox
            {
                FlowDirection = _context.FlowDirection,
                BlockIndex = _context.NextBlockIndex(),
            };
            TrackBlock(stack);
            foreach (MarkdownContent childContent in content.Children)
            {
                var child = TryBuildDeclarativeContent(childContent, fallbackKey);
                if (child is null)
                {
                    DiscardUnpublishedBlock(stack);
                    return null;
                }
                AddStackChild(stack, child);
            }
            if (stack.Children.Count == 0)
            {
                DiscardUnpublishedBlock(stack);
                return null;
            }
            return stack;
        }

        return null;
    }

    private InlineContainerBox BuildDeclarativeInlineBlock(MarkdownContent content)
    {
        var box = new InlineContainerBox(_context, GetElementKey(content, MarkdownElementKeys.Body))
        {
            BlockIndex = _context.NextBlockIndex(),
            TextAlignment = content.AccessibilityRole == MarkdownAccessibilityRole.Math
                ? CanvasHorizontalAlignment.Center
                : CanvasHorizontalAlignment.Left,
        };
        TrackBlock(box);
        var runs = new List<InlineRun>();
        AppendDeclarativeInline(content, runs);
        foreach (InlineRun run in runs)
            AddInlineRun(box, run);
        return box;
    }

    private ImageBox BuildDeclarativeImage(MarkdownContent content)
    {
        TryGetDeclarativeLength(content, MarkdownContentAttributes.ImageWidth, out SafeHtmlLength width);
        TryGetDeclarativeLength(content, MarkdownContentAttributes.ImageHeight, out SafeHtmlLength height);
        var box = new ImageBox(
            _context,
            content.Destination ?? string.Empty,
            content.Text ?? string.Empty,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        TrackBlock(box);
        if (!content.SourceSpan.IsEmpty)
            _context.SourceMap.Add(box.BlockIndex, 0, 1, content.SourceSpan);
        return box;
    }

    private BlockBox? BuildDeclarativeList(
        MarkdownContent content,
        HostedElementFallbackKey fallbackKey)
    {
        using var listScope = _context.PushListDepth();
        var stack = new StackBox
        {
            BlockIndex = _context.NextBlockIndex(),
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(stack);
        bool ordered = GetBooleanAttribute(content, MarkdownContentAttributes.ListOrdered);
        int ordinal = GetPositiveIntegerAttribute(content, MarkdownContentAttributes.ListStart, 1);
        foreach (MarkdownContent item in content.Children)
        {
            ListItemBox? listItem = BuildDeclarativeListItem(item, fallbackKey, ordered, ordinal);
            if (listItem is null)
            {
                DiscardUnpublishedBlock(stack);
                return null;
            }

            AddStackChild(stack, listItem);
            ordinal++;
        }

        return stack;
    }

    private ListItemBox? BuildDeclarativeListItem(
        MarkdownContent item,
        HostedElementFallbackKey fallbackKey,
        bool ordered,
        int ordinal)
    {
        ElementStyle listStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(
            1f,
            listStyle.ListIndent + Math.Max(0, _context.ListDepth - 1) * listStyle.NestedListIndent);
        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker)
        {
            BlockIndex = _context.NextBlockIndex(),
            TextAlignment = ordered
                ? CanvasHorizontalAlignment.Left
                : CanvasHorizontalAlignment.Center,
        };
        TrackBlock(marker);
        marker.Add(new TextRun(ordered ? $"{ordinal}." : "\u2022")
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(item.SourceSpan.Start, 0),
        });

        var body = new StackBox
        {
            BlockIndex = _context.NextBlockIndex(),
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(body);
        foreach (MarkdownContent childContent in item.Children)
        {
            BlockBox? child = TryBuildDeclarativeContent(childContent, fallbackKey);
            if (child is null)
            {
                DiscardUnpublishedBlock(body);
                DiscardUnpublishedBlock(marker);
                return null;
            }

            AddStackChild(body, child);
        }

        var listItem = new ListItemBox(marker, body, markerWidth)
        {
            BlockIndex = _context.NextBlockIndex(),
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(listItem);
        AdoptBlock(marker);
        AdoptBlock(body);
        return listItem;
    }

    private TableBox BuildDeclarativeTable(MarkdownContent content)
    {
        var headerRows = new List<InlineContainerBox[]>();
        var bodyRows = new List<InlineContainerBox[]>();
        foreach (MarkdownContent row in content.Children)
        {
            bool isHeader = GetBooleanAttribute(row, MarkdownContentAttributes.TableHeaderRow) ||
                row.Children.Count > 0 &&
                AllChildren(row, static cell => cell.StyleRole == MarkdownStyleRole.TableHeader);
            var cells = new InlineContainerBox[row.Children.Count];
            for (int index = 0; index < cells.Length; index++)
                cells[index] = BuildDeclarativeTableCell(row.Children[index], isHeader);

            (isHeader ? headerRows : bodyRows).Add(cells);
        }

        var table = new TableBox(_context, headerRows.ToArray(), bodyRows.ToArray())
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        TrackBlock(table);
        foreach (InlineContainerBox[] row in headerRows)
            foreach (InlineContainerBox cell in row)
                AdoptBlock(cell);
        foreach (InlineContainerBox[] row in bodyRows)
            foreach (InlineContainerBox cell in row)
                AdoptBlock(cell);
        return table;
    }

    private InlineContainerBox BuildDeclarativeTableCell(MarkdownContent cell, bool isHeader)
    {
        string elementKey = GetElementKey(
            cell,
            isHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell);
        var box = new InlineContainerBox(_context, elementKey)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        TrackBlock(box);
        var runs = new List<InlineRun>();
        foreach (MarkdownContent child in cell.Children)
            AppendDeclarativeInline(child, runs);
        foreach (InlineRun run in runs)
            AddInlineRun(box, run);
        return box;
    }

    private bool TryCreateDeclarativeInlineRuns(
        MarkdownContentFragment fragment,
        out IReadOnlyList<InlineRun> runs)
    {
        foreach (MarkdownContent item in fragment.Items)
        {
            if (!CanBuildDeclarativeInlineContent(item))
            {
                runs = Array.Empty<InlineRun>();
                return false;
            }
        }

        var result = new List<InlineRun>();
        foreach (MarkdownContent item in fragment.Items)
            AppendDeclarativeInline(item, result);
        runs = result;
        return result.Count > 0;
    }

    private MarkdownDeferredSceneBinding? NextMaterializedSceneBinding() =>
        _materializedSceneMarker is { } marker
            ? new MarkdownDeferredSceneBinding(marker, _materializedSceneOrdinal++, _materializedSceneProjectionIdentity)
            : null;

    private BlockBox? TryBuildMaterializedSceneBlock(
        string marker,
        MarkdownContentFragment fragment,
        object? projectionIdentity)
    {
        string? previousMarker = _materializedSceneMarker;
        int previousOrdinal = _materializedSceneOrdinal;
        object? previousIdentity = _materializedSceneProjectionIdentity;
        _materializedSceneMarker = marker;
        _materializedSceneOrdinal = 0;
        _materializedSceneProjectionIdentity = projectionIdentity;
        try
        {
            return TryBuildDeclarativeBlock(fragment);
        }
        finally
        {
            _materializedSceneMarker = previousMarker;
            _materializedSceneOrdinal = previousOrdinal;
            _materializedSceneProjectionIdentity = previousIdentity;
        }
    }

    private void AppendMaterializedSceneInline(
        string marker,
        MarkdownContentFragment fragment,
        object? projectionIdentity,
        List<InlineRun> destination)
    {
        string? previousMarker = _materializedSceneMarker;
        int previousOrdinal = _materializedSceneOrdinal;
        object? previousIdentity = _materializedSceneProjectionIdentity;
        _materializedSceneMarker = marker;
        _materializedSceneOrdinal = 0;
        _materializedSceneProjectionIdentity = projectionIdentity;
        try
        {
            foreach (MarkdownContent item in fragment.Items)
                AppendDeclarativeInline(item, destination);
        }
        finally
        {
            _materializedSceneMarker = previousMarker;
            _materializedSceneOrdinal = previousOrdinal;
            _materializedSceneProjectionIdentity = previousIdentity;
        }
    }

    private void AppendDeclarativeInline(MarkdownContent content, List<InlineRun> destination)
    {
        if (content.Kind == MarkdownContentKind.VectorScene &&
            content.Attributes.TryGetValue(DeferredSceneMarkerAttribute, out string? marker) &&
            !string.IsNullOrWhiteSpace(marker) &&
            _context.PerformanceSession is IMarkdownPerformanceSessionInternal session &&
            _context.PerformanceDocumentOwner is { } documentOwner &&
            session.TryGetDeferredSceneResult(documentOwner, marker,
                out MarkdownContentFragment? materialized, out object? projectionIdentity) &&
            materialized is not null && materialized.Items.Count > 0)
        {
            AppendMaterializedSceneInline(marker, materialized, projectionIdentity, destination);
            return;
        }

        switch (content.Kind)
        {
            case MarkdownContentKind.Text:
                destination.Add(new TextRun(content.Text ?? string.Empty)
                {
                    ElementKey = GetElementKey(content, string.Empty),
                    SourceSpan = content.SourceSpan,
                });
                break;
            case MarkdownContentKind.Image:
                destination.Add(CreateDeclarativeImageRun(content, linkUrl: null, linkTitle: null));
                break;
            case MarkdownContentKind.VectorScene:
                if (content.VectorScene is not null)
                {
                    string elementKey = GetElementKey(
                        content,
                        content.AccessibilityRole == MarkdownAccessibilityRole.Math
                            ? MarkdownElementKeys.Math
                            : MarkdownElementKeys.Diagram);
                    string? deferredMarker = GetAttribute(content, DeferredSceneMarkerAttribute);
                    var run = new InlineVectorSceneRun(
                        _context,
                        content.VectorScene,
                        content.SemanticText ?? string.Empty,
                        content.AccessibilityName ?? content.SemanticText ?? string.Empty,
                        content.AccessibilityDescription,
                        elementKey)
                    {
                        ElementKey = elementKey,
                        SourceSpan = content.SourceSpan,
                        DeferredSceneMarker = deferredMarker,
                        DeferredSceneBinding = deferredMarker is null
                            ? NextMaterializedSceneBinding()
                            : null,
                    };
                    _context.RegisterConstructionResource(run);
                    destination.Add(run);
                }
                break;
            case MarkdownContentKind.Link:
                if (content.Children.Count == 1 && content.Children[0].Kind == MarkdownContentKind.Image)
                {
                    destination.Add(CreateDeclarativeImageRun(
                        content.Children[0],
                        content.Destination,
                        GetAttribute(content, MarkdownContentAttributes.Title)));
                    break;
                }

                destination.Add(new LinkRun(
                    CollectDeclarativeText(content),
                    content.Destination ?? string.Empty,
                    GetAttribute(content, MarkdownContentAttributes.Title))
                {
                    AccessibilityName = content.AccessibilityName,
                    ElementKey = GetElementKey(content, MarkdownElementKeys.Link),
                    SourceSpan = content.SourceSpan,
                });
                break;
            case MarkdownContentKind.Container:
                foreach (MarkdownContent child in content.Children)
                    AppendDeclarativeInline(child, destination);
                break;
        }
    }

    private InlineImageRun CreateDeclarativeImageRun(
        MarkdownContent image,
        string? linkUrl,
        string? linkTitle)
    {
        TryGetDeclarativeLength(image, MarkdownContentAttributes.ImageWidth, out SafeHtmlLength width);
        TryGetDeclarativeLength(image, MarkdownContentAttributes.ImageHeight, out SafeHtmlLength height);
        return TrackInlineImageRun(new InlineImageRun(
            _context,
            image.Text ?? string.Empty,
            image.Destination ?? string.Empty,
            GetAttribute(image, MarkdownContentAttributes.Title),
            linkUrl,
            linkTitle,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            SourceSpan = image.SourceSpan,
        });
    }

    private static string CollectDeclarativeText(MarkdownContent content)
    {
        var builder = new System.Text.StringBuilder();
        Append(content, builder);
        return builder.Length == 0 ? content.SemanticText ?? string.Empty : builder.ToString();

        static void Append(MarkdownContent node, System.Text.StringBuilder target)
        {
            if (node.Kind == MarkdownContentKind.Text || node.Kind == MarkdownContentKind.Image)
                target.Append(node.Text);
            foreach (MarkdownContent child in node.Children)
                Append(child, target);
        }
    }

    private static string GetElementKey(MarkdownContent content, string fallback) =>
        content.StyleRole.IsEmpty ? fallback : content.StyleRole.Name;

    private static string? GetAttribute(MarkdownContent content, string name) =>
        content.Attributes.TryGetValue(name, out string? value) ? value : null;

    private static bool GetBooleanAttribute(MarkdownContent content, string name) =>
        content.Attributes.TryGetValue(name, out string? value) &&
        bool.TryParse(value, out bool parsed) && parsed;

    private static int GetPositiveIntegerAttribute(MarkdownContent content, string name, int fallback) =>
        content.Attributes.TryGetValue(name, out string? value) &&
        int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
            ? Math.Max(1, parsed)
            : fallback;

    private static bool TryGetDeclarativeLength(
        MarkdownContent content,
        string name,
        out SafeHtmlLength length)
    {
        length = default;
        if (!content.Attributes.TryGetValue(name, out string? raw))
            return false;

        string value = raw.Trim();
        bool percent = value.EndsWith('%');
        if (percent)
            value = value[..^1].Trim();
        if (!float.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float parsed) ||
            !float.IsFinite(parsed) ||
            parsed <= 0)
        {
            return false;
        }

        length = new SafeHtmlLength(
            percent ? Math.Min(parsed, 100f) : Math.Min(parsed, 8192f),
            percent);
        return true;
    }

    private BlockBox BuildParagraphOrImage(ParagraphBlock p)
    {
        // If the paragraph contains only a single image link (optionally wrapped
        // in a single ContainerInline), promote to an ImageBox.
        var inline = p.Inline;
        if (inline is not null)
        {
            LinkInline? onlyImage = null;
            int count = 0;
            foreach (var node in inline)
            {
                count++;
                if (count > 1) { onlyImage = null; break; }
                if (node is LinkInline li && li.IsImage) onlyImage = li;
                else { onlyImage = null; break; }
            }
            if (onlyImage is not null)
            {
                string url = onlyImage.Url ?? string.Empty;
                var altSb = new System.Text.StringBuilder();
                FlattenContainer(onlyImage, altSb);
                string alt = altSb.ToString();
                ElementStyle paragraphStyle = _context.ThemeSnapshot.GetStyle(
                    MarkdownElementKeys.Body,
                    _context.CreateStyleContextSnapshot(),
                    _context.CreateStyleAliasSnapshot());
                var img = new ImageBox(_context, url, alt)
                {
                    // This block represents the entire image-only paragraph, not
                    // only its image element. Reuse paragraph-flow margins while
                    // keeping the image atomic for painting and accessibility.
                    Margin = paragraphStyle.Margin,
                    MinimumParagraphLineHeight = paragraphStyle.FontSize * paragraphStyle.LineHeightMultiplier,
                };
                TrackBlock(img);
                img.BlockIndex = _context.NextBlockIndex();
                // Register the source span so Ctrl+C copies the original ![alt](url).
                var span = new MarkdownRenderer.SourceSpan(p.Span.Start, p.Span.Length);
                _context.SourceMap.Add(img.BlockIndex, 0, 1, span);
                return img;
            }
        }
        return BuildParagraph(p);
    }

    private BlockBox MakeThematicBreak()
    {
        var box = new ThematicBreakBox(_context);
        TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        return box;
    }

    private InlineContainerBox BuildHeading(HeadingBlock h)
    {
        string key = h.Level switch
        {
            1 => MarkdownElementKeys.Heading1,
            2 => MarkdownElementKeys.Heading2,
            3 => MarkdownElementKeys.Heading3,
            4 => MarkdownElementKeys.Heading4,
            5 => MarkdownElementKeys.Heading5,
            _ => MarkdownElementKeys.Heading6
        };
        var box = new InlineContainerBox(_context, key);
        TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        AddInlines(box, h.Inline);
        if (h.Span.Start >= 0 && h.Span.Length > 0)
            _context.SourceMap.AddSourceAffixesToBlock(box.BlockIndex, h.Span.Start, h.Span.Start + h.Span.Length);
        return box;
    }

    private InlineContainerBox BuildParagraph(ParagraphBlock p)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.Body);
        TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        Inline? leadingFlowBreak = _context.SafeHtmlInlineFlowContinuesPrevious
            ? FindLeadingHtmlBreak(p.Inline)
            : null;
        AddInlines(box, p.Inline, skipInline: leadingFlowBreak);
        return box;
    }

    private static bool IsBadgeOnlyHtmlFlowParagraph(ParagraphBlock paragraph)
    {
        bool containsImage = false;
        return paragraph.Inline is { } inline &&
            ContainsOnlyBadgeHtml(inline, ref containsImage) &&
            containsImage;
    }

    private static bool ContainsOnlyBadgeHtml(ContainerInline inline, ref bool containsImage)
    {
        foreach (Inline child in inline)
        {
            if (child is HtmlInline html)
            {
                if (!SafeHtmlParser.TryParseTagSequence(
                        html.Tag,
                        SafeHtmlParseLimits.Default,
                        out IReadOnlyList<SafeHtmlTag> tags) ||
                    tags.Count == 0)
                {
                    return false;
                }

                foreach (SafeHtmlTag tag in tags)
                {
                    if (tag.Name is not ("a" or "br" or "img" or "picture" or "source" or "span"))
                        return false;
                    containsImage |= tag.Name == "img";
                }

                continue;
            }

            if (child is LiteralInline literal &&
                string.IsNullOrWhiteSpace(literal.Content.ToString()))
            {
                continue;
            }

            if (child is ContainerInline nested && ContainsOnlyBadgeHtml(nested, ref containsImage))
                continue;

            return false;
        }

        return true;
    }

    private static Inline? FindLeadingHtmlBreak(ContainerInline? inline)
    {
        if (inline is null)
            return null;

        foreach (Inline child in inline)
        {
            if (child is LiteralInline literal &&
                string.IsNullOrWhiteSpace(literal.Content.ToString()))
            {
                continue;
            }

            if (child is HtmlInline html &&
                SafeHtmlParser.TryParseTagSequence(
                    html.Tag,
                    SafeHtmlParseLimits.Default,
                    out IReadOnlyList<SafeHtmlTag> tags) &&
                tags.Count == 1 &&
                tags[0].Name == "br")
            {
                return child;
            }

            return null;
        }

        return null;
    }

    private BlockBox BuildCodeBlock(LeafBlock block, string text)
    {
        text = CodeBlockMetadata.NormalizeCodeLineEndings(text);
        var metadata = CodeBlockMetadata.FromBlock(block, text);
        SourceSpan sourceSpan = block.Span.Start >= 0 && block.Span.Length > 0
            ? new SourceSpan(block.Span.Start, block.Span.Length)
            : SourceSpan.Empty;
        return BuildCodeBlock(metadata, text, sourceSpan, block);
    }

    private BlockBox BuildDeclarativeCodeBlock(MarkdownContent content)
    {
        string text = CodeBlockMetadata.NormalizeCodeLineEndings(content.Text);
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            content.SourceSpan,
            text,
            content.Language,
            content.Attributes);
        return BuildCodeBlock(metadata, text, content.SourceSpan, parsedBlock: null);
    }

    private BlockBox BuildCodeBlock(
        CodeBlockMetadata metadata,
        string text,
        SourceSpan sourceSpan,
        LeafBlock? parsedBlock)
    {
        bool hasExactParserSourceMap = parsedBlock is not null &&
            TryPrepareCodeBlockSourceMap(parsedBlock, text, out sourceSpan);
        if (parsedBlock is not null && !hasExactParserSourceMap)
            sourceSpan = SourceSpan.Empty;

        int lineCount = CountLogicalLines(text);
        bool showLineNumbers = metadata.ShowLineNumbers ?? _context.CodeBlockLineNumberMode switch
        {
            CodeBlockLineNumberMode.Always => true,
            CodeBlockLineNumberMode.Never => false,
            _ => lineCount > 1,
        };
        var codeBox = new CodeBlockBox(
            _context,
            metadata,
            text,
            _context.IsCodeBlockCopyEnabled,
            showLineNumbers)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        TrackBlock(codeBox);

        if (text.Length <= MaxMonolithicTextLayoutLength &&
            lineCount <= MaxCodeBlockLinesPerChunk)
        {
            var monolithicSourceCursor = new CodeBlockSourceCursor();
            MarkdownSourceMap.SourceProjection? projection = hasExactParserSourceMap
                ? TryCreateCodeBlockChunkProjection(
                    parsedBlock!, text, 0, text.Length, sourceSpan,
                    includeSourcePrefix: true,
                    includeSourceSuffix: true,
                    ref monolithicSourceCursor)
                : null;
            SourceSpan chunkSourceSpan = hasExactParserSourceMap && projection is not null
                ? sourceSpan
                : parsedBlock is null ? SliceSourceSpan(sourceSpan, 0, text.Length, text.Length) : SourceSpan.Empty;
            AddCodeBlockChunk(codeBox, BuildCodeBlockChunk(metadata, text, 0, chunkSourceSpan, projection));
            return codeBox;
        }

        int offset = 0;
        var chunkSourceCursor = new CodeBlockSourceCursor();
        while (offset < text.Length)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int length = GetCodeBlockChunkLength(text, offset, _context.CancellationToken);
            MarkdownSourceMap.SourceProjection? projection = hasExactParserSourceMap
                ? TryCreateCodeBlockChunkProjection(
                    parsedBlock!, text, offset, length, sourceSpan,
                    includeSourcePrefix: offset == 0,
                    includeSourceSuffix: offset + length == text.Length,
                    ref chunkSourceCursor)
                : null;
            SourceSpan chunkSourceSpan = parsedBlock is not null
                ? projection is not null ? sourceSpan : SourceSpan.Empty
                : SliceSourceSpan(sourceSpan, offset, length, text.Length);

            AddCodeBlockChunk(codeBox, BuildCodeBlockChunk(
                metadata,
                text.Substring(offset, length),
                offset,
                chunkSourceSpan,
                projection));
            offset += length;
        }

        return codeBox;
    }

    private static int GetCodeBlockChunkLength(
        string text,
        int offset,
        CancellationToken cancellationToken)
    {
        int maximumLength = Math.Min(MaxMonolithicTextLayoutLength, text.Length - offset);
        int maximumEnd = offset + maximumLength;
        int searchStart = offset;
        int lineBreaks = 0;
        int preferredLength = maximumLength;
        while (searchStart < maximumEnd)
        {
            int newline = text.IndexOf('\n', searchStart, maximumEnd - searchStart);
            if (newline < 0)
                break;
            lineBreaks++;
            searchStart = newline + 1;
            if (lineBreaks >= MaxCodeBlockLinesPerChunk)
            {
                preferredLength = searchStart - offset;
                break;
            }
        }

        if (preferredLength == maximumLength && maximumEnd < text.Length)
        {
            int newline = text.LastIndexOf('\n', maximumEnd - 1, maximumLength);
            if (newline > offset)
                preferredLength = newline - offset + 1;
        }

        return CodeBlockChunkBoundary.GetLinePreservingLength(
            text,
            offset,
            preferredLength,
            maximumLength,
            cancellationToken);
    }

    private InlineContainerBox BuildCodeBlockChunk(
        CodeBlockMetadata metadata,
        string text,
        int textOffset,
        SourceSpan sourceSpan,
        MarkdownSourceMap.SourceProjection? sourceProjection)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.CodeBlock)
        {
            CodeLanguage = metadata.Language,
            CodeBlockTextOffset = textOffset,
            CodeBlockTextLength = text.Length,
            WordWrapping = _context.CodeBlockWrappingMode == CodeBlockWrappingMode.Wrap
                ? Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.Wrap
                : Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
        };
        TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        // No ElementKey on the run: it inherits the container's CodeBlock style.
        // Setting ElementKey = CodeBlock would cause DrawDecorations to draw a
        // per-run background on top of the container-level background (double bg).
        var run = new TextRun(text)
        {
            SourceSpan = sourceSpan,
        };
        box.Add(run, sourceProjection);
        return box;
    }

    private bool TryPrepareCodeBlockSourceMap(LeafBlock block, string text, out SourceSpan sourceSpan)
    {
        sourceSpan = SourceSpan.Empty;
        if (block.Span.Start < 0 || block.Span.Length <= 0)
            return false;

        IReadOnlyList<StringLine> lines = block.Lines.Lines;
        int lineCount = block.Lines.Count;
        if (lineCount <= 0 || lineCount > lines.Count)
            return false;

        MarkdownSourceProjection? transformedSource = _context.SourceProjection;
        string? parserSource = null;
        if (transformedSource is null)
        {
            parserSource = _context.SourceMap.SourceText;
            if (block.Span.Start > parserSource.Length ||
                block.Span.Length > parserSource.Length - block.Span.Start)
            {
                return false;
            }

            sourceSpan = new SourceSpan(block.Span.Start, block.Span.Length);
        }
        else
        {
            // GitHubDetailsBlockRenderer calls MarkdownSourceProjection.ApplyTo
            // before building this fragment. That rewrites AST node spans into
            // document coordinates, while Markdig's StringLine slices remain
            // fragment-local. Do not project block.Span a second time; only
            // line slices below are still in the projection's local domain.
            string sourceText = _context.SourceMap.SourceText;
            if (block.Span.Start > sourceText.Length ||
                block.Span.Length > sourceText.Length - block.Span.Start)
            {
                return false;
            }

            sourceSpan = new SourceSpan(block.Span.Start, block.Span.Length);
        }

        int renderedOffset = 0;
        string? lineSourceText = null;
        for (int index = 0; index < lineCount; index++)
        {
            if ((index & 0x3f) == 0)
                _context.CancellationToken.ThrowIfCancellationRequested();

            StringLine line = lines[index];
            StringSlice slice = line.Slice;
            string? sliceSource = slice.Text;
            int sliceStart = slice.Start;
            int sliceLength = slice.Length;
            if (lineSourceText is null)
            {
                lineSourceText = sliceSource;
                if (transformedSource is null &&
                    (sliceSource is null || !string.Equals(
                        sliceSource,
                        _context.SourceMap.SourceText,
                        StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            if (sliceSource is null || sliceStart < 0 || sliceLength < 0 ||
                sliceStart > sliceSource.Length || sliceLength > sliceSource.Length - sliceStart ||
                !ReferenceEquals(lineSourceText, sliceSource) ||
                sliceLength > text.Length - renderedOffset ||
                !text.AsSpan(renderedOffset, sliceLength).SequenceEqual(sliceSource.AsSpan(sliceStart, sliceLength)))
            {
                return false;
            }

            if (sliceLength > 0 &&
                (!TryMapCodeBlockParserRange(sliceSource, sliceStart, sliceLength, out SourceSpan mappedLine) ||
                 !ContainsSourceRange(sourceSpan, mappedLine)))
            {
                return false;
            }

            renderedOffset += sliceLength;
            // Markdig's StringLineGroup.ToString emits separators only between
            // stored lines. The last line can carry NewLine metadata in the
            // original source (for example, the LF before a closing fence),
            // but that final terminator is not part of the rendered code text.
            int newlineLength = index + 1 < lineCount
                ? GetCodeBlockSourceNewlineLength(line.NewLine)
                : 0;
            if (newlineLength < 0)
                return false;
            if (newlineLength == 0)
                continue;

            int newlineStart = sliceStart + sliceLength;
            if (!IsExpectedSourceNewline(sliceSource, newlineStart, line.NewLine) ||
                renderedOffset >= text.Length || text[renderedOffset] != '\n' ||
                !TryMapCodeBlockParserRange(sliceSource, newlineStart, newlineLength, out SourceSpan mappedNewline) ||
                !ContainsSourceRange(sourceSpan, mappedNewline))
            {
                return false;
            }

            renderedOffset++;
        }

        if (renderedOffset != text.Length)
            return false;

        return true;
    }

    private bool TryMapCodeBlockParserRange(string parserSource, int localStart, int localLength, out SourceSpan sourceSpan)
    {
        sourceSpan = SourceSpan.Empty;
        if (localStart < 0 || localLength < 0 || localStart > parserSource.Length ||
            localLength > parserSource.Length - localStart)
        {
            return false;
        }

        if (_context.SourceProjection is { } projection)
            return projection.TryMapRange(localStart, localLength, out sourceSpan);

        string sourceText = _context.SourceMap.SourceText;
        if (localStart > sourceText.Length || localLength > sourceText.Length - localStart)
            return false;

        sourceSpan = new SourceSpan(localStart, localLength);
        return true;
    }

    private MarkdownSourceMap.SourceProjection? TryCreateCodeBlockChunkProjection(
        LeafBlock block,
        string text,
        int chunkStart,
        int chunkLength,
        SourceSpan blockSourceSpan,
        bool includeSourcePrefix,
        bool includeSourceSuffix,
        ref CodeBlockSourceCursor cursor)
    {
        if (chunkStart < 0 || chunkLength <= 0 || chunkStart > text.Length || chunkLength > text.Length - chunkStart ||
            blockSourceSpan.IsEmpty)
        {
            return null;
        }

        int chunkEnd = chunkStart + chunkLength;
        IReadOnlyList<StringLine> lines = block.Lines.Lines;
        int lineCount = block.Lines.Count;
        if (lineCount <= 0 || lineCount > lines.Count)
            return null;

        var segments = new List<MarkdownSourceMap.ProjectionSegment>(Math.Min(lineCount, 260));
        while (cursor.LineIndex < lineCount && cursor.RenderedOffset < chunkEnd)
        {
            if ((cursor.LineIndex & 0x3f) == 0)
                _context.CancellationToken.ThrowIfCancellationRequested();

            StringLine line = lines[cursor.LineIndex];
            StringSlice slice = line.Slice;
            string? parserSource = slice.Text;
            cursor.ParserSource ??= parserSource;
            int contentStart = cursor.RenderedOffset;
            int contentLength = slice.Length;
            if (parserSource is null || !ReferenceEquals(cursor.ParserSource, parserSource) ||
                slice.Start < 0 || slice.Start > parserSource.Length || contentLength < 0 ||
                contentLength > parserSource.Length - slice.Start || contentStart > text.Length ||
                contentLength > text.Length - contentStart)
            {
                return null;
            }

            int contentEnd = contentStart + contentLength;
            if (!text.AsSpan(contentStart, contentLength).SequenceEqual(parserSource.AsSpan(slice.Start, contentLength)))
            {
                return null;
            }

            int selectedContentStart = Math.Max(contentStart, chunkStart);
            int selectedContentEnd = Math.Min(contentEnd, chunkEnd);
            if (selectedContentEnd > selectedContentStart)
            {
                int parserStart = slice.Start + selectedContentStart - contentStart;
                int parserLength = selectedContentEnd - selectedContentStart;
                if (!TryAddCodeBlockProjectionSegment(
                    segments,
                    selectedContentStart - chunkStart,
                    selectedContentEnd - selectedContentStart,
                    parserSource,
                    parserStart,
                    parserLength,
                    blockSourceSpan,
                    allowAtomicLengthChange: false))
                {
                    return null;
                }
            }

            int sourceNewlineLength = cursor.LineIndex + 1 < lineCount
                ? GetCodeBlockSourceNewlineLength(line.NewLine)
                : 0;
            if (sourceNewlineLength < 0)
                return null;
            int renderedLineEnd = contentEnd;
            if (sourceNewlineLength > 0 && renderedLineEnd >= chunkStart && renderedLineEnd < chunkEnd)
            {
                int parserNewlineStart = slice.Start + contentLength;
                if (!IsExpectedSourceNewline(parserSource, parserNewlineStart, line.NewLine) ||
                    renderedLineEnd >= text.Length || text[renderedLineEnd] != '\n' ||
                    !TryAddCodeBlockProjectionSegment(
                        segments,
                        renderedLineEnd - chunkStart,
                        1,
                        parserSource,
                        parserNewlineStart,
                        sourceNewlineLength,
                        blockSourceSpan,
                        allowAtomicLengthChange: true))
                {
                    return null;
                }
            }

            int renderedLineLength = contentLength + (sourceNewlineLength > 0 ? 1 : 0);
            int renderedLineEndWithNewline = contentStart + renderedLineLength;
            if (renderedLineEndWithNewline <= chunkEnd)
            {
                cursor.LineIndex++;
                cursor.RenderedOffset = renderedLineEndWithNewline;
            }
            else
            {
                break;
            }
        }

        if (segments.Count == 0)
            return null;

        return MarkdownSourceMap.SourceProjection.TryCreateExactSegments(
            chunkLength,
            blockSourceSpan.Length,
            segments,
            includeSourcePrefix,
            includeSourceSuffix);
    }

    private bool TryAddCodeBlockProjectionSegment(
        List<MarkdownSourceMap.ProjectionSegment> segments,
        int renderedStart,
        int renderedLength,
        string parserSource,
        int parserStart,
        int parserSourceLength,
        SourceSpan blockSourceSpan,
        bool allowAtomicLengthChange)
    {
        if (renderedLength <= 0 || parserSourceLength <= 0 ||
            !TryMapCodeBlockParserRange(parserSource, parserStart, parserSourceLength, out SourceSpan mapped) ||
            !ContainsSourceRange(blockSourceSpan, mapped) || mapped.IsEmpty)
        {
            return false;
        }

        if (allowAtomicLengthChange)
        {
            // Markdig emits one normalized LF for CR, LF, or CRLF source line
            // endings. Preserve the parser's complete authored terminator as
            // one atomic source range instead of treating its source width as
            // rendered text.
            if (renderedLength != 1 ||
                !TryMapCodeBlockParserRange(parserSource, parserStart, parserSourceLength, out mapped) ||
                !ContainsSourceRange(blockSourceSpan, mapped) || mapped.IsEmpty)
            {
                return false;
            }

            segments.Add(new MarkdownSourceMap.ProjectionSegment(
                renderedStart,
                1,
                SourceStart: mapped.Start - blockSourceSpan.Start,
                SourceLength: mapped.Length,
                IsAtomic: mapped.Length != 1));
            return true;
        }

        MarkdownSourceProjection? transformedSource = _context.SourceProjection;
        int parserEnd = parserStart + parserSourceLength;
        int current = parserStart;
        int renderedCurrent = renderedStart;
        while (current < parserEnd)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int pieceEnd = transformedSource is null
                ? parserEnd
                : transformedSource.GetNextSegmentEnd(current, parserEnd);
            if (pieceEnd <= current ||
                !TryMapCodeBlockParserRange(parserSource, current, pieceEnd - current, out mapped) ||
                !ContainsSourceRange(blockSourceSpan, mapped) || mapped.IsEmpty)
            {
                return false;
            }

            int pieceLength = pieceEnd - current;
            if (mapped.Length != pieceLength)
            {
                return false;
            }

            segments.Add(new MarkdownSourceMap.ProjectionSegment(
                renderedCurrent,
                pieceLength,
                SourceStart: mapped.Start - blockSourceSpan.Start,
                SourceLength: mapped.Length));
            current = pieceEnd;
            renderedCurrent += pieceLength;
        }

        return renderedCurrent == renderedStart + renderedLength;
    }

    private static bool ContainsSourceRange(SourceSpan outer, SourceSpan inner)
        => !inner.IsEmpty && inner.Start >= outer.Start && inner.End <= outer.End;

    private static int GetCodeBlockSourceNewlineLength(NewLine newline)
        => newline switch
        {
            NewLine.None => 0,
            NewLine.CarriageReturn or NewLine.LineFeed => 1,
            NewLine.CarriageReturnLineFeed => 2,
            _ => -1,
        };

    private static bool IsExpectedSourceNewline(string source, int offset, NewLine newline)
    {
        int length = GetCodeBlockSourceNewlineLength(newline);
        if (offset < 0 || length <= 0 || offset > source.Length || length > source.Length - offset)
            return false;

        return newline switch
        {
            NewLine.CarriageReturn => source[offset] == '\r',
            NewLine.LineFeed => source[offset] == '\n',
            NewLine.CarriageReturnLineFeed => source[offset] == '\r' && source[offset + 1] == '\n',
            _ => false,
        };
    }

    private struct CodeBlockSourceCursor
    {
        internal int LineIndex;
        internal int RenderedOffset;
        internal string? ParserSource;
    }

    private static int CountLogicalLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int lines = 1;
        foreach (char ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return text.EndsWith("\n", StringComparison.Ordinal) && lines > 1 ? lines - 1 : lines;
    }

    private static SourceSpan SliceSourceSpan(
        SourceSpan sourceSpan,
        int textOffset,
        int textLength,
        int totalTextLength)
    {
        if (sourceSpan.IsEmpty || totalTextLength <= 0)
            return SourceSpan.Empty;

        double scale = sourceSpan.Length / (double)totalTextLength;
        int start = sourceSpan.Start + (int)Math.Round(textOffset * scale);
        int end = sourceSpan.Start + (int)Math.Round((textOffset + textLength) * scale);
        return new SourceSpan(start, Math.Max(0, end - start));
    }

    private StackBox BuildQuote(QuoteBlock qb)
    {
        var style = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Quote,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        var stack = new StackBox
        {
            ContentPadding = style.Padding,
            AccentBar = style.AccentBar,
            AccentBarThickness = style.AccentBarThickness,
            AccentBarAffectsContentLayout = style.AccentBarAffectsContentLayout,
            Background = style.Background,
            BorderBrush = style.BorderBrush,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
            Margin = style.Margin,
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(stack);
        stack.BlockIndex = _context.NextBlockIndex();
        using var quoteScoue = _context.PushStyleContext(MarkdownElementKeys.Quote);
        PopulateBlockChildren(stack, qb);
        CollapseTrailingQuoteParagraphMargin(stack);
        return stack;
    }

    private void CollapseTrailingQuoteParagraphMargin(StackBox quote)
    {
        // CSS collapses a last child's bottom margin with its parent's bottom
        // margin only when no bottom padding or border separates them. Keep the
        // larger positive margin, and limit this to stylesheets that opt into
        // margin collapse so authored/default additive layouts remain intact.
        if ((_context.ThemeSnapshot.LayoutPolicy & 1) == 0 ||
            quote.ContentPadding.Bottom > 0 ||
            quote.BorderThickness > 0 ||
            quote.Children.Count == 0)
        {
            return;
        }

        BlockBox trailingBlock = quote.Children[quote.Children.Count - 1];
        Thickness paragraphMargin;
        switch (trailingBlock)
        {
            case InlineContainerBox trailingParagraph:
                paragraphMargin = trailingParagraph.Margin;
                break;
            case ImageBox trailingImage:
                // Markdown image-only paragraphs are promoted to ImageBox but
                // still carry the paragraph's margin and line-box geometry.
                paragraphMargin = trailingImage.Margin;
                break;
            default:
                return;
        }

        double collapsedMargin = StackBox.GetPositiveCollapsedMargin(
            paragraphMargin.Bottom,
            quote.Margin.Bottom);
        if (collapsedMargin <= 0)
            return;

        quote.RememberCollapsedTrailingChildMargin(trailingBlock, paragraphMargin);

        var collapsedParagraphMargin = new Thickness(
            paragraphMargin.Left,
            paragraphMargin.Top,
            paragraphMargin.Right,
            paragraphMargin.Bottom - collapsedMargin);
        if (trailingBlock is InlineContainerBox inlineParagraph)
        {
            inlineParagraph.Margin = collapsedParagraphMargin;
            inlineParagraph.UseContainerMargin = false;
        }
        else if (trailingBlock is ImageBox imageParagraph)
        {
            imageParagraph.Margin = collapsedParagraphMargin;
        }
    }

    private StackBox BuildList(ListBlock list)
    {
        using var listScope = _context.PushListDepth();
        Thickness listMargin = _context.ThemeSnapshot.GetStyle(
            ListContainerStyleRole,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot()).Margin;
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
            Margin = listMargin,
            InterChildSpacing = list.IsLoose ? 0 : GetTightListSiblingSpacing(),
        };
        TrackBlock(stack);
        stack.BlockIndex = _context.NextBlockIndex();
        // Honour the ordered-list start number from the source (e.g. `5.`).
        // Markdig stores this as a string on ListBlock.OrderedStart.
        int index = 1;
        if (list.IsOrdered && !string.IsNullOrEmpty(list.OrderedStart)
            && int.TryParse(list.OrderedStart, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            index = parsed;
        }
        foreach (var item in list)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (item is not ListItemBlock li) continue;

            BlockBox? itemBox = null;
            using (var itemAttrs = _context.PushMarkdownAttributes(li))
            {
                if (_semanticDocument is { } document &&
                    DeclarativeBlockSelection.TryGetContent(
                        document,
                        li,
                        _hostedElementFallbacks,
                        out var extensionFragment))
                {
                    itemBox = TryBuildDeclarativeBlock(extensionFragment!);
                }

                if (itemBox is null &&
                    _context.Registry.TryGetRenderer(typeof(ListItemBlock), out var itemRenderer) &&
                    itemRenderer is not null)
                    itemBox = itemRenderer.BuildBlock(li, _context);

                itemBox ??= BuildDefaultListItem(li, list.IsOrdered, index, isTightList: !list.IsLoose);

                TrackBlock(itemBox);
                if (itemBox.BlockIndex == 0) itemBox.BlockIndex = _context.NextBlockIndex();
                _context.RegisterMarkdownAttributes(li, itemBox.BlockIndex);
                AddStackChild(stack, itemBox);
            }
            index++;
        }
        return stack;
    }

    private ListItemBox BuildDefaultListItem(
        ListItemBlock li,
        bool isOrdered,
        int index,
        bool isTightList)
    {
        var listStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(1f, listStyle.ListIndent + Math.Max(0, _context.ListDepth - 1) * listStyle.NestedListIndent);

        // Marker gutter — fixed width; center the unordered bullet without
        // changing the content column or ordered-marker alignment.
        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker);
        TrackBlock(marker);
        marker.BlockIndex = _context.NextBlockIndex();
        marker.TextAlignment = isOrdered
            ? CanvasHorizontalAlignment.Left
            : CanvasHorizontalAlignment.Center;
        string markerText = isOrdered ? $"{index}." : "•";
        marker.Add(new TextRun(markerText)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(li.Span.Start, 0)
        });

        // Content area — all child blocks of the list item.
        var content = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(content);
        content.BlockIndex = _context.NextBlockIndex();
        PopulateBlockChildren(content, li, suppressDirectParagraphMargins: isTightList);

        var itemBox = new ListItemBox(marker, content, markerWidth)
        {
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(itemBox);
        AdoptBlock(marker);
        AdoptBlock(content);
        return itemBox;
    }

    private StackBox BuildGenericContainer(ContainerBlock cb)
    {
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        TrackBlock(stack);
        stack.BlockIndex = _context.NextBlockIndex();
        PopulateBlockChildren(stack, cb);
        return stack;
    }

    private float GetTightListSiblingSpacing()
    {
        ElementStyle bodyStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Body,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float fontSize = bodyStyle.FontSize;
        return float.IsFinite(fontSize) && fontSize > 0
            ? fontSize * 0.25f
            : 0;
    }

    private void PopulateBlockChildren(
        StackBox destination,
        ContainerBlock container,
        bool suppressDirectParagraphMargins = false)
    {
        SafeHtmlBlockScopeTracker? htmlScopes = _context.Registry.SafeHtmlPolicy is null
            ? null
            : _context.SafeHtmlNestedScopes ??
              new SafeHtmlBlockScopeTracker(_context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = htmlScopes?.BudgetExceeded == true;
        foreach (Block child in container)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            SafeHtmlAlignment alignmentAtBlockEntry = htmlScopes?.CurrentAlignment ?? SafeHtmlAlignment.Inherit;
            int? inlineFlowScopeIdAtBlockEntry = htmlScopes?.CurrentInlineFlowScopeId;
            bool scopeOnly = child is HtmlBlock htmlBlock && htmlScopes?.Process(
                    htmlBlock.Lines.ToString(),
                    htmlBlock.Span.Start,
                    _context.DisclosureStates,
                    _context.CancellationToken) == true;
            int? inlineFlowScopeIdForBlock = htmlScopes?.CurrentInlineFlowScopeId;
            if (inlineFlowScopeIdAtBlockEntry is int entryScopeId &&
                inlineFlowScopeIdForBlock != entryScopeId)
            {
                inlineFlowScopeIdForBlock = null;
            }
            bool inlineFlowContinuesPrevious = inlineFlowScopeIdForBlock is int scopeId &&
                destination.Children.Count > 0 &&
                destination.Children[^1].SafeHtmlInlineFlowScopeId == scopeId;
            if (suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    _context.DisclosureStates,
                    _context.CancellationToken);
            }

            if (htmlScopes?.BudgetExceeded == true)
            {
                if (!htmlBudgetNoticeAdded)
                {
                    var notice = new InlineContainerBox(_context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = _context.NextBlockIndex(),
                    };
                    TrackBlock(notice);
                    AddHtmlBudgetNotice(notice);
                    AddStackChild(destination, notice);
                    htmlBudgetNoticeAdded = true;
                }

                if (child is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }

            if (scopeOnly || suppressedBeforeBlock)
                continue;

            SafeHtmlBlockScopeTracker? nestedScopes = child is ContainerBlock or HtmlBlock
                ? htmlScopes?.CloneForNestedContent()
                : null;
            BlockBox? box;
            using (_context.PushSafeHtmlBlockContext(
                       alignmentAtBlockEntry,
                       nestedScopes,
                       inlineFlowScopeIdForBlock,
                       inlineFlowContinuesPrevious))
                box = BuildBlock(child);
            if (box is null)
                continue;

            if (suppressDirectParagraphMargins && child is ParagraphBlock)
            {
                Thickness margin = box.Margin;
                box.Margin = new Thickness(margin.Left, 0, margin.Right, 0);
                if (box is InlineContainerBox paragraphBox)
                    paragraphBox.UseContainerMargin = false;
            }

            if (htmlScopes is not null && child is not HtmlBlock)
                ApplyHtmlAlignment(box, alignmentAtBlockEntry);

            if (child is ParagraphBlock paragraph &&
                box is InlineContainerBox inlineParagraph &&
                inlineFlowScopeIdForBlock is int inlineFlowScopeId &&
                IsBadgeOnlyHtmlFlowParagraph(paragraph))
            {
                inlineParagraph.SafeHtmlInlineFlowScopeId = inlineFlowScopeId;
                inlineParagraph.UseContainerMargin = false;
                inlineParagraph.Margin = default;
            }

            AddStackChild(destination, box);

            if (!suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    _context.DisclosureStates,
                    _context.CancellationToken);
                if (htmlScopes?.BudgetExceeded == true && !htmlBudgetNoticeAdded)
                {
                    var notice = new InlineContainerBox(_context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = _context.NextBlockIndex(),
                    };
                    TrackBlock(notice);
                    AddHtmlBudgetNotice(notice);
                    AddStackChild(destination, notice);
                    htmlBudgetNoticeAdded = true;
                }
            }
        }
    }

    private MarkdownLayoutConstructionScope BeginBlockBuildOwnership() =>
        _context.BeginConstructionScope();

    private void EndBlockBuildOwnership(MarkdownLayoutConstructionScope ownership) =>
        ownership.Dispose();

    private T TrackBlock<T>(T block) where T : BlockBox
    {
        _context.RegisterConstructionBlock(block);
        return block;
    }

    private void AdoptBlock(BlockBox child) => _context.AdoptConstructionBlock(child);

    private void AddStackChild(StackBox parent, BlockBox child)
    {
        parent.Add(child);
        AdoptBlock(child);
    }

    private InlineImageRun TrackInlineImageRun(InlineImageRun run)
    {
        TrackBlock(run.Image);
        return run;
    }

    private InlineRun? TrackInlineImageRun(InlineRun? run)
    {
        if (run is InlineImageRun image)
            TrackBlock(image.Image);
        return run;
    }

    private void AddInlineRun(InlineContainerBox parent, InlineRun run)
    {
        parent.Add(run);
        if (run is InlineImageRun image)
            AdoptBlock(image.Image);
        else if (run is InlineVectorSceneRun vector)
            _context.AdoptConstructionResource(vector);
    }

    private void AddCodeBlockChunk(CodeBlockBox parent, InlineContainerBox child)
    {
        parent.AddChunk(child);
        AdoptBlock(child);
    }

    private void DiscardUnpublishedBlock(BlockBox block)
    {
        _context.DiscardConstructionBlock(block);
    }

    private void DisposePreservingBuildFailure(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            TrackRetirementTask(Task.FromException(exception));
        }
    }

    private static Task RetireSnapshotPreservingBuildFailure(LayoutSnapshot snapshot)
    {
        try
        {
            return snapshot.Retire();
        }
        catch (Exception retirementFailure)
        {
            try
            {
                snapshot.Dispose();
            }
            catch (Exception disposalFailure)
            {
                return Task.FromException(new AggregateException(
                    "Snapshot retirement scheduling and synchronous fallback both failed.",
                    retirementFailure,
                    disposalFailure));
            }

            return Task.FromException(retirementFailure);
        }
    }

    private void TrackRetirementTasks(List<Task> retirementTasks)
    {
        foreach (Task task in retirementTasks)
            TrackRetirementTask(task);
    }

    private void TrackRetirementTask(Task task)
    {
        if (task.IsCompletedSuccessfully)
            return;

        try
        {
            _context.RegisterRetirement(task);
        }
        catch (Exception)
        {
            // Keep the original build failure if retirement accounting itself
            // is unavailable; the synchronous disposal was still attempted.
        }
    }

    private void AddInlines(
        InlineContainerBox box,
        ContainerInline? inline,
        int inheritedAliasStart = -1,
        Inline? skipInline = null)
    {
        if (inline is null) return;
        var htmlState = new SafeHtmlInlineState(_context.Registry.SafeHtmlPolicy);
        AddInlines(
            box,
            inline,
            htmlState,
            inheritedAliasStart,
            Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null,
            skipInline: skipInline);
        if (htmlState.BudgetExceeded)
            AddHtmlBudgetNotice(box);
    }

    private void AddExtensionContainerInlines(
        InlineContainerBox box, ContainerInline inlines,
        Func<Inline, bool>? skipFirstIf, int inheritedAliasStart)
    {
        TrackBlock(box);
        _context.CancellationToken.ThrowIfCancellationRequested();
        Inline? first = inlines.FirstChild;
        Inline? skip = first is not null && skipFirstIf?.Invoke(first) == true ? first : null;
        AddInlines(box, inlines, inheritedAliasStart, skip);
    }

    private void AddHtmlBudgetNotice(InlineContainerBox box) => box.Add(new TextRun(
        _context.ResolveString(MarkdownStringKeys.HtmlBudgetExceeded, MarkdownLocalizedStrings.HtmlBudgetExceeded))
    {
        SourceSpan = SourceSpan.Empty,
    });

    private void AddInlines(
        InlineContainerBox box,
        ContainerInline inline,
        SafeHtmlInlineState htmlState,
        int inheritedAliasStart,
        IReadOnlyList<string> inheritedStyleModifiers,
        string? containingLinkUrl,
        string? containingLinkTitle,
        Inline? skipInline = null)
    {
        foreach (var i in inline)
        {
            if (ReferenceEquals(i, skipInline))
                continue;

            _context.CancellationToken.ThrowIfCancellationRequested();
            if (!htmlState.TryAcceptNode(i))
                break;
            int aliasStart = _context.StyleAliasCount;
            using var inlineAttrs = _context.PushMarkdownAttributes(i);

            if (_semanticDocument is { } semanticDocument &&
                semanticDocument.TryGetInlineExtensionContent(i, out var declarativeFragment) &&
                declarativeFragment is not null &&
                TryCreateDeclarativeInlineRuns(declarativeFragment, out var declarativeRuns))
            {
                int declarativeAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                IReadOnlyList<string> aliases = _context.CreateStyleAliasSnapshotFrom(declarativeAliasStart);
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                foreach (InlineRun sourceRun in declarativeRuns)
                {
                    InlineRun declarativeRun = ApplyContainingLink(
                        sourceRun,
                        containingLinkUrl,
                        containingLinkTitle) ?? sourceRun;
                    declarativeRun.StyleAliases = aliases;
                    AddInlineRun(box, declarativeRun);
                }
                continue;
            }

            if (i is EmphasisInline emphasis)
            {
                string emphasisKey = GetEmphasisElementKey(emphasis);
                if (_context.ThemeSnapshot.HasRelativeScriptFontSizeRules || ContainsMarkdownLink(emphasis))
                {
                    _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                    int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                    using SafeHtmlInlineState.ScriptStyleScope scriptScope =
                        htmlState.PushMarkdownScript(emphasisKey);
                    AddInlines(
                        box,
                        emphasis,
                        htmlState,
                        effectiveAliasStart,
                        AppendStyleModifier(
                            inheritedStyleModifiers,
                            emphasisKey),
                        containingLinkUrl,
                        containingLinkTitle);
                    continue;
                }
            }

            InlineRun? run;
            if (i is HtmlInline html)
            {
                run = TrackInlineImageRun(htmlState.Process(
                    html,
                    _context,
                    containingLinkUrl,
                    containingLinkTitle));
            }
            else if (i is LinkInline link &&
                     TryGetOnlyHtmlImageChild(link, out HtmlInline? linkedHtmlImage) &&
                     htmlState.IsStandaloneImage(linkedHtmlImage, _context))
            {
                run = TrackInlineImageRun(htmlState.Process(
                    linkedHtmlImage,
                    _context,
                    link.Url,
                    link.Title,
                    GetLinkedHtmlImageSourceSpan(link, linkedHtmlImage, _context)));
            }
            else if (i is LinkInline mixedLink &&
                     !mixedLink.IsImage &&
                     ContainsRenderableLinkedInlineObject(mixedLink, htmlState, _context))
            {
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    mixedLink,
                    htmlState,
                    effectiveAliasStart,
                    inheritedStyleModifiers,
                    mixedLink.Url,
                    mixedLink.Title);
                continue;
            }
            else if (!string.IsNullOrWhiteSpace(containingLinkUrl) &&
                     i is LinkInline { IsImage: true } containedImage)
            {
                run = BuildImageRun(
                    containedImage,
                    containingLinkUrl,
                    containingLinkTitle,
                    containedImage.Span.Start,
                    containedImage.Span.Length);
            }
            else
            {
                int linkStyleAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run = htmlState.Apply(BuildInline(i, box.BlockIndex, linkStyleAliasStart));
                run = ApplyContainingLink(run, containingLinkUrl, containingLinkTitle);
            }
            if (run is not null)
            {
                run.StyleModifierKeys = CombineStyleAliases(
                    run.StyleModifierKeys,
                    inheritedStyleModifiers);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run.StyleAliases = CombineStyleAliases(
                    run.StyleAliases,
                    _context.CreateStyleAliasSnapshotFrom(effectiveAliasStart));
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                AddInlineRun(box, run);
            }
            else if (i is ContainerInline nested)
            {
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    nested,
                    htmlState,
                    effectiveAliasStart,
                    inheritedStyleModifiers,
                    containingLinkUrl,
                    containingLinkTitle);
            }
        }
    }

    private bool ContainsRenderableLinkedInlineObject(
        ContainerInline container,
        SafeHtmlInlineState htmlState,
        MarkdownLayoutContext context)
    {
        foreach (Inline child in container)
        {
            if (child is LinkInline { IsImage: true })
                return true;
            if (child is HtmlInline html && htmlState.IsStandaloneImage(html, context))
                return true;
            if (_semanticDocument is not null &&
                _semanticDocument.TryGetInlineExtensionContent(child, out MarkdownContentFragment? fragment) &&
                fragment is not null &&
                ContainsRenderableVectorScene(fragment.Items))
            {
                return true;
            }
            if (child is ContainerInline nested &&
                ContainsRenderableLinkedInlineObject(nested, htmlState, context))
            {
                return true;
            }
        }

        return false;

        static bool ContainsRenderableVectorScene(IReadOnlyList<MarkdownContent> content)
        {
            foreach (MarkdownContent item in content)
            {
                if (item.Kind == MarkdownContentKind.VectorScene && item.VectorScene is not null)
                    return true;
                if (item.Children.Count > 0 && ContainsRenderableVectorScene(item.Children))
                    return true;
            }

            return false;
        }
    }

    private static InlineRun? ApplyContainingLink(
        InlineRun? run,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (run is null ||
            run is LinkRun or InlineImageRun or InlineSpacerRun ||
            string.IsNullOrWhiteSpace(containingLinkUrl))
        {
            return run;
        }

        if (run is InlineVectorSceneRun vectorScene)
        {
            vectorScene.LinkUrl = containingLinkUrl;
            vectorScene.LinkTitle = containingLinkTitle;
            vectorScene.StyleModifierKeys = AppendStyleModifier(
                vectorScene.StyleModifierKeys,
                MarkdownElementKeys.Link);
            return vectorScene;
        }

        var linked = new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
            StyleAliases = run.StyleAliases,
            ScriptStyleAncestryKeys = run.ScriptStyleAncestryKeys,
            SafeHtmlScriptRoleIsRunElement = run.SafeHtmlScriptRoleIsRunElement,
        };
        linked.StyleModifierKeys = string.IsNullOrEmpty(run.ElementKey)
            ? run.StyleModifierKeys
            : AppendStyleModifier(run.StyleModifierKeys, run.ElementKey);
        return linked;
    }

    private static bool ContainsMarkdownLink(ContainerInline container)
    {
        foreach (Inline child in container)
        {
            // An autolink inside emphasis is a link too; flattening the
            // emphasis would drop it (P3-009).
            if (child is LinkInline or AutolinkInline)
                return true;

            if (child is ContainerInline nested && ContainsMarkdownLink(nested))
                return true;
        }

        return false;
    }

    private static string GetEmphasisElementKey(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
            return MarkdownElementKeys.Strikethrough;
        if (emphasis.DelimiterChar == '~')
            return MarkdownElementKeys.Subscript;
        if (emphasis.DelimiterChar == '^')
            return MarkdownElementKeys.Superscript;
        if (emphasis.DelimiterChar == '+')
            return MarkdownElementKeys.Inserted;
        if (emphasis.DelimiterChar == '=')
            return MarkdownElementKeys.Marked;
        return emphasis.DelimiterCount >= 2
            ? MarkdownElementKeys.Strong
            : MarkdownElementKeys.Emphasis;
    }

    private static IReadOnlyList<string> AppendStyleModifier(
        IReadOnlyList<string> modifiers,
        string elementKey)
    {
        var result = new string[modifiers.Count + 1];
        for (int index = 0; index < modifiers.Count; index++)
            result[index] = modifiers[index];
        result[^1] = elementKey;
        return result;
    }

    private static IReadOnlyList<string> CombineStyleAliases(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0)
            return second;
        if (second.Count == 0)
            return first;

        var combined = new string[first.Count + second.Count];
        for (int index = 0; index < first.Count; index++)
            combined[index] = first[index];
        for (int index = 0; index < second.Count; index++)
            combined[first.Count + index] = second[index];
        return combined;
    }

    private InlineRun? BuildInline(
        Inline inline,
        int parentBlockIndex = -1,
        int inlineStyleAliasStart = -1)
    {
        switch (inline)
        {
            case LiteralInline lit:
            {
                var span = new SourceSpan(lit.Span.Start, lit.Span.Length);
                string? sourceText = lit.Content.Text;
                if (sourceText is not null &&
                    ReferenceEquals(sourceText, _context.SourceMap.SourceText) &&
                    span.Length > 0 &&
                    span.Start >= 0 &&
                    span.Start <= sourceText.Length - span.Length &&
                    lit.Content.Start == span.Start &&
                    lit.Content.Length == span.Length)
                {
                    return new SourceSliceTextRun(sourceText, span.Start, span.Length)
                    {
                        SourceSpan = span,
                    };
                }

                return new TextRun(lit.Content.ToString())
                {
                    SourceSpan = span,
                };
            }
            case CodeInline ci:
                return new CodeInlineRun(ci.Content)
                {
                    SourceSpan = new SourceSpan(ci.Span.Start, ci.Span.Length)
                };
            case EmphasisInline emph:
                return BuildEmphasis(emph);
            case LinkInline link:
                return BuildLink(link, inlineStyleAliasStart);
            case LineBreakInline lineBreak:
                return new LineBreakRun(lineBreak.IsHard) { SourceSpan = new SourceSpan(inline.Span.Start, inline.Span.Length) };
            case AutolinkInline al:
                return new LinkRun(al.Url, al.Url) { SourceSpan = new SourceSpan(al.Span.Start, al.Span.Length) };
            case HtmlEntityInline entity:
                return new TextRun(entity.Transcoded.ToString())
                {
                    SourceSpan = new SourceSpan(entity.Span.Start, entity.Span.Length)
                };
            case AbbreviationInline abbreviation:
                return new AbbreviationRun(
                    abbreviation.Abbreviation?.Label ?? string.Empty,
                    abbreviation.Abbreviation?.Text.ToString() ?? string.Empty)
                {
                    SourceSpan = new SourceSpan(abbreviation.Span.Start, abbreviation.Span.Length)
                };
            case FootnoteLink fl when !fl.IsBackLink:
            {
                // Render footnote forward-references as clickable superscript links.
                // URL uses the internal fragment scheme "#footnote-def-{order}" which
                // MarkdownRendererControl intercepts to scroll to the definition.
                // Use fl.Footnote.Order (the footnote's 1-based sequence number), NOT
                // fl.Index (which is a global sequential counter across all citations
                // of all footnotes and differs from Order when a footnote is cited
                // more than once).
                int order = fl.Footnote is { } footnote
                    ? _context.GetOrCreateFootnoteOrder(footnote, fl.Index)
                    : Math.Max(1, fl.Index);
                var run = new LinkRun(ToSuperscript(order), $"#footnote-def-{order}")
                {
                    SourceSpan = new SourceSpan(fl.Span.Start, fl.Span.Length),
                    IsSuperscript = true,
                };
                // Record the containing paragraph as the "ref" block so the
                // footnote definition's ↩ link can scroll back to the citation.
                if (parentBlockIndex >= 0) _context.RegisterFootnoteRef(order, parentBlockIndex);
                return run;
            }
        }
        return null;
    }

    private InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new System.Text.StringBuilder();
        FlattenContainer(emph, sb);
        var span = new SourceSpan(emph.Span.Start, emph.Span.Length);
        if (emph.DelimiterChar == '~' && emph.DelimiterCount >= 2)
            return new StrikethroughRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '~')
            return new SubscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '^')
            return new SuperscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '+')
            return new InsertedRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '=')
            return new MarkedRun(sb.ToString()) { SourceSpan = span };
        return emph.DelimiterCount >= 2
            ? new StrongRun(sb.ToString()) { SourceSpan = span }
            : new EmphasisRun(sb.ToString()) { SourceSpan = span };
    }

    private InlineRun BuildLink(LinkInline link, int inlineStyleAliasStart)
    {
        if (link.IsImage)
        {
            return BuildImageRun(link, linkUrl: null, linkTitle: null, link.Span.Start, link.Span.Length);
        }

        if (TryGetOnlyImageChild(link, out var imageLink))
        {
            return BuildImageRun(imageLink, link.Url, link.Title, link.Span.Start, link.Span.Length);
        }

        var sb = new System.Text.StringBuilder();
        LinkInlineStyleMapBuilder? inlineStyles = null;
        FlattenContainer(
            link,
            sb,
            ref inlineStyles,
            path: null,
            inlineStyleAliasStart);
        return new LinkRun(
            sb.ToString(),
            link.Url ?? string.Empty,
            link.Title,
            inlineStyles?.Build())
        {
            SourceSpan = new SourceSpan(link.Span.Start, link.Span.Length)
        };
    }

    private InlineImageRun BuildImageRun(
        LinkInline imageLink,
        string? linkUrl,
        string? linkTitle,
        int sourceStart,
        int sourceLength)
    {
        var alt = new System.Text.StringBuilder();
        FlattenContainer(imageLink, alt);
        // Preserve explicitly empty alt text. The semantic layer omits an
        // unlinked empty-alt image instead of inventing an accessible name.
        string altText = alt.ToString();
        SafeHtmlLength? requestedWidth = null;
        SafeHtmlLength? requestedHeight = null;
        if (imageLink is SizedImageLinkInline sizedImage)
        {
            requestedWidth = sizedImage.RequestedWidth;
            requestedHeight = sizedImage.RequestedHeight;
        }

        return TrackInlineImageRun(new InlineImageRun(
            _context,
            altText,
            imageLink.Url ?? string.Empty,
            imageLink.Title,
            linkUrl,
            linkTitle,
            requestedWidth,
            requestedHeight)
        {
            SourceSpan = new SourceSpan(sourceStart, sourceLength)
        });
    }

    private static bool TryGetOnlyImageChild(ContainerInline container, out LinkInline imageLink)
    {
        imageLink = null!;
        int count = 0;
        foreach (var child in container)
        {
            count++;
            if (count > 1)
            {
                imageLink = null!;
                return false;
            }

            if (child is LinkInline { IsImage: true } image)
            {
                imageLink = image;
                continue;
            }

            imageLink = null!;
            return false;
        }

        return imageLink is not null;
    }

    private static bool TryGetOnlyHtmlImageChild(
        ContainerInline container,
        out HtmlInline htmlImage)
    {
        htmlImage = null!;
        int count = 0;
        foreach (Inline child in container)
        {
            if (++count > 1 || child is not HtmlInline html)
            {
                htmlImage = null!;
                return false;
            }

            htmlImage = html;
        }

        return htmlImage is not null;
    }

    private static SourceSpan GetLinkedHtmlImageSourceSpan(
        LinkInline link,
        HtmlInline htmlImage,
        MarkdownLayoutContext context)
    {
        string source = context.SourceMap.SourceText;
        int tagStart = source.IndexOf(
            htmlImage.Tag,
            Math.Clamp(link.Span.Start, 0, source.Length),
            StringComparison.Ordinal);
        if (tagStart >= 0)
        {
            int sourceStart = tagStart > 0 && source[tagStart - 1] == '[' ? tagStart - 1 : tagStart;
            int endExclusive = tagStart + htmlImage.Tag.Length;
            if (endExclusive + 1 < source.Length &&
                source[endExclusive] == ']' &&
                source[endExclusive + 1] == '(')
            {
                int closingParenthesis = FindLinkClosingParenthesis(source, endExclusive + 2);
                if (closingParenthesis >= 0)
                    endExclusive = closingParenthesis + 1;
            }

            return new SourceSpan(sourceStart, endExclusive - sourceStart);
        }

        int start = Math.Max(0, Math.Min(link.Span.Start, htmlImage.Span.Start));
        int inclusiveEnd = Math.Max(link.Span.End, htmlImage.Span.End);
        if (!link.UrlSpan.IsEmpty)
            inclusiveEnd = Math.Max(inclusiveEnd, link.UrlSpan.End);

        inclusiveEnd = Math.Min(inclusiveEnd, source.Length - 1);
        if (inclusiveEnd + 1 < source.Length && source[inclusiveEnd + 1] == ')')
            inclusiveEnd++;

        return new SourceSpan(start, Math.Max(0, inclusiveEnd - start + 1));
    }

    private static int FindLinkClosingParenthesis(string source, int destinationStart)
    {
        int depth = 1;
        bool escaped = false;
        for (int index = destinationStart; index < source.Length; index++)
        {
            char current = source[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    private void FlattenContainer(ContainerInline container, System.Text.StringBuilder sb)
    {
        LinkInlineStyleMapBuilder? inlineStyles = null;
        FlattenContainer(container, sb, ref inlineStyles, path: null, inlineStyleAliasStart: -1);
    }

    private void FlattenContainer(
        ContainerInline container,
        System.Text.StringBuilder sb,
        ref LinkInlineStyleMapBuilder? inlineStyles,
        LinkInlineStylePath? path,
        int inlineStyleAliasStart)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit:
                {
                    // A parser literal that is an exact slice of the original
                    // Markdown can be copied into the final wrapper string
                    // directly. Keep the same strict guard as BuildInline so
                    // decoded/transformed/provider-owned content retains the
                    // legacy StringSlice.ToString() behavior.
                    SourceSpan span = new(lit.Span.Start, lit.Span.Length);
                    string? sourceText = lit.Content.Text;
                    if (sourceText is not null &&
                        ReferenceEquals(sourceText, _context.SourceMap.SourceText) &&
                        span.Length > 0 &&
                        span.Start >= 0 &&
                        span.Start <= sourceText.Length - span.Length &&
                        lit.Content.Start == span.Start &&
                        lit.Content.Length == span.Length)
                    {
                        int start = sb.Length;
                        sb.Append(sourceText, span.Start, span.Length);
                        inlineStyles?.AddRange(start, sb.Length - start, path);
                    }
                    else
                    {
                        int start = sb.Length;
                        sb.Append(lit.Content.ToString());
                        inlineStyles?.AddRange(start, sb.Length - start, path);
                    }
                    break;
                }
                case CodeInline code:
                {
                    int start = sb.Length;
                    sb.Append(code.Content);
                    if (inlineStyles is not null || inlineStyleAliasStart >= 0)
                    {
                        using var attributes = _context.PushMarkdownAttributes(code);
                        LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
                        LinkInlineStylePath codePath = builder.CreatePath(
                            MarkdownElementKeys.CodeInline,
                            _context.CreateStyleAliasSnapshotFrom(inlineStyleAliasStart),
                            path);
                        builder.AddRange(start, sb.Length - start, codePath);
                    }
                    break;
                }
                case AbbreviationInline abbreviation:
                {
                    int start = sb.Length;
                    sb.Append(abbreviation.Abbreviation?.Label ?? string.Empty);
                    if (inlineStyles is not null || inlineStyleAliasStart >= 0)
                    {
                        using var attributes = _context.PushMarkdownAttributes(abbreviation);
                        LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
                        LinkInlineStylePath abbreviationPath = builder.CreatePath(
                            MarkdownElementKeys.Abbreviation,
                            _context.CreateStyleAliasSnapshotFrom(inlineStyleAliasStart),
                            path);
                        builder.AddRange(start, sb.Length - start, abbreviationPath);
                    }
                    break;
                }
                case LineBreakInline lineBreak:
                {
                    int start = sb.Length;
                    sb.Append(lineBreak.IsHard ? '\n' : ' ');
                    inlineStyles?.AddRange(start, sb.Length - start, path);
                    break;
                }
                case EmphasisInline emphasis:
                {
                    if (inlineStyleAliasStart < 0)
                    {
                        FlattenContainer(emphasis, sb, ref inlineStyles, path, inlineStyleAliasStart);
                        break;
                    }

                    using var attributes = _context.PushMarkdownAttributes(emphasis);
                    LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
                    LinkInlineStylePath emphasisPath = builder.CreatePath(
                        GetEmphasisElementKey(emphasis),
                        _context.CreateStyleAliasSnapshotFrom(inlineStyleAliasStart),
                        path);
                    FlattenContainer(emphasis, sb, ref inlineStyles, emphasisPath, inlineStyleAliasStart);
                    break;
                }
                case HtmlEntityInline entity:
                {
                    // "Q &amp; A" in link text or alt text keeps its "&".
                    int start = sb.Length;
                    sb.Append(entity.Transcoded.ToString());
                    inlineStyles?.AddRange(start, sb.Length - start, path);
                    break;
                }
                case AutolinkInline autolink:
                {
                    // A flattened run (alt text, a nested label) shows the URL
                    // text; a clickable autolink takes the AddInlines path.
                    int start = sb.Length;
                    sb.Append(autolink.Url);
                    inlineStyles?.AddRange(start, sb.Length - start, path);
                    break;
                }
                case LinkInline { IsImage: true } image:
                    FlattenContainer(image, sb, ref inlineStyles, path, inlineStyleAliasStart);
                    break;
                case ContainerInline nested:
                    FlattenContainer(nested, sb, ref inlineStyles, path, inlineStyleAliasStart);
                    break;
                default: break;
            }
        }
    }

    private static string ToSuperscript(int n)
    {
        const string digits = "\u2070\u00B9\u00B2\u00B3\u2074\u2075\u2076\u2077\u2078\u2079";
        var sb = new System.Text.StringBuilder();
        foreach (char c in n.ToString())
            sb.Append(c >= '0' && c <= '9' ? digits[c - '0'] : c);
        return sb.ToString();
    }
}
