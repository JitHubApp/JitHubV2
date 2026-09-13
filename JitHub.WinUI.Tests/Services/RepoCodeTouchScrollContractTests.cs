using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepoCodeTouchScrollContractTests
{
    [Fact]
    public void NativeEditorClaimsTouchDragsAndScrollsTheEditorInsteadOfSelectingText()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "CodeEditorControl.xaml.cs"));

        Assert.Contains("point.PointerDeviceType", source, StringComparison.Ordinal);
        Assert.Contains("PointerDeviceType.Touch", source, StringComparison.Ordinal);
        Assert.Contains("TouchScrollActivationDistance", source, StringComparison.Ordinal);
        Assert.Contains("AddHandler(UIElement.PointerPressedEvent", source, StringComparison.Ordinal);
        Assert.Contains("AddHandler(UIElement.PointerMovedEvent", source, StringComparison.Ordinal);
        Assert.Contains("AddHandler(UIElement.PointerReleasedEvent", source, StringComparison.Ordinal);
        Assert.Contains("AddHandler(UIElement.PointerCanceledEvent", source, StringComparison.Ordinal);
        Assert.Contains("AddHandler(UIElement.PointerCaptureLostEvent", source, StringComparison.Ordinal);
        Assert.Contains("CapturePointer(args.Pointer)", source, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", source, StringComparison.Ordinal);
        Assert.Contains("SetEmptySelection(InnerEditor.Editor.CurrentPos)", source, StringComparison.Ordinal);
        Assert.Contains("editor.LineScroll(0, lines)", source, StringComparison.Ordinal);
        Assert.Contains("editor.XOffset = _allowedEditorHorizontalOffset", source, StringComparison.Ordinal);

        int pressIndex = source.IndexOf("private void CodeEditor_PointerPressed", StringComparison.Ordinal);
        int moveIndex = source.IndexOf("private void CodeEditor_PointerMoved", StringComparison.Ordinal);
        int scrollIndex = source.IndexOf("private void ScrollEditorForTouch", StringComparison.Ordinal);
        Assert.True(pressIndex >= 0 && moveIndex > pressIndex && scrollIndex > moveIndex);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the JitHub repository root.");
    }
}
