using System;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Embeds;

/// <summary>A new placement for an inline embed inside a horizontally scrolled block.</summary>
internal readonly struct InlineEmbedPlacementUpdate(
    WeakReference<InlineEmbedPlan> Plan,
    Rect Rect)
{
    public readonly WeakReference<InlineEmbedPlan> Plan = Plan;
    public readonly Rect Rect = Rect;
}
