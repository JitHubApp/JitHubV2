using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Xaml;
using Markdig.Syntax;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds eager and lazy layout snapshots from a parsed Markdown document. The
/// block, container, inline, code and declarative builders it composes share
/// one construction context and tracker.
/// </summary>
internal sealed class LayoutBuilder : ILayoutBlockDispatcher
{
    private readonly MarkdownLayoutContext _context;
    private readonly SharedLayoutMetricsCache? _sharedLayoutMetrics;
    private readonly SharedLayoutMetricsKey? _sharedLayoutMetricsKey;
    private readonly LayoutConstructionTracker _tracker;
    private readonly MaterializedSceneScope _materializedScenes;
    private readonly DeclarativeInlineBuilder _declarativeInlines;
    private readonly DeclarativeBlockBuilder _declarativeBlocks;
    private readonly LayoutBlockBuilder _blocks;

    public LayoutBuilder(
        MarkdownLayoutContext context,
        IMarkdownEmbedFactory? embedFactory = null,
        bool enableDeclarativeHostedElements = false,
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument = null,
        IReadOnlySet<HostedElementFallbackKey>? hostedElementFallbacks = null,
        SharedLayoutMetricsCache? sharedLayoutMetrics = null,
        SharedLayoutMetricsKey? sharedLayoutMetricsKey = null)
    {
        _context = context;
        _sharedLayoutMetrics = sharedLayoutMetrics;
        _sharedLayoutMetricsKey = sharedLayoutMetricsKey;
        IReadOnlySet<HostedElementFallbackKey> fallbacks =
            hostedElementFallbacks ?? new HashSet<HostedElementFallbackKey>();
        _tracker = new LayoutConstructionTracker(context);
        _materializedScenes = new MaterializedSceneScope();
        var flattener = new InlineContainerFlattener(context);
        _declarativeInlines = new DeclarativeInlineBuilder(context, _tracker, _materializedScenes);
        var codeBlocks = new CodeBlockLayoutBuilder(context, _tracker, new CodeBlockSourceProjector(context));
        var inlines = new InlineRunBuilder(context, semanticDocument, _tracker, _declarativeInlines, flattener);
        _declarativeBlocks = new DeclarativeBlockBuilder(
            context, enableDeclarativeHostedElements, _tracker, _declarativeInlines, codeBlocks, _materializedScenes);
        var containers = new LayoutContainerBuilder(
            context, semanticDocument, fallbacks, this, _tracker, _declarativeBlocks);
        _blocks = new LayoutBlockBuilder(
            context, embedFactory, semanticDocument, fallbacks, _tracker, containers,
            _declarativeBlocks, codeBlocks, inlines, flattener);
    }

    /// <summary>Construction context shared by every composed builder.</summary>
    internal MarkdownLayoutContext Context => _context;

    /// <summary>Construction-scope registration shared by every composed builder.</summary>
    internal LayoutConstructionTracker Tracker => _tracker;

    /// <summary>Scene-free identity of the worker result being materialized.</summary>
    internal MaterializedSceneScope MaterializedScenes => _materializedScenes;

    internal DeclarativeBlockBuilder DeclarativeBlocks => _declarativeBlocks;

    internal DeclarativeInlineBuilder DeclarativeInlines => _declarativeInlines;

