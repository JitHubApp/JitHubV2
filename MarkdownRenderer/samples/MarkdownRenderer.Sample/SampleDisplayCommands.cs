using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Sample;

/// <summary>
/// The page header's preview display commands: theme, flow direction, forced
/// contrast and text scale for every page, plus task editing and viewport
/// ownership on the pages that exercise them.
/// </summary>
internal sealed class SampleDisplayCommands
{
    private readonly SampleStrings _strings;
    private readonly SampleRendererHost _host;
    private readonly SampleAutomationStatus _status;
    private readonly AppBarToggleButton _taskEditingToggle;
    private readonly AppBarToggleButton _viewportOwnershipToggle;

    /// <param name="setShellTheme">Applies a theme to the whole sample shell.</param>
    /// <param name="replaceRenderer">Replaces the active view; the argument says whether it owns its viewport.</param>
    internal SampleDisplayCommands(
        SampleStrings strings,
        SampleRendererHost host,
        SampleAutomationStatus status,
        Action<ElementTheme> setShellTheme,
        Action<bool> replaceRenderer)
    {
        _strings = strings;
        _host = host;
        _status = status;
        Element = new CommandBar
        {
            IsDynamicOverflowEnabled = true,
        };
        AutomationProperties.SetAutomationId(Element, "SampleDisplayControls");
        AutomationProperties.SetName(
            Element,
            strings.Get("DisplayControlsAutomationName", "Preview display controls"));

        Element.PrimaryCommands.Add(CreateThemeToggle(setShellTheme));
        Element.PrimaryCommands.Add(CreateFlowDirectionToggle());
        Element.PrimaryCommands.Add(CreateForcedContrastToggle());
        Element.PrimaryCommands.Add(CreateTextScaleToggle());
        _taskEditingToggle = CreateTaskEditingToggle();
        Element.PrimaryCommands.Add(_taskEditingToggle);
        _viewportOwnershipToggle = CreateViewportOwnershipToggle(replaceRenderer);
        Element.PrimaryCommands.Add(_viewportOwnershipToggle);
    }

    internal CommandBar Element { get; }

    /// <summary>
    /// Shows the page-specific commands a page supports and turns off the
    /// ones it does not, which restores the standard view.
    /// </summary>
    internal void ShowForPage(bool supportsTaskEditing, bool supportsViewportOwnership)
    {
        _taskEditingToggle.Visibility = supportsTaskEditing ? Visibility.Visible : Visibility.Collapsed;
        _viewportOwnershipToggle.Visibility = supportsViewportOwnership ? Visibility.Visible : Visibility.Collapsed;
        if (!supportsTaskEditing)
            _taskEditingToggle.IsChecked = false;
        if (!supportsViewportOwnership && _viewportOwnershipToggle.IsChecked == true)
            _viewportOwnershipToggle.IsChecked = false;
    }

    private AppBarToggleButton CreateThemeToggle(Action<ElementTheme> setShellTheme)
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = _strings.Get("CommandTheme", "Dark theme"),
            // The shell initially inherits the application's startup theme.
            // Initialize before subscribing so the toggle represents that
            // appearance without issuing a theme change during construction.
            IsChecked = Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
        AutomationProperties.SetAutomationId(toggle, "ThemeToggle");
        AutomationProperties.SetName(
            toggle,
            _strings.Get("CommandThemeAutomationName", "Use dark theme"));
        toggle.Checked += (_, _) => SetTheme(ElementTheme.Dark);
        toggle.Unchecked += (_, _) => SetTheme(ElementTheme.Light);
        return toggle;

        void SetTheme(ElementTheme theme)
        {
            _status.MarkThemePending();
            setShellTheme(theme);
        }
    }

    private AppBarToggleButton CreateFlowDirectionToggle()
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = _strings.Get("CommandRtl", "Right-to-left"),
            Name = "RtlToggle",
        };
        AutomationProperties.SetAutomationId(toggle, "RtlToggle");
        AutomationProperties.SetName(
            toggle,
            _strings.Get("CommandRtlAutomationName", "Use right-to-left preview flow"));
        toggle.Checked += (_, _) => SetFlowDirection(FlowDirection.RightToLeft);
        toggle.Unchecked += (_, _) => SetFlowDirection(FlowDirection.LeftToRight);
        return toggle;
    }

    private AppBarToggleButton CreateForcedContrastToggle()
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = _strings.Get("CommandHighContrast", "Forced contrast"),
            Name = "ForcedHighContrastToggle",
        };
        AutomationProperties.SetAutomationId(toggle, "ForcedHighContrastToggle");
        AutomationProperties.SetName(
            toggle,
            _strings.Get("CommandHighContrastAutomationName", "Force high contrast preview colors"));
        toggle.Checked += (_, _) => SetForcedContrast(true);
        toggle.Unchecked += (_, _) => SetForcedContrast(false);
        return toggle;
    }

    private AppBarToggleButton CreateTextScaleToggle()
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = _strings.Get("CommandTextScale", "200% text"),
            Name = "TextScaleToggle",
        };
        AutomationProperties.SetAutomationId(toggle, "TextScaleToggle");
        AutomationProperties.SetName(
            toggle,
            _strings.Get("CommandTextScaleAutomationName", "Use two hundred percent preview text scale"));
        toggle.Checked += (_, _) => SetTextScale(2f);
        toggle.Unchecked += (_, _) => SetTextScale(1f);
        return toggle;
    }

    private AppBarToggleButton CreateTaskEditingToggle()
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = _strings.Get("CommandTaskEditing", "Edit tasks"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(toggle, "TaskEditingToggle");
        AutomationProperties.SetName(
            toggle,
            _strings.Get("CommandTaskEditingAutomationName", "Enable task-list editing"));
        toggle.Checked += (_, _) => _host.Renderer.IsTaskListEditingEnabled = true;
        toggle.Unchecked += (_, _) => _host.Renderer.IsTaskListEditingEnabled = false;
        return toggle;
    }

    private AppBarToggleButton CreateViewportOwnershipToggle(Action<bool> replaceRenderer)
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = _strings.Get("CommandAncestorViewport", "Ancestor viewport"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(toggle, "TouchViewportOwnershipToggle");
        AutomationProperties.SetName(
            toggle,
            _strings.Get(
                "CommandAncestorViewportAutomationName",
                "Use a page-owned ScrollViewer for the touch sample"));
        toggle.Checked += (_, _) => replaceRenderer(false);
        toggle.Unchecked += (_, _) => replaceRenderer(true);
        return toggle;
    }

    private void SetFlowDirection(FlowDirection flowDirection)
    {
        _host.Renderer.FlowDirection = flowDirection;
        _status.SetFlowDirection(_host.Renderer.FlowDirection);
    }

    private void SetForcedContrast(bool enabled)
    {
        _status.MarkThemePending();
        if (_host.Renderer.Theme is { } theme)
        {
            if (enabled)
                SampleThemeAdjustments.ApplyForcedContrast(theme);
            else
                SampleThemeAdjustments.ClearForcedContrast(theme);
        }
        _status.SetForcedHighContrast(enabled);
    }

    private void SetTextScale(float scale)
    {
        if (_host.Renderer.Theme is not { } theme)
            return;

        _status.MarkThemePending();
        SampleThemeAdjustments.ApplyTextScale(theme, scale);
        _status.SetTextScale(scale);
    }
}
