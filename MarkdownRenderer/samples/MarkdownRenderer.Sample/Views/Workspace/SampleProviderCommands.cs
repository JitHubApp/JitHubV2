using System;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Sample.Services.Automation;
using MarkdownRenderer.Sample.Services.Localization;
using MarkdownRenderer.Sample.Services.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Sample.Views.Workspace;

/// <summary>
/// The Providers page's commands. They swap the engine, grammar provider,
/// hosted factory and optional providers while documents are live, and add a
/// second view that shares them; the Sandbox UI tests drive these commands
/// and read the provider probe this class keeps current.
/// </summary>
internal sealed class SampleProviderCommands
{
    private readonly SampleStrings _strings;
    private readonly SampleProviderSet _providers;
    private readonly SampleRendererHost _host;
    private readonly SampleAutomationStatus _status;
    private readonly Action<bool> _replaceRenderer;
    private readonly Func<string> _currentSource;
    private readonly AppBarButton _replaceEngineButton;
    private readonly AppBarToggleButton _withoutCSharpGrammarToggle;
    private readonly AppBarToggleButton _alternateFactoryToggle;
    private readonly AppBarToggleButton _minimalProvidersToggle;
    private readonly AppBarToggleButton _secondViewToggle;

    /// <param name="replaceRenderer">Replaces the active view; the argument says whether it owns its viewport.</param>
    /// <param name="currentSource">Returns the editor's current source.</param>
    internal SampleProviderCommands(
        SampleStrings strings,
        SampleProviderSet providers,
        SampleRendererHost host,
        SampleAutomationStatus status,
        Action<bool> replaceRenderer,
        Func<string> currentSource)
    {
        _strings = strings;
        _providers = providers;
        _host = host;
        _status = status;
        _replaceRenderer = replaceRenderer;
        _currentSource = currentSource;
        Element = new CommandBar
        {
            IsDynamicOverflowEnabled = false,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(Element, "ProviderControls");
        AutomationProperties.SetName(
            Element,
            strings.Get("ProviderControlsAutomationName", "Provider configuration controls"));

        _replaceEngineButton = new AppBarButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = strings.Get("CommandReplaceEngine", "Replace engine"),
        };
        AutomationProperties.SetAutomationId(_replaceEngineButton, "ProviderReplaceEngine");
        _replaceEngineButton.Click += (_, _) => ReplaceEngine();
        Element.PrimaryCommands.Add(_replaceEngineButton);

        _withoutCSharpGrammarToggle = CreateToggle(
            "",
            "CommandWithoutCSharpGrammar", "No C# grammar",
            "ProviderWithoutCSharpGrammar",
            "CommandWithoutCSharpGrammarAutomationName", "Replace the grammar provider with one that has no C# grammar",
            ReplaceHighlighter);
        _alternateFactoryToggle = CreateToggle(
            "",
            "CommandAlternateFactory", "Alternate controls",
            "ProviderAlternateFactory",
            "CommandAlternateFactoryAutomationName", "Replace the hosted control factory",
            SetAlternateHostedFactory);
        _minimalProvidersToggle = CreateToggle(
            "",
            "CommandMinimalProviders", "Minimal providers",
            "ProviderMinimal",
            "CommandMinimalProvidersAutomationName", "Use strict GFM without optional providers",
            SetMinimalProviders);
        _secondViewToggle = CreateToggle(
            "",
            "CommandSecondView", "Second view",
            "ProviderSecondView",
            "CommandSecondViewAutomationName", "Show a second view that shares the providers",
            SetSecondView);
        UpdateStatus();
    }

    internal CommandBar Element { get; }

    /// <summary>
    /// Shows the commands on the Providers page. Provider swaps belong to that
    /// page, so leaving it restores the standard configuration.
    /// </summary>
    internal void ShowForPage(bool isProvidersPage)
    {
        Element.Visibility = isProvidersPage ? Visibility.Visible : Visibility.Collapsed;
        if (isProvidersPage)
            return;

        _secondViewToggle.IsChecked = false;
        _alternateFactoryToggle.IsChecked = false;
        _withoutCSharpGrammarToggle.IsChecked = false;
        _minimalProvidersToggle.IsChecked = false;
    }

    private AppBarToggleButton CreateToggle(
        string glyph,
        string labelKey,
        string labelFallback,
        string automationId,
        string automationNameKey,
        string automationNameFallback,
        Action<bool> toggled)
    {
        var toggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = glyph },
            Label = _strings.Get(labelKey, labelFallback),
        };
        AutomationProperties.SetAutomationId(toggle, automationId);
        AutomationProperties.SetName(toggle, _strings.Get(automationNameKey, automationNameFallback));
        toggle.Checked += (_, _) => toggled(true);
        toggle.Unchecked += (_, _) => toggled(false);
        Element.PrimaryCommands.Add(toggle);
        return toggle;
    }

    private void ReplaceEngine()
    {
        if (_providers.ReplaceEngine(_host.Views))
            UpdateStatus();
    }

    private void ReplaceHighlighter(bool withoutCSharp)
    {
        if (_providers.ReplaceHighlighter(withoutCSharp, _host.Views))
            UpdateStatus();
    }

    private void SetAlternateHostedFactory(bool alternate)
    {
        if (_providers.SetAlternateHostedFactory(alternate, _host.Views))
            UpdateStatus();
    }

    private void SetMinimalProviders(bool minimal)
    {
        if (!_providers.SetMinimal(minimal))
            return;

        _replaceEngineButton.IsEnabled = !minimal;
        // Optional providers are configuration of a view, so the sample swaps
        // the whole view, the way an app changes its feature set.
        _replaceRenderer(_host.Renderer is MarkdownScrollView);
        UpdateStatus();
    }

    private void SetSecondView(bool visible)
    {
        _host.SetSecondView(visible, _currentSource());
        UpdateStatus();
    }

    private void UpdateStatus() => _status.SetProviders(_providers.DescribeStatus(_host.HasSecondView));
}
