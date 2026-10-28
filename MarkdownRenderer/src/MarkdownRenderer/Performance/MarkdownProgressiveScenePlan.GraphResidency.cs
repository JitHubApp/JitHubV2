using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

internal sealed partial class MarkdownProgressiveScenePlan
{
    private readonly Dictionary<VectorSceneBox, GraphRebindWork> _graphRebinds =
        new(ReferenceEqualityComparer.Instance);

    internal bool CanDetachGraph(VectorSceneBox box)
    {
        lock (_gate)
            return TryGetGraphWorkNoLock(box, out _, out _, out _);
    }

    private bool TryGetGraphWorkNoLock(
        VectorSceneBox box, out string marker, out DeferredSceneWork? work,
        out MarkdownCompletedSceneDescriptor? descriptor)
    {
        marker = string.Empty;
        work = null;
        descriptor = null;
        if (_disposed || _session.IsDisposed || box.GraphRebindFailed ||
            box.DeferredSceneBinding is not { SceneOrdinal: 0 } binding ||
            !_works.TryGetValue(binding.Marker, out work) || work.IsInline ||
            box.SourceRange != work.Node.SourceSpan ||
            !_completed.TryGetValue(binding.Marker, out descriptor) ||
            descriptor.SingleSceneProjection is null || binding.ProjectionIdentity is null ||
            !binding.ProjectionIdentity.Equals(descriptor.ProjectionIdentity))
            return false;
        marker = binding.Marker;
        return true;
    }

    // Called by the active lazy-layout request, outside its layout queue slot.
    // The snapshot is borrowed only for short synchronous read/swap leases;
    // neither provider work nor an await runs under its mutation lock.
    internal async Task<bool> RebindBandAsync(
        LayoutSnapshot snapshot, LazyLayoutBand band, CancellationToken cancellationToken)
    {
        GraphRebindCandidate[] pending;
        long revision;
        MarkdownVectorSceneResidencyPolicy policy;
        while (!snapshot.TryBeginPaint())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.GeometryRecoveryRequired || snapshot.IsRetiringOrDisposed)
                return false;
            await Task.Delay(4, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (snapshot.GeometryRecoveryRequired)
                return false;
            if (snapshot.VectorSceneResidencyPolicy is not MarkdownVectorSceneResidencyPolicy currentPolicy)
                return false;
            policy = currentPolicy;
            pending = new GraphRebindCandidate[policy.PendingGraphRebinds.Count];
            for (int index = 0; index < pending.Length; index++)
            {
                VectorSceneBox box = policy.PendingGraphRebinds[index];
                pending[index] = new GraphRebindCandidate(box, VectorScenePreparation.Capture(box));
            }
            revision = snapshot.LayoutRevision;
        }
        finally
        {
            snapshot.EndPaint();
        }

        // Do not allocate a document-sized task storm. Admission is shared with
        // initial scene preparation, and each bounded batch fully drains before
        // the next batch can enter the global queue.
        const int MaximumBatchSize = 32;
        for (int first = 0; first < pending.Length; first += MaximumBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = new Task[Math.Min(MaximumBatchSize, pending.Length - first)];
            for (int index = 0; index < tasks.Length; index++)
                tasks[index] = RebindOwnerAsync(pending[first + index], snapshot, policy, revision, band, cancellationToken);
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        // True means at least all copied pending owners reached an attached or
        // terminal state. A retired/ineligible/stale producer is not progress;
        // the caller must not spin EnsureMeasuredBand on the same graph miss.
        while (!snapshot.TryBeginPaint())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.GeometryRecoveryRequired || snapshot.IsRetiringOrDisposed)
                return false;
            await Task.Delay(4, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (snapshot.GeometryRecoveryRequired)
                return false;
            foreach (GraphRebindCandidate candidate in pending)
            {
                VectorSceneBox box = candidate.Box;
                if (policy.CanCommitGraphRebind(box, band) && box.IsGraphDetached && !box.GraphRebindFailed)
                    return false;
            }
            return true;
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private async Task RebindOwnerAsync(
        GraphRebindCandidate candidate, LayoutSnapshot snapshot, MarkdownVectorSceneResidencyPolicy policy, long revision,
        LazyLayoutBand band, CancellationToken cancellationToken)
    {
        VectorSceneBox box = candidate.Box;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphRebindWork request;
            bool superseded;
            lock (_gate)
            {
                if (!TryGetGraphWorkNoLock(box, out string marker, out DeferredSceneWork? work,
                        out MarkdownCompletedSceneDescriptor? descriptor))
                    return;
                if (_graphRebinds.TryGetValue(box, out request!))
                {
                    superseded = request.Work.Generation != _generation ||
                        request.Work.Token.IsCancellationRequested ||
                        !ReferenceEquals(request.Snapshot, snapshot) || request.Revision != revision ||
                        request.Band.Top != band.Top || request.Band.Bottom != band.Bottom;
                    if (superseded)
                        request.Work.Cancel();
                }
                else
                {
                    var active = new ActiveSceneWork(_generation,
                        CancellationTokenSource.CreateLinkedTokenSource(_generationCancellation.Token, cancellationToken));
                    request = new GraphRebindWork(active, snapshot, policy, candidate.Preparation, revision, band);
                    _graphRebinds.Add(box, request);
                    active.Task = Task.Run(() => PrepareGraphRebindAsync(
                        box, marker, work!, descriptor!, request), CancellationToken.None);
                    _retirements.Track(active.Task);
                    superseded = false;
                }
            }
            try
            {
                await request.Work.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                // Cancellation callbacks run asynchronously. Generation
                // invalidation must win even when a fast producer observes
                // the stale generation before its linked token is signaled.
                if (request.WasCanceled || request.Work.Token.IsCancellationRequested ||
                    request.Work.Generation != Volatile.Read(ref _generation))
                    throw new OperationCanceledException(request.Work.Token);
            }
            catch (OperationCanceledException) when (superseded && !cancellationToken.IsCancellationRequested)
            {
                // Drain an older generation/band before giving this owner a
                // new producer; the canceled work cannot be counted as ready.
            }
            if (!superseded)
                return;
        }
    }

