using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// Creates, pools and labels the copy buttons of code blocks. A small pool
/// of buttons, kept collapsed in the overlay where possible, makes a code
/// block crossing the viewport edge cheap.
/// </summary>
internal sealed class CodeBlockCopyButtons
{
    private const int PoolCapacity = 8;

    private readonly ICodeBlockCopyButtonHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly IEmbedPlanHost _planHost;
    private readonly Stack<(Button Button, bool Attached)> _pool = new();

    internal CodeBlockCopyButtons(
        ICodeBlockCopyButtonHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        IEmbedPlanHost planHost)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _planHost = planHost;
    }

    /// <summary>
    /// Returns a pooled button, or a new one. <c>Attached</c> is true when
    /// the button is still a collapsed child of the overlay.
    /// </summary>
    internal (Button Button, bool Attached) Rent()
    {
        if (_pool.Count == 0)
            return (Create(), false);

        var pooled = _pool.Pop();
        Button button = pooled.Button;
        if (!ReferenceEquals(button.Style, _host.CodeBlockCopyButtonStyle))
            button.Style = _host.CodeBlockCopyButtonStyle;
        return (button, pooled.Attached);
    }

    /// <summary>
    /// Collapses <paramref name="button"/> and pools it, leaving it in the
    /// overlay when <paramref name="keepAttached"/> allows.
    /// </summary>
    internal void Return(Button button, bool keepAttached)
    {
        keepAttached = keepAttached && _pool.Count < PoolCapacity;
        button.Visibility = Visibility.Collapsed;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
        {
            try { _surface.Overlay?.Children.Remove(button); } catch { }
            return;
        }
        if (_pool.Count < PoolCapacity)
        {
            _pool.Push((button,
                keepAttached && ReferenceEquals(VisualTreeHelper.GetParent(button), _surface.Overlay)));
            return;
        }

        try { _surface.Overlay?.Children.Remove(button); } catch { }
    }

    /// <summary>
    /// Forgets pooled buttons. The caller is about to clear or discard the
    /// overlay that may still hold them.
    /// </summary>
    internal void Clear() => _pool.Clear();

    /// <summary>Shows the copy or copied icon, name and tooltip.</summary>
    internal void SetState(Button button, bool copied)
    {
        string label = _host.ResolveCodeBlockCopyButtonLabel(copied);
        if (button.Content is SymbolIcon icon)
            icon.Symbol = copied ? Symbol.Accept : Symbol.Copy;
        else
            button.Content = new SymbolIcon(copied ? Symbol.Accept : Symbol.Copy);

        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    /// <summary>
    /// Resets a button adopted by a raster-candidate publication to its copy
    /// state. Returns false as soon as any step leaves <paramref name="plan"/>
    /// no longer the button's current owner.
    /// </summary>
    internal bool SetStateForRasterCandidate(
        CodeBlockActionPlan plan,
        Button button,
        FrameworkElement element,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
    {
        if (!ReferenceEquals(button, element) ||
            !plan.IsCurrentRasterActionOwner(
                _planHost, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        // Localization can call a host StringProvider. Resolve it before the
        // first visual mutation, then revalidate before touching this recycled
        // element. Every subsequent XAML property update is independently
        // fenced because dependency-property callbacks may reenter the host.
        string label = _host.ResolveCodeBlockCopyButtonLabel(copied: false);
        if (!plan.IsCurrentRasterActionOwner(
                _planHost, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        if (button.Content is SymbolIcon icon)
        {
            icon.Symbol = Symbol.Copy;
            if (!plan.IsCurrentRasterActionOwner(
                    _planHost, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        else
        {
            button.Content = new SymbolIcon(Symbol.Copy);
            if (!plan.IsCurrentRasterActionOwner(
                    _planHost, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }

        AutomationProperties.SetName(button, label);
        if (!plan.IsCurrentRasterActionOwner(
                _planHost, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        ToolTipService.SetToolTip(button, label);
        return plan.IsCurrentRasterActionOwner(
            _planHost, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element);
    }

    private Button Create()
    {
        var button = new Button
        {
            Content = new SymbolIcon(Symbol.Copy),
            Style = _host.CodeBlockCopyButtonStyle,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(button, "MarkdownCodeBlockCopyButton");
        SetState(button, copied: false);
        return button;
    }
}
