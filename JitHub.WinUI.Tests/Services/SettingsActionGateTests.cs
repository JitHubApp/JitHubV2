using System;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SettingsActionGateTests
{
    [Fact]
    public async Task TryRunAsync_SerializesRapidRequestsWithoutQueuingAnotherDialog()
    {
        SettingsActionGate gate = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationCount = 0;

        Task<bool> first = gate.TryRunAsync(async () =>
        {
            invocationCount++;
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        bool second = await gate.TryRunAsync(() =>
        {
            invocationCount++;
            return Task.CompletedTask;
        });

        Assert.False(second);
        Assert.True(gate.IsActive);
        Assert.Equal(1, invocationCount);

        release.SetResult();
        Assert.True(await first);
        Assert.False(gate.IsActive);
    }

    [Fact]
    public async Task TryRunAsync_ReleasesGateWhenActionFails()
    {
        SettingsActionGate gate = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.TryRunAsync(() => throw new InvalidOperationException("failure")));

        Assert.False(gate.IsActive);
        Assert.True(await gate.TryRunAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task TryRunAsync_ReleasesGateWhenStateSubscriberFails()
    {
        SettingsActionGate gate = new();
        int notificationCount = 0;
        gate.StateChanged += (_, _) =>
        {
            notificationCount++;
            throw new InvalidOperationException("subscriber failure");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.TryRunAsync(() => Task.CompletedTask));

        Assert.False(gate.IsActive);
        Assert.Equal(2, notificationCount);
    }
}
