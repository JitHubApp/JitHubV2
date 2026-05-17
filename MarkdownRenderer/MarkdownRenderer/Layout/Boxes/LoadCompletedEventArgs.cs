using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Payload for <see cref="ImageBox.LoadCompleted"/>. Lets the host distinguish
/// load completions that change intrinsic layout (initial fetch, decode) from
/// repaint-only completions (e.g. device-specific SVG re-parse against the
/// drawing-session device). The former requires a full rebuild; the latter
/// only a canvas invalidation, avoiding a rebuild/reparse feedback loop.
/// </summary>
internal sealed class LoadCompletedEventArgs : EventArgs
{
    public LoadCompletedEventArgs(bool layoutInvalidated)
    {
        LayoutInvalidated = layoutInvalidated;
    }

    /// <summary>True when intrinsic size or load state changed; host should
    /// rebuild layout. False when the box was already laid out and only the
    /// paint contents are now ready (canvas invalidate is sufficient).</summary>
    public bool LayoutInvalidated { get; }
}
