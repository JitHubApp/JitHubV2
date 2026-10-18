using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Opt-in probe coordination only. Both monitor callbacks execute on one
/// dedicated worker, never on the XAML thread or across an await boundary.
/// Every acquisition and hold has a deadline, including abandoned test runs.
/// </summary>
internal sealed class BackgroundInteractionLease
{
    private readonly TaskCompletionSource<bool> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private int _held;

    internal BackgroundInteractionLease(
        Func<bool> tryBegin,
        Action end,
        int acquisitionTimeoutMilliseconds = 2_000,
        int holdTimeoutMilliseconds = 15_000)
    {
        ArgumentNullException.ThrowIfNull(tryBegin);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentOutOfRangeException.ThrowIfLessThan(acquisitionTimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(acquisitionTimeoutMilliseconds, 2_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(holdTimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(holdTimeoutMilliseconds, 15_000);
        var worker = new Thread(() => Run(tryBegin, end, acquisitionTimeoutMilliseconds, holdTimeoutMilliseconds))
        {
            IsBackground = true,
            Name = "MarkdownRenderer deferred-touch probe lease",
        };
        worker.Start();
    }

    internal Task<bool> Acquired => _acquired.Task;
    internal Task<bool> Completed => _completed.Task;
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal bool IsHeld => Volatile.Read(ref _held) != 0;
    internal void Release() => _release.TrySetResult(true);

    private void Run(Func<bool> tryBegin, Action end, int acquisitionTimeout, int holdTimeout)
    {
        bool ownsLease = false;
        try
        {
            var watch = Stopwatch.StartNew();
            while (!_release.Task.IsCompleted)
            {
                ownsLease = tryBegin();
                if (ownsLease)
                    break;
                if (watch.ElapsedMilliseconds >= acquisitionTimeout)
                    throw new TimeoutException("The probe could not acquire the current snapshot on its worker.");
                Thread.Sleep(2);
            }

            Volatile.Write(ref _held, ownsLease ? 1 : 0);
            _acquired.TrySetResult(ownsLease);
            if (!ownsLease)
                return;

            watch.Restart();
            while (!_release.Task.IsCompleted)
            {
                if (watch.ElapsedMilliseconds >= holdTimeout)
                    throw new TimeoutException("The probe's snapshot hold expired before the terminal was dropped.");
                Thread.Sleep(2);
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error);
        }
        finally
        {
            if (ownsLease)
            {
                try { end(); }
                catch (Exception error) { Volatile.Write(ref _failure, error); }
            }
            Volatile.Write(ref _held, 0);
            _acquired.TrySetResult(false);
            _completed.TrySetResult(Failure is null);
        }
    }
}
