using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PointerSnapshotReentrancyFenceTests
{
    [Fact]
    public void PointerPressRechecksTheLeasedSnapshotAfterFocusBeforeReadingGeometry()
    {
        string source = ReadControlSource();
        string press = ReadRegion(
            source,
            "private void ProcessPointerPressed(ref PointerInput e)",
            "private void OnTapped(");

        Assert.Contains("if (_snapshot is not { } snapshot || _canvas is null)", press, StringComparison.Ordinal);
        Assert.Contains("long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);", press, StringComparison.Ordinal);
        Assert.Contains("long layoutRevision = snapshot.LayoutRevision;", press, StringComparison.Ordinal);
        AssertOrdered(
            press,
            "FocusRendererForPointerInteraction();",
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))",
            "if (snapshot.HitTest(pt, out var pos))");
        Assert.DoesNotContain("_snapshot.HitTest(pt", press, StringComparison.Ordinal);
        Assert.Contains("ExpandSelectionToBlock(\n                    snapshot, pos, snapshotGeneration, layoutRevision)", press, StringComparison.Ordinal);
        Assert.Contains("ExpandSelectionToWord(\n                    snapshot, pos, snapshotGeneration, layoutRevision)", press, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionCallbacksCannotContinueIntoTheNextMutationOrPointerCaptureAfterReplacement()
    {
        string source = ReadControlSource();
        string press = ReadRegion(
            source,
            "private void ProcessPointerPressed(ref PointerInput e)",
            "private void OnTapped(");
        string wordExpansion = ReadRegion(
            source,
            "private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToWord(",
            "private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToBlock(");
        string blockExpansion = ReadRegion(
            source,
            "private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToBlock(",
            "private static Layout.Boxes.InlineContainerBox? FindInlineContainerAt(");

        AssertSelectionMutationIsFenced(wordExpansion);
        AssertSelectionMutationIsFenced(blockExpansion);
        AssertOrdered(
            press,
            "_selection.SetAnchor(_dragAnchorStart);",
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))",
            "return;",
            "_selection.ExtendTo(_dragAnchorEnd);",
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))",
            "return;",
            "if (!CapturePointerForInput(_canvas, e))");
        Assert.Contains(
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))\n                    return;\n                _selection.ExtendTo(_dragAnchorEnd);",
            press,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))\n                return;\n            // Selection is rendered by the XAML overlay",
            press,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HoldingAndDoubleTapStageCaptureReleaseAndKeepTheSnapshotAfterFocus()
    {
        string source = ReadControlSource();
        string doubleTap = ReadRegion(
            source,
            "private void ProcessDoubleTapped(ref PointerInput e)",
            "private void OnHolding(");
        string holding = ReadRegion(
            source,
            "private void ProcessHolding(ref PointerInput e)",
            "private void EndCapturedSelectionForContextGesture(");
        string selectWord = ReadRegion(
            source,
            "private bool TrySelectWordForTouchContext(",
            "private bool TryHitTestTouchSelection(");
        string hitTest = ReadRegion(
            source,
            "private bool TryHitTestTouchSelection(",
            "private bool IsPointInsideSelection(");
        string stageCapture = ReadRegion(
            source,
            "private void EndCapturedSelectionForContextGesture(",
            "private bool TrySelectWordForTouchContext(");

        AssertGestureCapturesBeforeReleaseAndRechecks(doubleTap);
        AssertGestureCapturesBeforeReleaseAndRechecks(holding);
        AssertOrdered(
            selectWord,
            "TryHitTestTouchSelection(point, snapshot, capturedContact, out DocumentPosition position)",
            "FocusRendererForPointerInteraction();",
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))",
            "ExpandSelectionToWord(\n            snapshot, position, snapshotGeneration, layoutRevision)");
        AssertOrdered(
            selectWord,
            "UpdateSelectionOverlay();",
            "return IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) && _selection.IsActive;");
        Assert.Contains("IsSelectableTouchPoint(gesturePoint, snapshot, out position)", hitTest, StringComparison.Ordinal);
        Assert.Contains("snapshot.HitTest(point, out position)", hitTest, StringComparison.Ordinal);
        Assert.DoesNotContain("_snapshot.HitTest", hitTest, StringComparison.Ordinal);
        AssertOrdered(stageCapture,
            "uint pointerId = _pointerSession.PointerId;",
            "_pointerSession = default;",
            "input.InteractionCanvas = _canvas;",
            "input.ReleasePointerIdAfterInteraction = pointerId;",
            "input.ReleaseCanvasCaptureAfterInteraction = pointerId != 0;");
        Assert.DoesNotContain("ReleasePointerCapture(", stageCapture, StringComparison.Ordinal);
    }

    private static void AssertSelectionMutationIsFenced(string method)
    {
        AssertOrdered(
            method,
            "_selection.SetAnchor(start);",
            "if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))",
            "return (start, end);",
            "_selection.ExtendTo(end);");
    }

    private static void AssertGestureCapturesBeforeReleaseAndRechecks(string method)
    {
        Assert.Contains("if (_snapshot is not { } snapshot || _canvas is null", method, StringComparison.Ordinal);
        Assert.Contains("long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);", method, StringComparison.Ordinal);
        Assert.Contains("long layoutRevision = snapshot.LayoutRevision;", method, StringComparison.Ordinal);
        AssertOrdered(
            method,
            "EndCapturedSelectionForContextGesture(ref e);",
            "if (IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) &&",
            "TrySelectWordForTouchContext(",
            "point,",
            "snapshot,",
            "snapshotGeneration,",
            "layoutRevision,",
            "e.HasGestureContact ? e.GestureContact : null)");
    }

    private static void AssertOrdered(string source, params string[] fragments)
    {
        int previous = -1;
        foreach (string fragment in fragments)
        {
            int current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{fragment}' after the preceding fenced operation.");
            previous = current;
        }
    }

    private static string ReadRegion(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find source member '{start}'.");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find the end of source member '{start}'.");
        return source[startIndex..endIndex];
    }

    private static string ReadControlSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string[] candidates =
            [
                Path.Combine(directory.FullName, "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs"),
                Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs"),
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs from the test output.");
    }
}
