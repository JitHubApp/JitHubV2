using System;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The renderer's context menu: Copy and Copy as Markdown for the selection,
/// commands for the link, image, code block or table under the pointer, and
/// Select All.
/// </summary>
/// <remarks>
/// A menu is bound to the layout it was opened on. It is built and shown
/// only while that snapshot, generation and layout revision are still
/// published, and the check repeats after every step that can reenter the
/// renderer, such as a host command provider query.
/// </remarks>
internal sealed class SelectionContextMenuPresenter : IContextMenuOpener
{
    private readonly ISelectionContextMenuHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly SelectionCommands _commands;
    private readonly ContextMenuTargetCommands _targetCommands;

    private MenuFlyout? _menu;
    private bool _isOpen;

    internal SelectionContextMenuPresenter(
        ISelectionContextMenuHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        SelectionHandlesPresenter handles,
        SelectionAdornerPresenter adorner,
        SelectionCommands commands)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _selection = selection;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _handles = handles;
        _adorner = adorner;
        _commands = commands;
        _targetCommands = new ContextMenuTargetCommands(host, menu => ReferenceEquals(_menu, menu));
    }

    /// <summary>True from the moment a menu is built until it closes.</summary>
    internal bool IsOpen => _isOpen;

    /// <summary>
    /// Opens the menu at <paramref name="placementPoint"/> on the canvas.
    /// Returns false when the layout changed or nothing could be shown.
    /// </summary>
    /// <param name="placementPoint">The canvas point to open the menu at.</param>
    /// <param name="touchSelection">True when a touch or pen gesture opened the menu.</param>
    /// <param name="requiredSnapshot">
    /// The snapshot a pointer gesture was interpreted on. Null for keyboard
    /// invocation, which uses the snapshot published at entry.
    /// </param>
    /// <param name="requiredSnapshotGeneration">The generation that goes with <paramref name="requiredSnapshot"/>.</param>
    /// <param name="requiredLayoutRevision">The layout revision that goes with <paramref name="requiredSnapshot"/>.</param>
    /// <remarks>
    /// Item construction lives in its own methods. The rest stays whole:
    /// building the target, attaching its commands and opening the flyout can
    /// each reenter XAML, and every exit path must dispose the target it
    /// created, so the currency checks and disposals stay in one method.
    /// </remarks>
    internal bool Show(
        Point placementPoint,
        bool touchSelection = false,
        LayoutSnapshot? requiredSnapshot = null,
        long requiredSnapshotGeneration = 0,
        long requiredLayoutRevision = 0)
    {
        UIElement? canvas = _surface.Canvas;
        if (canvas is null)
        {
            return false;
        }

        var fence = ContextMenuSnapshotFence.For(
            _published, requiredSnapshot, requiredSnapshotGeneration, requiredLayoutRevision);
        if (!fence.IsCurrent(_lifetime, _published) ||
            !ReferenceEquals(canvas, _surface.Canvas))
        {
            return false;
        }

        Close();
        if (!fence.IsCurrent(_lifetime, _published) ||
            !ReferenceEquals(canvas, _surface.Canvas))
        {
            return false;
        }

        var menu = new MenuFlyout();
        ContextMenuTarget target = new();
        try
        {
            if (!FindTarget(placementPoint, fence, out target))
            {
                return false;
            }

            _menu = menu;
            _isOpen = true;
            if (touchSelection)
                _touchSelection.OpenContextMenu();
            menu.Closed += (_, _) => OnClosed(menu, touchSelection);

            bool hasSelectionItems = _selection.IsActive;
            if (hasSelectionItems)
                AddSelectionItems(menu);

            if (!ReferenceEquals(_menu, menu) ||
                !fence.IsCurrent(_lifetime, _published) ||
                !ReferenceEquals(canvas, _surface.Canvas))
            {
                ContextMenuTargets.DisposeContextMenuTarget(target);
                OnClosed(menu, touchSelection);
                return false;
            }

            bool hasTargetItems = _targetCommands.AddItems(menu, target);
            if ((hasSelectionItems || hasTargetItems) && menu.Items.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());

            AddSelectAllItem(menu);

            if (!ReferenceEquals(_menu, menu) || !_isOpen ||
                !fence.IsCurrent(_lifetime, _published) ||
                !ReferenceEquals(canvas, _surface.Canvas))
            {
                ContextMenuTargets.DisposeContextMenuTarget(target);
                OnClosed(menu, touchSelection);
                return false;
            }

            // Command providers and MenuFlyout construction can reenter the
            // control. Fence again immediately before exposing the flyout.
            if (!fence.IsCurrent(_lifetime, _published) ||
                !ReferenceEquals(canvas, _surface.Canvas) ||
                !ReferenceEquals(_menu, menu) ||
                !_isOpen)
            {
                ContextMenuTargets.DisposeContextMenuTarget(target);
                OnClosed(menu, touchSelection);
                return false;
            }

            menu.ShowAt(canvas, placementPoint);
            return true;
        }
        catch (Exception ex)
        {
            ContextMenuTargets.DisposeContextMenuTarget(target);
            OnClosed(menu, touchSelection);
            try { menu.Hide(); } catch { }
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] context menu failed: {ex.Message}");
            return false;
        }
    }

    private void AddSelectionItems(MenuFlyout menu)
    {
        var copyItem = new MenuFlyoutItem
        {
            Text = _host.ResolveLocalizedString(
                MarkdownStringKeys.Copy,
                MarkdownLocalizedStrings.ContextMenuCopy),
            IsEnabled = _commands.CanExecuteOrDefault(
                MarkdownCommandKind.CopyRendered),
        };
        AutomationProperties.SetAutomationId(copyItem, "MarkdownContextCopy");
        copyItem.Click += (_, _) =>
            _commands.Execute(MarkdownCommandKind.CopyRendered);
        menu.Items.Add(copyItem);

        var copyMarkdownItem = new MenuFlyoutItem
        {
            Text = _host.ResolveLocalizedString(
                MarkdownStringKeys.CopyMarkdown,
                MarkdownLocalizedStrings.ContextMenuCopyMarkdown),
            IsEnabled = _commands.CanExecuteOrDefault(
                MarkdownCommandKind.CopyMarkdown),
        };
        AutomationProperties.SetAutomationId(copyMarkdownItem, "MarkdownContextCopyMarkdown");
        copyMarkdownItem.Click += (_, _) =>
            _commands.Execute(MarkdownCommandKind.CopyMarkdown);
        menu.Items.Add(copyMarkdownItem);
    }

    private void AddSelectAllItem(MenuFlyout menu)
    {
        var selectAllItem = new MenuFlyoutItem
        {
            Text = _host.ResolveLocalizedString(
                MarkdownStringKeys.SelectAll,
                MarkdownLocalizedStrings.ContextMenuSelectAll),
            IsEnabled = _host.IsSelectionEnabled,
        };
        AutomationProperties.SetAutomationId(selectAllItem, "MarkdownContextSelectAll");
        selectAllItem.Click += (_, _) =>
        {
            if (!_host.IsSelectionEnabled) return;
            if (_handles.HasCaptureTarget)
                _handles.ResetDrag();
            _touchTapSuppression.Reset();
            _touchSelection.Reset();
            _selection.SetAnchor(DocumentPosition.Zero);
            _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
        };
        menu.Items.Add(selectAllItem);
    }

    private void OnClosed(MenuFlyout menu, bool touchSelection)
    {
        _targetCommands.Release(menu);
        if (!ReferenceEquals(_menu, menu))
            return;

        _isOpen = false;
        _menu = null;
        if (touchSelection)
            _touchSelection.CloseContextMenu(_selection.IsActive);
        _handles.Update();
    }

    /// <summary>Closes the open menu, if any, and releases its target leases.</summary>
    internal void Close()
    {
        MenuFlyout? menu = _menu;
        _menu = null;
        _isOpen = false;
        if (menu is null)
            return;

        try
        {
            _targetCommands.Release(menu);
            _touchSelection.CloseContextMenu(_selection.IsActive);
            _handles.Update();
        }
        finally
        {
            // Detached native state can reject a handle update. The flyout
            // reference has already been removed from the owner, so hide this
            // captured instance even if preceding cleanup fails.
            try { menu.Hide(); }
            catch { }
        }
    }

    /// <summary>
    /// The canvas point for a menu opened from the keyboard: below the focused
    /// item, else below the first visible selection rectangle, else near the
    /// viewport's top-left corner.
    /// </summary>
    internal Point GetKeyboardPoint()
    {
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        // This returns a copied Rect after its nonblocking snapshot lease
        // ends; the flyout and command providers are invoked later.
        if (_host.GetFocusedItemRect() is { } focusedRect)
        {
            return new Point(
                Math.Clamp(focusedRect.Left + focusedRect.Width / 2.0, 0, Math.Max(0, viewportWidth - 1)),
                Math.Clamp(focusedRect.Bottom, viewportTop, Math.Max(viewportTop, viewportBottom - 1)));
        }

        Rect selectionRect = default;
        foreach (Rect rectangle in _adorner.Rectangles)
        {
            if (rectangle.Bottom < viewportTop || rectangle.Top > viewportBottom)
            {
                continue;
            }

            selectionRect = rectangle;
            break;
        }

        if (selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            return new Point(12, viewportTop + 12);
        }

        return new Point(
            Math.Clamp(selectionRect.Left + 8, 0, Math.Max(0, viewportWidth - 1)),
            Math.Clamp(selectionRect.Bottom, viewportTop, Math.Max(viewportTop, viewportBottom - 1)));
    }

    bool IContextMenuOpener.Show(
        Point placementPoint,
        bool touchSelection,
        LayoutSnapshot? requiredSnapshot,
        long requiredSnapshotGeneration,
        long requiredLayoutRevision) =>
        Show(placementPoint, touchSelection, requiredSnapshot, requiredSnapshotGeneration, requiredLayoutRevision);

    private bool FindTarget(
        Point point,
        ContextMenuSnapshotFence fence,
        out ContextMenuTarget target)
    {
        target = new ContextMenuTarget();
        if (!fence.IsCurrent(_lifetime, _published))
        {
            return false;
        }

        LayoutSnapshot? snapshot = fence.Snapshot;
        if (snapshot is null)
            return true;

        // Busy means target-specific commands may be omitted, but only if the
        // originally observed tuple is still current. Never substitute a newer
        // snapshot/revision here.
        if (!snapshot.TryBeginInteraction())
            return fence.IsCurrent(_lifetime, _published);

        bool captured = false;
        try
        {
            try
            {
                if (fence.IsCurrent(_lifetime, _published))
                {
                    foreach (BlockBox block in snapshot.Blocks)
                    {
                        Point physicalPoint = snapshot.ToPhysicalPointForTopLevelBlockUnderPaint(block, point);
                        using LayoutPlacementTransformContext.Scope placementScope =
                            snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(block);
                        if (ContextMenuTargets.PopulateContextMenuTarget(block, physicalPoint, target))
                            break;
                    }
                    captured = true;
                }
            }
            finally
            {
                snapshot.EndInteraction();
            }
        }
        catch
        {
            ContextMenuTargets.DisposeContextMenuTarget(target);
            target = new ContextMenuTarget();
            throw;
        }

        if (!captured || !fence.IsCurrent(_lifetime, _published))
        {
            ContextMenuTargets.DisposeContextMenuTarget(target);
            target = new ContextMenuTarget();
            return false;
        }

        return true;
    }
}
