using System.Collections.Concurrent;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DialogPresentationCoordinatorTests
{
    [Fact]
    public void NativeAndShellPresentationStorm_AdmitsExactlyOneOwner()
    {
        DialogPresentationCoordinator coordinator = new();
        ConcurrentBag<(DialogPresentationKind Kind, long Lease)> admitted = [];

        Parallel.For(0, 256, index =>
        {
            DialogPresentationKind kind = index % 2 == 0
                ? DialogPresentationKind.NativeContentDialog
                : DialogPresentationKind.ShellOverlay;
            if (coordinator.TryBegin(kind, out long lease))
            {
                admitted.Add((kind, lease));
            }
        });

        Assert.Single(admitted);
        Assert.True(coordinator.IsPresenting);
        Assert.Equal(admitted.Single().Kind, coordinator.ActiveKind);
    }

    [Fact]
    public void StaleCompletion_CannotReleaseNewerOwner()
    {
        DialogPresentationCoordinator coordinator = new();
        Assert.True(coordinator.TryBegin(DialogPresentationKind.ShellOverlay, out long staleLease));
        Assert.True(coordinator.Complete(staleLease));
        Assert.True(coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out long currentLease));

        Assert.False(coordinator.Complete(staleLease));
        Assert.True(coordinator.IsPresenting);
        Assert.Equal(DialogPresentationKind.NativeContentDialog, coordinator.ActiveKind);
        Assert.True(coordinator.Complete(currentLease));
    }

    [Fact]
    public void ResetInvalidatesOutstandingLease()
    {
        DialogPresentationCoordinator coordinator = new();
        Assert.True(coordinator.TryBegin(DialogPresentationKind.ShellOverlay, out long lease));

        coordinator.Reset();

        Assert.False(coordinator.Complete(lease));
        Assert.False(coordinator.IsPresenting);
        Assert.True(coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out _));
    }
}
