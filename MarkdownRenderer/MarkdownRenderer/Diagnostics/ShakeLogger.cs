using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Ultra-low-overhead diagnostic logger used to investigate the "selection
/// shake" bug. Producers enqueue value tuples on the UI thread; a single
/// background drain task flushes them via <see cref="Debug.WriteLine(string)"/>.
/// Disabled by default — enable via <see cref="Enabled"/> from the sample app.
/// </summary>
public static class ShakeLogger
{
    private static readonly ConcurrentQueue<string> _queue = new();
    private static int _started;
    private static long _frame;

    public static volatile bool Enabled;

    public static long NextFrame() => Interlocked.Increment(ref _frame);
    public static long CurrentFrame => Interlocked.Read(ref _frame);

    public static void Log(string tag, string payload)
    {
        if (!Enabled) return;
        _queue.Enqueue($"[shake t={Environment.TickCount} f={Interlocked.Read(ref _frame)}] {tag} {payload}");
        EnsureStarted();
    }

    public static void LogPaint(string tag, int blockIndex, double x, double y, double w, double h)
    {
        if (!Enabled) return;
        _queue.Enqueue(
            $"[shake t={Environment.TickCount} f={Interlocked.Read(ref _frame)}] {tag} block={blockIndex} " +
            $"x={x:F4} y={y:F4} w={w:F4} h={h:F4}");
        EnsureStarted();
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        Task.Run(DrainLoopAsync);
    }

    private static async Task DrainLoopAsync()
    {
        while (true)
        {
            int drained = 0;
            while (_queue.TryDequeue(out var msg))
            {
                Debug.WriteLine(msg);
                if (++drained >= 256) break;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
    }
}
