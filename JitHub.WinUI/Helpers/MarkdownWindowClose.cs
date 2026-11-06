using System;
using System.Threading.Tasks;
using JitHub.Markdown;
using JitHub.WinUI.Views.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers;

/// <summary>
/// The main window's visual-tree side of <see cref="MarkdownWindowCloseCoordinator"/>:
/// it finds the window's page and the shell's nested content, and removes each
/// with a bounded wait for its Unloaded event so the Markdown controls in it
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
                timeout => DetachPageAsync(contentFrame, page, timeout),
                app.MarkdownRuntime.ShutdownAsync,
                close);
    }

    // Clears the frame and, when the page read before the close began was
    // loaded, waits up to the timeout for it to unload.
    private static async Task DetachPageAsync(Frame contentFrame, FrameworkElement? page, TimeSpan unloadTimeout)
    {
        if (page is null)
        {
            contentFrame.Content = null;
            return;
        }

        bool wasLoaded = page.IsLoaded;
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
        if (wasLoaded)
            page.Unloaded += OnUnloaded;

        try
        {
            contentFrame.Content = null;
            if (wasLoaded)
            {
                try
                {
                    await unloaded.Task.WaitAsync(unloadTimeout);
                }
                catch (TimeoutException)
                {
                    // Bounded: MarkdownViewShutdown drains the renderers next.
                }
            }
        }
        finally
        {
            if (wasLoaded)
                page.Unloaded -= OnUnloaded;
        }
    }
}
