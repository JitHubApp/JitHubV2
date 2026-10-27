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
internal static class NativePaintSessionResources
{
    private static readonly ConditionalWeakTable<CanvasDrawingSession, PaintSessionState> s_sessions = new();

    internal static LayerLease CreateLayer(CanvasDrawingSession drawingSession, float opacity, Windows.Foundation.Rect clip)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        PaintSessionState state = GetState(drawingSession);
        LayerLease lease = state.ReserveLayer(default);
        CanvasActiveLayer layer;
        try
        {
            state.RunTestAfterLayerReservation();
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

    internal static LayerLease CreateLayer(
        CanvasDrawingSession drawingSession,
        float opacity,
        CanvasGeometry clip,
        AuxiliaryLease clipOwner)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(clip);
        PaintSessionState? state = clipOwner.State;
        if (state is null || !s_sessions.TryGetValue(drawingSession, out PaintSessionState? registered) ||
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
            state.RunTestAfterLayerReservation();
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
    internal static AuxiliaryLease ReserveAuxiliary(CanvasDrawingSession drawingSession)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        return GetState(drawingSession).ReserveAuxiliary();
    }

    // Isolated tests inject a timerless owner and native fault plan before a
    // real Win2D drawing session is used. Production never supplies callbacks.
    internal static void RegisterTestOwner(
        CanvasDrawingSession drawingSession,
        NativeResourceRetirement retirement,
        Action<IDisposable>? disposer = null,
        Action? afterLayerReservation = null)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(retirement);
        if (!retirement.AllowsTestDisposer && (disposer is not null || afterLayerReservation is not null))
            throw new ArgumentException("Paint-resource fault injection requires an isolated retirement owner.", nameof(retirement));
        s_sessions.Add(drawingSession, new PaintSessionState(retirement, disposer, afterLayerReservation));
    }

