using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Sample.Services.Providers;

internal sealed class SampleHostedElementFactory : IMarkdownHostedElementFactory
{
    internal const string ButtonKey = "MarkdownRenderer.Sample.Button";
    internal const string PanelKey = "MarkdownRenderer.Sample.Panel";
    internal const string UnsupportedKey = "MarkdownRenderer.Sample.Unsupported";
    internal const string ThrowingKey = "MarkdownRenderer.Sample.Throwing";
    internal const string AttachFailedKey = "MarkdownRenderer.Sample.AttachFailed";
    // Keeps the attach-failed probe's element parented elsewhere.
    private readonly Grid _foreignParent = new();
    // The alternate factory marks every control it creates, so replacing the
    // factory on a live document is observable in each control's name.
    private readonly string _labelSuffix;

    private SampleHostedElementFactory(string labelSuffix) => _labelSuffix = labelSuffix;

    /// <summary>Creates the standard factory a window installs by default.</summary>
    internal static SampleHostedElementFactory CreateDefault() => new(string.Empty);

    /// <summary>Creates the replacement factory the Providers page swaps in.</summary>
    internal static SampleHostedElementFactory CreateAlternate() => new(" (alternate)");

    public ValueTask<FrameworkElement?> CreateAsync(
        MarkdownHostedElementRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Attributes.TryGetValue("label", out string? label);
        label = (string.IsNullOrWhiteSpace(label) ? "Native action" : label) + _labelSuffix;

        if (request.FactoryKey == ThrowingKey)
            throw new InvalidOperationException("The sample's throwing factory probe failed by design.");

        FrameworkElement? element = request.FactoryKey switch
        {
            ButtonKey => CreateButton(request, label),
            PanelKey => CreatePanel(request, label),
            AttachFailedKey => CreateAlreadyParentedButton(request, label),
            _ => null,
        };
        if (element is not null && _labelSuffix.Length > 0)
            AutomationProperties.SetName(element, AutomationProperties.GetName(element) + _labelSuffix);
        return ValueTask.FromResult(element);
    }

    public void Recycle(
        MarkdownHostedElementRequest request,
        FrameworkElement element)
    {
        // The sample attaches no external handlers or resources. A real host
        // releases its app-owned state here before the element is discarded.
    }

    private static Button CreateButton(MarkdownHostedElementRequest request, string label)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyAutomation(button, request, label);
        button.Click += (sender, _) =>
        {
            if (sender is not Button invoked)
                return;

            string activatedLabel = $"{label} activated";
            invoked.Content = activatedLabel;
            AutomationProperties.SetName(invoked, activatedLabel);
        };
        return button;
    }

    private Button CreateAlreadyParentedButton(MarkdownHostedElementRequest request, string label)
    {
        Button button = CreateButton(request, label);
        _foreignParent.Children.Add(button);
        return button;
    }

    private static Grid CreatePanel(MarkdownHostedElementRequest request, string label)
    {
        var panel = new Grid
        {
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var value = new TextBox
        {
            Text = "Editable sample value",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(value, "CompositeValueTextBox");
        AutomationProperties.SetName(value, "Composite value");

        var action = new Button
        {
            Content = "Apply",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(action, 1);
        AutomationProperties.SetAutomationId(action, "CompositeActionButton");
        AutomationProperties.SetName(action, $"{label} apply");

        panel.Children.Add(value);
        panel.Children.Add(action);
        ApplyAutomation(panel, request, label);
        return panel;
    }

    private static void ApplyAutomation(
        FrameworkElement element,
        MarkdownHostedElementRequest request,
        string fallbackName)
    {
        AutomationProperties.SetAutomationId(
            element,
            request.AutomationId ?? $"Hosted_{request.SourceRange.Start}_{request.SourceRange.Length}");
        AutomationProperties.SetName(element, request.AccessibilityName ?? fallbackName);
        if (!string.IsNullOrWhiteSpace(request.AccessibilityDescription))
            AutomationProperties.SetHelpText(element, request.AccessibilityDescription);
    }
}
