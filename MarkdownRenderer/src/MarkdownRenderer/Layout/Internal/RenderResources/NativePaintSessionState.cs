using System;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Internal.RenderResources;

/// <summary>
/// Per-drawing-session custody of paint layers and their auxiliary resources:
/// reserves pooled owner nodes, closes them in LIFO order, and poisons the
/// session so uncertain native slots move to nonretryable custody.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because layers and auxiliaries are one
/// state machine under one gate: a layer pins its clip auxiliary, both kinds
/// share the single in-flight operation slot, and poisoning must retire
/// every active, pending and pooled node of either kind together.
/// </remarks>
internal sealed class NativePaintSessionState
{
    private readonly object _gate = new();
    private readonly NativeResourceRetirement _retirement;
    private Action<IDisposable>? _disposer;
    private NativePaintLayerNode? _layerPool;
    private NativePaintLayerNode? _layers;
    private NativePaintAuxiliaryNode? _auxiliaryPool;
    private NativePaintAuxiliaryNode? _auxiliaries;
    private NativePaintLayerNode? _pendingLayerFactory;
    private NativePaintLayerNode? _nativeLayerOperation;
    private NativePaintAuxiliaryNode? _pendingAuxiliaryFactory;
    private NativePaintAuxiliaryNode? _nativeAuxiliaryOperation;
    private bool _poisoned;
    private bool _operationInProgress;

    internal NativePaintSessionState(NativeResourceRetirement retirement)
    {
        _retirement = retirement;
        // A poisoned session stops using the owner's disposal seam.
        _disposer = retirement.Disposer;
    }

    internal NativePaintSessionResources.LayerLease ReserveLayer(NativePaintSessionResources.AuxiliaryLease clipOwner)
    {
        lock (_gate)
        {
            ThrowIfPoisonedNoLock();
            if (_operationInProgress)
                throw new InvalidOperationException("A CanvasDrawingSession cannot create overlapping paint-resource operations.");

            NativePaintAuxiliaryNode? clipNode = null;
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

            NativePaintLayerNode node = GetReusableLayerNodeNoLock();
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
            return new NativePaintSessionResources.LayerLease(this, node, generation);
        }
    }

    internal NativePaintSessionResources.AuxiliaryLease ReserveAuxiliary()
    {
        lock (_gate)
        {
            ThrowIfPoisonedNoLock();
            if (_operationInProgress)
                throw new InvalidOperationException("A CanvasDrawingSession cannot create overlapping paint-resource operations.");

            NativePaintAuxiliaryNode node = GetReusableAuxiliaryNodeNoLock();
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
            return new NativePaintSessionResources.AuxiliaryLease(this, node, generation);
        }
    }