    /// <summary>
    /// Poison an already-tracked session after another native active-layer
    /// stack failed. This path never allocates a state/CWT entry or invokes a
    /// native close; it only transfers pre-reserved owners to durable custody.
    /// </summary>
    internal static void PoisonSession(CanvasDrawingSession drawingSession)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        if (s_sessions.TryGetValue(drawingSession, out PaintSessionState? state))
            state.PoisonFromExternalLayerFailure();
    }

    private static PaintSessionState GetState(CanvasDrawingSession drawingSession) =>
        s_sessions.GetValue(
            drawingSession,
            static _ => new PaintSessionState(NativeResourceRetirement.Shared, null, null));

    /// <summary>A stack-only token; stale copies cannot close a reused node.</summary>
    internal readonly struct LayerLease
    {
        private readonly PaintSessionState? _state;
        private readonly LayerNode? _node;
        private readonly long _generation;

        internal LayerLease(PaintSessionState state, LayerNode node, long generation)
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

        internal PaintSessionState? State => _state;
        internal LayerNode? Node => _node;
        internal long Generation => _generation;
    }

    /// <summary>A stack-only token for a native geometry used by a paint layer.</summary>
    internal readonly struct AuxiliaryLease
    {
        private readonly PaintSessionState? _state;
        private readonly AuxiliaryNode? _node;
        private readonly long _generation;

        internal AuxiliaryLease(PaintSessionState state, AuxiliaryNode node, long generation)
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

        internal PaintSessionState? State => _state;
        internal AuxiliaryNode? Node => _node;
        internal long Generation => _generation;
    }

    internal sealed class LayerNode
    {
        internal LayerNode(NativeResourceRetirementRecord retirement) => Retirement = retirement;
        internal NativeResourceRetirementRecord Retirement { get; }
        internal LayerNode? PoolNext;
        internal LayerNode? StackNext;
        internal CanvasActiveLayer? Layer;
        internal AuxiliaryLease ClipOwner;
        internal long ClipOwnerGeneration;
        internal long Generation;
        internal bool Active;
        internal bool FactoryPending;
        internal bool Retired;
    }

    internal sealed class AuxiliaryNode
    {
        internal AuxiliaryNode(NativeResourceRetirementRecord retirement) => Retirement = retirement;
        internal NativeResourceRetirementRecord Retirement { get; }
        internal AuxiliaryNode? PoolNext;
        internal AuxiliaryNode? ActiveNext;
        internal IDisposable? Resource;
        internal long Generation;
        internal bool Active;
        internal bool FactoryPending;
        internal bool DependentLayerOpen;
        internal bool Retired;
    }

    internal sealed class PaintSessionState
    {
        private readonly object _gate = new();
        private readonly NativeResourceRetirement _retirement;
        private Action<IDisposable>? _testDisposer;
        private Action? _testAfterLayerReservation;
        private LayerNode? _layerPool;
        private LayerNode? _layers;
        private AuxiliaryNode? _auxiliaryPool;
        private AuxiliaryNode? _auxiliaries;
        private LayerNode? _pendingLayerFactory;
        private LayerNode? _nativeLayerOperation;
        private AuxiliaryNode? _pendingAuxiliaryFactory;
        private AuxiliaryNode? _nativeAuxiliaryOperation;
        private bool _poisoned;
        private bool _operationInProgress;

        internal PaintSessionState(
            NativeResourceRetirement retirement,
            Action<IDisposable>? testDisposer,
            Action? testAfterLayerReservation)
        {
            _retirement = retirement;
            _testDisposer = testDisposer;
            _testAfterLayerReservation = testAfterLayerReservation;
        }

        internal LayerLease ReserveLayer(AuxiliaryLease clipOwner)
        {
            lock (_gate)
            {
                ThrowIfPoisonedNoLock();
                if (_operationInProgress)
                    throw new InvalidOperationException("A CanvasDrawingSession cannot create overlapping paint-resource operations.");

                AuxiliaryNode? clipNode = null;
                if (clipOwner.Node is not null)
                {
                    if (!ReferenceEquals(clipOwner.State, this) ||
                        !IsCurrentAuxiliaryNoLock(clipOwner) || clipOwner.Node.DependentLayerOpen)
                    {
                        throw new InvalidOperationException("The layer clip geometry has no available paint-session owner.");
                    }
                    clipNode = clipOwner.Node;
                }
                else if (clipOwner.State is not null)
                {
                    throw new InvalidOperationException("The layer clip geometry token is invalid.");
                }

                LayerNode node = GetReusableLayerNodeNoLock();
                long generation = NextGeneration(node.Generation);
                node.Generation = generation;
                node.Active = true;
                node.FactoryPending = true;
                node.Layer = null;
                node.ClipOwner = clipOwner;
                node.ClipOwnerGeneration = clipOwner.Generation;
                node.Retirement.SetTemporary(0, null, retryable: false);
                node.StackNext = _layers;
                _layers = node;
                _pendingLayerFactory = node;
                _operationInProgress = true;
                if (clipNode is not null)
                    clipNode.DependentLayerOpen = true;
                return new LayerLease(this, node, generation);
            }
        }

        internal AuxiliaryLease ReserveAuxiliary()
        {
            lock (_gate)
            {
                ThrowIfPoisonedNoLock();
                if (_operationInProgress)
                    throw new InvalidOperationException("A CanvasDrawingSession cannot create overlapping paint-resource operations.");

                AuxiliaryNode node = GetReusableAuxiliaryNodeNoLock();
                long generation = NextGeneration(node.Generation);
                node.Generation = generation;
                node.Active = true;
                node.FactoryPending = true;
                node.DependentLayerOpen = false;
                node.Resource = null;
                node.Retirement.SetTemporary(0, null, retryable: false);
                node.ActiveNext = _auxiliaries;
                _auxiliaries = node;
                _pendingAuxiliaryFactory = node;
                _operationInProgress = true;
                return new AuxiliaryLease(this, node, generation);
            }
        }

        internal void RunTestAfterLayerReservation()
        {
            Action? callback;
            lock (_gate)
            {
                callback = _testAfterLayerReservation;
                _testAfterLayerReservation = null;
            }
            callback?.Invoke();
        }

        internal void AttachLayer(LayerLease lease, CanvasActiveLayer layer)
        {
            lock (_gate)
            {
                LayerNode node = RequireLayerNode(lease);
                if (!IsCurrentLayerNoLock(lease) || !node.Active || node.Retired || !node.FactoryPending)
                    throw new InvalidOperationException("The paint-session layer reservation is no longer current.");

                // Store into the reserved slot before checking a reentrant
                // poison. The factory result can therefore never be orphaned.
                node.Retirement.SetTemporary(0, layer, retryable: false);
                node.Layer = layer;
                node.FactoryPending = false;
                bool current = _operationInProgress && ReferenceEquals(_pendingLayerFactory, node);
                if (ReferenceEquals(_pendingLayerFactory, node))
                    _pendingLayerFactory = null;
                _operationInProgress = HasNativeOperationNoLock();
                if (_poisoned || !current)
                {
                    UnlinkLayerNoLock(node);
                    RetireLayerNodeNoLock(node, publish: true);
                    PoisonAndPublishNoLock();
                    throw new InvalidOperationException("The CanvasDrawingSession was poisoned while creating a native layer.");
                }
            }
        }

        internal void AttachAuxiliary(AuxiliaryLease lease, IDisposable resource)
        {
            lock (_gate)
            {
                AuxiliaryNode node = RequireAuxiliaryNode(lease);
                if (!IsCurrentAuxiliaryNoLock(lease) || !node.Active || node.Retired || !node.FactoryPending)
                    throw new InvalidOperationException("The auxiliary paint-resource reservation is no longer current.");

                node.Retirement.SetTemporary(0, resource, retryable: false);
                node.Resource = resource;
                node.FactoryPending = false;
                bool current = _operationInProgress && ReferenceEquals(_pendingAuxiliaryFactory, node);
                if (ReferenceEquals(_pendingAuxiliaryFactory, node))
                    _pendingAuxiliaryFactory = null;
                _operationInProgress = HasNativeOperationNoLock();
                if (_poisoned || !current)
                {
                    UnlinkAuxiliaryNoLock(node);
                    RetireAuxiliaryNodeNoLock(node, publish: true);
                    PoisonAndPublishNoLock();
                    throw new InvalidOperationException("The CanvasDrawingSession was poisoned while creating an auxiliary resource.");
                }
            }
        }

        internal void CancelAuxiliaryFactory(AuxiliaryLease lease)
        {
            lock (_gate)
            {
                if (!IsCurrentAuxiliaryNoLock(lease) || !lease.Node!.FactoryPending)
                    return;
                AuxiliaryNode node = lease.Node;
                UnlinkAuxiliaryNoLock(node);
                _pendingAuxiliaryFactory = null;
                _operationInProgress = HasNativeOperationNoLock();
                node.FactoryPending = false;
                RetireAuxiliaryNodeNoLock(node, publish: true);
                PoisonAndPublishNoLock();
            }
        }

        internal void FailLayerFactory(LayerLease lease)
        {
            lock (_gate)
            {
                if (IsCurrentLayerNoLock(lease) && lease.Node!.FactoryPending)
                {
                    LayerNode node = lease.Node;
                    UnlinkLayerNoLock(node);
                    _pendingLayerFactory = null;
                    _operationInProgress = HasNativeOperationNoLock();
                    node.FactoryPending = false;
                    RetireLayerNodeNoLock(node, publish: true);
                }
                PoisonAndPublishNoLock();
            }
        }

        internal void FailLayerAttach(LayerLease lease, CanvasActiveLayer layer)
        {
            lock (_gate)
            {
                if (IsCurrentLayerNoLock(lease) && lease.Node!.FactoryPending)
                {
                    LayerNode node = lease.Node;
                    node.Retirement.SetTemporary(0, layer, retryable: false);
                    node.Layer = layer;
                    node.FactoryPending = false;
                    UnlinkLayerNoLock(node);
                    _pendingLayerFactory = null;
                    _operationInProgress = HasNativeOperationNoLock();
                    RetireLayerNodeNoLock(node, publish: true);
                }
                PoisonAndPublishNoLock();
            }
        }

        internal CanvasActiveLayer? GetLayer(LayerLease lease)
        {
            lock (_gate)
                return IsCurrentLayerNoLock(lease) && lease.Node!.Active ? lease.Node.Layer : null;
        }

        internal bool IsLayerClosed(LayerLease lease)
        {
            lock (_gate)
                return !IsCurrentLayerNoLock(lease) || !lease.Node!.Active;
        }

        internal void CloseLayer(LayerLease lease)
        {
            LayerNode node;
            CanvasActiveLayer layer;
            AuxiliaryLease clipOwner;
            lock (_gate)
            {
                if (!IsCurrentLayerNoLock(lease) || !lease.Node!.Active || lease.Node.Retired || _poisoned)
                    return;
                if (_operationInProgress)
                    throw new InvalidOperationException("A CanvasDrawingSession cannot close overlapping paint-resource operations.");
                node = lease.Node;
                if (!ReferenceEquals(_layers, node))
                    throw new InvalidOperationException("Canvas active layers must be closed in stack order.");
                if (node.Layer is not { } activeLayer)
                {
                    PoisonAndPublishNoLock();
                    throw new InvalidOperationException("A reserved paint-session layer was not attached to its native owner.");
                }
                layer = activeLayer;
                clipOwner = node.ClipOwner;
                _nativeLayerOperation = node;
                _operationInProgress = true;
            }

            try
            {
                DisposeNative(layer);
            }
            catch
            {
                lock (_gate)
                {
                    _nativeLayerOperation = null;
                    _operationInProgress = HasNativeOperationNoLock();
                    PoisonAndPublishNoLock();
                }
                throw;
            }

            bool poisonedDuringClose;
            lock (_gate)
            {
                // PoisonSession preserves this in-flight owner until the native
                // close returns; the un-published private slot is still safe.
                node.Retirement.SetTemporary(0, null, retryable: false);
                if (ReferenceEquals(_layers, node))
                    _layers = node.StackNext;
                else
                    PoisonAndPublishNoLock();
                _nativeLayerOperation = null;
                node.Active = false;
                node.FactoryPending = false;
                node.Layer = null;
                node.StackNext = null;
                node.ClipOwner = default;
                node.ClipOwnerGeneration = 0;
                if (clipOwner.Node is { } clipNode && clipNode.Generation == clipOwner.Generation)
                    clipNode.DependentLayerOpen = false;
                poisonedDuringClose = _poisoned;
                if (poisonedDuringClose)
                    RetireLayerNodeNoLock(node, publish: true);
                _operationInProgress = HasNativeOperationNoLock();
            }

            if (poisonedDuringClose)
                throw new InvalidOperationException("The CanvasDrawingSession was poisoned while closing a native layer.");

            if (clipOwner.State is not null)
                CloseAuxiliary(clipOwner);
        }

        internal void CloseAuxiliary(AuxiliaryLease lease)
        {
            AuxiliaryNode node;
            IDisposable resource;
            lock (_gate)
            {
                if (!IsCurrentAuxiliaryNoLock(lease) || !lease.Node!.Active || lease.Node.Retired || _poisoned)
                    return;
                if (_operationInProgress)
                    throw new InvalidOperationException("A CanvasDrawingSession cannot close overlapping paint-resource operations.");
                node = lease.Node;
                if (node.FactoryPending || node.DependentLayerOpen || !ContainsAuxiliaryNoLock(node))
                    throw new InvalidOperationException("A paint auxiliary cannot close while a factory or dependent layer is active.");
                if (node.Resource is not { } nativeResource)
                {
                    UnlinkAuxiliaryNoLock(node);
                    RetireAuxiliaryNodeNoLock(node, publish: true);
                    return;
                }
                resource = nativeResource;
                _nativeAuxiliaryOperation = node;
                _operationInProgress = true;
            }

            try
            {
                DisposeNative(resource);
            }
            catch
            {
                lock (_gate)
                {
                    node.Retirement.SetTemporary(0, resource, retryable: true);
                    _nativeAuxiliaryOperation = null;
                    UnlinkAuxiliaryNoLock(node);
                    RetireAuxiliaryNodeNoLock(node, publish: true);
                    _operationInProgress = HasNativeOperationNoLock();
                }
                throw;
            }

            bool poisonedDuringClose;
            lock (_gate)
            {
                node.Retirement.SetTemporary(0, null, retryable: true);
                _nativeAuxiliaryOperation = null;
                UnlinkAuxiliaryNoLock(node);
                node.Active = false;
                node.Resource = null;
                node.FactoryPending = false;
                node.DependentLayerOpen = false;
                node.ActiveNext = null;
                poisonedDuringClose = _poisoned;
                if (poisonedDuringClose)
                    RetireAuxiliaryNodeNoLock(node, publish: true);
                _operationInProgress = HasNativeOperationNoLock();
            }

            if (poisonedDuringClose)
                throw new InvalidOperationException("The CanvasDrawingSession was poisoned while closing an auxiliary resource.");
        }

        internal void PoisonFromExternalLayerFailure()
        {
            lock (_gate)
                PoisonAndPublishNoLock();
        }

        private LayerNode GetReusableLayerNodeNoLock()
        {
            for (LayerNode? node = _layerPool; node is not null; node = node.PoolNext)
            {
                if (!node.Active && !node.Retired && node.Generation < long.MaxValue)
                    return node;
                if (!node.Active && !node.Retired)
                    node.Retired = true;
            }

            var created = new LayerNode(_retirement.Reserve(0, 1, _testDisposer))
            {
                PoolNext = _layerPool,
            };
            _layerPool = created;
            return created;
        }

        private AuxiliaryNode GetReusableAuxiliaryNodeNoLock()
        {
            for (AuxiliaryNode? node = _auxiliaryPool; node is not null; node = node.PoolNext)
            {
                if (!node.Active && !node.Retired && node.Generation < long.MaxValue)
                    return node;
                if (!node.Active && !node.Retired)
                    node.Retired = true;
            }

            var created = new AuxiliaryNode(_retirement.Reserve(0, 1, _testDisposer))
            {
                PoolNext = _auxiliaryPool,
            };
            _auxiliaryPool = created;
            return created;
        }

        private bool IsCurrentLayerNoLock(LayerLease lease) =>
            ReferenceEquals(lease.State, this) && lease.Node is { } node && node.Generation == lease.Generation;

        private bool IsCurrentAuxiliaryNoLock(AuxiliaryLease lease) =>
            ReferenceEquals(lease.State, this) && lease.Node is { } node && node.Generation == lease.Generation;

        private static LayerNode RequireLayerNode(LayerLease lease) =>
            lease.Node ?? throw new InvalidOperationException("The paint-session layer token is empty.");

        private static AuxiliaryNode RequireAuxiliaryNode(AuxiliaryLease lease) =>
            lease.Node ?? throw new InvalidOperationException("The paint auxiliary token is empty.");

        private static long NextGeneration(long generation) =>
            generation < long.MaxValue ? generation + 1 : throw new InvalidOperationException("The paint-resource generation is exhausted.");

        private bool ContainsAuxiliaryNoLock(AuxiliaryNode candidate)
        {
            for (AuxiliaryNode? node = _auxiliaries; node is not null; node = node.ActiveNext)
                if (ReferenceEquals(node, candidate))
                    return true;
            return false;
        }

        private void UnlinkLayerNoLock(LayerNode candidate)
        {
            LayerNode? previous = null;
            for (LayerNode? node = _layers; node is not null; node = node.StackNext)
            {
                if (!ReferenceEquals(node, candidate))
                {
                    previous = node;
                    continue;
                }
                if (previous is null)
                    _layers = node.StackNext;
                else
                    previous.StackNext = node.StackNext;
                node.StackNext = null;
                return;
            }
        }

        private void UnlinkLayerPoolNoLock(LayerNode candidate)
        {
            LayerNode? previous = null;
            for (LayerNode? node = _layerPool; node is not null; node = node.PoolNext)
            {
                if (!ReferenceEquals(node, candidate))
                {
                    previous = node;
                    continue;
                }
                if (previous is null)
                    _layerPool = node.PoolNext;
                else
                    previous.PoolNext = node.PoolNext;
                node.PoolNext = null;
                return;
            }
        }

        private void UnlinkAuxiliaryNoLock(AuxiliaryNode candidate)
        {
            AuxiliaryNode? previous = null;
            for (AuxiliaryNode? node = _auxiliaries; node is not null; node = node.ActiveNext)
            {
                if (!ReferenceEquals(node, candidate))
                {
                    previous = node;
                    continue;
                }
                if (previous is null)
                    _auxiliaries = node.ActiveNext;
                else
                    previous.ActiveNext = node.ActiveNext;
                node.ActiveNext = null;
                return;
            }
        }

        private void UnlinkAuxiliaryPoolNoLock(AuxiliaryNode candidate)
        {
            AuxiliaryNode? previous = null;
            for (AuxiliaryNode? node = _auxiliaryPool; node is not null; node = node.PoolNext)
            {
                if (!ReferenceEquals(node, candidate))
                {
                    previous = node;
                    continue;
                }
                if (previous is null)
                    _auxiliaryPool = node.PoolNext;
                else
                    previous.PoolNext = node.PoolNext;
                node.PoolNext = null;
                return;
            }
        }

        private void PoisonAndPublishNoLock()
        {
            _poisoned = true;
            _testDisposer = null;
            _testAfterLayerReservation = null;

            LayerNode? preserveLayer = _pendingLayerFactory ?? _nativeLayerOperation;
            LayerNode? layer = _layers;
            _layers = null;
            while (layer is not null)
            {
                LayerNode? next = layer.StackNext;
                if (ReferenceEquals(layer, preserveLayer))
                {
                    layer.StackNext = null;
                    _layers = layer;
                }
                else
                {
                    RetireLayerNodeNoLock(layer, publish: true);
                }
                layer = next;
            }

            AuxiliaryNode? preserveAuxiliary = _pendingAuxiliaryFactory ?? _nativeAuxiliaryOperation;
            AuxiliaryNode? auxiliary = _auxiliaries;
            _auxiliaries = null;
            while (auxiliary is not null)
            {
                AuxiliaryNode? next = auxiliary.ActiveNext;
                if (ReferenceEquals(auxiliary, preserveAuxiliary))
                {
                    auxiliary.ActiveNext = null;
                    _auxiliaries = auxiliary;
                }
                else
                {
                    RetireAuxiliaryNodeNoLock(auxiliary, publish: true);
                }
                auxiliary = next;
            }

            _operationInProgress = HasNativeOperationNoLock();
        }

        private void RetireLayerNodeNoLock(LayerNode node, bool publish)
        {
            if (publish)
                node.Retirement.Publish();
            UnlinkLayerPoolNoLock(node);
            node.Active = false;
            node.FactoryPending = false;
            node.Layer = null;
            node.StackNext = null;
            node.ClipOwner = default;
            node.ClipOwnerGeneration = 0;
            node.Retired = true;
        }

        private void RetireAuxiliaryNodeNoLock(AuxiliaryNode node, bool publish)
        {
            if (publish)
                node.Retirement.Publish();
            UnlinkAuxiliaryPoolNoLock(node);
            node.Active = false;
            node.FactoryPending = false;
            node.DependentLayerOpen = false;
            node.Resource = null;
            node.ActiveNext = null;
            node.Retired = true;
        }

        private bool HasNativeOperationNoLock() =>
            _pendingLayerFactory is not null || _nativeLayerOperation is not null ||
            _pendingAuxiliaryFactory is not null || _nativeAuxiliaryOperation is not null;

        private void DisposeNative(IDisposable resource)
        {
            if (_testDisposer is { } disposer)
                disposer(resource);
            else
                resource.Dispose();
        }

        private void ThrowIfPoisonedNoLock()
        {
            if (_poisoned)
                throw new InvalidOperationException("The CanvasDrawingSession was poisoned by a failed layer operation.");
        }
    }
}
