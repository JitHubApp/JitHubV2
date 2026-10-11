using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Base class for all block-level boxes (paragraphs, headings, lists, etc.).
/// </summary>
internal abstract class BlockBox
{
    private Thickness _margin;
    internal double CollapsedTopMargin;
    internal bool SuppressTopMargin { get; set; }
    internal bool SuppressBottomMargin { get; set; }

    /// <summary>Gets or sets the logical block index assigned during layout.</summary>
    public int BlockIndex { get; set; }

    /// <summary>
    /// Identifies a fragment belonging to one persistent safe-HTML div/center
    /// inline flow. It is set only on renderer-confirmed inline fragments.
    /// </summary>
    internal int? SafeHtmlInlineFlowScopeId { get; set; }

    /// <summary>Gets the arranged document-space bounds.</summary>
    public Rect Bounds { get; protected set; }

    /// <summary>Gets or sets the outer margin.</summary>
    public Thickness Margin
    {
        get => ResolvePlacementMargin(_margin);
        set => _margin = value;
    }

    /// <summary>
    /// Applies the placement-time collapse and suppression metadata to an
    /// authored margin without publishing that margin on this box.
    /// </summary>
    internal Thickness ResolvePlacementMargin(Thickness rawMargin)
    {
        if (CollapsedTopMargin <= 0 && !SuppressTopMargin && !SuppressBottomMargin)
            return rawMargin;

        return new Thickness(
            rawMargin.Left,
            SuppressTopMargin ? 0 : CollapsedTopMargin > 0 ? rawMargin.Top - CollapsedTopMargin : rawMargin.Top,
            rawMargin.Right,
            SuppressBottomMargin ? 0 : rawMargin.Bottom);
    }

    /// <summary>
    /// Returns the authored margin before collapsed/suppressed layout rules
    /// are applied. Measured-placement fences use this immutable value so a
    /// mutation hidden by the effective <see cref="Margin"/> getter cannot be
    /// mistaken for an unchanged source box.
    /// </summary>
    internal Thickness RawPlacementMargin => _margin;

    /// <summary>
    /// Copies only the scalar margin-collapse inputs into a private layout
    /// clone. The public Margin getter is effective layout state and cannot
    /// recover raw values hidden by suppression or collapse.
    /// </summary>
    internal void CopyPlacementMarginsFrom(BlockBox source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _margin = source._margin;
        CollapsedTopMargin = source.CollapsedTopMargin;
        SuppressTopMargin = source.SuppressTopMargin;
        SuppressBottomMargin = source.SuppressBottomMargin;
    }

    /// <summary>Gets whether this block has estimated or stale layout state.</summary>
    public bool IsDirty { get; internal set; } = true;

    /// <summary>
    /// Compute desired height for the available width and update internal state.
    /// </summary>
    public abstract float Measure(float availableWidth);

    /// <summary>
    /// Throws when the current layout pass has been cancelled.
    /// </summary>
    internal virtual void ThrowIfCancellationRequested() { }

    /// <summary>
    /// Place the box at the given top-left and finalise <see cref="Bounds"/>.
    /// </summary>
    public virtual void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, Bounds.Height);
        IsDirty = false;
    }

    /// <summary>
    /// Adopts only a measured root's outer bounds after its prepared descendant
    /// placements have been transferred. This deliberately does not dispatch
    /// virtual Arrange or touch child/native geometry.
    /// </summary>
    internal void AdoptMeasuredOuterBounds(float x, float y, float width, float height)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(width) || width < 0 || !float.IsFinite(height) || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Measured outer bounds must be finite and nonnegative.");
        }

        Bounds = new Rect(x, y, width, height);
        IsDirty = false;
    }

    /// <summary>
    /// Assigns temporary bounds for a lazily measured block. The block remains
    /// dirty so consumers know its children/native layouts have not been
    /// realized yet.
    /// </summary>
    internal void ArrangeEstimated(float x, float y, float width, float height)
    {
        Bounds = new Rect(x, y, width, Math.Max(0, height));
        IsDirty = true;
    }

    /// <summary>
    /// Paint the box. Implementations must be idempotent and allocation-free on
    /// the hot path.
    /// </summary>
    public abstract void Paint(CanvasDrawingSession ds, Rect viewport);

    /// <summary>
    /// Hit test a point in document coordinates and produce a logical position.
    /// Returns false when the point is outside this block.
    /// </summary>
    public virtual bool HitTest(Point point, out Document.DocumentPosition position)
    {
        position = new Document.DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    /// <summary>
    /// Hit tests an endpoint while an existing selection is being extended.
    /// Atomic visual blocks can override this without making their otherwise
    /// non-selectable background start a new pointer selection.
    /// </summary>
    internal virtual bool HitTestSelectionEndpoint(
        Point point,
        out Document.DocumentPosition position) =>
        HitTest(point, out position);

    /// <summary>
    /// Returns rectangles to highlight when this block participates in a
    /// document selection. Default implementation returns the entire bounds
    /// when the block index falls inside the (normalized) range; leaf
    /// containers like <c>InlineContainerBox</c> override this to return
    /// per-character regions.
    /// </summary>
    public virtual System.Collections.Generic.IEnumerable<Rect> GetSelectionRects(Document.DocumentRange range)
    {
        var n = range.Normalized();
        if (BlockIndex >= n.Start.BlockIndex && BlockIndex <= n.End.BlockIndex
            && !(n.Start.BlockIndex == n.End.BlockIndex
                 && n.Start.InlineIndex == n.End.InlineIndex
                 && n.Start.CharacterOffset == n.End.CharacterOffset))
        {
            yield return Bounds;
        }
    }

    /// <summary>
    /// Repaiits foreground chrome that would otherwise be covered by the
    /// opaque native selection fill. Text containers repaint glyphs; visual
    /// blocks such as thematic breaks repaint their separator line.
    /// </summary>
    public virtual void PaintSelectionForeground(
        CanvasDrawingSession ds,
        Document.DocumentRange range,
        Windows.UI.Color color,
        Rect viewport)
    {
    }

    /// <summary>
    /// Releases native resources held by this box (e.g. <c>CanvasTextLayout</c>
    /// handles).  Containers must recurse into their children.  Called when
    /// a snapshot is replaced or the control unloads.
    /// </summary>
    public virtual void Dispose() { }

    /// <summary>
    /// Starts any asynchronous native-work retirement owned by this box and
    /// appends only the resulting task barriers. Containers recurse into their
    /// children; snapshots await the collected tasks after releasing the
    /// layout lock, so provider/network work can never block the UI thread.
    /// </summary>
    internal virtual void CollectRetirementTasks(List<Task> pendingTasks) { }
}

