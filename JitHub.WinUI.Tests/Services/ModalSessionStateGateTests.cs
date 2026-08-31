using System.Collections.Concurrent;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ModalSessionStateGateTests
{
    [Fact]
    public void RapidSubmitStorm_AdmitsExactlyOneMutation()
    {
        ModalSessionStateGate gate = new();
        ConcurrentBag<int> admitted = [];

        Parallel.For(0, 256, index =>
        {
            if (gate.TryBeginMutation())
            {
                admitted.Add(index);
            }
        });

        Assert.Single(admitted);
        Assert.True(gate.IsMutationInProgress);
    }

    [Fact]
    public void ClosedSession_RejectsQueuedAndFutureSubmissions()
    {
        ModalSessionStateGate gate = new();
        Assert.True(gate.TryBeginMutation());

        gate.Deactivate();

        Assert.False(gate.IsActive);
        Assert.False(gate.IsMutationInProgress);
        Assert.False(gate.TryBeginMutation());
        Assert.False(gate.EndMutation());
    }

    [Fact]
    public void FailedMutation_CanRetryWhileSessionRemainsActive()
    {
        ModalSessionStateGate gate = new();
        Assert.True(gate.TryBeginMutation());
        Assert.True(gate.EndMutation());

        Assert.True(gate.IsActive);
        Assert.True(gate.TryBeginMutation());
    }

    [Fact]
    public void CloseClaim_IsAtomicAndCanBeRestoredAfterHostFailure()
    {
        ModalSessionStateGate gate = new();

        Assert.True(gate.TryDeactivateForClose());
        Assert.False(gate.TryDeactivateForClose());
        Assert.False(gate.TryBeginMutation());

        gate.Reactivate();

        Assert.True(gate.IsActive);
        Assert.True(gate.TryBeginMutation());
        Assert.False(gate.TryDeactivateForClose());
    }
}
