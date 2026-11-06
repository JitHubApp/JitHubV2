using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JitHub.Markdown;

/// <summary>
/// App-scoped ledger of Markdown renderer borrowers. Markdown hosts borrow the
/// process-wide engine, highlighter, performance session and SVG worker; the
/// window close path uses this ledger to wait for every borrowed renderer to
/// finish retiring before those shared providers shut down.
/// </summary>
public sealed class MarkdownRendererLifetimeRegistry
{
    private const int PruneInterval = 64;

    private readonly object _gate = new();
    private readonly List<WeakReference<IMarkdownRendererShutdownParticipant>> _participants = [];
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _rendererDisposalCompleted = CreateCompletedSource();
    private int _activeRendererCount;
    private int _registrationsSincePrune;
    private bool _applicationShutdownRequested;

    public MarkdownRendererLifetimeRegistry()
        : this(TimeProvider.System)
    {
    }

    internal MarkdownRendererLifetimeRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Gets the number of renderers that have not finished retiring.</summary>
    public int ActiveRendererCount
    {
        get
        {
            lock (_gate)
                return _activeRendererCount;
        }
    }

    /// <summary>
    /// Tracks a host weakly so application shutdown can retire its renderer.
    /// </summary>
    /// <param name="participant">The host to retire during application shutdown.</param>
    /// <returns><see langword="true"/> when application shutdown has already begun.</returns>
    public bool Register(IMarkdownRendererShutdownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_gate)
        {
            if (++_registrationsSincePrune >= PruneInterval)
            {
                for (int index = _participants.Count - 1; index >= 0; index--)
                {
                    if (!_participants[index].TryGetTarget(out _))
                        _participants.RemoveAt(index);
                }

                _registrationsSincePrune = 0;
            }

            _participants.Add(new WeakReference<IMarkdownRendererShutdownParticipant>(participant));
            return _applicationShutdownRequested;
        }
    }

    /// <summary>Records a renderer that now borrows the shared providers.</summary>
    public void RecordRendererCreated()
    {
        lock (_gate)
        {
            if (_activeRendererCount == 0)
                _rendererDisposalCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _activeRendererCount++;
        }
    }

    /// <summary>Records a renderer whose retired resources have fully drained.</summary>
    public void RecordRendererDisposed()
    {
        lock (_gate)
        {
            if (_activeRendererCount == 0)
                return;

            _activeRendererCount--;
            if (_activeRendererCount == 0)
                _rendererDisposalCompleted.TrySetResult();
        }
    }

    /// <summary>Waits until every tracked renderer has finished retiring.</summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <returns><see langword="true"/> when no renderer remained active within <paramref name="timeout"/>.</returns>
    public async Task<bool> WaitForRendererDisposalAsync(TimeSpan timeout)
    {
        Task waitForDisposal;
        lock (_gate)
        {
            if (_activeRendererCount == 0)
                return true;

            waitForDisposal = _rendererDisposalCompleted.Task;
        }

        try
        {
            await waitForDisposal.WaitAsync(timeout, _timeProvider);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks application shutdown as begun and retires every live host's renderer.
    /// </summary>
    /// <returns>The number of renderers whose tracking was released by this call.</returns>
    /// <exception cref="AggregateException">One or more hosts could not release their renderer.</exception>
    public int PrepareAllForApplicationShutdown()
    {
        List<IMarkdownRendererShutdownParticipant> participants = [];
        lock (_gate)
        {
            _applicationShutdownRequested = true;
            for (int index = _participants.Count - 1; index >= 0; index--)
            {
                if (_participants[index].TryGetTarget(out IMarkdownRendererShutdownParticipant? participant))
                    participants.Add(participant);
                else
                    _participants.RemoveAt(index);
            }
        }

        int rendererCountBefore = ActiveRendererCount;
        List<Exception>? failures = null;
        foreach (IMarkdownRendererShutdownParticipant participant in participants)
        {
            try
            {
                participant.PrepareForApplicationShutdown();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more Markdown viewers could not release their renderer during application shutdown.",
                failures);
        }

        return Math.Max(0, rendererCountBefore - ActiveRendererCount);
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
