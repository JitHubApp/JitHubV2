using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// Prepares one visible deferred scene off the UI thread and hands the
/// immutable result to its plan's UI-thread publication. The pipeline enters
/// shared scene admission, restores or renders the result, computes any
/// diagnostics change off the plan gate, then queues a cancelable dispatcher
/// envelope and waits for its acknowledgement before releasing admission.
/// Active-work bookkeeping and the commit itself stay with the plan.
/// </summary>
internal sealed class DeferredScenePublisher
{
    private readonly object _gate;
    private readonly IDeferredScenePublicationHost _host;
    private readonly IScenePublicationTarget _target;
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly object _documentOwner;
    private readonly DeferredSceneDiagnostics _diagnosticSet;

    /// <param name="gate">
    /// The owning plan's monitor. Publication currency and the diagnostics
    /// basis are read together with the plan's active work under it.
    /// </param>
    /// <param name="host">The owning plan's active-work checks and completion path.</param>
    /// <param name="target">Receives the queued publication on the UI thread.</param>
    /// <param name="session">Session that admits scene preparation.</param>
    /// <param name="documentOwner">Document owner whose admission slot producers use.</param>
    /// <param name="diagnosticSet">The plan's diagnostics; read under <paramref name="gate"/>.</param>
    internal DeferredScenePublisher(
        object gate,
        IDeferredScenePublicationHost host,
        IScenePublicationTarget target,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        DeferredSceneDiagnostics diagnosticSet)
    {
        _gate = gate;
        _host = host;
        _target = target;
        _session = session;
        _documentOwner = documentOwner;
        _diagnosticSet = diagnosticSet;
    }

