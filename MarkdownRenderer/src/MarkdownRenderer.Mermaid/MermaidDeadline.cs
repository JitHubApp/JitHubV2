using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownRenderer.Mermaid;

internal static class MermaidDeadline
{
    internal static long Start(TimeSpan duration)
    {
        long now = Stopwatch.GetTimestamp();
        double rawTicks = Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
        long durationTicks = rawTicks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)rawTicks);
        return now > long.MaxValue - durationTicks ? long.MaxValue : now + durationTicks;
    }

    internal static bool IsExpired(long deadlineTimestamp) =>
        Stopwatch.GetTimestamp() >= deadlineTimestamp;

    internal static TimeSpan GetRemaining(long deadlineTimestamp)
    {
        long now = Stopwatch.GetTimestamp();
        if (deadlineTimestamp <= now)
            return TimeSpan.Zero;

        long remainingTicks = deadlineTimestamp - now;
        return TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
    }

    internal static CancellationTokenSource CreateCancellation(long deadlineTimestamp)
    {
        var cancellation = new CancellationTokenSource();
        TimeSpan remaining = GetRemaining(deadlineTimestamp);
        if (remaining <= TimeSpan.Zero)
            cancellation.Cancel();
        else
            cancellation.CancelAfter(remaining);
        return cancellation;
    }
}
