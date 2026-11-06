using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The renderer's keyboard input: shortcuts on the document and key
/// presses that leave or move within hosted embeds.
/// </summary>
internal sealed class KeyboardInput
{
    private readonly MarkdownRendererControl _renderer;
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;
    private readonly SelectionCommands _selectionCommands;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemCommit _focusCommit;
    private readonly HostedElementFocus _hostedFocus;
    private readonly KeyboardFocusNavigator _navigator;
    private readonly GeometryRecovery _geometryRecovery;

    internal KeyboardInput(
        MarkdownRendererControl renderer,
        PublishedLayout published,
        SelectionController selection,
        SelectionCommands selectionCommands,
        SelectionContextMenuPresenter contextMenu,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemCommit focusCommit,
        HostedElementFocus hostedFocus,
        KeyboardFocusNavigator navigator,
        GeometryRecovery geometryRecovery)
    {
        _renderer = renderer;
        _published = published;
        _selection = selection;
        _selectionCommands = selectionCommands;
        _contextMenu = contextMenu;
        _focus = focus;
        _focusRing = focusRing;
        _focusCommit = focusCommit;
        _hostedFocus = hostedFocus;
        _navigator = navigator;
        _geometryRecovery = geometryRecovery;
    }

    /// <summary>Handles keys pressed on the renderer: copy, select all, context menu, focus navigation and activation.</summary>
    internal void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_published.Snapshot is null || _geometryRecovery.RequestIfRequired(_published.Snapshot)) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        switch (e.Key)
        {
            case VirtualKey.C when ctrl && _selection.IsActive:
                e.Handled = _selectionCommands.Execute(MarkdownCommandKind.CopyRendered);
                return;
            case VirtualKey.A when MarkdownKeyboardInputPolicy.ShouldSelectAll(ctrl, _renderer.IsSelectionEnabled):
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
                e.Handled = true;
                return;
            case VirtualKey.F10 or VirtualKey.Application
                when MarkdownKeyboardInputPolicy.ShouldOpenSelectionContextMenu(e.Key, shift, _selection.IsActive):
                e.Handled = _contextMenu.Show(_contextMenu.GetKeyboardPoint());
                return;
            case VirtualKey.Tab:
                e.Handled = _navigator.MoveTab(reverse: shift);
                return;
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Home:
            case VirtualKey.End:
            case VirtualKey.Up:
            case VirtualKey.Down:
                e.Handled = _navigator.Navigate(e.Key);
                return;
            case VirtualKey.Enter:
            case VirtualKey.Space when _focus.FocusedIndex >= 0:
                _navigator.ClearQueuedKeys();
                e.Handled = _focusCommit.Activate();
                return;
            case VirtualKey.Escape:
                _navigator.ClearQueuedKeys();
                if (_focus.FocusedIndex >= 0)
                {
                    _focus.ClearFocus();
                    _focusRing.Update();
                    e.Handled = true;
                }
                else if (_selection.IsActive)
                {
                    _selection.Clear();
                    e.Handled = true;
                }
                return;
        }
    }

    /// <summary>
    /// Handles keys pressed inside a hosted embed or code-block action: Tab
    /// leaves the embed through the renderer's focus order, arrows move
    /// spatially except inside a text box.
    /// </summary>
    internal void OnHostedEmbedKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (!_hostedFocus.TrySetFocusedItemFor(element)) return;

        if (e.Key == VirtualKey.Tab)
        {
            if (HostedFocusNavigation.TryMoveFocusWithinHostedEmbed(element, e.OriginalSource, shift))
            {
                e.Handled = true;
                return;
            }

            e.Handled = _navigator.MoveTab(reverse: shift);
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down &&
            element is not TextBox)
        {
            e.Handled = _navigator.MoveSpatial(e.Key);
        }
    }
}