    internal async Task PrepareAndPublishAsync(
        string marker,
        DeferredSceneWork work,
        ActiveSceneWork activeWork,
        CancellationToken generationToken,
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onRetryVisibleBand)
    {
        var attempt = new PublicationAttempt(marker, activeWork, generationToken);
        try
        {
            if (!await TryPrepareResultAsync(attempt, work).ConfigureAwait(false))
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            // Scene-preparation time ends when the immutable result is ready.
            // Its shared admission slot remains held below until the queued
            // layout publication commits or cancellation wins while queued.
            MarkdownPerformanceSession.CompleteScenePreparationTiming(attempt.SceneLease);

            if (attempt.Result is null || attempt.Descriptor is null || generationToken.IsCancellationRequested || activeWork.Token.IsCancellationRequested)
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            (bool publicationIsCurrent, PreparedDiagnosticPublication? diagnosticPublication) =
                await PrepareDiagnosticPublicationAsync(attempt).ConfigureAwait(false);
            if (!publicationIsCurrent)
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            if (!await TryQueueAndAwaitPublicationAsync(
                    attempt,
                    diagnosticPublication,
                    enqueuePublication,
                    onPublished,
                    onRetryVisibleBand).ConfigureAwait(false))
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
            }
        }
        catch (OperationCanceledException)
        {
            _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
        }
        catch
        {
            _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
            throw;
        }
        finally
        {
            await attempt.ReleaseAsync(_diagnosticSet).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enters scene admission and restores or renders the result. Returns
    /// false when the producer was canceled or left the visible band; a
    /// provider failure becomes the readable source fallback instead.
    /// </summary>
    private async Task<bool> TryPrepareResultAsync(PublicationAttempt attempt, DeferredSceneWork work)
    {
        string marker = attempt.Marker;
        ActiveSceneWork activeWork = attempt.ActiveWork;
        try
        {
            activeWork.Token.ThrowIfCancellationRequested();
            if (!_host.IsVisible(marker, activeWork))
                throw new OperationCanceledException(activeWork.Token);
            attempt.SceneLease = await _session
                .EnterScenePreparationAsync(_documentOwner, activeWork.Token)
                .ConfigureAwait(false);
            CancellationToken workToken = attempt.SceneLease.CancellationToken;
            activeWork.Token.ThrowIfCancellationRequested();
            workToken.ThrowIfCancellationRequested();
            if (!_host.IsVisible(marker, activeWork))
                throw new OperationCanceledException(activeWork.Token);
            if (!_host.TryRestoreOnWorker(marker, workToken, out attempt.Result, out attempt.Descriptor))
            {
                attempt.Result = await work.RenderAsync(workToken).ConfigureAwait(false);
                workToken.ThrowIfCancellationRequested();
                attempt.Descriptor = MarkdownCompletedSceneDescriptor.Create(attempt.Result, workToken);
            }
            workToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // A worker/provider failure never escapes into layout or the UI
            // thread. Preserve the original source as accessible code fallback
            // and retain the exact syntax range for the diagnostic.
            attempt.Result = work.CreateFailureResult("MDP0001", "Deferred native content could not be prepared.");
            attempt.Descriptor = MarkdownCompletedSceneDescriptor.Create(attempt.Result);
        }

        return true;
    }

    /// <summary>
    /// Checks that the prepared result is still current and, when its
    /// diagnostics contribution changed, computes the replacement snapshot off
    /// the plan gate while holding the publication semaphore.
    /// </summary>
    private async Task<(bool PublicationIsCurrent, PreparedDiagnosticPublication? Publication)>
        PrepareDiagnosticPublicationAsync(PublicationAttempt attempt)
    {
        string marker = attempt.Marker;
        ActiveSceneWork activeWork = attempt.ActiveWork;
        CancellationToken generationToken = attempt.GenerationToken;
        ImmutableArray<MarkdownDiagnostic> newContribution = attempt.Descriptor!.DiagnosticContribution;
        bool publicationIsCurrent;
        bool diagnosticsChanged;
        lock (_gate)
        {
            publicationIsCurrent = _host.IsPublicationCurrentNoLock(marker, activeWork, generationToken);
            diagnosticsChanged = publicationIsCurrent && !_diagnosticSet.ContributionEquals(marker, newContribution);
        }

        if (!publicationIsCurrent)
            return (false, null);

        PreparedDiagnosticPublication? diagnosticPublication = null;
        if (diagnosticsChanged)
        {
            await _diagnosticSet.WaitPublicationAsync(activeWork.Token).ConfigureAwait(false);
            attempt.DiagnosticGateHeld = true;

            DeferredSceneDiagnosticBasis basis = default;
            lock (_gate)
            {
                publicationIsCurrent = _host.IsPublicationCurrentNoLock(marker, activeWork, generationToken);
                if (publicationIsCurrent)
                {
                    if (_diagnosticSet.ContributionEquals(marker, newContribution))
                    {
                        diagnosticsChanged = false;
                    }
                    else
                    {
                        basis = _diagnosticSet.BeginPublication(marker);
                    }
                }
            }

            if (!publicationIsCurrent)
                return (false, null);

            if (diagnosticsChanged)
            {
                ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> updatedContributions = newContribution.IsEmpty
                    ? basis.Contributions.Remove(marker)
                    : basis.Contributions.SetItem(marker, newContribution);
                ImmutableList<MarkdownDiagnostic> updatedDiagnostics = DeferredSceneDiagnostics.ReplaceContribution(
                    basis.Diagnostics,
                    basis.PreviousContribution,
                    newContribution);
                activeWork.Token.ThrowIfCancellationRequested();
                diagnosticPublication = new PreparedDiagnosticPublication(
                    basis.Version,
                    updatedContributions,
                    updatedDiagnostics);
            }
            else
            {
                _diagnosticSet.ReleasePublication();
                attempt.DiagnosticGateHeld = false;
            }
        }

        return (true, diagnosticPublication);
    }

    /// <summary>
    /// Queues the cancelable dispatcher envelope and waits for the UI thread to
    /// acknowledge it. Returns false when the envelope could not be queued.
    /// </summary>
    private async Task<bool> TryQueueAndAwaitPublicationAsync(
        PublicationAttempt attempt,
        PreparedDiagnosticPublication? diagnosticPublication,
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onRetryVisibleBand)
    {
        ActiveSceneWork activeWork = attempt.ActiveWork;
        CancellationToken generationToken = attempt.GenerationToken;
        activeWork.Token.ThrowIfCancellationRequested();
        CancellationToken publicationToken = attempt.SceneLease?.CancellationToken ?? activeWork.Token;
        var publicationEnvelope = new ScenePublicationEnvelope(
            new QueuedScenePublication(
                _target,
                attempt.Marker,
                activeWork,
                generationToken,
                attempt.Result!,
                attempt.Descriptor!,
                diagnosticPublication,
                onPublished,
                onRetryVisibleBand));
        attempt.Envelope = publicationEnvelope;
        attempt.PublicationCancellation = publicationToken.Register(
            static state => ((ScenePublicationEnvelope)state!).CancelQueued(),
            publicationEnvelope);
        attempt.PublicationCancellationRegistered = true;

        if (!publicationEnvelope.TryMarkQueued())
            return false;

        if (publicationEnvelope.IsQueued)
        {
            bool queued;
            try
            {
                // This bound delegate captures only the clearable envelope.
                // A canceled dispatcher item cannot pin its result,
                // descriptor, plan, control, or callback roots.
                queued = enqueuePublication(publicationEnvelope.Dispatch);
            }
            catch
            {
                await publicationEnvelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
                throw;
            }

            if (!queued)
            {
                await publicationEnvelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
                return false;
            }
        }

        await publicationEnvelope.Completion.ConfigureAwait(false);
        publicationToken.ThrowIfCancellationRequested();
        generationToken.ThrowIfCancellationRequested();
        activeWork.Token.ThrowIfCancellationRequested();
        return true;
    }

    /// <summary>
    /// One producer run's acquired resources. The pipeline stages record each
    /// resource as they acquire it, and <see cref="ReleaseAsync"/> releases
    /// them in a fixed order however the run ends.
    /// </summary>
    private sealed class PublicationAttempt(
        string marker,
        ActiveSceneWork activeWork,
        CancellationToken generationToken)
    {
        internal MarkdownDeferredSceneResult? Result;
        internal MarkdownCompletedSceneDescriptor? Descriptor;
        internal IMarkdownScenePreparationLease? SceneLease;
        internal ScenePublicationEnvelope? Envelope;
        internal CancellationTokenRegistration PublicationCancellation;
        internal bool PublicationCancellationRegistered;
        internal bool DiagnosticGateHeld;

        internal string Marker { get; } = marker;

        internal ActiveSceneWork ActiveWork { get; } = activeWork;

        internal CancellationToken GenerationToken { get; } = generationToken;

        /// <summary>
        /// Waits out a queued or executing envelope, then drops its cancellation
        /// registration, the scene admission lease and the diagnostics
        /// publication semaphore, each even when an earlier step throws.
        /// </summary>
        internal async Task ReleaseAsync(DeferredSceneDiagnostics diagnosticSet)
        {
            try
            {
                if (Envelope is not null)
                    await Envelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (PublicationCancellationRegistered)
                        PublicationCancellation.Dispose();
                }
                finally
                {
                    try
                    {
                        SceneLease?.Dispose();
                    }
                    finally
                    {
                        if (DiagnosticGateHeld)
                            diagnosticSet.ReleasePublication();
                    }
                }
            }
        }
    }
}
