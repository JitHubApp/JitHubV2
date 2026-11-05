using System.Globalization;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Hidden status probes that mirror sample state for UI automation, so tests
/// can verify virtualization, flow direction, contrast, text scale, link
/// activation, theme, page and provider state without adding text to the
/// renderer's own UIA surface (HelpText is read aloud by Narrator). Each probe
/// is an invisible, raw-view TextBlock whose text and name carry the value.
/// </summary>
internal sealed class SampleAutomationStatus
{
    private const string ThemePending = "theme:pending";

    private readonly TextBlock _realizedCount;
    private readonly TextBlock _flowDirection;
    private readonly TextBlock _highContrast;
    private readonly TextBlock _textScale;
    private readonly TextBlock _linkActivation;
    private readonly TextBlock _theme;
    private readonly TextBlock _currentPage;
    private readonly TextBlock _providers;

    internal SampleAutomationStatus()
    {
        _realizedCount = CreateProbe("RealizedEmbedCount", "realized:0");
        _flowDirection = CreateProbe("FlowDirectionStatus", "flow:ltr");
        _highContrast = CreateProbe("HighContrastStatus", "hc:off");
        _textScale = CreateProbe("TextScaleStatus", "text-scale:1");
        _linkActivation = CreateProbe("LinkActivationStatus", "link:none");
        _theme = CreateProbe("ThemeStatus", ThemePending);
        _currentPage = CreateProbe("CurrentSamplePage", "page:pending");
        TouchContact = CreateProbe(
            "TouchContactObservation",
            "{\"version\":1,\"rendererGeneration\":0," +
            "\"observationErrors\":0,\"observationStatus\":\"ok\"," +
            "\"active\":[],\"contacts\":[]}");
        _providers = CreateProbe("ProviderStatus", text: null);
    }

    /// <summary>The probe <see cref="TouchContactObservation"/> publishes to.</summary>
    internal TextBlock TouchContact { get; }

    /// <summary>Adds every probe to <paramref name="workspace"/> in a stable order.</summary>
    internal void AddTo(Grid workspace, int row)
    {
        foreach (TextBlock probe in new[]
                 {
                     _realizedCount, _flowDirection, _highContrast, _textScale,
                     _linkActivation, _theme, _currentPage, TouchContact, _providers,
                 })
        {
            Grid.SetRow(probe, row);
            workspace.Children.Add(probe);
        }
    }

    internal void SetRealizedCount(int count)
        => Publish(_realizedCount, "realized:" + count.ToString(CultureInfo.InvariantCulture));

    internal void SetFlowDirection(FlowDirection flowDirection)
        => Publish(_flowDirection, flowDirection == FlowDirection.RightToLeft ? "flow:rtl" : "flow:ltr");

    internal void SetForcedHighContrast(bool enabled)
        => Publish(_highContrast, enabled
            ? "hc:on;fg:#FFFFFF;bg:#000000;hotlight:#00FFFF;highlight:#FFFF00;highlightText:#000000"
            : "hc:off");

    internal void SetTextScale(float scale) => Publish(_textScale, $"text-scale:{scale:0.#}");

    internal void SetLinkActivation(string status) => Publish(_linkActivation, status);

    internal void MarkThemePending() => Publish(_theme, ThemePending);

    internal void SetRenderedTheme(MarkdownThemeVariant variant) => Publish(_theme, variant switch
    {
        MarkdownThemeVariant.HighContrast => "theme:high-contrast",
        MarkdownThemeVariant.Dark => "theme:dark",
        _ => "theme:light",
    });

    internal void SetCurrentPage(string pageKey) => Publish(_currentPage, "page:" + pageKey);

    internal void SetProviders(string status) => Publish(_providers, status);

    private static TextBlock CreateProbe(string automationId, string? text)
    {
        var probe = new TextBlock
        {
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        if (text is not null)
            probe.Text = text;
        AutomationProperties.SetAutomationId(probe, automationId);
        if (text is not null)
            AutomationProperties.SetName(probe, text);
        AutomationProperties.SetAccessibilityView(probe, AccessibilityView.Raw);
        return probe;
    }

    private static void Publish(TextBlock probe, string value)
    {
        probe.Text = value;
        AutomationProperties.SetName(probe, value);
    }
}
