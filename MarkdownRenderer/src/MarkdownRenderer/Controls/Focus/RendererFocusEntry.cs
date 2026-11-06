using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Decides what happens when native focus enters the renderer. Keyboard
/// entry promotes to the first or last focusable item, as Tab or Shift+Tab
/// would; pointer and programmatic entry leave virtual focus alone. Focus
/// the renderer takes itself, for a pointer press or an automation request,
/// suppresses the promotion for that one entry.
/// </summary>
internal sealed class RendererFocusEntry
{
    private readonly IRendererFocusEntryHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemCommit _commit;
    private readonly KeyboardFocusNavigator _navigator;
    private readonly DispatcherQueue _dispatcher;

    private bool _promoting;
    private bool _wasKeyboardTraversal;
    private bool _wasKeyboardInput;
    private bool _wasReverse;
    private bool _suppressNextPromotion;
    // GotFocus is raised asynchronously, after the dispatcher may have
    // cleared _suppressNextPromotion. GettingFocus captures it in time.
    private bool _entrySuppressed;

    internal RendererFocusEntry(
        IRendererFocusEntryHost host,
        MarkdownViewLifetime lifetime,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemCommit commit,
        KeyboardFocusNavigator navigator,
        DispatcherQueue dispatcher)
    {
        _host = host;
        _lifetime = lifetime;
        _focus = focus;
        _focusRing = focusRing;
        _commit = commit;
        _navigator = navigator;
        _dispatcher = dispatcher;
    }

    /// <summary>Records how focus is about to enter the renderer itself.</summary>
    internal void NoteGettingFocus(FocusInputDeviceKind inputDevice, FocusNavigationDirection direction)
    {
        _entrySuppressed = _suppressNextPromotion;

        _wasKeyboardInput = inputDevice == FocusInputDeviceKind.Keyboard;
        _wasKeyboardTraversal =
            direction is FocusNavigationDirection.Next
                      or FocusNavigationDirection.Previous;
        _wasReverse = direction == FocusNavigationDirection.Previous;
    }

    /// <summary>
    /// Handles native focus arriving on the renderer itself in
    /// <paramref name="focusState"/>: keyboard entry focuses an item.
    /// </summary>
    internal void OnGotFocus(FocusState focusState)
    {
        if (_promoting || _lifetime.IsUnloaded)
            return;

        if (focusState == FocusState.Pointer)
            return;

        if (_entrySuppressed || _suppressNextPromotion)
        {
            _entrySuppressed = false;
            _suppressNextPromotion = false;
            _wasKeyboardTraversal = false;
            _wasKeyboardInput = false;
            return;
        }

        if (!_wasKeyboardTraversal &&
            !_wasKeyboardInput &&
            focusState != FocusState.Keyboard)
            return;

        if (_focus.PendingLazy is not null)
        {
            _focusRing.Hide();
            _wasKeyboardTraversal = false;
            _wasKeyboardInput = false;
            return;
        }

        if (_focus.FocusedIndex >= 0)
        {
            _focusRing.Update();
            _commit.NotifyAutomation();
            return;
        }

        _promoting = true;
        try
        {
            _navigator.MoveTab(_wasReverse);
        }
        finally
        {
            _promoting = false;
            _wasKeyboardTraversal = false;
            _wasKeyboardInput = false;
        }
    }

    /// <summary>Ends a suppression that focus never consumed.</summary>
    internal void OnLosingFocus() => _suppressNextPromotion = false;

    /// <summary>
    /// Suppresses the promotion of the next entry. Set before work that can
    /// move focus ahead of <see cref="FocusWithoutPromotion"/>.
    /// </summary>
    internal void SuppressNextPromotion() => _suppressNextPromotion = true;

    /// <summary>
    /// Focuses the renderer programmatically without promoting the entry to
    /// an item. Returns whether focus moved.
    /// </summary>
    internal bool FocusWithoutPromotion()
    {
        SuppressNextPromotion();
        bool focused = _host.FocusRendererProgrammatically();
        if (!focused)
        {
            _suppressNextPromotion = false;
            return false;
        }

        _dispatcher.TryEnqueue(() => _suppressNextPromotion = false);
        return true;
    }
}
