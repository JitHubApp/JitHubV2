using System.Collections.Concurrent;
using JitHub.Core.Services.Dialogs;
using Xunit;

namespace JitHub.Core.Tests.Services.Dialogs;

public sealed class CompactDialogAutomationContractTests
{
    [Fact]
    public void BusySubmissionGateAdmitsOnlyOneRepeatedSubmit()
    {
        DialogSubmissionGate gate = new();
        ConcurrentBag<int> admitted = [];

        Parallel.For(0, 128, index =>
        {
            if (gate.TryBegin())
            {
                admitted.Add(index);
            }
        });

        Assert.Single(admitted);
        Assert.True(gate.IsSubmitting);
        gate.Complete();
        Assert.False(gate.IsSubmitting);
        Assert.True(gate.TryBegin());
    }

    [Fact]
    public void PresentationCoordinatorDeduplicatesRapidCrossHostOpen()
    {
        DialogPresentationCoordinator coordinator = new();
        Assert.True(coordinator.TryBegin(DialogPresentationKind.ShellOverlay, out long shellLease));
        Assert.False(coordinator.TryBegin(DialogPresentationKind.ShellOverlay, out _));
        Assert.False(coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out _));
        Assert.Equal(DialogPresentationKind.ShellOverlay, coordinator.ActiveKind);

        Assert.True(coordinator.Complete(shellLease));
        Assert.True(coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out long dialogLease));
        Assert.Equal(DialogPresentationKind.NativeContentDialog, coordinator.ActiveKind);
        Assert.True(coordinator.Complete(dialogLease));
    }

    [Theory]
    [InlineData(900, 700)]
    [InlineData(760, 650)]
    [InlineData(640, 600)]
    public void SharedLayoutPolicyPreservesCompactBounds(double width, double height)
    {
        DialogLayoutTokenSet tokens = new(
            CompactBreakpoint: 640,
            CompactMargin: 12,
            StandardMargin: 24,
            ConfirmationPreferredWidth: 620,
            CompactFormPreferredWidth: 480,
            StandardPreferredWidth: 620,
            EditorPreferredWidth: 840,
            ConfirmationPreferredHeight: 360,
            CompactFormPreferredHeight: 340,
            StandardPreferredHeight: 520,
            EditorPreferredHeight: 720,
            PreferredMinimumWidth: 320);
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(width, height, tokens);

        Assert.True(metrics.MinimumWidth <= metrics.MaximumWidth);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
        Assert.InRange(metrics.MaximumWidth, 1, 620);
        Assert.InRange(metrics.MaximumHeight, 1, 720);
    }
}
