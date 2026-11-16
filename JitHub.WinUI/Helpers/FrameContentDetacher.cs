using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers;

/// <summary>
/// Removes a frame's page during the Markdown close and waits, bounded, for the
/// page's Unloaded event, so the Markdown controls in it retire before the
/// caller drains the renderers and shuts their shared providers down.
/// </summary>
internal static class FrameContentDetacher
{
    /// <summary>
    /// Clears <paramref name="frame"/>'s content and, when <paramref name="page"/>
    /// was loaded, waits up to <paramref name="unloadTimeout"/> for it to unload.
    /// </summary>
    /// <param name="frame">The frame whose content is removed.</param>
    /// <param name="page">The page the caller read from the frame.</param>
    /// <param name="unloadTimeout">How long to wait for the page to unload.</param>
    internal static async Task DetachAsync(Frame frame, FrameworkElement? page, TimeSpan unloadTimeout)
    {
        if (page is null)
        {
            frame.Content = null;
            return;
        }

        bool wasLoaded = page.IsLoaded;
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
        if (wasLoaded)
            page.Unloaded += OnUnloaded;

        try
        {
            frame.Content = null;
            if (wasLoaded)
            {
                try
                {
                    await unloaded.Task.WaitAsync(unloadTimeout);
                }
                catch (TimeoutException)
                {
                    // Bounded: the caller drains the renderers next.
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
