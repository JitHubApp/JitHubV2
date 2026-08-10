// SPDX-License-Identifier: MIT

using System;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer;

/// <summary>
/// Process-lifetime hooks for native resources owned by MarkdownRenderer.
/// </summary>
public static class MarkdownRendererRuntime
{
    private static int _shutdownStarted;

    /// <summary>
    /// Gets whether the host app has begun process shutdown.
    /// </summary>
    public static bool IsShutdownInProgress =>
        System.Threading.Volatile.Read(ref _shutdownStarted) != 0;

    /// <summary>
    /// Marks the process as closing before XAML starts unloading controls.
    /// </summary>
    public static void BeginShutdown()
    {
        if (System.Threading.Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            ThorVgRasterizer.BeginShutdown();
        }
    }

    /// <summary>
    /// Stops background-native markdown rendering work and releases native
    /// renderer process state. Call once while the app is closing.
    /// </summary>
    public static void Shutdown(TimeSpan? timeout = null)
    {
        BeginShutdown();
        ThorVgRasterizer.ShutdownForProcessExit(timeout ?? TimeSpan.FromSeconds(2));
    }
}
