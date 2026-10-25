using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-040 (200% P1-016): a touch selection's handles hang 0.22 of their 44-DIP
/// target over the selected line, so at 200% the start handle covered the
/// vertical center of the word it marked. Right-tap is wired only on the
/// canvas; the handles are Buttons in a sibling overlay, so a mouse
/// right-click on that part of the selected word opened no context menu.
/// A mouse right-click on a handle must open the same selection menu as the
/// text beneath it.
/// </summary>
public sealed class SelectionHandleContextMenuTests
{
    [Fact]
    public void SelectionHandlesForwardRightTapToTheSelectionMenu()
    {
        string control = ReadControlSource();
        string create = Slice(control, "private Button CreateSelectionHandle(", "private void ");
        Assert.Contains("handle.RightTapped += OnSelectionHandleRightTapped;", create);

        string detach = Slice(control, "private void DetachSelectionHandleHandlers(Button? handle)", "private ");
        Assert.Contains("handle.RightTapped -= OnSelectionHandleRightTapped;", detach);

        string forward = Slice(control, "private void OnSelectionHandleRightTapped(", "private ");
        Assert.Contains("DispatchRecognizedGesture(PointerInputKind.RightTap, e.GetPosition(_canvas), e.PointerDeviceType)", forward);
        Assert.Contains("ForwardsSelectionHandleRightTap(", forward);
    }

    [Fact]
    public void OnlyMouseRightTapOnAHandleOpensTheMenu()
    {
        Assert.True(MarkdownPointerGesturePolicy.ForwardsSelectionHandleRightTap(MarkdownPointerModality.Mouse));
        Assert.False(MarkdownPointerGesturePolicy.ForwardsSelectionHandleRightTap(MarkdownPointerModality.Touch));
        Assert.False(MarkdownPointerGesturePolicy.ForwardsSelectionHandleRightTap(MarkdownPointerModality.Pen));
        Assert.False(MarkdownPointerGesturePolicy.ForwardsSelectionHandleRightTap(MarkdownPointerModality.Unknown));
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
