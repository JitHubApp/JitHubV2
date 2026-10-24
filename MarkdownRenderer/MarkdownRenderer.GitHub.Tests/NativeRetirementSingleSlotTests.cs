using System.Runtime.CompilerServices;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class NativeRetirementSingleSlotTests
{
    [Fact]
    public void SingleSlotRetainsFailedResourceAndClearsItOnlyAfterSuccessfulRetry()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        var resource = new Probe { Fail = true };
        NativeResourceRetirementRecord record = owner.Reserve(0, 1);
        record.SetTemporary(0, resource);
        record.Publish();
        record.Publish();
        Assert.Equal(1, owner.PendingRecordCount);
        Assert.Equal(1, owner.PendingResourceCount);

        Assert.False(owner.DrainOnce());
        Assert.Equal(1, resource.Attempts);
        Assert.Equal(1, record.RemainingResourceCount);
        Assert.Equal(1, record.FailedAttemptCount);
        Assert.NotEqual(0, record.LastFailureHResult);
        Assert.Throws<ObjectDisposedException>(() => record.SetTemporary(0, null));

        resource.Fail = false;
        Assert.True(owner.DrainOnce());
        Assert.Equal(2, resource.Attempts);
        Assert.Equal(0, record.RemainingResourceCount);
        Assert.Equal(0, owner.PendingRecordCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.False(owner.DrainOnce());
    }

    [Fact]
    public void EmptySingleSlotPublicationDoesNotQueueWorkAndRejectsMutation()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord record = owner.Reserve(0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => record.SetTemporary(1, null));
        Assert.Throws<InvalidOperationException>(() => record.SetGroup(0, []));
        record.SetTemporary(0, null);
        record.Publish();
        Assert.Equal(0, owner.PendingRecordCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.False(owner.DrainOnce());
        Assert.Throws<ObjectDisposedException>(() => record.SetTemporary(0, null));
    }

    [Fact]
    public void UnknownNativeCloseDuringRetryQuarantinesWithoutReportingRelease()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord record = owner.Reserve(0, 1);
        var resource = new UnknownCloseProbe(record);
        record.SetTemporary(0, resource);
        record.Publish();

        Assert.False(owner.DrainOnce());
        Assert.Equal(1, resource.Attempts);
        Assert.Equal(0, owner.PendingRecordCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.Equal(1, owner.QuarantinedResourceCount);
        Assert.Equal(1, record.RemainingResourceCount);
        Assert.Equal(0, record.RetryableResourceCount);
        Assert.Equal(1, record.FailedAttemptCount);
        Assert.False(owner.DrainOnce());
        Assert.Equal(0, record.Drain(1));
        Assert.Equal(1, resource.Attempts);
    }

    [Fact]
    public void LateQuarantineAndSuccessfulReleaseMaintainSeparateLedgers()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord unknownRecord = owner.Reserve(0, 1);
        var unknown = new UnknownCloseProbe(unknownRecord);
        unknownRecord.SetTemporary(0, unknown);
        unknownRecord.Publish();
        NativeResourceRetirementRecord releasedRecord = owner.Reserve(0, 1);
        var released = new Probe();
        releasedRecord.SetTemporary(0, released);
        releasedRecord.Publish();

        Assert.True(owner.DrainOnce());
        Assert.Equal(1, released.Attempts);
        Assert.Equal(0, releasedRecord.RemainingResourceCount);
        Assert.Equal(1, unknown.Attempts);
        Assert.Equal(1, unknownRecord.RemainingResourceCount);
        Assert.Equal(0, owner.PendingRecordCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.Equal(1, owner.QuarantinedResourceCount);
        Assert.False(owner.DrainOnce());
    }

    [Fact]
    public void SessionBoundSingleSlotIsQuarantinedWithoutBackgroundClose()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        var resource = new Probe();
        NativeResourceRetirementRecord record = owner.Reserve(0, 1);
        record.SetTemporary(0, resource, retryable: false);
        record.Publish();
        record.Publish();
        Assert.Equal(1, owner.QuarantinedResourceCount);
        Assert.Equal(0, owner.PendingRecordCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.Equal(1, record.RemainingResourceCount);
        Assert.Equal(0, record.RetryableResourceCount);
        Assert.Equal(0, record.Drain(1));
        Assert.False(owner.DrainOnce());
        Assert.Equal(0, resource.Attempts);
        Assert.Throws<ObjectDisposedException>(() => record.SetTemporary(0, null, retryable: true));
    }

    [Fact]
    public void ClearedSessionBoundSingleSlotDoesNotCreateQuarantine()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord record = owner.Reserve(0, 1);
        var resource = new Probe();
        record.SetTemporary(0, resource, retryable: false);
        resource.Dispose();
        record.SetTemporary(0, null, retryable: false);
        record.Publish();
        Assert.Equal(1, resource.Attempts);
        Assert.Equal(0, owner.QuarantinedResourceCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.Equal(0, record.RemainingResourceCount);
    }

    [Fact]
    public void GroupedTemporarySlotsCannotSilentlyChangeTheirSharedRetryPolicy()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord record = owner.Reserve(1, 2);
        var resource = new Probe();
        Assert.Throws<InvalidOperationException>(() => record.SetTemporary(0, resource, retryable: false));
        record.Publish();
        Assert.Equal(0, owner.QuarantinedResourceCount);
        Assert.Equal(0, owner.PendingResourceCount);
        Assert.Equal(0, resource.Attempts);
    }

    [Fact]
    public void CommonSingleSlotReservationAvoidsPerRecordGroupArrays()
    {
        var owner = new NativeResourceRetirement(createTimer: false);
        // Warm static delegates, empty-array instances and both code paths
        // before measuring allocation on this thread. No native work is timed.
        _ = MeasureReservationBytes(owner, singleSlot: true, count: 64);
        _ = MeasureReservationBytes(owner, singleSlot: false, count: 64);
        const int count = 1024;
        long singleBytes = MeasureReservationBytes(owner, singleSlot: true, count);
        long groupBytes = MeasureReservationBytes(owner, singleSlot: false, count);
        Assert.InRange(singleBytes, count, 192L * count);
        long minimumGroupStorage = IntPtr.Size == sizeof(int) ? 72 : 96;
        Assert.True(groupBytes - singleBytes >= minimumGroupStorage * count,
            $"Single-slot={singleBytes}; grouped={groupBytes}.");
        Assert.Equal(0, owner.PendingRecordCount);
        Assert.Equal(0, owner.PendingResourceCount);
        GC.KeepAlive(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureReservationBytes(NativeResourceRetirement owner, bool singleSlot, int count)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
        {
            NativeResourceRetirementRecord record = singleSlot
                ? owner.Reserve(0, 1)
                : owner.Reserve(1, 0);
            record.Publish();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed class Probe : IDisposable
    {
        internal bool Fail;
        internal int Attempts;
        public void Dispose()
        {
            Attempts++;
            if (Fail)
                throw new InvalidOperationException("Injected cleanup failure.");
        }
    }

    private sealed class UnknownCloseProbe(NativeResourceRetirementRecord record) : IDisposable
    {
        internal int Attempts;

        public void Dispose()
        {
            Attempts++;
            record.StopSingleSlotRetries();
            throw new InvalidOperationException("The native close result is unknown.");
        }
    }
}
