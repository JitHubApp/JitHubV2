using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

/// <summary>
/// Reads the diagnostic CPU and memory counters of a worker process and
/// converts timestamps for timeout evidence. Every read is best-effort: an
/// unavailable counter or a closed handle yields the -1 sentinel instead of
/// failing the exchange that asked for it.
/// </summary>
internal static class WorkerProcessCounters
{
    internal static long GetCpuTicks(SafeProcessHandle process)
    {
        try
        {
            if (!WorkerNativeMethods.GetProcessTimes(process, out _, out _, out WorkerNativeMethods.FileTime kernel, out WorkerNativeMethods.FileTime user))
                return -1;

            ulong kernelTicks = ((ulong)kernel.High << 32) | kernel.Low;
            ulong userTicks = ((ulong)user.High << 32) | user.Low;
            return userTicks <= long.MaxValue &&
                   kernelTicks <= (ulong)long.MaxValue - userTicks
                ? (long)(kernelTicks + userTicks)
                : -1;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    internal static WorkerMemorySnapshot GetMemorySnapshot(SafeProcessHandle process)
    {
        try
        {
            var counters = new WorkerNativeMethods.ProcessMemoryCountersEx
            {
                Size = (uint)Marshal.SizeOf<WorkerNativeMethods.ProcessMemoryCountersEx>()
            };
            if (!WorkerNativeMethods.GetProcessMemoryInfo(process, ref counters, counters.Size))
                return WorkerMemorySnapshot.Unavailable;

            return new WorkerMemorySnapshot(
                ToKiB(counters.WorkingSetSize),
                ToKiB(counters.PrivateUsage),
                (int)Math.Min(counters.PageFaultCount, int.MaxValue));
        }
        catch (ObjectDisposedException)
        {
            return WorkerMemorySnapshot.Unavailable;
        }
    }

    internal static int GetElapsedMilliseconds(long startedAt) =>
        startedAt == 0
            ? -1
            : (int)Math.Min(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, int.MaxValue);

    private static int ToKiB(nuint bytes) =>
        (int)Math.Min((ulong)bytes / 1024UL, int.MaxValue);
}
