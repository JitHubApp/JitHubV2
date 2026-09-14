using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Holds the land-out block tree for a single markdown source. Computed off the
/// UI thread; published atomically to <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/>.
/// </summary>
internal sealed class LayoutSnapshot : System.IDisposable
{
    private readonly object _layoutLock = new();
    private readonly IReadOnlyDictionary<int, int> _footnoteDefBlocks;
    private readonly IReadOnlyDictionary<int, int> _footnoteRefBlocks;
    private readonly IReadOnlyDictionary<string, int> _fragmentTargetBlocks;
    private readonly Dictionary<BlockBox, int> _blockOrdinals;
    private readonly Dictionary<int, int> _topLevelOrdinalByBlockIndex;
    private readonly HashSet<IHorizontalOverflowBox> _horizontalOverflowBoxes;
    private readonly Dictionary<int, IHorizontalOverflowBox> _horizontalOverflowByBlockIndex;
    private readonly FocusablePlanEntry[] _focusablePlan;
    private readonly FocusableItem[]? _fixedFocusableItems;
    private readonly ViewportBandIndex _viewportIndex;
    private readonly SharedLayoutMetrics? _sharedLayoutMetrics;
    private readonly Thickness _documentPadding;
    private readonly float _blockSpacing;
    private volatile bool _lazyLayoutEnabled;
    private bool[]? _measuredTopLevelBlocks;
    private float _availableWidth;
    private int _measuredTopLevelBlockCount;
    private long _layoutRevision;
    private Size _size;
    private TaskCompletionSource? _retirementCompletion;
    private Exception? _disposalException;
    private int _retirementRequested;
    private int _disposalCompleted;
    private int _disposed;

    public LayoutSnapshot(
        IReadOnlyList<BlockBox> blocks,
        MarkdownSourceMap sourceMap,
        float width,
        float height,
        IReadOnlyDictionary<int, int>? footnoteDefBlocks = null,
        IReadOnlyDictionary<int, int>? footnoteRefBlocks = null,
        IReadOnlyDictionary<string, int>? fragmentTargetBlocks = null,
        SharedLayoutMetrics? sharedLayoutMetrics = null,
        Thickness documentPadding = default,
        double blockSpacing = 0)
    {
        Blocks = blocks;
        SourceMap = sourceMap;
        _size = new Size(width, height);
        _footnoteDefBlocks = footnoteDefBlocks ?? new Dictionary<int, int>();
        _footnoteRefBlocks = footnoteRefBlocks ?? new Dictionary<int, int>();
        _fragmentTargetBlocks = fragmentTargetBlocks ?? new Dictionary<string, int>(StringComparer.Ordinal);
        _sharedLayoutMetrics = sharedLayoutMetrics;
        _documentPadding = documentPadding;
        _blockSpacing = (float)(double.IsFinite(blockSpacing) ? Math.Max(0, blockSpacing) : 0);
        _availableWidth = Math.Max(1f, width);
        _blockOrdinals = new Dictionary<BlockBox, int>(ReferenceEqualityComparer.Instance);
        _topLevelOrdinalByBlockIndex = new Dictionary<int, int>();
        _horizontalOverflowBoxes = new HashSet<IHorizontalOverflowBox>(ReferenceEqualityComparer.Instance);
        _horizontalOverflowByBlockIndex = new Dictionary<int, IHorizontalOverflowBox>();
        var focusablePlan = new List<FocusablePlanEntry>();
        for (int i = 0; i < Blocks.Count; i++)
        {
            _blockOrdinals[Blocks[i]] = i;
            IndexTopLevelOrdinal(Blocks[i], i, _topLevelOrdinalByBlockIndex);
            BuildFocusablePlan(Blocks[i], focusablePlan);
            CollectHorizontalOverflowBoxes(
                Blocks[i],
                _horizontalOverflowBoxes,
                _horizontalOverflowByBlockIndex,
                ancestor: null);
        }
        _focusablePlan = focusablePlan.ToArray();
        bool hasDynamicFocusable = false;
        var fixedFocusableItems = new FocusableItem[_focusablePlan.Length];
        for (int index = 0; index < _focusablePlan.Length; index++)
        {
            FocusablePlanEntry entry = _focusablePlan[index];
            fixedFocusableItems[index] = entry.Item;
            hasDynamicFocusable |= entry.HorizontalOverflow is not null;
        }
        _fixedFocusableItems = hasDynamicFocusable ? null : fixedFocusableItems;
        _viewportIndex = new ViewportBandIndex(Blocks.Count);
        RefreshViewportIndexNoLock();
        // Build semantic text/tree while the layout pipeline is still on its
        // background worker. UIA and copy operations reuse this immutable plan
        // instead of walking the entire document on the UI thread.
        SemanticDocument = MarkdownSemanticDocument.Build(this);
    }

