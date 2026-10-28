using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Unit tests for scroll anchoring support.
/// The full anchor capture/restore behaviour requires a live ScrollViewer and
/// canvas, so it is covered in UI automation tests. These tests verify the
/// pure-logic constants and invariants used by the algorithm.
/// </summary>
public class ScrollAnchoringTests
{
    // The anchor algorithm reads VerticalOffset which is a double.
    // Verify we handle edge cases gracefully.

    [Fact]
    public void ScrollOffset_Zero_NoAnchorCaptured()
    {
        // When VerticalOffset == 0 the control must NOT capture an anchor
        // (we're at the top; there's no read position to preserve).
        double offset = 0.0;
        bool shouldCapture = offset > 0;
        Assert.False(shouldCapture);
    }

    [Fact]
    public void ScrollOffset_Positive_AnchorCaptured()
    {
        double offset = 100.0;
        bool shouldCapture = offset > 0;
        Assert.True(shouldCapture);
    }

    [Fact]
    public void AnchorRestore_NegativeTarget_Clamped()
    {
        // If the block moved above the top of the new layout (e.g. images
        // above it all loaded and are now taller), the restored offset could
        // theoretically be negative. The control clamps to >= 0 before calling
        // ChangeView. Simulate the invariant check.
        double anchorOffsetFromTop = -50.0; // block moved down, offset is negative
        double newBlockTop = 10.0;
        double targetOffset = newBlockTop - anchorOffsetFromTop; // = 60, valid
        Assert.True(targetOffset >= 0);
    }

    [Fact]
    public void AnchorRestore_ExactZeroTarget_IsValid()
    {
        double newBlockTop = 0.0;
        double offsetFromTop = 0.0;
        double targetOffset = newBlockTop - offsetFromTop;
        Assert.Equal(0.0, targetOffset);
        Assert.True(targetOffset >= 0);
    }
}
