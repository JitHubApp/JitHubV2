using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Providers;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Handles the renderer’s own focus events: entry into the document, and
/// clearing the focused item when focus leaves the renderer.
/// </summary>
internal sealed class RendererFocusEvents
{
    private readonly MarkdownRendererControl _control;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly DocumentPointerInteraction _document;
    private readonly KeyboardFocusModel _focus;
    private readonly RendererFocusEntry _focusEntry;
    private readonly FocusRingPresenter _focusRing;

    internal RendererFocusEvents(
        MarkdownRendererControl control,
        SelectionContextMenuPresenter contextMenu,
        DocumentPointerInteraction document,
        KeyboardFocusModel focus,
        RendererFocusEntry focusEntry,
        FocusRingPresenter focusRing)
    {
        _control = control;
        _contextMenu = contextMenu;
        _document = document;
        _focus = focus;
        _focusEntry = focusEntry;
        _focusRing = focusRing;
    }

    internal void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (ReferenceEquals(e.NewFocusedElement, _control))
            _focusEntry.NoteGettingFocus(e.InputDevice, e.Direction);
    }

    internal void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _control))
            _focusEntry.OnGotFocus(_control.FocusState);
    }

    internal void OnLosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        _focusEntry.OnLosingFocus();

        if (e.NewFocusedElement is DependencyObject next && IsElementWithinRenderer(next))
            return;

        _focus.ClearFocus();
        _focusRing.Update();
        if (!_contextMenu.IsOpen)
        {
            _document.ForgetDragAnchor();
        }
    }

    private bool IsElementWithinRenderer(DependencyObject element)
        => VisualTreeAncestry.IsWithin(element, _control);
}