    private async Task PrepareGraphRebindAsync(
        VectorSceneBox box, string marker, DeferredSceneWork work,
        MarkdownCompletedSceneDescriptor expected, GraphRebindWork request)
    {
        IMarkdownScenePreparationLease? sceneLease = null;
        VectorSceneDrawing? replacement = null;
        bool diagnosticGateHeld = false;
        try
        {
            CancellationToken token = request.Work.Token;
            MarkdownDeferredSceneResult? result = null;
            MarkdownCompletedSceneDescriptor? descriptor = null;
            long estimate = 0;
            bool failed = false;
            try
            {
                sceneLease = await _session.EnterScenePreparationAsync(_documentOwner, token).ConfigureAwait(false);
                token = sceneLease.CancellationToken;
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (TryGetCompletedNoLock(marker, consumeHandoff: false, out result))
                        descriptor = expected;
                }
                if (result is null && !TryRestoreOnWorker(marker, token, out result, out descriptor))
                {
                    result = await RenderAsync(work, token).ConfigureAwait(false);
                    descriptor = MarkdownCompletedSceneDescriptor.Create(result, token);
                }
                token.ThrowIfCancellationRequested();
                // A custom provider may be nondeterministic. Never replace the
                // retained UIA/selection/geometry projection with changed data.
                if (descriptor?.SingleSceneProjection != expected.SingleSceneProjection ||
                    result?.Fragment.Items.Count != 1 || result.Fragment.Items[0].VectorScene is not { } scene)
                    throw new InvalidOperationException("The refilled scene changed its immutable projection.");
                // Own the drawing before a native factory can fail after a
                // partial prefix. The outer finally retires that prefix even
                // when preparation's first cleanup attempt also fails.
                replacement = request.Preparation.Create(scene, token, deferNativeResources: true);
                // Capacity denial is a typed readable fallback, not a provider
                // failure and never an MDP0002 diagnostic. The incoming band
                // has already released outgoing pins; a later refund can wake
                // another attempt through the current viewport reconciliation.
                replacement.TryPrepareNativeResources(token, out _);
                // This command walk runs before taking the snapshot swap lease.
                estimate = request.Policy.GetPreparedResourceEstimateForRebind(scene, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failed = true;
            }
            if (!failed && replacement is { IsPrepared: false })
            {
                // Native descriptor faults are not capacity denial or provider
                // projection failures. Prewarm off both commit gates and let a
                // real descriptor/device fault reach the owning control's
                // existing recovery path without publishing MDP0002.
                if (!request.Preparation.PrepareCapacityFallback(token))
                    throw new InvalidOperationException("A denied replacement requires bounded fallback resources.");
                token.ThrowIfCancellationRequested();
            }
            MarkdownPerformanceSession.CompleteScenePreparationTiming(sceneLease);
            token.ThrowIfCancellationRequested();
            await _diagnosticPublicationGate.WaitAsync(token).ConfigureAwait(false);
            diagnosticGateHeld = true;
            while (!request.Snapshot.TryBeginPaint())
            {
                token.ThrowIfCancellationRequested();
                if (request.Snapshot.GeometryRecoveryRequired || request.Snapshot.IsRetiringOrDisposed)
                    return;
                await Task.Delay(4, token).ConfigureAwait(false);
            }
            try
            {
                if (request.Snapshot.GeometryRecoveryRequired)
                    return;
                MarkdownVectorSceneResidencyPolicy? policy = request.Snapshot.VectorSceneResidencyPolicy
                    as MarkdownVectorSceneResidencyPolicy;
                if (!ReferenceEquals(policy, request.Policy) || request.Snapshot.LayoutRevision != request.Revision ||
                    !policy.CanCommitGraphRebind(box, request.Band) || !box.IsGraphDetached)
                    return;
                lock (_gate)
                {
                    if (_disposed || _session.IsDisposed || request.Work.Generation != _generation ||
                        token.IsCancellationRequested || request.Work.Token.IsCancellationRequested ||
                        !_graphRebinds.TryGetValue(box, out GraphRebindWork? current) ||
                        !ReferenceEquals(current, request))
                        return;
                    ImmutableArray<MarkdownDiagnostic> contribution;
                    if (failed)
                    {
                        contribution = [new MarkdownDiagnostic("MDP0002", MarkdownDiagnosticSeverity.Error,
                            "Deferred native content could not be restored without changing its projection.", work.Node.SourceSpan)];
                        PublishGraphRebindDiagnosticsNoLock(marker, contribution);
                        policy.RecordGraphStatusChange(box);
                        try
                        {
                            box.PrepareDetachedFallback(token, MarkdownDetachedSceneFallback.CreateFailed,
                                replaceExisting: true);
                        }
                        finally
                        {
                            // Null/failed/canceled native fallback construction
                            // keeps the last readable source layout and the
                            // already-published typed diagnostic intact.
                            box.MarkGraphRebindFailed();
                        }
                    }
                    else
                    {
                        if (!policy.AdoptGraphRebind(box, request.Band, replacement!, estimate))
                            return;
                        replacement = null; // ownership transferred to the box/policy
                        descriptor ??= expected;
                        if (descriptor.DetachMetadataOwner() is { } metadata)
                            _completedCache.RetainMetadata(this, marker, metadata, descriptor.MetadataWeightBytes);
                        _completed[marker] = descriptor;
                        _completedCache.Retain(this, marker, result!, descriptor.EstimatedGraphWeightBytes);
                        contribution = descriptor.DiagnosticContribution;
                    }
                    PublishGraphRebindDiagnosticsNoLock(marker, contribution);
                }
            }
            finally
            {
                request.Snapshot.EndPaint();
            }
        }
        catch (OperationCanceledException) when (request.Work.Token.IsCancellationRequested ||
            sceneLease?.CancellationToken.IsCancellationRequested == true)
        {
            // Actual-use retirement waits for the producer and its cleanup,
            // not for a canceled caller result. Preserve cancellation for the
            // awaiting request without poisoning generation/disposal drains.
            request.WasCanceled = true;
        }
        finally
        {
            try
            {
                replacement?.Dispose();
            }
            finally
            {
                try
                {
                    sceneLease?.Dispose();
                }
                finally
                {
                    if (diagnosticGateHeld)
                        _diagnosticPublicationGate.Release();
                    _retirements.Track(request.Work.Dispose(request.Work.Task));
                    lock (_gate)
                    {
                        if (_graphRebinds.TryGetValue(box, out GraphRebindWork? current) && ReferenceEquals(current, request))
                            _graphRebinds.Remove(box);
                    }
                }
            }
        }
    }

