using System;
using System.Threading;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Recovery;

/// <summary>
/// The renderer's 100 ms recovery clock. Deferred work whose UI handoff was
/// rejected or withheld (image relayout, layout commit publication, lazy
/// layout, focus geometry) keeps the clock running until it drains; each
/// tick asks the host to retry that work.
/// </summary>
/// <remarks>
/// The UI timer and its managed fallback reach the clock through a weak
/// reference only, so neither keeps the renderer alive.
/// </remarks>
internal sealed class RecoveryClock
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly IRecoveryClockHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly IMarkdownUiScheduler? _scheduler;
    private IMarkdownUiTimer? _timer;
    private WeakRecoveryTimer<RecoveryClock>? _managedTimer;

    internal RecoveryClock(IRecoveryClockHost host, MarkdownViewLifetime lifetime, IMarkdownUiScheduler? scheduler)
    {
        _host = host;
        _lifetime = lifetime;
        _scheduler = scheduler;
    }

    /// <summary>True while a UI or managed timer is running.</summary>
    internal bool IsRunning => _timer is not null || _managedTimer is not null;

    /// <summary>Starts the clock unless it is running or there is no UI scheduler.</summary>
    internal void Start()
    {
        if (IsRunning || _scheduler is not { } scheduler)
            return;
        try
        {
            var receiver = new WeakReference<RecoveryClock>(this);
            var timer = scheduler.CreateTimer();
            timer.Interval = Interval;
            timer.IsRepeating = true;
            timer.Tick += (sender, _) =>
            {
                if (!receiver.TryGetTarget(out var clock) ||
                    !ReferenceEquals(sender, clock._timer))
                {
                    try { ((IMarkdownUiTimer)sender!).Stop(); }
                    catch (Exception) { /* A stopped dispatcher has no delivery to recover. */ }
                    return;
                }
                Tick(clock);
            };
            _timer = timer;
            timer.Start();
        }
        catch (Exception exception)
        {
            var failedTimer = _timer;
            _timer = null;
            try { failedTimer?.Stop(); }
            catch (Exception) { /* Its replacement clock owns recovery now. */ }
            RelayoutDiagnostics.ReportFailure("relayout recovery", exception.Message);
            // A transient native timer failure must not clear one-shot overflow
            // signals or pending publication. This fallback remains weak and
            // has at most one queued UI tick, even if delivery is withheld.
            if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded)
            {
                try
                {
                    _managedTimer = new(
                        this, callback => scheduler.TryEnqueue(MarkdownUiPriority.Normal, callback),
                        Tick, Interval);
                }
                catch (Exception fallbackException)
                {
                    RelayoutDiagnostics.ReportFailure("managed relayout recovery", fallbackException.Message);
                    // Preserve pending state; subsequent normal control work
                    // can retry clock creation. Resource exhaustion is not a
                    // reason to silently discard a deferred image demand.
                }
            }
        }
    }

    /// <summary>Stops whichever timer is running.</summary>
    internal void Stop()
    {
        var timer = _timer;
        _timer = null;
        Interlocked.Exchange(ref _managedTimer, null)?.Dispose();
        try { timer?.Stop(); }
        catch (Exception) { /* Dispatcher shutdown already ended UI delivery. */ }
    }

    // Static so neither timer's callback captures the clock.
    private static void Tick(RecoveryClock clock) => clock._host.ProcessRecoveryTick();
}