    internal void AttachLayer(NativePaintSessionResources.LayerLease lease, CanvasActiveLayer layer)
    {
        lock (_gate)
        {
            NativePaintLayerNode node = RequireLayerNode(lease);
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

    internal void AttachAuxiliary(NativePaintSessionResources.AuxiliaryLease lease, IDisposable resource)
    {
        lock (_gate)
        {
            NativePaintAuxiliaryNode node = RequireAuxiliaryNode(lease);
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

    internal void CancelAuxiliaryFactory(NativePaintSessionResources.AuxiliaryLease lease)
    {
        lock (_gate)
        {
            if (!IsCurrentAuxiliaryNoLock(lease) || !lease.Node!.FactoryPending)
                return;
            NativePaintAuxiliaryNode node = lease.Node;
            UnlinkAuxiliaryNoLock(node);
            _pendingAuxiliaryFactory = null;
            _operationInProgress = HasNativeOperationNoLock();
            node.FactoryPending = false;
            RetireAuxiliaryNodeNoLock(node, publish: true);
            PoisonAndPublishNoLock();
        }
    }

    internal void FailLayerFactory(NativePaintSessionResources.LayerLease lease)
    {
        lock (_gate)
        {
            if (IsCurrentLayerNoLock(lease) && lease.Node!.FactoryPending)
            {
                NativePaintLayerNode node = lease.Node;
                UnlinkLayerNoLock(node);
                _pendingLayerFactory = null;
                _operationInProgress = HasNativeOperationNoLock();
                node.FactoryPending = false;
                RetireLayerNodeNoLock(node, publish: true);
            }
            PoisonAndPublishNoLock();
        }
    }

    internal void FailLayerAttach(NativePaintSessionResources.LayerLease lease, CanvasActiveLayer layer)
    {
        lock (_gate)
        {
            if (IsCurrentLayerNoLock(lease) && lease.Node!.FactoryPending)
            {
                NativePaintLayerNode node = lease.Node;
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

    internal CanvasActiveLayer? GetLayer(NativePaintSessionResources.LayerLease lease)
    {
        lock (_gate)
            return IsCurrentLayerNoLock(lease) && lease.Node!.Active ? lease.Node.Layer : null;
    }

    internal bool IsLayerClosed(NativePaintSessionResources.LayerLease lease)
    {
        lock (_gate)
            return !IsCurrentLayerNoLock(lease) || !lease.Node!.Active;
    }

    internal void CloseLayer(NativePaintSessionResources.LayerLease lease)
    {
        NativePaintLayerNode node;
        CanvasActiveLayer layer;
        NativePaintSessionResources.AuxiliaryLease clipOwner;
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

    internal void CloseAuxiliary(NativePaintSessionResources.AuxiliaryLease lease)
    {
        NativePaintAuxiliaryNode node;
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

    private NativePaintLayerNode GetReusableLayerNodeNoLock()
    {
        for (NativePaintLayerNode? node = _layerPool; node is not null; node = node.PoolNext)
        {
            if (!node.Active && !node.Retired && node.Generation < long.MaxValue)
                return node;
            if (!node.Active && !node.Retired)
                node.Retired = true;
        }

        var created = new NativePaintLayerNode(_retirement.Reserve(0, 1))
        {
            PoolNext = _layerPool,
        };
        _layerPool = created;
        return created;
    }

    private NativePaintAuxiliaryNode GetReusableAuxiliaryNodeNoLock()
    {
        for (NativePaintAuxiliaryNode? node = _auxiliaryPool; node is not null; node = node.PoolNext)
        {
            if (!node.Active && !node.Retired && node.Generation < long.MaxValue)
                return node;
            if (!node.Active && !node.Retired)
                node.Retired = true;
        }

        var created = new NativePaintAuxiliaryNode(_retirement.Reserve(0, 1))
        {
            PoolNext = _auxiliaryPool,
        };
        _auxiliaryPool = created;
        return created;
    }

    private bool IsCurrentLayerNoLock(NativePaintSessionResources.LayerLease lease) =>
        ReferenceEquals(lease.State, this) && lease.Node is { } node && node.Generation == lease.Generation;

    private bool IsCurrentAuxiliaryNoLock(NativePaintSessionResources.AuxiliaryLease lease) =>
        ReferenceEquals(lease.State, this) && lease.Node is { } node && node.Generation == lease.Generation;

    private static NativePaintLayerNode RequireLayerNode(NativePaintSessionResources.LayerLease lease) =>
        lease.Node ?? throw new InvalidOperationException("The paint-session layer token is empty.");

    private static NativePaintAuxiliaryNode RequireAuxiliaryNode(NativePaintSessionResources.AuxiliaryLease lease) =>
        lease.Node ?? throw new InvalidOperationException("The paint auxiliary token is empty.");

    private static long NextGeneration(long generation) =>
        generation < long.MaxValue ? generation + 1 : throw new InvalidOperationException("The paint-resource generation is exhausted.");

    private bool ContainsAuxiliaryNoLock(NativePaintAuxiliaryNode candidate)
    {
        for (NativePaintAuxiliaryNode? node = _auxiliaries; node is not null; node = node.ActiveNext)
            if (ReferenceEquals(node, candidate))
                return true;
        return false;
    }

    private void UnlinkLayerNoLock(NativePaintLayerNode candidate)
    {
        NativePaintLayerNode? previous = null;
        for (NativePaintLayerNode? node = _layers; node is not null; node = node.StackNext)
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

    private void UnlinkLayerPoolNoLock(NativePaintLayerNode candidate)
    {
        NativePaintLayerNode? previous = null;
        for (NativePaintLayerNode? node = _layerPool; node is not null; node = node.PoolNext)
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

    private void UnlinkAuxiliaryNoLock(NativePaintAuxiliaryNode candidate)
    {
        NativePaintAuxiliaryNode? previous = null;
        for (NativePaintAuxiliaryNode? node = _auxiliaries; node is not null; node = node.ActiveNext)
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

    private void UnlinkAuxiliaryPoolNoLock(NativePaintAuxiliaryNode candidate)
    {
        NativePaintAuxiliaryNode? previous = null;
        for (NativePaintAuxiliaryNode? node = _auxiliaryPool; node is not null; node = node.PoolNext)
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
        _disposer = null;

        NativePaintLayerNode? preserveLayer = _pendingLayerFactory ?? _nativeLayerOperation;
        NativePaintLayerNode? layer = _layers;
        _layers = null;
        while (layer is not null)
        {
            NativePaintLayerNode? next = layer.StackNext;
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

        NativePaintAuxiliaryNode? preserveAuxiliary = _pendingAuxiliaryFactory ?? _nativeAuxiliaryOperation;
        NativePaintAuxiliaryNode? auxiliary = _auxiliaries;
        _auxiliaries = null;
        while (auxiliary is not null)
        {
            NativePaintAuxiliaryNode? next = auxiliary.ActiveNext;
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

    private void RetireLayerNodeNoLock(NativePaintLayerNode node, bool publish)
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

    private void RetireAuxiliaryNodeNoLock(NativePaintAuxiliaryNode node, bool publish)
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
        if (_disposer is { } disposer)
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