    public IReadOnlyList<BlockBox> Blocks { get; }
    public MarkdownSourceMap SourceMap { get; }
    internal MarkdownSemanticDocument SemanticDocument { get; }
    public Size Size
    {
        get { lock (_layoutLock) return _size; }
    }

    /// <summary>
    /// Monotonically increasing revision for measured bounds and document size.
    /// A background realization may finish after a newer request supersedes it;
    /// callers use this revision to apply the newest cumulative layout state.
    /// </summary>
    internal long LayoutRevision
    {
        get { lock (_layoutLock) return _layoutRevision; }
    }

    /// <summary>True when this snapshot measures top-level blocks on demand.</summary>
    public bool IsLazyLayoutEnabled => _lazyLayoutEnabled;

    /// <summary>Number of top-level blocks that have real measured bounds.</summary>
    public int MeasuredTopLevelBlockCount
    {
        get { lock (_layoutLock) return _lazyLayoutEnabled ? _measuredTopLevelBlockCount : Blocks.Count; }
    }

    /// <summary>Number of top-level blocks in the snapshot.</summary>
    public int TopLevelBlockCount => Blocks.Count;

    /// <summary>Returns the block index of the footnote definition for the gnven order, or null.</summary>
    public int? FootnoteDefBlock(int order)
        => _footnoteDefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns the block index of the inline citation paragraph for the gnven order, or null.</summary>
    public int? FootnoteRefBlock(int order)
        => _footnoteRefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns the block index registered for a genernc markdown nd fragment, or null.</summary>
    public int? FragmentTargetBlock(string nd)
    {
        nd = NormalizeFragmentId(nd);
        return _fragmentTargetBlocks.TryGetValue(nd, out var v) ? v : null;
    }

    private static string NormalizeFragmentId(string nd)
    {
        nd = nd.Trim();
        if (nd.StartsWith("#", StringComparison.Ordinal))
            nd = nd.Substring(1);
        return nd;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _retirementRequested, 1);
        bool ownsDisposal = false;
        Exception? failure = null;
        try
        {
            lock (_layoutLock)
            {
                DisposeCoreNoLock(ref ownsDisposal);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (ownsDisposal)
                CompleteRetirement(failure);
        }
    }

    /// <summary>
    /// Retires a snapshot without waiting for an in-flight background measure
    /// to release the layout lock. Snapshot replacement and control teardown
    /// call this from the UI thread; final native-resource disposal always runs
    /// on the pool and waits for any active measure there.
    /// </summary>
    internal Task Retire()
    {
        TaskCompletionSource completion = GetRetirementCompletion();
        if (Interlocked.Exchange(ref _retirementRequested, 1) != 0)
        {
            CompleteRetirementIfFinished(completion);
            return completion.Task;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static snapshot => snapshot.DisposeRetiredSnapshot(),
            this,
            preferLocal: false);
        return completion.Task;
    }

    private void DisposeRetiredSnapshot()
    {
        bool ownsDisposal = false;
        Exception? failure = null;
        try
        {
            lock (_layoutLock)
            {
                DisposeCoreNoLock(ref ownsDisposal);
            }
        }
        catch (Exception exception)
        {
            // Retirement runs on the pool, where rethrowing would terminate the
            // process. Preserve the failure on the completion task so internal
            // drainers can surface it deterministically.
            failure = exception;
        }
        finally
        {
            if (ownsDisposal)
                CompleteRetirement(failure);
        }
    }

