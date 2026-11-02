using System;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// One hosted element's position and factory, captured up front so the
/// element is realized only while scrolling keeps it near the viewport. An
/// element beyond the wider derealization band leaves the visual tree and is
/// handed back to its factory, which keeps memory bounded for long documents
/// with many hosted controls.
/// </summary>
internal abstract class EmbedPlan
{
    public Rect Rect;
    public FrameworkElement? Realized;
    public FocusableItem? FocusIdentity;
    public virtual bool HasActiveWork => Realized is not null;
    public abstract object LogicalOwner { get; }
    public abstract void Realize(EmbedPlanContext context);
    public abstract void Derealize(EmbedPlanContext context);
    public abstract bool IsSameLogicalEmbed(EmbedPlan other);
    public virtual void UpdatePlacement() => UpdatePlacement(snapEdges: true);

    protected void UpdatePlacement(bool snapEdges)
    {
        FrameworkElement? element = Realized;
        if (element is null) return;
        // Rect was copied under the snapshot read lease. A hosted element
        // can synchronously recycle/unload us from any DP callback.
        Rect rect = Rect;
        double left = Math.Round(rect.X);
        double top = Math.Round(rect.Y);
        double width = snapEdges ? Math.Round(rect.Right) - left : Math.Round(rect.Width);
        double height = snapEdges ? Math.Round(rect.Bottom) - top : Math.Round(rect.Height);
        element.Width = Math.Max(0, width);
        if (!ReferenceEquals(Realized, element)) return;
        element.Height = Math.Max(0, height);
        if (!ReferenceEquals(Realized, element)) return;
        Canvas.SetLeft(element, left);
        if (!ReferenceEquals(Realized, element)) return;
        Canvas.SetTop(element, top);
        if (!ReferenceEquals(Realized, element)) return;
        Canvas.SetZIndex(element, 2);
    }
}
