using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class WeakRecoveryTimerTests
{
    private static readonly TimeSpan LongInterval = TimeSpan.FromDays(1);

    [Fact]
    public void WithheldDispatcherCallbackCoalescesTicksAndAllowsNextTickAfterCompletion()
    {
        var queued = new List<Action>();
        var target = new object();
        int recoveries = 0;
        using var timer = new WeakRecoveryTimer<object>(
            target,
            action => { queued.Add(action); return true; },
            _ => recoveries++,
            LongInterval);

        for (int i = 0; i < 20; i++)
            timer.Pulse();

        Assert.Single(queued);
        Assert.Equal(0, recoveries);
        queued[0]();
        Assert.Equal(1, recoveries);

        timer.Pulse();
        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(2, recoveries);
    }

    [Fact]
    public void RejectedAndThrowingEnqueuesResetPendingForLaterTicks()
    {
        var queued = new List<Action>();
        var target = new object();
        int attempts = 0;
        int recoveries = 0;
        using var timer = new WeakRecoveryTimer<object>(
            target,
            action =>
            {
                attempts++;
                if (attempts == 1)
                    return false;
                if (attempts == 2)
                    throw new InvalidOperationException("synthetic dispatcher failure");
                queued.Add(action);
                return true;
            },
            _ => recoveries++,
            LongInterval);

        timer.Pulse();
        timer.Pulse();
        timer.Pulse();

        Assert.Equal(3, attempts);
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void DisposeFencesAcceptedButWithheldDispatcherCallback()
    {
        Action? withheld = null;
        var target = new object();
        int recoveries = 0;
        var timer = new WeakRecoveryTimer<object>(
            target,
            action => { withheld = action; return true; },
            _ => recoveries++,
            LongInterval);

        timer.Pulse();
        Assert.NotNull(withheld);
        timer.Dispose();
        timer.Dispose();
        withheld!();
        timer.Pulse();

        Assert.True(timer.IsDisposed);
        Assert.Equal(0, recoveries);
    }

    [Fact]
    public void LostWeakOwnerDisposesClockAndWithheldCallbackIsHarmless()
    {
        (WeakRecoveryTimer<object> timer, WeakReference<object> target, List<Action> queued) = CreateTimerWithQueuedCallback();
        try
        {
            CollectUntilDead(target);
            Assert.False(target.TryGetTarget(out _));

            timer.Pulse();

            Assert.True(timer.IsDisposed);
            Assert.Single(queued);
            queued[0]();
        }
        finally
        {
            timer.Dispose();
        }
    }

    [Fact]
    public void RecoveryExceptionDoesNotEscapeOrBlockFutureTicks()
    {
        var queued = new List<Action>();
        var target = new object();
        int attempts = 0;
        using var timer = new WeakRecoveryTimer<object>(
            target,
            action => { queued.Add(action); return true; },
            _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("synthetic recovery failure");
            },
            LongInterval);

        timer.Pulse();
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(1, attempts);

        timer.Pulse();
        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(2, attempts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakRecoveryTimer<object> Timer, WeakReference<object> Target, List<Action> Queued)
        CreateTimerWithQueuedCallback()
    {
        object target = new();
        var weakTarget = new WeakReference<object>(target);
        var queued = new List<Action>();
        var timer = new WeakRecoveryTimer<object>(
            target,
            action => { queued.Add(action); return true; },
            static _ => { },
            LongInterval);
        timer.Pulse();
        return (timer, weakTarget, queued);
    }

    private static void CollectUntilDead(WeakReference<object> target)
    {
        for (int attempt = 0; attempt < 10 && IsAlive(target); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<object> target) => target.TryGetTarget(out _);
}