    /// <summary>
    /// Builds a code child with the same parsing, source mapping, and chunking
    /// behavior as an ordinary layout block when an extension invokes its
    /// child builder outside an active <see cref="LayoutBlockBuilder.BuildBlocks"/> scope.
    /// </summary>
    internal static BlockBox? BuildCodeBlockForExtension(
        CodeBlock block,
        MarkdownLayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(context);
        return new LayoutBuilder(context)._blocks.BuildBlock(block);
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
        MarkdownLayoutConstructionScope ownership = _tracker.BeginBlockBuildOwnership();
        LayoutSnapshot? snapshot = null;
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy = null;
        try
        {
            MarkdownLayoutDiagnostics? diagnostics = ResolveUncapturedDiagnostics();
            var blocks = BuildTopLevelBlocks(document, diagnostics, effectiveCancellationToken);
            long phaseStarted = 0;

            SharedLayoutMetrics? sharedMetrics = AcquireSharedMetrics(blocks.Count);
            if (diagnostics is not null)
                phaseStarted = Stopwatch.GetTimestamp();

            Thickness documentPadding = _context.ThemeSnapshot.DocumentPadding;
            float blockSpacing = (float)_context.ThemeSnapshot.BlockSpacing;
            float y = ArrangeEagerBlocks(
                blocks,
                availableWidth,
                documentPadding,
                blockSpacing,
                sharedMetrics,
                effectiveCancellationToken);

            if (diagnostics is not null)
            {
                diagnostics.RecordMeasureArrange(Stopwatch.GetTimestamp() - phaseStarted);
                phaseStarted = Stopwatch.GetTimestamp();
            }

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
                EnsureEagerDocumentResidencyBand(snapshot, effectiveCancellationToken);
            if (diagnostics is not null)
                diagnostics.RecordSnapshotConstruction(Stopwatch.GetTimestamp() - phaseStarted);
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
            _tracker.EndBlockBuildOwnership(ownership);
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
        MarkdownLayoutConstructionScope ownership = _tracker.BeginBlockBuildOwnership();
        LayoutSnapshot? snapshot = null;
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy = null;
        try
        {
            MarkdownLayoutDiagnostics? diagnostics = ResolveUncapturedDiagnostics();
            var blocks = BuildTopLevelBlocks(document, diagnostics, effectiveCancellationToken);
            long phaseStarted = 0;

            effectiveCancellationToken.ThrowIfCancellationRequested();
            SharedLayoutMetrics? sharedMetrics = AcquireSharedMetrics(blocks.Count);

            if (diagnostics is not null)
                phaseStarted = Stopwatch.GetTimestamp();
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
            if (diagnostics is not null)
            {
                diagnostics.RecordSnapshotConstruction(Stopwatch.GetTimestamp() - phaseStarted);
                phaseStarted = Stopwatch.GetTimestamp();
            }
            snapshot.EnableLazyLayout(
                availableWidth,
                viewportTop,
                viewportHeight,
                overscan,
                effectiveCancellationToken,
                initialVisibleVectorBand);
            if (diagnostics is not null)
                diagnostics.RecordMeasureArrange(Stopwatch.GetTimestamp() - phaseStarted);
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
            _tracker.EndBlockBuildOwnership(ownership);
        }
    }

    // Diagnostics already captured by an earlier pass are not recorded again.
    private MarkdownLayoutDiagnostics? ResolveUncapturedDiagnostics()
    {
        MarkdownLayoutDiagnostics? diagnostics = _context.LayoutDiagnostics;
        return diagnostics?.IsCaptured == true ? null : diagnostics;
    }

    private List<BlockBox> BuildTopLevelBlocks(
        MarkdownDocument document,
        MarkdownLayoutDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        long phaseStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        var blocks = _blocks.BuildBlocks(document, cancellationToken);
        if (diagnostics is not null)
        {
            diagnostics.RecordBlockConstruction(Stopwatch.GetTimestamp() - phaseStarted);
            diagnostics.SetTopLevelBlockCount(blocks.Count);
        }

        return blocks;
    }

    // Measures and arranges every top-level block in document order and
    // returns the document height including padding.
    private static float ArrangeEagerBlocks(
        List<BlockBox> blocks,
        float availableWidth,
        Thickness documentPadding,
        float blockSpacing,
        SharedLayoutMetrics? sharedMetrics,
        CancellationToken cancellationToken)
    {
        float contentX = (float)documentPadding.Left;
        float contentWidth = (float)Math.Max(
            1,
            availableWidth - documentPadding.Left - documentPadding.Right);
        float y = (float)documentPadding.Top;
        for (int blockOrdinal = 0; blockOrdinal < blocks.Count; blockOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();
        y += (float)documentPadding.Bottom;
        return y;
    }

    // The explicit eager API keeps its all-document measure/arrange
    // contract. When the caller has opted into native admission,
    // attach the policy and make the whole eagerly built document
    // its initial residency band so any capacity fallback remains
    // owned and can later be narrowed/refilled by a viewport host.
    private static void EnsureEagerDocumentResidencyBand(
        LayoutSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        LazyLayoutBand eagerDocumentBand = LazyLayoutBand.FromViewport(
            0, snapshot.Size.Height, overscan: 0);
        snapshot.EnsureMeasuredBand(
            eagerDocumentBand, eagerDocumentBand, cancellationToken);
    }

    private SharedLayoutMetrics? AcquireSharedMetrics(int topLevelBlockCount)
    {
        if (_sharedLayoutMetricsKey is null)
            return null;

        // Without an owning cache the build still records heights, but in a
        // private buffer no other view can reuse.
        return _sharedLayoutMetrics?.GetOrCreate(_sharedLayoutMetricsKey, topLevelBlockCount) ??
            new SharedLayoutMetrics(topLevelBlockCount, _sharedLayoutMetricsKey.EstimatedKeyWeightBytes);
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

    BlockBox? ILayoutBlockDispatcher.BuildBlock(Block block) => _blocks.BuildBlock(block);
}
