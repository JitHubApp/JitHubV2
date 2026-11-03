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
        MarkdownDeferredSceneResult? result = null;
        MarkdownCompletedSceneDescriptor? descriptor = null;
        IMarkdownScenePreparationLease? sceneLease = null;
        ScenePublicationEnvelope? publicationEnvelope = null;
        CancellationTokenRegistration publicationCancellation = default;
        bool publicationCancellationRegistered = false;
        bool diagnosticGateHeld = false;
        try
        {
            try
            {
                activeWork.Token.ThrowIfCancellationRequested();
                if (!_host.IsVisible(marker, activeWork))
                    throw new OperationCanceledException(activeWork.Token);
                sceneLease = await _session
                    .EnterScenePreparationAsync(_documentOwner, activeWork.Token)
                    .ConfigureAwait(false);
                CancellationToken workToken = sceneLease.CancellationToken;
                activeWork.Token.ThrowIfCancellationRequested();
                workToken.ThrowIfCancellationRequested();
                if (!_host.IsVisible(marker, activeWork))
                    throw new OperationCanceledException(activeWork.Token);
                if (!_host.TryRestoreOnWorker(marker, workToken, out result, out descriptor))
                {
                    result = await work.RenderAsync(workToken).ConfigureAwait(false);
                    workToken.ThrowIfCancellationRequested();
                    descriptor = MarkdownCompletedSceneDescriptor.Create(result, workToken);
                }
                workToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }
            catch (Exception)
            {
                // A worker/provider failure never escapes into layout or the UI
                // thread. Preserve the original source as accessible code fallback
                // and retain the exact syntax range for the diagnostic.
                result = work.CreateFailureResult("MDP0001", "Deferred native content could not be prepared.");
                descriptor = MarkdownCompletedSceneDescriptor.Create(result);
            }

            // Scene-preparation time ends when the immutable result is ready.
            // Its shared admission slot remains held below until the queued
            // layout publication commits or cancellation wins while queued.
            MarkdownPerformanceSession.CompleteScenePreparationTiming(sceneLease);

            if (result is null || descriptor is null || generationToken.IsCancellationRequested || activeWork.Token.IsCancellationRequested)
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            ImmutableArray<MarkdownDiagnostic> newContribution = descriptor.DiagnosticContribution;
            bool publicationIsCurrent;
            bool diagnosticsChanged;
            lock (_gate)
            {
                publicationIsCurrent = _host.IsPublicationCurrentNoLock(marker, activeWork, generationToken);
                diagnosticsChanged = publicationIsCurrent && !_diagnosticSet.ContributionEquals(marker, newContribution);
            }

            if (!publicationIsCurrent)
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            PreparedDiagnosticPublication? diagnosticPublication = null;
            if (diagnosticsChanged)
            {
                await _diagnosticSet.WaitPublicationAsync(activeWork.Token).ConfigureAwait(false);
                diagnosticGateHeld = true;

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
                {
                    _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                    return;
                }

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
                    diagnosticGateHeld = false;
                }
            }

            activeWork.Token.ThrowIfCancellationRequested();
            CancellationToken publicationToken = sceneLease?.CancellationToken ?? activeWork.Token;
            publicationEnvelope = new ScenePublicationEnvelope(
                new QueuedScenePublication(
                    _target,
                    marker,
                    activeWork,
                    generationToken,
                    result,
                    descriptor,
                    diagnosticPublication,
                    onPublished,
                    onRetryVisibleBand));
            publicationCancellation = publicationToken.Register(
                static state => ((ScenePublicationEnvelope)state!).CancelQueued(),
                publicationEnvelope);
            publicationCancellationRegistered = true;

            if (!publicationEnvelope.TryMarkQueued())
            {
                _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

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
                    _host.CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                    return;
                }
            }

            await publicationEnvelope.Completion.ConfigureAwait(false);
            publicationToken.ThrowIfCancellationRequested();
            generationToken.ThrowIfCancellationRequested();
            activeWork.Token.ThrowIfCancellationRequested();
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
            try
            {
                if (publicationEnvelope is not null)
                    await publicationEnvelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (publicationCancellationRegistered)
                        publicationCancellation.Dispose();
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
                            _diagnosticSet.ReleasePublication();
                    }
                }
            }
        }
    }
}
