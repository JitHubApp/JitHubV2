using System;

namespace MarkdownRenderer.Controls.Internal.Input;

/// <summary>
/// Reads system input settings that WinUI does not expose.
/// </summary>
internal static class SystemInputMetrics
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>
    /// The system double-click interval in milliseconds, or 500 if it cannot
    /// be read.
    /// </summary>
    internal static int GetDoubleClickTimeMs()
    {
        try { return (int)Math.Min(GetDoubleClickTime(), (uint)int.MaxValue); } catch { return 500; }
    }
}
