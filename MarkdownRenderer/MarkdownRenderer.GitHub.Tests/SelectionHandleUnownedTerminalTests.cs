using MarkdownRenderer.Controls;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-041 (200% P1-016): a touch double-tap selects the word on its second
/// tap and shows the selection handles while that finger is still down. At
/// 200% the start handle's top edge sits 1 px above the word's vertical center,
/// so the second contact's release was routed to the handle instead of the
/// canvas that owned the contact. The handle ignored a release it did not own,
/// the contact never ended, and every later touch contact on the document was
/// rejected until a mouse press or page change. A handle must hand terminal
/// events for contacts it does not own to the canvas pointer path.
/// </summary>
public sealed class SelectionHandleUnownedTerminalTests
{
    [Fact]
    public void HandleForwardsReleasesAndCancelsItDoesNotOwnToTheCanvasPath()
    {
        string control = ReadControlSource();

        string release = Slice(control, "private void ProcessSelectionHandlePointerReleased(ref PointerInput e)", "    private ");
        int releaseGuard = release.IndexOf("if (_selectionHandlePointerId != e.Pointer.PointerId)", System.StringComparison.Ordinal);
        Assert.True(releaseGuard >= 0, "The handle release ownership guard was not found.");
        string releaseUnowned = release[releaseGuard..release.IndexOf('}', releaseGuard)];
        Assert.Contains("ProcessPointerReleased(ref e);", releaseUnowned);

        string cancel = Slice(control, "private void ProcessSelectionHandlePointerCanceled(ref PointerInput e)", "    private ");
        int cancelGuard = cancel.IndexOf("if (_selectionHandlePointerId != e.Pointer.PointerId)", System.StringComparison.Ordinal);
        Assert.True(cancelGuard >= 0, "The handle cancel ownership guard was not found.");
        string cancelUnowned = cancel[cancelGuard..cancel.IndexOf('}', cancelGuard)];
        Assert.Contains("ProcessPointerCanceledOrCaptureLost(ref e);", cancelUnowned);
    }

    [Fact]
    public void AnUnendedContactBlocksEveryLaterContact()
    {
        // The mechanism the forwarding prevents: ownership belongs to the first
        // contact until its terminal event arrives.
        var policy = new TouchTapSuppressionPolicy();
        Assert.True(policy.TryBeginContact(7, out _));
        Assert.False(policy.TryBeginContact(8, out _));
        policy.EndContact(7);
        Assert.True(policy.TryBeginContact(8, out _));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' was not found.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, System.StringComparison.Ordinal);
        Assert.True(end > start, $"No '{endMarker}' follows '{startMarker}'.");
        return source[start..end];
    }

    private static string ReadControlSource()
    {
        for (System.IO.DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = System.IO.Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path);
        }
        throw new System.IO.FileNotFoundException("Could not locate MarkdownRendererControl.cs.");
    }
}
