using System;
using System.IO;
using MarkdownRenderer.Diagnostics;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class DrawCallbackCancellationGuardTests
{
    [Fact]
    public void TryDraw_ContainsOnlyExpectedCancellation()
    {
        Assert.False(DrawCallbackCancellationGuard.TryDraw(
            static () => throw new OperationCanceledException()));

        InvalidOperationException failure = new("draw failed");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => DrawCallbackCancellationGuard.TryDraw(() => throw failure)));
    }

    [Fact]
    public void TryDraw_ReturnsTrueAfterSuccessfulDraw()
    {
        bool called = false;
        Assert.True(DrawCallbackCancellationGuard.TryDraw(() => called = true));
        Assert.True(called);
    }

    [Fact]
    public void SelectionLayoutPreparation_DoesNotObserveSupersededBuildCancellation()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Layout",
            "Boxes",
            "InlineContainerBox.cs"));

        Assert.Contains(
            "ApplyRunStyles(_selectionLayout, applyColors: false, observeCancellation: false);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyEmbedSpacing(_selectionLayout, observeCancellation: false);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionAdorner_IsViewportBoundedAndLazy()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));

        Assert.Contains("Width = 1,", source, StringComparison.Ordinal);
        Assert.Contains("Height = 1,", source, StringComparison.Ordinal);
        Assert.Contains("Visibility = Visibility.Collapsed,", source, StringComparison.Ordinal);
        Assert.Contains("bool hasContent = HasInteractiveTextAdornerContent();", source, StringComparison.Ordinal);
        Assert.Contains("_selectionAdorner.Visibility = Visibility.Visible;", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
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
