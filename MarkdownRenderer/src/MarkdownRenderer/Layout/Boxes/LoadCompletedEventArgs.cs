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
    private static readonly LoadCompletedEventArgs LayoutInvalidatedArgs = new(layoutInvalidated: true);
    private static readonly LoadCompletedEventArgs PaintOnlyArgs = new(layoutInvalidated: false);
    private static readonly LoadCompletedEventArgs BitmapOutputRecoverySignalArgs =
        new(layoutInvalidated: false, isBitmapOutputRecoverySignal: true);

    public LoadCompletedEventArgs(bool layoutInvalidated)
        : this(layoutInvalidated, isBitmapOutputRecoverySignal: false)
    {
    }

    private LoadCompletedEventArgs(bool layoutInvalidated, bool isBitmapOutputRecoverySignal)
    {
        LayoutInvalidated = layoutInvalidated;
        IsBitmapOutputRecoverySignal = isBitmapOutputRecoverySignal;
    }

    internal static LoadCompletedEventArgs For(bool layoutInvalidated) =>
        layoutInvalidated ? LayoutInvalidatedArgs : PaintOnlyArgs;

    /// <summary>A coalesced source-free hint that the host should probe its
    /// bounded bitmap-admission recovery queue. It does not indicate changed
    /// image content and must not trigger layout, UIA, or paint work directly.</summary>
    internal static LoadCompletedEventArgs ForBitmapOutputRecoverySignal() =>
        BitmapOutputRecoverySignalArgs;

    /// <summary>True when intrinsic size or load state changed; host should
    /// rebuild layout. False when the box was already laid out and only the
    /// paint contents are now ready (canvas invalidate is sufficient).</summary>
    public readonly bool LayoutInvalidated;

    /// <summary>True only for a source-free bitmap-admission recovery hint;
    /// hosts must handle it before ordinary completion/UIA/paint processing.</summary>
    public readonly bool IsBitmapOutputRecoverySignal;
}
