using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Pins a measurement phase to one deterministic logical processor so hybrid
/// CPUs cannot turn a build comparison into a P-core/E-core comparison. The
/// owned real thread handle allows restoration even if an asynchronous caller
/// unexpectedly disposes the scope from a different thread.
/// </summary>
internal sealed class PinnedMeasurementThreadScope : IDisposable
{
    private const uint ThreadQueryInformation = 0x0040;
    private const uint ThreadSetInformation = 0x0020;
    private const int ThreadPriorityErrorReturn = int.MaxValue;

    private readonly SafeWaitHandle _thread;
    private readonly nuint _previousAffinityMask;
    private readonly int _previousNativePriority;
    private readonly string _operation;
    private bool _disposed;

    private PinnedMeasurementThreadScope(
        SafeWaitHandle thread,
        nuint previousAffinityMask,
        int previousNativePriority,
        nuint selectedAffinityMask,
        NativeProcessorNumber processor,
        ThreadPriority requestedPriority,
        string operation)
    {
        _thread = thread;
        _previousAffinityMask = previousAffinityMask;
        _previousNativePriority = previousNativePriority;
        _operation = operation;
        AffinityMask = $"0x{(ulong)selectedAffinityMask:X16}";
        ProcessorGroup = processor.Group;
        ProcessorNumber = processor.Number;
        ThreadPriority = requestedPriority.ToString();
    }

    internal string AffinityMask { get; }
    internal int ProcessorGroup { get; }
    internal int ProcessorNumber { get; }
    internal string ThreadPriority { get; }

    internal static bool IsEvidenceValid(
        string affinityMask,
        int processorGroup,
        int processorNumber,
        string threadPriority,
        ThreadPriority expectedPriority)
    {
        if (affinityMask.Length != 18 ||
            !affinityMask.StartsWith("0x", StringComparison.Ordinal) ||
            !ulong.TryParse(
                affinityMask.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong mask) ||
            !BitOperations.IsPow2(mask) ||
            processorGroup < 0 ||
            processorNumber < 0 ||
            processorNumber >= IntPtr.Size * 8 ||
            BitOperations.TrailingZeroCount(mask) != processorNumber)
        {
            return false;
        }

        ushort activeGroupCount = GetActiveProcessorGroupCount();
        if (processorGroup >= activeGroupCount ||
            processorNumber >= GetActiveProcessorCount((ushort)processorGroup))
        {
            return false;
        }

        return string.Equals(
            threadPriority,
            expectedPriority.ToString(),
            StringComparison.Ordinal);
    }

    internal static PinnedMeasurementThreadScope Enter(
        ThreadPriority requestedPriority,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        int nativePriority = requestedPriority switch
        {
            System.Threading.ThreadPriority.Normal => 0,
            System.Threading.ThreadPriority.Highest => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(requestedPriority),
                requestedPriority,
                "Measurement priority must be Normal or Highest."),
        };

        SafeWaitHandle thread = OpenThread(
            ThreadQueryInformation | ThreadSetInformation,
            inheritHandle: false,
            GetCurrentThreadId());
        if (thread.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            thread.Dispose();
            throw new Win32Exception(error, $"Could not open the {operation} measurement thread.");
        }

        if (!GetThreadGroupAffinity(thread, out NativeGroupAffinity allowedAffinity))
        {
            int error = Marshal.GetLastWin32Error();
            thread.Dispose();
            throw new Win32Exception(error, $"Could not read the {operation} measurement thread affinity.");
        }

        ulong allowedBits = (ulong)allowedAffinity.Mask;
        if (allowedBits == 0)
        {
            thread.Dispose();
            throw new InvalidOperationException("The measurement thread has no available processor-affinity bits.");
        }

        ulong selectedBit = allowedBits & (~allowedBits + 1UL);
        nuint selectedMask = (nuint)selectedBit;

        nuint previousMask = SetThreadAffinityMask(thread, selectedMask);
        if (previousMask == 0)
        {
            int error = Marshal.GetLastWin32Error();
            thread.Dispose();
            throw new Win32Exception(error, $"Could not pin the {operation} measurement thread.");
        }

        int previousPriority = GetThreadPriority(thread);
        if (previousPriority == ThreadPriorityErrorReturn)
        {
            int error = Marshal.GetLastWin32Error();
            _ = SetThreadAffinityMask(thread, previousMask);
            thread.Dispose();
            throw new Win32Exception(error, $"Could not read the {operation} measurement thread priority.");
        }

        if (!SetThreadPriority(thread, nativePriority))
        {
            int error = Marshal.GetLastWin32Error();
            _ = SetThreadAffinityMask(thread, previousMask);
            thread.Dispose();
            throw new Win32Exception(error, $"Could not set the {operation} measurement thread priority.");
        }

        try
        {
            GetCurrentProcessorNumberEx(out NativeProcessorNumber processor);
            int expectedProcessorNumber = BitOperations.TrailingZeroCount(selectedBit);
            if (processor.Group != allowedAffinity.Group ||
                processor.Number != expectedProcessorNumber)
            {
                throw new InvalidOperationException(
                    $"Affinity selected group {allowedAffinity.Group}, processor {expectedProcessorNumber}, but the {operation} " +
                    $"measurement thread ran on group {processor.Group}, processor {processor.Number}.");
            }

            return new PinnedMeasurementThreadScope(
                thread,
                previousMask,
                previousPriority,
                selectedMask,
                processor,
                requestedPriority,
                operation);
        }
        catch
        {
            try
            {
                _ = SetThreadPriority(thread, previousPriority);
                _ = SetThreadAffinityMask(thread, previousMask);
            }
            finally
            {
                thread.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Win32Exception? priorityFailure = null;
        try
        {
            if (!SetThreadPriority(_thread, _previousNativePriority))
            {
                priorityFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not restore the {_operation} measurement thread priority.");
            }

            if (SetThreadAffinityMask(_thread, _previousAffinityMask) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not restore the {_operation} measurement thread affinity.");
            }

            if (priorityFailure is not null)
                throw priorityFailure;
        }
        finally
        {
            _thread.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessorNumber
    {
        internal ushort Group;
        internal byte Number;
        private byte _reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGroupAffinity
    {
        internal nuint Mask;
        internal ushort Group;
        private ushort _reserved0;
        private ushort _reserved1;
        private ushort _reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(
        SafeWaitHandle thread,
        out NativeGroupAffinity groupAffinity);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(
        SafeWaitHandle thread,
        nuint affinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadPriority(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(SafeWaitHandle thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out NativeProcessorNumber processorNumber);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
