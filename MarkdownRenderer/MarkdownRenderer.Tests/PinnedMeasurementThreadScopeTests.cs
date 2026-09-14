using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PinnedMeasurementThreadScopeTests
{
    [Fact]
    public void EnterPinsOneProcessorAndDisposeRestoresAffinityAndPriority()
    {
        NativeGroupAffinity before = GetCurrentAffinity();
        ThreadPriority priorityBefore = Thread.CurrentThread.Priority;

        using (PinnedMeasurementThreadScope scope = PinnedMeasurementThreadScope.Enter(
                   ThreadPriority.Normal,
                   "contract-test"))
        {
            NativeGroupAffinity during = GetCurrentAffinity();
            ulong evidenceMask = ulong.Parse(
                scope.AffinityMask.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture);

            Assert.Equal((ulong)during.Mask, evidenceMask);
            Assert.Equal(during.Group, scope.ProcessorGroup);
            Assert.Equal(BitOperations.TrailingZeroCount(evidenceMask), scope.ProcessorNumber);
            Assert.Equal(ThreadPriority.Normal, Thread.CurrentThread.Priority);
            Assert.True(PinnedMeasurementThreadScope.IsEvidenceValid(
                scope.AffinityMask,
                scope.ProcessorGroup,
                scope.ProcessorNumber,
                scope.ThreadPriority,
                ThreadPriority.Normal));
        }

        NativeGroupAffinity after = GetCurrentAffinity();
        Assert.Equal((ulong)before.Mask, (ulong)after.Mask);
        Assert.Equal(before.Group, after.Group);
        Assert.Equal(priorityBefore, Thread.CurrentThread.Priority);
    }

    [Fact]
    public void EnterRejectsUnsupportedPriorityWithoutChangingThreadState()
    {
        NativeGroupAffinity before = GetCurrentAffinity();
        ThreadPriority priorityBefore = Thread.CurrentThread.Priority;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PinnedMeasurementThreadScope.Enter(ThreadPriority.AboveNormal, "contract-test"));

        NativeGroupAffinity after = GetCurrentAffinity();
        Assert.Equal((ulong)before.Mask, (ulong)after.Mask);
        Assert.Equal(before.Group, after.Group);
        Assert.Equal(priorityBefore, Thread.CurrentThread.Priority);
    }

    [Fact]
    public void EvidenceRejectsProcessorsOutsideTheActiveNativeTopology()
    {
        ushort activeGroupCount = GetActiveProcessorGroupCount();
        Assert.True(activeGroupCount > 0);

        Assert.False(PinnedMeasurementThreadScope.IsEvidenceValid(
            "0x0000000000000001",
            activeGroupCount,
            0,
            ThreadPriority.Normal.ToString(),
            ThreadPriority.Normal));

        uint activeProcessorCount = GetActiveProcessorCount(0);
        Assert.True(activeProcessorCount > 0);
        int unavailableProcessor = checked((int)activeProcessorCount);
        if (unavailableProcessor < IntPtr.Size * 8)
        {
            ulong mask = 1UL << unavailableProcessor;
            Assert.False(PinnedMeasurementThreadScope.IsEvidenceValid(
                $"0x{mask:X16}",
                0,
                unavailableProcessor,
                ThreadPriority.Normal.ToString(),
                ThreadPriority.Normal));
        }
    }

    private static NativeGroupAffinity GetCurrentAffinity()
    {
        Assert.True(GetThreadGroupAffinity(GetCurrentThread(), out NativeGroupAffinity affinity));
        return affinity;
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

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(
        nint thread,
        out NativeGroupAffinity groupAffinity);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
