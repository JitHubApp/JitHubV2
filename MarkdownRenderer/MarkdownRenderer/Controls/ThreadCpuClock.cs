using System;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Audit-only CPU time for a synchronous layout build on one pool thread.
/// Comparing this with wall time distinguishes work from descheduling or waits
/// without collecting document text or sampling a process-wide workload.
/// </summary>
internal static partial class ThreadCpuClock
{
    internal static long ReadCurrentThreadTicks()
    {
        if (!OperatingSystem.IsWindows() ||
            !GetThreadTimes(
                GetCurrentThread(),
                out _,
                out _,
                out FileTime kernel,
                out FileTime user))
        {
            return -1;
        }

        return checked(ToTicks(kernel) + ToTicks(user));
    }

    internal static double ElapsedMilliseconds(long startTicks, long endTicks) =>
        startTicks < 0 || endTicks < startTicks
            ? -1
            : (endTicks - startTicks) / 10_000d;

    private static long ToTicks(FileTime time) =>
        (long)(((ulong)time.High << 32) | time.Low);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(
        nint thread,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }
}
