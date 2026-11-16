using System;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime;
using JitHub.WinUI.Views.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers;

/// <summary>
/// The main window's visual-tree side of <see cref="MarkdownWindowCloseCoordinator"/>:
/// it finds the window's page and the shell's nested content, and removes each
/// through <see cref="FrameContentDetacher"/> so the Markdown controls in it
/// retire before the app's shared Markdown providers shut down.
/// </summary>
internal static class MarkdownWindowClose
{
    /// <summary>
    /// Retires the Markdown views in <paramref name="contentFrame"/> and the
    /// app's Markdown providers, then runs <paramref name="close"/>, which runs
    /// even when a step failed.
    /// </summary>
    /// <param name="contentFrame">The window's root content frame.</param>
    /// <param name="close">Lets the window close and closes it.</param>
    internal static Task RetireViewsThenCloseAsync(Frame contentFrame, Action close)
    {
        App app = (App)Application.Current;
        FrameworkElement? page = contentFrame.Content as FrameworkElement;
        return new MarkdownWindowCloseCoordinator(app.MarkdownRenderers, App.LogHandledException)
            .RetireViewsThenCloseAsync(
                page is ShellPage shellPage ? shellPage.DetachNestedContentForMarkdownShutdownAsync : null,
                timeout => FrameContentDetacher.DetachAsync(contentFrame, page, timeout),
                app.MarkdownRuntime.ShutdownAsync,
                close);
    }
}
