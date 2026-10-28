using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A focus reveal belongs to the moment focus moved: any later scroll request,
/// or a viewport the reveal did not set, ends it. Otherwise a reveal retrying
/// while diagrams lay out scrolls back over a newer UIA or user scroll.
/// </summary>
public sealed class FocusRevealPolicyTests
{
    [Fact]
    public void RevealIsCurrentOnlyWhileTheViewportIsWhereFocusOrTheRevealLeftIt()
    {
        var origin = new FocusRevealOrigin(ViewportRequestSerial: 4, ViewportTop: 0, RevealTop: null);
        Assert.True(FocusRevealPolicy.IsCurrent(origin, 4, 0));
        Assert.True(FocusRevealPolicy.IsCurrent(origin, 4, 0.75));

        // A newer scroll was requested but has not applied yet, so the offset
        // still reads 0.
        Assert.False(FocusRevealPolicy.IsCurrent(origin, 5, 0));
        // The newer scroll applied first.
        Assert.False(FocusRevealPolicy.IsCurrent(origin, 4, 7103));

        // The reveal's own animated scroll toward 314.27 keeps it current,
        // including a viewport clamped short of the target.
        var revealed = origin with { RevealTop = 314.27 };
        Assert.True(FocusRevealPolicy.IsCurrent(revealed, 4, 314.27));
        Assert.True(FocusRevealPolicy.IsCurrent(revealed, 4, 0));
        Assert.True(FocusRevealPolicy.IsCurrent(revealed, 4, 200));
        Assert.False(FocusRevealPolicy.IsCurrent(revealed, 4, 400));
        Assert.False(FocusRevealPolicy.IsCurrent(revealed, 4, 7103));
        Assert.False(FocusRevealPolicy.IsCurrent(revealed, 5, 200));

        Assert.False(FocusRevealPolicy.IsCurrent(null, 4, 0));
        Assert.False(FocusRevealPolicy.IsCurrent(origin, 4, double.NaN));
    }
}
