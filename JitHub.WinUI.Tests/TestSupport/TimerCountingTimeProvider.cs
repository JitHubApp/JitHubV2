using System;
using System.Threading;
using Microsoft.Extensions.Time.Testing;

namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that counts how many of its timers have fired.
/// </summary>
/// <remarks>
/// <see cref="FakeTimeProvider.Advance"/> runs due timer callbacks on the calling thread,
/// so <see cref="FiredTimerCount"/> is exact as soon as <c>Advance</c> returns. Code awaiting
/// <c>Task.Delay</c> on this clock resumes later on the thread pool, so assert "nothing started"
/// with this count rather than with a side effect of that later continuation.
/// </remarks>
internal sealed class TimerCountingTimeProvider : FakeTimeProvider
{
    private int _firedTimerCount;

    public int FiredTimerCount => Volatile.Read(ref _firedTimerCount);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return base.CreateTimer(
            timerState =>
            {
                Interlocked.Increment(ref _firedTimerCount);
                callback(timerState);
            },
            state,
            dueTime,
            period);
    }
}
