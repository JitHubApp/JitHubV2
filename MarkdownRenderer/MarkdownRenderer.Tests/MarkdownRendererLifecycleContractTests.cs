using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownRendererLifecycleContractTests
{
    [Fact]
    public void Unload_DetachesWin2DCallbacksBeforeReleasingSnapshots()
    {
        string source = ReadSource(
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs");

        int unload = source.IndexOf("private void OnUnloaded()", StringComparison.Ordinal);
        int detach = source.IndexOf("DetachCanvasRenderHandlers();", unload, StringComparison.Ordinal);
        int release = source.IndexOf("ReleaseLoadedResources();", detach, StringComparison.Ordinal);

        Assert.True(unload >= 0);
        Assert.True(detach > unload);
        Assert.True(release > detach);
        Assert.Matches(new Regex(@"if\s*\(_isUnloaded\)\s*return;"), source);
    }

    [Fact]
    public void Unload_RemovesWin2DControlsBeforeReleasingLoadedResources()
    {
        string source = ReadSource(
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs");

        int unload = source.IndexOf("private void OnUnloaded()", StringComparison.Ordinal);
        int removeCanvas = source.IndexOf("_canvas?.RemoveFromVisualTree();", unload, StringComparison.Ordinal);
        int release = source.IndexOf("ReleaseLoadedResources();", removeCanvas, StringComparison.Ordinal);

        Assert.True(unload >= 0);
        Assert.True(removeCanvas > unload);
        Assert.True(release > removeCanvas);
        Assert.DoesNotContain("new CanvasControl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new CanvasImageSource", source, StringComparison.Ordinal);
        Assert.Contains("private Canvas? _selectionAdorner;", source, StringComparison.Ordinal);
        Assert.Contains("PaintInteractiveDocumentState(ds, region);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JitHubHost_RemovesRendererBeforeDisposingIt()
    {
        string source = ReadSource(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs");

        int remove = source.IndexOf("RendererHost.Children.Remove(_renderer);", StringComparison.Ordinal);
        int dispose = source.IndexOf("_renderer.Dispose();", remove, StringComparison.Ordinal);

        Assert.True(remove >= 0);
        Assert.True(dispose > remove);
    }

    [Fact]
    public void AccessibilityScroll_RealizesLazyTargetBeforeImmediateViewportChange()
    {
        string source = ReadSource(
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs");

        int scrollToBlock = source.IndexOf("public void ScrollToBlock(int blockIndex)", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("private static double? FindNearestScrollAnchor", scrollToBlock, StringComparison.Ordinal);

        Assert.True(scrollToBlock >= 0 && nextMethod > scrollToBlock);
        string method = source[scrollToBlock..nextMethod];
        int lazyGuard = method.IndexOf("_snapshot.IsLazyLayoutEnabled", StringComparison.Ordinal);
        int realize = method.IndexOf("EnsureLazyLayoutForBand(", lazyGuard, StringComparison.Ordinal);
        int scroll = method.IndexOf("disableAnimation: true", realize, StringComparison.Ordinal);
        Assert.True(lazyGuard >= 0);
        Assert.True(realize > lazyGuard);
        Assert.True(scroll > realize);
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(segments).ToArray()));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
