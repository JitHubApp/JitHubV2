using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The renderer services a <see cref="SelectionHandlesPresenter"/> calls
/// while it places the touch selection handles and runs their drags.
/// </summary>
internal interface ISelectionHandlesHost
{
    /// <summary>The applied theme, which sets the handles' size and color.</summary>
    ThemeSnapshot? ThemeSnapshot { get; }

    /// <summary>Resolves a localized string, falling back to <paramref name="fallback"/>.</summary>
    string ResolveLocalizedString(string key, string fallback);

    /// <summary>
    /// Starts geometry recovery when <paramref name="snapshot"/> needs it.
    /// Returns true when recovery was requested.
    /// </summary>
    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    /// <summary>Moves keyboard focus to the renderer for a pointer interaction.</summary>
    void FocusRendererForPointerInteraction();

    /// <summary>
    /// Snaps a canvas point inside an inline or block embed to the embed's
    /// nearer edge, so a selection never ends inside it.
    /// </summary>
    bool TryHitTestEmbed(Point point, out DocumentPosition position);

    /// <summary>
    /// True when the deferred input path still holds a capture for, or a
    /// queued event from, <paramref name="pointerId"/>.
    /// </summary>
    bool HasDeferredInputFor(uint pointerId);

    /// <summary>Resumes a rebuild that waited for the pointer interaction to end.</summary>
    void ResumePendingRebuildAfterPointerInteraction();

    /// <summary>
    /// Drops the request to restore the touch handles after the next rebuild.
    /// A new handle drag owns their presentation.
    /// </summary>
    void CancelPendingTouchSelectionRestore();
}
