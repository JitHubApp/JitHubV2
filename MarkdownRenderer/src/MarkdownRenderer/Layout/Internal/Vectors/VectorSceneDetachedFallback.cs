using System;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.RenderResources;

namespace MarkdownRenderer.Layout.Internal.Vectors;

/// <summary>
/// Custody of one <see cref="VectorSceneBox"/>'s detached-scene fallback: the
/// adopted native text layout, the retirement record reserved for it, the
/// record of a candidate still being prepared, and the cleanup-pending flag
/// that keeps a layout whose Dispose failed from being painted.
/// </summary>
/// <remarks>
/// The host box composes this owner and keeps its own disposed flag; this
/// type reads that flag and, during terminal retirement, sets it at the exact
/// point the host's disposal contract requires.
/// </remarks>
internal sealed class VectorSceneDetachedFallback
{
    private readonly VectorSceneBox _host;
    private CanvasTextLayout? _layout;
    private bool _cleanupPending;
    private NativeResourceRetirementRecord? _retirementRecord;
    private NativeResourceRetirementRecord? _candidateRecord;

    internal VectorSceneDetachedFallback(VectorSceneBox host) => _host = host;

    /// <summary>
    /// The adopted fallback layout, or a layout whose Dispose failed and is
    /// retained only for a later retirement attempt.
    /// </summary>
    internal CanvasTextLayout? Layout => _layout;

    /// <summary>True when an adopted layout exists and no failed cleanup has made it non-paintable.</summary>
    internal bool HasPaintableLayout => _layout is not null && !_cleanupPending;

    /// <summary>Transfers a staged layout; the caller disposes any rejected candidate.</summary>
    internal bool TryAdopt(
        ref CanvasTextLayout? candidate,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
            return false;
        NativeResourceRetirementRecord? candidateRecord = Volatile.Read(ref _candidateRecord);
        if (candidateRecord is null)
            return false;
        if (ReferenceEquals(_layout, candidate))
        {
            candidateRecord.SetTemporary(0, null);
            candidate = null;
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_host.IsDisposed, _host);
        if (!_host.IsGraphDetached)
            return false;
        // Do not sever the old layout's owner before successful cleanup. On a
        // failure the caller still owns its candidate and this box retains only
        // the old, non-paintable cleanup slot for a later retirement attempt.
        Release();
        _layout = candidate;
        _retirementRecord = candidateRecord;
        candidate = null;
        return true;
    }

    internal bool TryBeginCandidate(NativeResourceRetirementRecord retirementRecord)
    {
        ArgumentNullException.ThrowIfNull(retirementRecord);
        if (_host.IsDisposed || Interlocked.CompareExchange(
                ref _candidateRecord, retirementRecord, null) is not null)
            return false;
        if (!_host.IsDisposed)
            return true;
        Interlocked.CompareExchange(ref _candidateRecord, null, retirementRecord);
        return false;
    }

    internal NativeResourceRetirementRecord? CandidateRecord => Volatile.Read(ref _candidateRecord);

    internal void EndCandidate(NativeResourceRetirementRecord retirementRecord) =>
        Interlocked.CompareExchange(ref _candidateRecord, null, retirementRecord);

    /// <summary>
    /// Reversibly disposes the adopted layout. A failed Dispose keeps the layout
    /// as a non-paintable cleanup slot; a successful one publishes its record.
    /// </summary>
    internal void Release()
    {
        BlockBoxCleanupScope cleanup = default;
        _cleanupPending = true;
        cleanup.TryDisposeAndClear(
            ref _layout,
            _retirementRecord?.Disposer);
        _cleanupPending = _layout is not null;
        if (!_cleanupPending)
            CompleteRetirement();
        cleanup.ThrowIfAny("disposing detached vector scene fallback");
    }

    /// <summary>
    /// The host's terminal step: disposes the layout into
    /// <paramref name="cleanup"/>, transfers a retained layout to its reserved
    /// record, and sets <paramref name="hostDisposed"/> after the slot leaves
    /// this owner and before the record is published.
    /// </summary>
    internal void RetireWithHost(ref BlockBoxCleanupScope cleanup, ref bool hostDisposed)
    {
        _cleanupPending = true;
        cleanup.TryDisposeAndClear(
            ref _layout,
            _retirementRecord?.Disposer);
        _cleanupPending = _layout is not null;
        if (_layout is { } retainedFallback)
        {
            NativeResourceRetirementRecord? retirement = _retirementRecord;
            if (retirement is not null)
            {
                // Transfer the already-reserved native slot before releasing
                // this terminal owner. No queue node or callback is allocated.
                retirement.SetTemporary(0, retainedFallback);
                _layout = null;
                _retirementRecord = null;
                hostDisposed = true;
                retirement.Publish();
            }
            else
            {
                // Every adopted fallback is paired with a preallocated record.
                // Retain the field if that invariant is ever violated rather
                // than silently losing the only remaining native owner.
                hostDisposed = true;
            }
        }
        else
        {
            NativeResourceRetirementRecord? retirement = _retirementRecord;
            _retirementRecord = null;
            hostDisposed = true;
            if (retirement is not null)
            {
                retirement.SetTemporary(0, null);
                retirement.Publish();
            }
        }
    }

    private void CompleteRetirement()
    {
        NativeResourceRetirementRecord? retirement = _retirementRecord;
        if (retirement is null)
            return;

        retirement.SetTemporary(0, null);
        retirement.Publish();
        _retirementRecord = null;
    }
}
