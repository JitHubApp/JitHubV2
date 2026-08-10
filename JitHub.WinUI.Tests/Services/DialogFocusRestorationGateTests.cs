using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DialogFocusRestorationGateTests
{
    [Fact]
    public void ClosedCurrentSession_CanRestoreFocus()
    {
        DialogFocusRestorationGate gate = new();
        long generation = gate.BeginSession();

        Assert.True(gate.CanRestore(generation, isDialogVisible: false));
    }

    [Fact]
    public void StaleCloseCallback_CannotStealFocusFromNewSession()
    {
        DialogFocusRestorationGate gate = new();
        long firstGeneration = gate.BeginSession();
        long secondGeneration = gate.BeginSession();

        Assert.False(gate.CanRestore(firstGeneration, isDialogVisible: false));
        Assert.True(gate.CanRestore(secondGeneration, isDialogVisible: false));
    }

    [Fact]
    public void VisibleDialog_BlocksFocusRestorationEvenForCurrentSession()
    {
        DialogFocusRestorationGate gate = new();
        long generation = gate.BeginSession();

        Assert.False(gate.CanRestore(generation, isDialogVisible: true));
    }
}
