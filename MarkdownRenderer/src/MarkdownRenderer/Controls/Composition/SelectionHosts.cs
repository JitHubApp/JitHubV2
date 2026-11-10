using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;

namespace MarkdownRenderer.Controls.Composition;

/// <summary>
/// The selection presenters’ hosts: adorner paint, commands, dismissal, handles and the context menu.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class SelectionHosts :
    ISelectionAdornerHost,
    ISelectionCommandsHost,
    ISelectionDismissalHost,
    ISelectionHandlesHost,
    ISelectionContextMenuHost
{
    private readonly MarkdownRendererControl _control;
    private Parts? _owners;

    internal SelectionHosts(MarkdownRendererControl control)
    {
        _control = control;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        RendererEvents Events,
        PublishedLayout Published,
        CanvasDeviceRecovery DeviceRecovery,
        SelectionHandlesPresenter Handles,
        PointerInputRouter PointerInput,
        EmbedRealization Embeds,
        RebuildScheduler Rebuild,
        GeometryRecovery GeometryRecovery,
        InteractiveTextAdorner TextAdorner,
        TouchSelectionRestore TouchRestore,
        RendererLocalization Localization,
        InteractionReset InteractionReset,
        PointerInteractionResume PointerResume,
        KeyboardFocusModel Focus,
        FocusGeometryReader FocusGeometry,
        RendererFocusEntry FocusEntry);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its SelectionHosts before connecting them.");

    bool ISelectionAdornerHost.IsDeviceRecoveryQueued => Owners.DeviceRecovery.IsQueued;

    bool ISelectionAdornerHost.HasFocusedLink => Owners.Focus.TryGetFocusedLink(out _, out _);

    bool ISelectionAdornerHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot snapshot)
        => Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void ISelectionAdornerHost.HandleDeviceLost(Exception exception) => Owners.DeviceRecovery.HandleDeviceLost(exception);

    void ISelectionAdornerHost.RequestRebuildForDeviceChange() => Owners.Rebuild.Request(RebuildReason.Full);

    void ISelectionAdornerHost.UpdateSelectionHandlesUnderPaint(LayoutSnapshot snapshot, Rect viewport)
        => Owners.Handles.UpdateUnderPaint(snapshot, viewport);

    void ISelectionAdornerHost.PaintInteractiveDocumentState(
        LayoutSnapshot snapshot,
        CanvasDrawingSession drawingSession,
        Rect viewport,
        IReadOnlyList<SelectionBackgroundGeometry> selectionBackgrounds)
        => Owners.TextAdorner.PaintInteractiveDocumentState(snapshot, drawingSession, viewport, selectionBackgrounds);

    IMarkdownCommandProvider? ISelectionCommandsHost.CommandProvider => _control.CommandProvider;

    void ISelectionCommandsHost.RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
        => Owners.Events.RaiseCopyCompleted(kind, succeeded);

    bool ISelectionDismissalHost.IsElementWithinCodeBlockAction(DependencyObject element)
    {
        foreach (var plan in Owners.Embeds.CodeBlockActionPlans)
        {
            if (plan.Realized is DependencyObject realized && VisualTreeAncestry.IsWithin(element, realized))
                return true;
        }

        return false;
    }

    bool ISelectionDismissalHost.IsElementWithinSelectionHandle(DependencyObject element)
        => Owners.Handles.ContainsElement(element);

    bool ISelectionDismissalHost.IsPointOverEmbed(Point canvasPoint) => Owners.Embeds.HitTargets.IsPointOverEmbed(canvasPoint);

    void ISelectionDismissalHost.ClearSelectionForExternalInteraction() => Owners.InteractionReset.ClearSelectionForExternalInteraction();

    Theming.ThemeSnapshot? ISelectionHandlesHost.ThemeSnapshot => Owners.Published.Theme;

    string ISelectionHandlesHost.ResolveLocalizedString(string key, string fallback)
        => Owners.Localization.Resolve(key, fallback);

    bool ISelectionHandlesHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void ISelectionHandlesHost.FocusRendererForPointerInteraction() => Owners.FocusEntry.FocusWithoutPromotion();

    bool ISelectionHandlesHost.TryHitTestEmbed(Point point, out DocumentPosition position)
        => Owners.Embeds.HitTargets.TryHitTest(
            point, _control.FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft, out position);

    bool ISelectionHandlesHost.HasDeferredInputFor(uint pointerId)
        => Owners.PointerInput.HasDeferredInputFor(pointerId);

    void ISelectionHandlesHost.ResumePendingRebuildAfterPointerInteraction()
        => Owners.PointerResume.ResumePendingWork();

    void ISelectionHandlesHost.CancelPendingTouchSelectionRestore()
        => Owners.TouchRestore.Cancel();

    bool ISelectionContextMenuHost.IsSelectionEnabled => _control.IsSelectionEnabled;

    IMarkdownCommandProvider? ISelectionContextMenuHost.CommandProvider => _control.CommandProvider;

    string ISelectionContextMenuHost.ResolveLocalizedString(string key, string fallback)
        => Owners.Localization.Resolve(key, fallback);

    void ISelectionContextMenuHost.RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
        => Owners.Events.RaiseCopyCompleted(kind, succeeded);

    Rect? ISelectionContextMenuHost.GetFocusedItemRect()
        => Owners.Focus.Items is { } items &&
           Owners.Focus.FocusedIndex >= 0 &&
           Owners.Focus.FocusedIndex < items.Count
            ? Owners.FocusGeometry.GetCurrentItemRect(items[Owners.Focus.FocusedIndex])
            : null;
}
