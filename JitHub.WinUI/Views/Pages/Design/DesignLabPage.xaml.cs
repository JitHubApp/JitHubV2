using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.WinUI.Views.Pages.Design;

public sealed partial class DesignLabPage : Page
{
    private readonly Dictionary<string, FrameworkElement> _scenarioMap;

    public DesignLabPage()
    {
        InitializeComponent();
        _scenarioMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["buttons"] = ButtonsScenarioCard,
            ["inputs"] = InputsScenarioCard,
            ["navigation"] = NavigationScenarioCard,
            ["settings"] = SettingsScenarioCard,
            ["repo"] = RepoScenarioCard,
            ["conversation"] = ConversationScenarioCard,
            ["empty"] = EmptyScenarioCard
        };
        Loaded += DesignLabPage_Loaded;
    }

    public string? RequestedScenario { get; private set; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RequestedScenario = e.Parameter as string;
    }

    private void DesignLabPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RequestedScenario))
        {
            return;
        }

        if (_scenarioMap.TryGetValue(RequestedScenario, out FrameworkElement? element))
        {
            element.StartBringIntoView();
        }
    }
}
