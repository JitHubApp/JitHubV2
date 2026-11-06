using System;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Tracks Win2D paint resources whose cleanup is bound to one drawing session.
/// The weak-key value never stores the session or any layout/source object.
/// One pre-reserved owner node is allocated per maximum nesting depth and
/// reused after successful closes; published failure records are never reused.
/// Failed active-layer pops poison the session and transfer every uncertain
/// slot to nonretryable custody so a background clock never pops a layer after
/// its drawing session has returned.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="MarkdownRenderResources"/> owns one instance, so the
/// sessions its documents paint into are tracked with its native
/// retirement owner, whose disposal seam closes their resources.
/// </para>
/// <para>
/// An instance may be created with a hook that runs between a layer's
/// custody reservation and the native layer factory. Native-lifetime tests
/// use it to fail or poison a session at that boundary; production owners
/// have none.
/// </para>
/// </remarks>
internal sealed class NativePaintSessionResources
{
    private readonly ConditionalWeakTable<CanvasDrawingSession, NativePaintSessionState> _sessions = new();
    private readonly NativeResourceRetirement _retirement;
    private readonly Action? _afterLayerReservation;

    /// <summary>Creates paint-session tracking that retires through <paramref name="retirement"/>.</summary>
    /// <param name="retirement">The owner of failed paint resources.</param>
    /// <param name="afterLayerReservation">
    /// Runs after each layer's custody is reserved and before Win2D creates
    /// the layer, or <see langword="null"/> for none.
    /// </param>
    internal NativePaintSessionResources(NativeResourceRetirement retirement, Action? afterLayerReservation = null)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        _retirement = retirement;
        _afterLayerReservation = afterLayerReservation;
    }

    internal LayerLease CreateLayer(CanvasDrawingSession drawingSession, float opacity, Windows.Foundation.Rect clip)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        NativePaintSessionState state = GetState(drawingSession);
        LayerLease lease = state.ReserveLayer(default);
        CanvasActiveLayer layer;
        try
        {
            _afterLayerReservation?.Invoke();
            layer = drawingSession.CreateLayer(opacity, clip);
        }
        catch
        {
            state.FailLayerFactory(lease);
            throw;
        }

        try
        {
            state.AttachLayer(lease, layer);
            return lease;
        }
        catch
        {
            state.FailLayerAttach(lease, layer);
            throw;
        }
    }

    internal LayerLease CreateLayer(
        CanvasDrawingSession drawingSession,
        float opacity,
        CanvasGeometry clip,
        AuxiliaryLease clipOwner)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(clip);
        NativePaintSessionState? state = clipOwner.State;
        if (state is null || !_sessions.TryGetValue(drawingSession, out NativePaintSessionState? registered) ||
            !ReferenceEquals(state, registered))
        {
            var invalid = new InvalidOperationException("The clip geometry owner does not belong to this drawing session.");
            clipOwner.ClosePreservingPrimary(invalid);
            throw invalid;
        }

        LayerLease lease;
        try
        {
            lease = state.ReserveLayer(clipOwner);
        }
        catch (Exception primary)
        {
            clipOwner.ClosePreservingPrimary(primary);
            throw;
        }
        CanvasActiveLayer layer;
        try
        {
            _afterLayerReservation?.Invoke();
            layer = drawingSession.CreateLayer(opacity, clip);
        }
        catch
        {
            state.FailLayerFactory(lease);
            throw;
        }

        try
        {
            state.AttachLayer(lease, layer);
            return lease;
        }
        catch
        {
            state.FailLayerAttach(lease, layer);
            throw;
        }
    }

    /// <summary>Reserve source-free custody before creating a native clip geometry.</summary>
    internal AuxiliaryLease ReserveAuxiliary(CanvasDrawingSession drawingSession)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        return GetState(drawingSession).ReserveAuxiliary();
    }

    /// <summary>
    /// Poison an already-tracked session after another native active-layer
    /// stack failed. This path never allocates a state/CWT entry or invokes a
    /// native close; it only transfers pre-reserved owners to durable custody.
    /// </summary>
    internal void PoisonSession(CanvasDrawingSession drawingSession)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        if (_sessions.TryGetValue(drawingSession, out NativePaintSessionState? state))
            state.PoisonFromExternalLayerFailure();
    }

    private NativePaintSessionState GetState(CanvasDrawingSession drawingSession) =>
        _sessions.GetOrAdd(
            drawingSession,
            static (_, retirement) => new NativePaintSessionState(retirement),
            _retirement);

    /// <summary>A stack-only token; stale copies cannot close a reused node.</summary>
    internal readonly struct LayerLease
    {
        private readonly NativePaintSessionState? _state;
        private readonly NativePaintLayerNode? _node;
        private readonly long _generation;

        internal LayerLease(NativePaintSessionState state, NativePaintLayerNode node, long generation)
        {
            _state = state;
            _node = node;
            _generation = generation;
        }

        internal CanvasActiveLayer? Layer => _state?.GetLayer(this);
        internal bool IsClosed => _state is null || _state.IsLayerClosed(this);
        internal void Close()
        {
            if (_state is not null)
                _state.CloseLayer(this);
        }

        internal void ClosePreservingPrimary(Exception primary)
        {
            ArgumentNullException.ThrowIfNull(primary);
            try { Close(); }
            catch (Exception cleanup)
            {
                // The owner record is published before Close reports a failed
                // PopLayer. Keep both failures visible without storing either.
                throw new AggregateException("Painting failed and the active layer could not be closed.", primary, cleanup);
            }
        }

        internal NativePaintSessionState? State => _state;
        internal NativePaintLayerNode? Node => _node;
        internal long Generation => _generation;
    }

    /// <summary>A stack-only token for a native geometry used by a paint layer.</summary>
    internal readonly struct AuxiliaryLease
    {
        private readonly NativePaintSessionState? _state;
        private readonly NativePaintAuxiliaryNode? _node;
        private readonly long _generation;

        internal AuxiliaryLease(NativePaintSessionState state, NativePaintAuxiliaryNode node, long generation)
        {
            _state = state;
            _node = node;
            _generation = generation;
        }

        internal void Attach(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            _state?.AttachAuxiliary(this, resource);
        }

        /// <summary>Cancel a native geometry factory that returned no handle.</summary>
        internal void CancelFactory() => _state?.CancelAuxiliaryFactory(this);

        /// <summary>Close after every layer that references the resource has closed.</summary>
        internal void Close() => _state?.CloseAuxiliary(this);

        internal void ClosePreservingPrimary(Exception primary)
        {
            ArgumentNullException.ThrowIfNull(primary);
            try { Close(); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Painting failed and an auxiliary resource could not be closed.", primary, cleanup);
            }
        }

        internal NativePaintSessionState? State => _state;
        internal NativePaintAuxiliaryNode? Node => _node;
        internal long Generation => _generation;
    }
}
