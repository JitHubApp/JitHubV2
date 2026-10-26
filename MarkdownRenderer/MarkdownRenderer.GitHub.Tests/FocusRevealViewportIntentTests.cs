using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-042: on a cold start of the Mermaid page, a UIA scroll to the bottom
/// ended at 1.35% or snapped back from 100% to 4.42%. Keyboard focus sat on
/// the page's first focusable item; revealing it kept retrying while the
/// diagrams laid out, and the retry scrolled back to that item after the
/// user's newer scroll request. A focus reveal belongs to the moment focus
/// moved: any later scroll request, or a viewport the reveal did not set,
/// ends it.
/// </summary>
public sealed class FocusRevealViewportIntentTests
{
    [Fact]
    public void FocusRevealRetriesYieldToNewerScrollRequests()
    {
        string control = ReadControlSource();

        foreach (string entry in new[]
                 {
                     "internal void SetAutomationVerticalScrollPercent(double percent)",
                     "internal void ScrollFromAutomation(ScrollAmount amount)",
                     "internal void ScrollDocumentRectIntoView(Windows.Foundation.Rect rect, bool alignToTop)",
                     "public void ScrollToBlock(int blockIndex)",
                 })
        {
            Assert.Contains("NoteViewportRequest();", Slice(control, entry, "\n    }\n"));
        }

        string apply = Slice(control, "private void ApplyFocusedItemScroll(", "\n    }\n");
        Assert.Contains("if (!IsFocusRevealCurrent())", apply);

        string retry = Slice(control, "private void ProcessPendingFocusGeometry()", "\n    }\n");
        Assert.Contains("IsFocusRevealCurrent()", retry);

        foreach (string mover in new[]
                 {
                     "private bool FocusLinkedAtomicFromAutomation(InlineRun run)",
                     "internal bool FocusVectorSemanticFromAutomation(",
                     "internal bool FocusHorizontalOverflowFromAutomation(IHorizontalOverflowBox overflow)",
                     "private bool CommitFocusedItem(bool reverse)",
                 })
        {
            string body = Slice(control, mover, "\n    }\n");
            Assert.Contains("BeginFocusedItemReveal()", body);
            Assert.DoesNotContain(" ScrollFocusedItemIntoView();", body);
        }
    }

    /// <summary>
    /// The P1-042 root cause: a UIA client sets focus on the document before
    /// scrolling it. FocusDocumentFromAutomation suppresses first-item
    /// promotion for that focus, but cleared the flag on the dispatcher, and
    /// WinUI raises GotFocus asynchronously after it. Promotion then revealed
    /// the first item and scrolled back over the client's scroll. The
    /// suppression must be captured by the synchronous GettingFocus event.
    /// </summary>
    [Fact]
    public void FocusEntrySuppressionIsCapturedSynchronouslyBeforeGotFocus()
    {
        string control = ReadControlSource();

        string getting = Slice(control, "private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)", "\n    }\n");
        Assert.Contains("_focusEntrySuppressed = _suppressNextFocusPromotion;", getting);

        string got = Slice(control, "private void OnGotFocus(object sender, RoutedEventArgs e)", "\n    }\n");
        int suppression = got.IndexOf("if (_focusEntrySuppressed || _suppressNextFocusPromotion)", System.StringComparison.Ordinal);
        int promotion = got.IndexOf("MoveFocus(", System.StringComparison.Ordinal);
        Assert.True(suppression >= 0, "OnGotFocus does not consult the synchronously captured suppression.");
        Assert.True(promotion > suppression, "Suppression must be checked before first-item promotion.");
    }

    [Fact]
    public void RevealIsCurrentOnlyWhileTheViewportIsWhereFocusOrTheRevealLeftIt()
    {
        var origin = new FocusRevealOrigin(ViewportRequestSerial: 4, ViewportTop: 0, RevealTop: null);
        Assert.True(FocusRevealPolicy.IsCurrent(origin, 4, 0));
        Assert.True(FocusRevealPolicy.IsCurrent(origin, 4, 0.75));

        // The cold-start failure: the UIA scroll to the bottom was requested
        // but had not applied yet, so the offset still read 0.
        Assert.False(FocusRevealPolicy.IsCurrent(origin, 5, 0));
        // The snap-back: the bottom scroll applied first.
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

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' was not found.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, System.StringComparison.Ordinal);
        Assert.True(end > start, $"No '{endMarker.Trim()}' follows '{startMarker}'.");
        return source[start..end];
    }

    private static string ReadControlSource()
    {
        for (System.IO.DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = System.IO.Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path).Replace("\r\n", "\n");
        }
        throw new System.IO.FileNotFoundException("Could not locate MarkdownRendererControl.cs.");
    }
}