    private void PublishGraphRebindDiagnosticsNoLock(string marker, ImmutableArray<MarkdownDiagnostic> contribution)
    {
        if (DiagnosticContributionEqualsNoLock(marker, contribution))
            return;
        _diagnosticContributions.TryGetValue(marker, out ImmutableArray<MarkdownDiagnostic> previous);
        _diagnostics = ReplaceDiagnosticContribution(_diagnostics,
            previous.IsDefault ? ImmutableArray<MarkdownDiagnostic>.Empty : previous, contribution);
        _diagnosticContributions = contribution.IsEmpty
            ? _diagnosticContributions.Remove(marker)
            : _diagnosticContributions.SetItem(marker, contribution);
        _diagnosticsPublicationVersion++;
    }

    private sealed class GraphRebindWork(
        ActiveSceneWork work, LayoutSnapshot snapshot, MarkdownVectorSceneResidencyPolicy policy,
        VectorScenePreparation preparation, long revision, LazyLayoutBand band)
    {
        internal readonly ActiveSceneWork Work = work;
        internal readonly LayoutSnapshot Snapshot = snapshot;
        internal readonly MarkdownVectorSceneResidencyPolicy Policy = policy;
        internal readonly VectorScenePreparation Preparation = preparation;
        internal readonly long Revision = revision;
        internal readonly LazyLayoutBand Band = band;
        internal volatile bool WasCanceled;
    }

    private readonly struct GraphRebindCandidate(VectorSceneBox box, VectorScenePreparation preparation)
    {
        internal readonly VectorSceneBox Box = box;
        internal readonly VectorScenePreparation Preparation = preparation;
    }
}
