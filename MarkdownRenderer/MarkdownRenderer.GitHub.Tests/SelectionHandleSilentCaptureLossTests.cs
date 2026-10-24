using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-037: WinUI raises no PointerCaptureLost when a touch capture is released
/// by code or by a tree change, because it keeps a replay pointer point only for
/// mouse. The live touch-lifecycle capture-loss case moved a dragging handle's
/// capture and the renderer kept a stale DraggingStartHandle owner after the
/// contact ended. The control must detect the lost capture itself.
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

    [Fact]
    public void RetirementMatchesTheRoutedHandleCancelAndGuardsQueuedInput()
    {
        string body = MethodBody(ReadControl("MarkdownRendererControl.cs"), "private bool RetireSilentlyLostSelectionHandleCapture()");
        Assert.Contains("IsSelectionHandleCaptureSilentlyLost(", body, StringComparison.Ordinal);
        Assert.Contains("_deferredPointerCaptures.ContainsKey(pointerId) || HasQueuedPointerInput(pointerId)", body, StringComparison.Ordinal);
        Assert.Contains("ContainsPointerCapture(captures, pointerId)", body, StringComparison.Ordinal);
        int cancelContact = body.IndexOf("_touchTapSuppression.CancelContact(pointerId);", StringComparison.Ordinal);
        int reset = body.IndexOf("ResetSelectionHandleDrag();", StringComparison.Ordinal);
        int cancel = body.IndexOf("_touchSelection.Cancel(pointerId);", StringComparison.Ordinal);
        Assert.True(cancelContact >= 0 && reset > cancelContact && cancel > reset,
            "Retirement must cancel the tap contact, reset the drag and cancel the touch owner, as a routed handle cancel does.");
        Assert.Contains("UpdateSelectionHandles();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameClockRunsForTheWholeDragAndChecksCaptureUnderTheLease()
    {
        string frame = MethodBody(ReadControl("MarkdownRendererControl.cs"), "private void OnSelectionHandleFrame(object? sender, object args)");
        int lease = frame.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int retire = frame.IndexOf("RetireSilentlyLostSelectionHandleCapture()", StringComparison.Ordinal);
        Assert.True(lease >= 0 && retire > lease, "The capture check must run under the snapshot interaction lease.");
        // Before P1-037 a stationary contact stopped the clock, so nothing
        // noticed a capture lost while the finger rested on the handle.
        Assert.DoesNotContain("if (!keepRunning)", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void NewPressesRetireASilentlyLostDragFirst()
    {
        string dispatch = MethodBody(ReadControl("MarkdownRendererControl.PointerInput.cs"),
            "private void DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)");
        int retire = dispatch.IndexOf("RetireSilentlyLostSelectionHandleCapture();", StringComparison.Ordinal);
        int process = dispatch.IndexOf("ProcessPointerInput(ref input);", StringComparison.Ordinal);
        Assert.True(retire >= 0 && process > retire, "A new press must retire a silently lost drag before it is processed.");
        string guard = dispatch[..retire];
        Assert.Contains("kind is PointerInputKind.Press or PointerInputKind.HandlePress", guard, StringComparison.Ordinal);
        Assert.Contains("snapshot.TryBeginInteraction()", guard[guard.LastIndexOf("kind is PointerInputKind.Press or PointerInputKind.HandlePress", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    private static string ReadControl(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
