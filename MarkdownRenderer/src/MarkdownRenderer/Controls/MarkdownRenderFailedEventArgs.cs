using System;

namespace MarkdownRenderer.Controls;

/// <summary>Event data for a Markdown document render failure.</summary>
public sealed class MarkdownRenderFailedEventArgs : EventArgs
{
    /// <summary>Initializes render-failure event data.</summary>
    public MarkdownRenderFailedEventArgs(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>Gets the exception raised by the render pipeline.</summary>
    public Exception Exception { get; }
}
