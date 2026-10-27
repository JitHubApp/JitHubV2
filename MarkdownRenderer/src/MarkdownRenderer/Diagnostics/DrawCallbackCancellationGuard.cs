using System;

namespace MarkdownRenderer.Diagnostics;

/// <summary>Contains expected cancellation at the Win2D draw boundary.</summary>
internal static class DrawCallbackCancellationGuard
{
    public static bool TryDraw(Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        try
        {
            draw();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