/// <summary>
/// Attempts every owned-resource cleanup in a container before surfacing
/// failures. This is used only on retirement paths, so the success path is a
/// small value type with no allocation; exception storage is bounded and is
/// allocated only after the first cleanup failure.
/// </summary>
internal struct BlockBoxCleanupScope
{
    private const int MaximumFailureDetails = 8;
    private const int MaximumAggregateDepth = 8;

    private List<Exception>? _failures;
    private bool _hasAdditionalFailures;

    internal void TryDispose(BlockBox? box)
    {
        if (box is null)
            return;

        try
        {
            box.Dispose();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    internal void TryDispose(IDisposable? resource)
    {
        if (resource is null)
            return;

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    internal void TryDisposeAndClear<T>(ref T? resource)
        where T : class, IDisposable
        => TryDisposeAndClear(ref resource, disposer: null);

    internal void TryDisposeAndClear<T>(ref T? resource, Action<IDisposable>? disposer)
        where T : class, IDisposable
    {
        T? current = resource;
        if (current is null)
            return;

        try
        {
            if (disposer is null)
                current.Dispose();
            else
                disposer(current);
            resource = null;
        }
        catch (Exception exception)
        {
            // Keep the resource reachable by its owner so a later retirement
            // attempt can retry cleanup. Clearing is deliberately conditional
            // on successful Dispose; a throwing native dispose has unknown
            // ownership state.
            RecordFailure(exception);
        }
    }

    internal void TryCollectRetirementTasks(BlockBox? box, List<Task> pendingTasks)
    {
        if (box is null)
            return;

        try
        {
            box.CollectRetirementTasks(pendingTasks);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    internal void ThrowIfAny(string operation)
    {
        if (_failures is not { Count: > 0 } failures)
            return;

        if (failures.Count == 1 && !_hasAdditionalFailures)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        if (_hasAdditionalFailures)
        {
            failures.Add(new InvalidOperationException(
                "Additional resource-retirement failures were omitted."));
        }

        throw new AggregateException(
            $"One or more errors occurred while {operation}.",
            failures);
    }

    internal void RecordFailure(Exception exception)
    {
        if (_hasAdditionalFailures)
            return;

        if (exception is not AggregateException aggregate || aggregate.InnerExceptions.Count == 0)
        {
            RecordLeafFailure(exception);
            return;
        }

        // Flatten only a bounded prefix. AggregateException may contain many
        // nested failures; retaining it as one item would bypass our detail
        // bound and could keep an arbitrarily large exception graph alive.
        var pending = new Stack<AggregateFailureCursor>(MaximumAggregateDepth);
        pending.Push(new AggregateFailureCursor(aggregate.InnerExceptions, 0, depth: 0));
        while (true)
        {
            AggregateFailureCursor cursor = pending.Pop();
            Exception current = cursor.InnerExceptions[cursor.NextIndex];
            int nextIndex = cursor.NextIndex + 1;
            if (nextIndex < cursor.InnerExceptions.Count)
                pending.Push(new AggregateFailureCursor(cursor.InnerExceptions, nextIndex, cursor.Depth));

            if (current is AggregateException nested && nested.InnerExceptions.Count > 0)
            {
                int nestedDepth = cursor.Depth + 1;
                if (nestedDepth >= MaximumAggregateDepth)
                {
                    RecordLeafFailure(new InvalidOperationException(
                        "Nested resource-retirement failures exceeded the diagnostic depth limit."));
                    _hasAdditionalFailures = true;
                }
                else
                {
                    pending.Push(new AggregateFailureCursor(nested.InnerExceptions, 0, nestedDepth));
                }
            }
            else
            {
                RecordLeafFailure(current);
            }

            if (_hasAdditionalFailures || pending.Count == 0)
                return;
        }
    }

    private void RecordLeafFailure(Exception exception)
    {
        _failures ??= new List<Exception>(MaximumFailureDetails + 1);
        if (_failures.Count < MaximumFailureDetails)
        {
            _failures.Add(exception);
            return;
        }

        _hasAdditionalFailures = true;
    }

    private readonly struct AggregateFailureCursor
    {
        internal AggregateFailureCursor(
            IReadOnlyList<Exception> innerExceptions,
            int nextIndex,
            int depth)
        {
            InnerExceptions = innerExceptions;
            NextIndex = nextIndex;
            Depth = depth;
        }

        internal readonly IReadOnlyList<Exception> InnerExceptions;
        internal readonly int NextIndex;
        internal readonly int Depth;
    }
}
