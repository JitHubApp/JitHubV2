using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Ultra-low-overhead diagnostic logger used to investigate the "selection
/// shake" bug. Producers enqueue value tuples on the UI thread; a single
/// background drain task flushes them to a file and via Debug.WriteLine.
/// Disabled by default — enable via <see cref="Enabled"/> from the sample app.
/// </summary>
public static class ShakeLogger
{
    private static readonly ConcurrentQueue<string> _queue = new();
    private static int _started;
    private static long _frame;

    // Log file written to the repo root next to text_shaking.log
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory,
        @"..\..\..\..\..\..\..\text_shaking2.log");

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
        // Resolve path once
        string path;
        try { path = Path.GetFullPath(LogPath); }
        catch { path = Path.Combine(Path.GetTempPath(), "text_shaking2.log"); }

        // Truncate any previous run
        await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(false);
        Debug.WriteLine($"[ShakeLogger] writing to {path}");

        using var writer = new StreamWriter(path, append: true) { AutoFlush = false };
        while (true)
        {
            int drained = 0;
            while (_queue.TryDequeue(out var msg))
            {
                Debug.WriteLine(msg);
                await writer.WriteLineAsync(msg).ConfigureAwait(false);
                if (++drained >= 256) break;
            }
            if (drained > 0) await writer.FlushAsync().ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
        }
    }
}
