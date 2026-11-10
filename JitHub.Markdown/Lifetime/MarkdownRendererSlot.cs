using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime.Internal;
using JitHub.Markdown.Runtime;

namespace JitHub.Markdown.Lifetime;

/// <summary>
/// Owns one Markdown host's renderer from creation until its retired resources
/// have drained. A renderer is retired through its public
/// <see cref="IAsyncDisposable.DisposeAsync"/> lifetime API; the slot keeps the
/// borrower tracked in <see cref="MarkdownRendererLifetimeRegistry"/> until the
/// returned drain completes, and refuses to reattach a renderer whose teardown failed.
/// </summary>
/// <typeparam name="TRenderer">The renderer control type.</typeparam>
public sealed class MarkdownRendererSlot<TRenderer>
    where TRenderer : class, IAsyncDisposable
{
    private readonly MarkdownRendererLifetimeRegistry _registry;
    private readonly IMarkdownRendererSlotHost<TRenderer> _host;
    private readonly Action<Task> _observeRetirement;
    private TRenderer? _current;
    private TRenderer? _retirementPending;
    private bool _disposalFaulted;

    /// <param name="registry">The app-scoped renderer ledger.</param>
    /// <param name="host">The host whose renderer this slot owns.</param>
    /// <param name="observeRetirement">Observes the terminal retirement task so no failure goes unobserved.</param>
    public MarkdownRendererSlot(
        MarkdownRendererLifetimeRegistry registry,
        IMarkdownRendererSlotHost<TRenderer> host,
        Action<Task> observeRetirement)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _observeRetirement = observeRetirement ?? throw new ArgumentNullException(nameof(observeRetirement));
    }

    /// <summary>Gets the attached renderer, or <see langword="null"/> when none is attached.</summary>
    public TRenderer? Current => _current;

    /// <summary>Gets whether a retired renderer is still draining its resources.</summary>
    public bool IsRetirementPending => _retirementPending is not null;

    /// <summary>
    /// Gets whether a renderer teardown failed. A failed teardown can latch the
    /// control's disposed state before it released every resource, so the host
    /// must never attach that or any later renderer.
    /// </summary>
    public bool IsDisposalFaulted => _disposalFaulted;

    /// <summary>Takes ownership of a newly created renderer.</summary>
    /// <param name="renderer">The renderer the host just created.</param>
    public void Attach(TRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _current = renderer;
        _registry.RecordRendererCreated();
    }

    /// <summary>
    /// Retires the current renderer. Throws when the renderer could not finish
    /// its synchronous teardown; the drain completes asynchronously.
    /// </summary>
    public void Retire()
    {
        TRenderer? renderer = _current;
        if (renderer is null)
        {
            return;
        }

        // Repeated Unloaded/shutdown paths must join the original drain rather
        // than start a second one or release the same renderer twice.
        if (ReferenceEquals(_retirementPending, renderer))
            return;

        IMarkdownUiDispatcher? retirementDispatcher = _host.Dispatcher;
        _retirementPending = renderer;
        Task retirementCompletion;
        Task? resourceRelease = null;
        try
        {
            retirementCompletion = MarkdownRendererDisposalCommit.Execute(
                detachAndDispose: () =>
                {
                    _host.DetachRenderer(renderer);
                    // Throws when the view could not finish teardown; the task
                    // completes when the resources it released are freed.
                    resourceRelease = renderer.DisposeAsync().AsTask();
                },
                disposalCompletedSuccessfully: () => resourceRelease is not null,
                retirementBegan: () => _current = null,
                drainRetiredResources: () => resourceRelease ??
                    throw new InvalidOperationException("The Markdown renderer returned no resource-release task."),
                releaseTracking: () => ReleaseTrackingOnDispatcherAsync(renderer, retirementDispatcher));
        }
        catch
        {
            // If the view did not finish teardown, the existing retry/failure
            // path still owns this renderer. If retirement had already begun,
            // retain the pending latch and active count.
            if (resourceRelease is null)
                _retirementPending = null;
            throw;
        }

        _observeRetirement(ObserveRetirementAsync(renderer, retirementCompletion, retirementDispatcher));
    }

    /// <summary>
    /// Retires the current renderer during an unload or viewport change. A
    /// teardown failure latches the faulted state and is reported to the host.
    /// </summary>
    /// <returns><see langword="false"/> when teardown failed.</returns>
    public bool TryRetireFromLifecycle()
    {
        try
        {
            Retire();
            return true;
        }
        catch (Exception exception)
        {
            _disposalFaulted = true;
            _host.OnRendererTeardownFaulted(exception);
            Trace.TraceError("Markdown renderer disposal failed during a lifecycle transition: {0}", exception);
            return false;
        }
    }

    private Task ReleaseTrackingOnDispatcherAsync(
        TRenderer renderer,
        IMarkdownUiDispatcher? dispatcher)
    {
        if (dispatcher is null)
        {
            return Task.FromException(new InvalidOperationException(
                "The Markdown viewer dispatcher was unavailable before renderer retirement completed."));
        }

        if (dispatcher.HasThreadAccess)
        {
            ReleaseTracking(renderer);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    ReleaseTracking(renderer);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException(
                "The Markdown viewer dispatcher rejected renderer-retirement completion."));
        }

        return completion.Task;
    }

    private void ReleaseTracking(TRenderer renderer)
    {
        if (_current is not null ||
            !ReferenceEquals(_retirementPending, renderer))
        {
            throw new InvalidOperationException(
                "The Markdown renderer changed before its snapshot retirement completed.");
        }

        _current = null;
        _retirementPending = null;
        _registry.RecordRendererDisposed();

        // A fast unload/reload or viewport ownership change can request a new
        // renderer while the old snapshot drains. The host recreates only after
        // its ownership has been fully released.
        _host.OnRendererReleased();
    }

    private async Task ObserveRetirementAsync(
        TRenderer renderer,
        Task retirementCompletion,
        IMarkdownUiDispatcher? dispatcher)
    {
        try
        {
            await retirementCompletion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                Trace.TraceError(
                    "Markdown renderer snapshot retirement failed: {0}; HRESULT=0x{1:X8}",
                    exception.GetType().Name,
                    exception.HResult);

                void ReportFailure()
                {
                    try
                    {
                        if (!ReferenceEquals(_retirementPending, renderer))
                            return;

                        _disposalFaulted = true;
                        _host.OnRendererTeardownFaulted(new InvalidOperationException(
                            $"Snapshot retirement failed with {exception.GetType().Name} " +
                            $"(HRESULT 0x{exception.HResult:X8})."));
                    }
                    catch (Exception reportingException)
                    {
                        Trace.TraceError(
                            "Markdown renderer retirement failure reporting failed: {0}; HRESULT=0x{1:X8}",
                            reportingException.GetType().Name,
                            reportingException.HResult);
                    }
                }

                if (dispatcher is { HasThreadAccess: true })
                {
                    ReportFailure();
                }
                else if (dispatcher is null || !dispatcher.TryEnqueue(ReportFailure))
                {
                    // Keep the active-renderer count latched. The close path will
                    // therefore skip shared-provider teardown rather than treating
                    // an unreported or rejected completion as success.
                    Trace.TraceError("Markdown viewer dispatcher rejected retirement-failure reporting.");
                }
            }
            catch (Exception observerException)
            {
                // This observer is intentionally terminal: even diagnostics
                // failure must not surface as an unobserved task exception.
                try
                {
                    Trace.TraceError(
                        "Markdown renderer retirement observer failed: {0}; HRESULT=0x{1:X8}",
                        observerException.GetType().Name,
                        observerException.HResult);
                }
                catch (Exception)
                {
                    // Trace listeners are the last diagnostic channel; a failing
                    // listener has nowhere left to report and must not escape.
                }
            }
        }
    }
}