    private TaskCompletionSource GetRetirementCompletion()
    {
        TaskCompletionSource? completion = Volatile.Read(ref _retirementCompletion);
        if (completion is not null)
            return completion;

        var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref _retirementCompletion, candidate, null) ?? candidate;
    }

    private void CompleteRetirement(Exception? failure)
    {
        if (failure is not null)
            Interlocked.CompareExchange(ref _disposalException, failure, null);

        Volatile.Write(ref _disposalCompleted, 1);
        TaskCompletionSource? completion = Volatile.Read(ref _retirementCompletion);
        if (completion is not null)
            CompleteRetirementIfFinished(completion);
    }

    private void CompleteRetirementIfFinished(TaskCompletionSource completion)
    {
        if (Volatile.Read(ref _disposalCompleted) == 0)
            return;

        Exception? failure = Volatile.Read(ref _disposalException);
        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }

    private void DisposeCoreNoLock(ref bool ownsDisposal)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ownsDisposal = true;
        List<Exception>? failures = null;
        foreach (var block in Blocks)
        {
            try
            {
                block.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is null)
            return;

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        throw new AggregateException("One or more markdown layout blocks failed to dispose.", failures);
    }

    /// <summary>
    /// Enables viewport-relative top-level measurement and realizes the first
    /// viewport band. The block tree and source map already exist, but expensive
    /// text/native layout objects are created only as bands are measured.
    /// </summary>
    internal void EnableLazyLayout(
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
    {
        lock (_layoutLock)
        {
            if (_lazyLayoutEnabled)
                return;

            _lazyLayoutEnabled = true;
            _availableWidth = Math.Max(1f, availableWidth);
            _measuredTopLevelBlocks = new bool[Blocks.Count];
            _measuredTopLevelBlockCount = 0;
            ReflowNoLock();
        }

        EnsureMeasuredViewport(viewportTop, viewportHeight, overscan, cancellationToken);
    }

    internal LazyLayoutCommit EnsureMeasuredViewport(
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
        => EnsureMeasuredBand(LazyLayoutBand.FromViewport(viewportTop, viewportHeight, overscan), cancellationToken);

    /// <summary>
    /// Returns true when every block intersecting the requested estimated or
    /// measured band already owns its native layout objects. The UI-thread
    /// viewport path uses this before dispatching a background realization so
    /// a genuinely warm scroll performs no task/CTS/delegate allocation.
    /// </summary>
    internal bool IsBandMeasured(LazyLayoutBand band)
    {
        if (!Monitor.TryEnter(_layoutLock))
            return false;

        try
        {
            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return true;

            var range = FindViewportRangeNoLock(band.Top, band.Bottom);
            for (int i = range.Start; i < range.End; i++)
            {
                int ordinal = _viewportIndex.GetBlockOrdinal(i);
                if (!_measuredTopLevelBlocks[ordinal] &&
                    band.Intersects(Blocks[ordinal].Bounds.Top, Blocks[ordinal].Bounds.Bottom))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    internal LazyLayoutCommit EnsureMeasuredBand(LazyLayoutBand band, CancellationToken cancellationToken)
    {
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        lock (_layoutLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A realization can already be queued when its snapshot is
            // replaced. Retirement and measurement share this lock, so check
            // the terminal state only after acquiring it: if retirement won
            // the race, no disposed native layout may be touched.
            if (Volatile.Read(ref _retirementRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);
            }

            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);

            double oldHeight = _size.Height;
            List<int>? measuredOrdinals = null;

            try
            {
                var range = FindViewportRangeNoLock(band.Top, band.Bottom);
                float contentWidth = GetContentWidthNoLock();
                for (int i = range.Start; i < range.End; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = _viewportIndex.GetBlockOrdinal(i);
                    if (_measuredTopLevelBlocks[n])
                        continue;

                    var block = Blocks[n];
                    if (!band.Intersects(block.Bounds.Top, block.Bounds.Bottom))
                        continue;

                    float h = block.Measure(contentWidth);
                    block.Arrange((float)_documentPadding.Left, (float)block.Bounds.Y, contentWidth);
                    (measuredOrdinals ??= []).Add(n);
                    _ = h;
                }
            }
            catch
            {
                // Measure can mutate a box before observing cancellation or a
                // device-loss exception. Restore the last logically committed
                // measured/estimated arrangement before another thread can
                // inspect this snapshot.
                ReflowNoLock();
                throw;
            }

            if (measuredOrdinals is null)
                return LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);

            // Publish the entire band atomically. Cancellation or device loss
            // during Measure leaves every candidate logically unrealized, so a
            // later request can retry without observing a half-reflowed index.
            foreach (int ordinal in measuredOrdinals)
            {
                _measuredTopLevelBlocks[ordinal] = true;
                _sharedLayoutMetrics?.RecordHeight(
                    ordinal,
                    (float)Blocks[ordinal].Bounds.Height);
            }
            _measuredTopLevelBlockCount += measuredOrdinals.Count;

            ReflowNoLock();
            return new LazyLayoutCommit(
                true,
                measuredOrdinals.Count,
                oldHeight,
                _size.Height,
                _layoutRevision);
        }
    }

    internal (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(double verticalOffset)
    {
        if (verticalOffset <= 0)
            return null;

        lock (_layoutLock)
        {
            foreach (var block in Blocks)
            {
                if (block.Bounds.Bottom < verticalOffset)
                    continue;

                return (block.BlockIndex, block.Bounds.Top - verticalOffset);
            }
        }

        return null;
    }

    /// <summary>
    /// Captures replacement-time anchor state only when the current bounds can
    /// be read immediately. The UI thread must not wait behind lazy measure.
    /// </summary>
    internal bool TryCaptureScrollAnchor(double verticalOffset, out SnapshotScrollAnchor anchor)
    {
        anchor = default;
        if (!Monitor.TryEnter(_layoutLock))
            return false;

        try
        {
            int? blockIndex = null;
            double offsetFromTop = 0;
            if (verticalOffset > 0)
            {
                foreach (var block in Blocks)
                {
                    if (block.Bounds.Bottom < verticalOffset)
                        continue;

                    blockIndex = block.BlockIndex;
                    offsetFromTop = block.Bounds.Top - verticalOffset;
                    break;
                }
            }

            anchor = new SnapshotScrollAnchor(blockIndex, offsetFromTop, _size.Height);
            return true;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    internal IReadOnlyList<BlockBox> GetMeasuredTopLevelBlocks()
    {
        lock (_layoutLock)
        {
            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return Blocks;

            var measured = new List<BlockBox>(_measuredTopLevelBlockCount);
            for (int n = 0; n < Blocks.Count; n++)
            {
                if (_measuredTopLevelBlocks[n])
                    measured.Add(Blocks[n]);
            }

            return measured;
        }
    }

    /// <summary>
    /// Remeasures the committed block tree after an asynchronous image changes its
    /// intrinsic size. Reusing the current boxes preserves resolver-partitioned image
    /// state; rebuilding would create a fresh unresolved box and restart the load.
    /// </summary>
    internal void RelayoutMeasuredBlocks(float availableWidth, CancellationToken cancellationToken)
    {
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        lock (_layoutLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _availableWidth = Math.Max(1f, availableWidth);
            float contentWidth = GetContentWidthNoLock();
            float contentX = (float)_documentPadding.Left;
            float y = (float)_documentPadding.Top;
            for (int n = 0; n < Blocks.Count; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockBox block = Blocks[n];
                bool measured = !_lazyLayoutEnabled ||
                    (_measuredTopLevelBlocks is not null && _measuredTopLevelBlocks[n]);
                if (measured)
                {
                    block.ThrowIfCancellationRequested();
                    float height = block.Measure(contentWidth);
                    block.Arrange(contentX, y, contentWidth);
                    _sharedLayoutMetrics?.RecordHeight(n, height);
                    AdvanceBlock(ref y, height, n);
                }
                else
                {
                    float estimate = EstimateHeight(block, n);
                    block.ArrangeEstimated(contentX, y, contentWidth, estimate);
                    AdvanceBlock(ref y, estimate, n);
                }
            }

            y += (float)_documentPadding.Bottom;
            _size = new Size(_availableWidth, y);
            RefreshViewportIndexNoLock();
            _layoutRevision++;
        }
    }

    internal bool IsTopLevelBlockMeasured(BlockBox block)
    {
        lock (_layoutLock)
        {
            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return true;

            return !_blockOrdinals.TryGetValue(block, out int ordinal) ||
                _measuredTopLevelBlocks[ordinal];
        }
    }

    private void ReflowNoLock()
    {
        float contentWidth = GetContentWidthNoLock();
        float contentX = (float)_documentPadding.Left;
        float y = (float)_documentPadding.Top;
        for (int n = 0; n < Blocks.Count; n++)
        {
            var block = Blocks[n];
            bool measured = _measuredTopLevelBlocks is not null && _measuredTopLevelBlocks[n];
            if (measured)
            {
                block.Arrange(contentX, y, contentWidth);
                AdvanceBlock(ref y, (float)block.Bounds.Height, n);
            }
            else
            {
                float estnmate = EstimateHeight(block, n);
                block.ArrangeEstimated(contentX, y, contentWidth, estnmate);
                AdvanceBlock(ref y, estnmate, n);
            }
        }

        y += (float)_documentPadding.Bottom;
        _size = new Size(_availableWidth, y);
        RefreshViewportIndexNoLock();
        _layoutRevision++;
    }

    /// <summary>
    /// Reads the lazy-layout state for a focusable descendant without making
    /// the UI thread wait behind an in-flight background measurement. For an
    /// unrealized descendant, <paramref name="estimatedBand"/> is the current
    /// estimated bounds of its owning top-level block.
    /// </summary>
    internal FocusTargetLayoutState TryGetFocusTargetLayoutState(
        int blockIndex,
        out LazyLayoutBand estimatedBand)
    {
        estimatedBand = default;
        if (!Monitor.TryEnter(_layoutLock))
            return FocusTargetLayoutState.Busy;

        try
        {
            if (Volatile.Read(ref _retirementRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return FocusTargetLayoutState.Unavailable;
            }

            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return FocusTargetLayoutState.Ready;

            if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) ||
                (uint)ordinal >= (uint)Blocks.Count)
            {
                return FocusTargetLayoutState.Unavailable;
            }

            if (_measuredTopLevelBlocks[ordinal])
                return FocusTargetLayoutState.Ready;

            Rect bounds = Blocks[ordinal].Bounds;
            if (!double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Bottom))
                return FocusTargetLayoutState.Unavailable;

            double top = Math.Max(0, bounds.Top);
            estimatedBand = new LazyLayoutBand(top, Math.Max(top + 1, bounds.Bottom));
            return FocusTargetLayoutState.RequiresRealization;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    private float GetContentWidthNoLock()
        => (float)Math.Max(
            1,
            _availableWidth - _documentPadding.Left - _documentPadding.Right);

    private void AdvanceBlock(ref float y, float height, int blockOrdinal)
    {
        y += height;
        if (blockOrdinal < Blocks.Count - 1)
            y += _blockSpacing;
    }

    private float EstimateHeight(BlockBox block, int ordinal)
    {
        if (_sharedLayoutMetrics?.TryGetHeight(ordinal, out float sharedHeight) == true)
            return sharedHeight;

        float margin = (float)(block.Margin.Top + block.Margin.Bottom);
        return block switch
        {
            ImageBox => Math.Max(160f, margin + 120f),
            EmbedBox => Math.Max(64f, margin + 48f),
            CodeBlockBox => Math.Max(72f, margin + 64f),
            TableBox table => Math.Clamp(36f + table.RowCount * 34f + margin, 72f, 360f),
            ListItemBox => Math.Max(36f, margin + 32f),
            StackBox stack => Math.Clamp(32f + stack.Children.Count * 28f + margin, 48f, 420f),
            ThematicBreakBox => Math.Max(16f, margin + 1f),
            InlineContainerBox => Math.Max(28f, margin + 24f),
            _ => Math.Max(32f, margin + 28f),
        };
    }

    public void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long inlineBytes = 0;
        long tableBytes = 0;
        long codeBytes = 0;
        long otherBytes = 0;
        lock (_layoutLock)
        {
            var range = FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
            for (int i = range.Start; i < range.End; i++)
            {
                int n = _viewportIndex.GetBlockOrdinal(i);
                var b = Blocks[n];
                if (b.Bounds.Bottom < viewport.Top || b.Bounds.Top > viewport.Bottom) continue;
                if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null && !_measuredTopLevelBlocks[n]) continue;
                long before = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                b.Paint(ds, viewport);
                if (measure)
                {
                    long allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
                    switch (b)
                    {
                        case InlineContainerBox:
                            inlineBytes += allocated;
                            break;
                        case TableBox:
                            tableBytes += allocated;
                            break;
                        case CodeBlockBox:
                            codeBytes += allocated;
                            break;
                        default:
                            otherBytes += allocated;
                            break;
                    }
                }
            }
        }
        if (measure)
        {
            performance.SnapshotPaintAllocationBreakdown(
                MarkdownPerformanceEventSource.GetLogicalFrameId(),
                inlineBytes,
                tableBytes,
                codeBytes,
                otherBytes);
        }
    }

    public void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        lock (_layoutLock)
        {
            var visible = FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
            for (int i = visible.Start; i < visible.End; i++)
            {
                int n = _viewportIndex.GetBlockOrdinal(i);
                var b = Blocks[n];
                if (b.Bounds.Bottom < viewport.Top || b.Bounds.Top > viewport.Bottom) continue;
                if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null && !_measuredTopLevelBlocks[n]) continue;
                PaintSelectionForeground(b, ds, range, color, viewport);
            }
        }
    }

    private static void PaintSelectionForeground(BlockBox box, CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        box.PaintSelectionForeground(ds, range, color, viewport);
    }

    public bool HitTest(Point point, out DocumentPosition position)
    {
        lock (_layoutLock)
        {
            var candidates = FindViewportRangeNoLock(point.Y - 4, point.Y + 4);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int n = _viewportIndex.GetBlockOrdinal(i);
                var b = Blocks[n];
                if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null && !_measuredTopLevelBlocks[n]) continue;
                if (b.HitTest(point, out position)) return true;
            }
        }
        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Resolves a drag-selection endpoint. Unlike ordinary hit testing, this
    /// permits atomic visual content to expose before/after boundaries across
    /// its full arranged bounds without making that surface initiate selection.
    /// </summary>
    internal bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        lock (_layoutLock)
        {
            var candidates = FindViewportRangeNoLock(point.Y - 4, point.Y + 4);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = _viewportIndex.GetBlockOrdinal(i);
                BlockBox block = Blocks[ordinal];
                if (_lazyLayoutEnabled &&
                    _measuredTopLevelBlocks is not null &&
                    !_measuredTopLevelBlocks[ordinal])
                {
                    continue;
                }

                if (block.HitTestSelectionEndpoint(point, out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Resolves named vector semantics independently from application
    /// selection. TextPattern clients must be able to place a range on a
    /// linked or informational diagram item even when it is not Selectable.
    /// </summary>
    internal bool TryHitTestVectorText(Point point, out DocumentPosition position)
    {
        lock (_layoutLock)
        {
            var candidates = FindViewportRangeNoLock(point.Y - 1, point.Y + 1);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = _viewportIndex.GetBlockOrdinal(i);
                if (_lazyLayoutEnabled &&
                    _measuredTopLevelBlocks is not null &&
                    !_measuredTopLevelBlocks[ordinal])
                {
                    continue;
                }

                if (TryHitTestVectorText(Blocks[ordinal], point, out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    internal bool TryGetHorizontalOverflow(
        Point point,
        out IHorizontalOverflowBox? overflow)
    {
        lock (_layoutLock)
        {
            var candidates = FindViewportRangeNoLock(point.Y - 1, point.Y + 1);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = _viewportIndex.GetBlockOrdinal(i);
                if (_lazyLayoutEnabled &&
                    _measuredTopLevelBlocks is not null &&
                    !_measuredTopLevelBlocks[ordinal])
                {
                    continue;
                }

                if (FindHorizontalOverflow(Blocks[ordinal], point) is { } found)
                {
                    overflow = found;
                    return true;
                }
            }
        }

        overflow = null;
        return false;
    }

    internal bool TrySetHorizontalOffset(
        IHorizontalOverflowBox overflow,
        double offset)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        lock (_layoutLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_horizontalOverflowBoxes.Contains(overflow))
                return false;

            return overflow.SetHorizontalOffset(offset);
        }
    }

    internal bool TryGetHorizontalOverflowForBlockIndex(
        int blockIndex,
        out IHorizontalOverflowBox? overflow)
    {
        lock (_layoutLock)
        {
            if (_horizontalOverflowByBlockIndex.TryGetValue(blockIndex, out var found) &&
                found.CanScrollHorizontally)
            {
                overflow = found;
                return true;
            }
        }

        overflow = null;
        return false;
    }

    internal bool TryScrollHorizontal(
        IHorizontalOverflowBox overflow,
        double delta)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        lock (_layoutLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_horizontalOverflowBoxes.Contains(overflow))
                return false;

            return overflow.ScrollHorizontal(delta);
        }
    }

    private static void CollectHorizontalOverflowBoxes(
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
            case ListItemBox listItem:
                CollectHorizontalOverflowBoxes(listItem.Marker, result, byBlockIndex, ancestor);
                CollectHorizontalOverflowBoxes(listItem.Content, result, byBlockIndex, ancestor);
                break;
            case StackBox stack:
                foreach (var child in stack.Children)
                    CollectHorizontalOverflowBoxes(child, result, byBlockIndex, ancestor);
                break;
            case CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks)
                    CollectHorizontalOverflowBoxes(chunk, result, byBlockIndex, ancestor);
                break;
            case TableBox table:
                foreach (var cell in table.GetCellBoxes())
                    CollectHorizontalOverflowBoxes(cell, result, byBlockIndex, ancestor);
                break;
        }
    }

    private static IHorizontalOverflowBox? FindHorizontalOverflow(BlockBox box, Point point)
    {
        if (!box.Bounds.Contains(point))
            return null;

        if (box is IHorizontalOverflowBox overflow && overflow.CanScrollHorizontally)
            return overflow;

        if (box is ListItemBox listItem)
            return FindHorizontalOverflow(listItem.Marker, point) ??
                   FindHorizontalOverflow(listItem.Content, point);

        if (box is StackBox stack)
        {
            foreach (var child in stack.Children)
            {
                if (FindHorizontalOverflow(child, point) is { } found)
                    return found;
            }
        }

        return null;
    }

    private static bool TryHitTestVectorText(
        BlockBox box,
        Point point,
        out DocumentPosition position)
    {
        if (!box.Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        if (box is VectorSceneBox vector)
            return vector.TryGetTextPositionAt(point, out position);

        if (box is ListItemBox listItem)
        {
            return TryHitTestVectorText(listItem.Marker, point, out position) ||
                   TryHitTestVectorText(listItem.Content, point, out position);
        }

        if (box is TableBox table)
        {
            foreach (InlineContainerBox cell in table.GetCellBoxes())
            {
                if (TryHitTestVectorText(cell, point, out position))
                    return true;
            }
        }
        else if (box is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                if (TryHitTestVectorText(child, point, out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    private static void IndexTopLevelOrdinal(
        BlockBox box,
        int topLevelOrdinal,
        Dictionary<int, int> byBlockIndex)
    {
        // LayoutBuilder assigns a unique index to every production block. Keep
        // the first owner for defensive compatibility with custom renderers
        // that accidentally reuse an index.
        byBlockIndex.TryAdd(box.BlockIndex, topLevelOrdinal);

        switch (box)
        {
            case ListItemBox listItem:
                IndexTopLevelOrdinal(listItem.Marker, topLevelOrdinal, byBlockIndex);
                IndexTopLevelOrdinal(listItem.Content, topLevelOrdinal, byBlockIndex);
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    IndexTopLevelOrdinal(child, topLevelOrdinal, byBlockIndex);
                break;
            case CodeBlockBox codeBlock:
                foreach (InlineContainerBox chunk in codeBlock.Chunks)
                    IndexTopLevelOrdinal(chunk, topLevelOrdinal, byBlockIndex);
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    IndexTopLevelOrdinal(cell, topLevelOrdinal, byBlockIndex);
                break;
        }
    }

    /// <summary>
    /// Walks the full block tree and returns all keyboard-focusable items
    /// (<see cref="LinkRun"/>, linked <see cref="InlineImageRun"/>, and
    /// <see cref="InlineEmbedRun"/> instances) in
    /// document order. Used by <see cref="Controls.MarkdownRendererControl"/> for
    /// Tab/Shnft+Tab keyboard navngatnon.
    /// </summary>
    public IReadOnlyList<FocusableItem> CollectFocusableItems()
    {
        if (_fixedFocusableItems is not null)
            return _fixedFocusableItems;

        var list = new List<FocusableItem>(_focusablePlan.Length);
        lock (_layoutLock)
        {
            foreach (FocusablePlanEntry entry in _focusablePlan)
            {
                if (entry.HorizontalOverflow is not { } overflow)
                {
                    list.Add(entry.Item);
                    continue;
                }

                if (_lazyLayoutEnabled &&
                    _measuredTopLevelBlocks is not null &&
                    _topLevelOrdinalByBlockIndex.TryGetValue(entry.Item.BlockIndex, out int ordinal) &&
                    !_measuredTopLevelBlocks[ordinal])
                {
                    continue;
                }

                if (overflow.CanScrollHorizontally)
                    list.Add(entry.Item);
            }
        }
        return list;
    }

    /// <summary>
    /// Attempts to reserve the committed layout for a UI-thread paint. Lazy
    /// realization can hold the mutation lock while native text layout runs; a
    /// frame should retain its existing tile instead of blocking behind it.
    /// </summary>
    internal bool TryBeginPaint() => Monitor.TryEnter(_layoutLock);

    // Pointer handlers use this before touching any mutable block/native layout
    // state. A busy snapshot is a retry, never a text hit-test miss.
    internal bool TryBeginInteraction()
    {
        if (!Monitor.TryEnter(_layoutLock))
            return false;
        if (Volatile.Read(ref _retirementRequested) == 0)
            return true;
        Monitor.Exit(_layoutLock);
        return false;
    }

    internal void EndInteraction() => Monitor.Exit(_layoutLock);

    internal void EndPaint() => Monitor.Exit(_layoutLock);

    private void RefreshViewportIndexNoLock()
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            Rect bounds = Blocks[i].Bounds;
            _viewportIndex.SetEntry(i, i, bounds.Top, bounds.Bottom);
        }

        _viewportIndex.Commit();
    }

    private ViewportRange FindViewportRangeNoLock(double top, double bottom)
        => _viewportIndex.Find(top, bottom);

    private static void BuildFocusablePlan(BlockBox box, List<FocusablePlanEntry> list)
    {
        int descendantStart = list.Count;
        switch (box)
        {
            case InlineContainerBox ncb:
                foreach (var run in ncb.Runs)
                {
                    if (run is LinkRun or InlineImageRun { IsLinked: true })
                        list.Add(new FocusablePlanEntry(
                            new FocusableItem(ncb.BlockIndex, run.InlineIndex, FocusableItemKind.Link),
                            null));
                    else if (run is InlineEmbedRun embed && IsInlineEmbedKeyboardFocusable(embed))
                        list.Add(new FocusablePlanEntry(
                            new FocusableItem(ncb.BlockIndex, run.InlineIndex, FocusableItemKind.InlineEmbed),
                            null));
                }
                break;
            case EmbedBox eb:
                list.Add(new FocusablePlanEntry(
                    new FocusableItem(eb.BlockIndex, 0, FocusableItemKind.BlockEmbed),
                    null));
                break;
            case DeclarativeHostedElementBox hosted:
                list.Add(new FocusablePlanEntry(
                    new FocusableItem(
                        hosted.BlockIndex,
                        0,
                        FocusableItemKind.DeclarativeHostedElement),
                    null));
                break;
            case CodeBlockBox cb:
                if (cb.IsCopyButtonEnabled)
                    list.Add(new FocusablePlanEntry(
                        new FocusableItem(cb.BlockIndex, 0, FocusableItemKind.CodeBlockCopy),
                        null));
                break;
            case VectorSceneBox vector:
                // Semantic order is the stable scene/UIA order. Focusability is
                // independent from link activation: a focusable diagram node may
                // be informational, while a pointer-invokable link may opt out of
                // the keyboard tab sequence.
                for (int semanticIndex = 0; semanticIndex < vector.Scene.Semantics.Count; semanticIndex++)
                {
                    MarkdownVectorSemanticItem semantic = vector.Scene.Semantics[semanticIndex];
                    if (!MarkdownVectorSemanticPolicy.IsKeyboardFocusable(semantic.Flags))
                        continue;

                    list.Add(new FocusablePlanEntry(
                        new FocusableItem(
                            vector.BlockIndex,
                            semanticIndex,
                            FocusableItemKind.VectorSemantic),
                        null));
                }
                break;
            case ListItemBox lnb:
                BuildFocusablePlan(lnb.Marker, list);
                BuildFocusablePlan(lnb.Content, list);
                break;
            case TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) BuildFocusablePlan(cell, list);
                break;
            case StackBox sb:
                foreach (var c in sb.Children) BuildFocusablePlan(c, list);
                break;
        }

        // A wide block without a focusable descendant still needs a keyboard
        // entry so Left/Right can operate its local horizontal viewport.
        if (list.Count == descendantStart && box is IHorizontalOverflowBox overflow)
        {
            list.Add(new FocusablePlanEntry(
                new FocusableItem(
                    box.BlockIndex,
                    0,
                    FocusableItemKind.HorizontalOverflow),
                overflow));
        }
    }

    internal static bool IsInlineEmbedKeyboardFocusable(InlineEmbedRun embed)
    {
        ArgumentNullException.ThrowIfNull(embed);
        // Task markers are represented by inline embeds even when their host
        // deliberately exposes read-only document semantics. Do not add those
        // inert objects to Tab order; custom embeds without task metadata keep
        // the legacy assumption that their realized element is interactive.
        return embed.AutomationMetadata?.CanToggle != false;
    }

    private readonly record struct FocusablePlanEntry(
        FocusableItem Item,
        IHorizontalOverflowBox? HorizontalOverflow);

}

internal readonly record struct LazyLayoutCommit(
    bool Changed,
    int MeasuredBlocks,
    double OldHeight,
    double NewHeight,
    long LayoutRevision)
{
    public static LazyLayoutCommit Unchanged(double height, long layoutRevision)
        => new(false, 0, height, height, layoutRevision);
}

internal readonly record struct SnapshotScrollAnchor(
    int? BlockIndex,
    double OffsetFromTop,
    double Height);

internal enum FocusTargetLayoutState
{
    Ready,
    RequiresRealization,
    Busy,
    Unavailable,
}
