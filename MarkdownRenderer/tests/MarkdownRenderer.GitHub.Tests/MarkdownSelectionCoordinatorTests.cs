using System;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Selection;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSelectionCoordinatorTests
{
    [Fact]
    public void SelectingClearsOnlyTheOtherPeersOfTheSameCoordinator()
    {
        var root = new MarkdownSelectionCoordinator();
        var otherRoot = new MarkdownSelectionCoordinator();
        var owner = new RecordingPeer();
        var sibling = new RecordingPeer();
        var otherWindowPeer = new RecordingPeer();
        root.Register(owner);
        root.Register(sibling);
        otherRoot.Register(otherWindowPeer);

        root.ClearSelectionsExcept(owner);

        Assert.Equal(0, owner.ClearCount);
        Assert.Equal(1, sibling.ClearCount);
        Assert.Equal(0, otherWindowPeer.ClearCount);
    }

    [Fact]
    public void RegistrationIsIdempotentAndUnregisterStopsClears()
    {
        var coordinator = new MarkdownSelectionCoordinator();
        var peer = new RecordingPeer();
        coordinator.Register(peer);
        coordinator.Register(peer);

        coordinator.ClearSelectionsExcept(owner: null);
        Assert.Equal(1, peer.ClearCount);

        coordinator.Unregister(peer);
        coordinator.ClearSelectionsExcept(owner: null);
        Assert.Equal(1, peer.ClearCount);
    }

    [Fact]
    public void CoordinatorDoesNotKeepPeersAlive()
    {
        var coordinator = new MarkdownSelectionCoordinator();
        WeakReference collected = RegisterUnreferencedPeer(coordinator);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(collected.IsAlive);
        coordinator.ClearSelectionsExcept(owner: null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterUnreferencedPeer(MarkdownSelectionCoordinator coordinator)
    {
        var peer = new RecordingPeer();
        coordinator.Register(peer);
        return new WeakReference(peer);
    }

    private sealed class RecordingPeer : ISelectionPeer
    {
        public int ClearCount { get; private set; }

        public void ClearSelectionFromCoordinator() => ClearCount++;
    }
}
