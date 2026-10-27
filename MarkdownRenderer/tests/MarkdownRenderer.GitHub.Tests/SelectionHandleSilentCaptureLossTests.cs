using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// WinUI raises no PointerCaptureLost when a touch capture is released by code
/// or by a tree change, because it keeps a replay pointer point only for mouse.
/// Without detection, moving a dragging handle's capture left a stale
/// DraggingStartHandle owner after the contact ended. The control must detect
/// the lost capture itself.
/// </summary>
public sealed class SelectionHandleSilentCaptureLossTests
{
    [Fact]
    public void CaptureMissingFromAnActiveDragIsLost()
    {
#pragma warning disable MR1001 // Exercises the compatibility control's internal capture policy.
        Assert.True(MarkdownRendererControl.IsSelectionHandleCaptureSilentlyLost(
            dragPointerId: 7, hasCaptureTarget: true, captureViewAvailable: true,
            captureTargetHoldsPointer: false, hasDeferredCaptureOrQueuedInput: false));
#pragma warning restore MR1001
    }

    [Theory]
    [InlineData(0u, true, true, false, false)]  // no drag
    [InlineData(7u, false, true, false, false)] // capture not yet published
    [InlineData(7u, true, false, false, false)] // WinUI capture view unavailable: unknown
    [InlineData(7u, true, true, true, false)]   // the handle still owns the pointer
    [InlineData(7u, true, true, false, true)]   // a deferred capture or queued terminal must replay
    public void UnknownOrStillOwnedCaptureIsNeverRetired(
        uint dragPointerId, bool hasCaptureTarget, bool captureViewAvailable,
        bool captureTargetHoldsPointer, bool hasDeferredCaptureOrQueuedInput)
    {
#pragma warning disable MR1001 // Exercises the compatibility control's internal capture policy.
        Assert.False(MarkdownRendererControl.IsSelectionHandleCaptureSilentlyLost(
            dragPointerId, hasCaptureTarget, captureViewAvailable,
            captureTargetHoldsPointer, hasDeferredCaptureOrQueuedInput));
#pragma warning restore MR1001
    }
}
