using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SourceByteAdmissionCancellationBoundaryTests
{
    [Fact]
    public async Task CancellationWhileWaitingForGatePreventsImmediateGrant()
    {
        var warmup = new SourceByteAdmission(64, 16);
        using (IDisposable warmupLease = await warmup.EnterAsync(
            new object(), 1, false, CancellationToken.None))
        {
        }
        _ = GetAdmissionLock(warmup);

        var admission = new SourceByteAdmission(64, 16);
        using var cancellation = new CancellationTokenSource();
        using var entering = new ManualResetEventSlim();
        Exception? failure = null;
        Task<IDisposable>? returnedTask = null;
        var thread = new Thread(() =>
        {
            entering.Set();
            try
            {
                returnedTask = admission.EnterAsync(
                    new object(), 8, false, cancellation.Token).AsTask();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;

        object admissionLock = GetAdmissionLock(admission);
        bool blockedOnLock;
        lock (admissionLock)
        {
            thread.Start();
            bool started = entering.Wait(TimeSpan.FromSeconds(5));
            blockedOnLock = started && SpinWait.SpinUntil(
                () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(blockedOnLock);

        if (returnedTask is not null)
        {
            try
            {
                (await returnedTask.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.IsType<OperationCanceledException>(failure);
        Assert.Null(returnedTask);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Equal(0, admission.ActiveBackgroundBytes);
        Assert.Equal(0, admission.PeakActiveBytes);
        Assert.Equal(0, admission.PendingRequests);
    }

    private static object GetAdmissionLock(SourceByteAdmission admission) =>
        typeof(SourceByteAdmission).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(admission)!;
}
