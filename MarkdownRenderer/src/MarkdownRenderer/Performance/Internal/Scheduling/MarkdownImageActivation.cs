using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;

namespace MarkdownRenderer.Performance.Internal.Scheduling;

/// <summary>
/// One queued image activation: its borrowed demand, start/retire delegates,
/// cancellation and promotion registrations. Completion runs exactly once and
/// clears the image delegates. Queue membership (<see cref="Node"/>,
/// <see cref="State"/>) is mutated only under the owning scheduler's gate.
/// </summary>
internal sealed class MarkdownImageActivation
{
    private readonly IMarkdownImageActivationHost _owner;
    private Action? _startAsyncWork;
    private Action? _onNotStarted;
    private CancellationTokenRegistration _cancellationRegistration;
    private IDisposable? _promotionRegistration;
    private int _completed;

    internal MarkdownImageActivation(
        IMarkdownImageActivationHost owner,
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        Demand = demand;
        _startAsyncWork = startAsyncWork;
        _onNotStarted = onNotStarted;
        CancellationToken = cancellationToken;
    }

    internal IMarkdownImageActivationDemand Demand { get; }
    internal Action? PromotionCallback { get; private set; }
    internal LinkedListNode<MarkdownImageActivation>? Node { get; set; }
    internal long ArrivalSequence { get; set; }
    internal MarkdownImageActivationState State { get; set; }
    internal CancellationToken CancellationToken { get; }

    internal void PrepareNode() => Node = new LinkedListNode<MarkdownImageActivation>(this);
    internal void PreparePromotionCallback() => PromotionCallback = OnDemandPromoted;
    private void OnDemandPromoted() => _owner.PromoteQueuedActivation(this);
    internal void SetPromotionRegistration(IDisposable? registration) => _promotionRegistration = registration;
    internal void RegisterCancellation()
    {
        if (CancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = CancellationToken.Register(
                static state =>
                {
                    var activation = (MarkdownImageActivation)state!;
                    activation._owner.CancelQueued(activation);
                },
                this);
        }
    }

    internal void DisposePromotionRegistration() =>
        DisposeRegistrationSafely(Interlocked.Exchange(ref _promotionRegistration, null));

    internal void Run()
    {
        bool started = !CancellationToken.IsCancellationRequested;
        try
        {
            if (started)
                Volatile.Read(ref _startAsyncWork)?.Invoke();
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation start failed: {exception.Message}");
        }
        finally { Complete(started); }
    }

    internal void Complete(bool started)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        DisposePromotionRegistration();
        Interlocked.Exchange(ref _startAsyncWork, null);
        Action? retiredNotStarted = Interlocked.Exchange(ref _onNotStarted, null);
        try { _cancellationRegistration.Unregister(); }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation cancellation cleanup failed: {exception.Message}");
        }
        if (!started)
        {
            try { retiredNotStarted?.Invoke(); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Image activation retirement failed: {exception.Message}");
            }
        }
    }

    internal static void DisposeRegistrationSafely(IDisposable? registration)
    {
        if (registration is null)
            return;
        try { registration.Dispose(); }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation promotion cleanup failed: {exception.Message}");
        }
    }
}
