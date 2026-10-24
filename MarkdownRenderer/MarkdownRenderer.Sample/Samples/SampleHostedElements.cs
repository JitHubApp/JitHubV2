using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample;

internal sealed class SampleHostedElementExtension : IMarkdownExtension
{
    internal const string StyleRoleName = "SampleExtensionCallout";

    internal static SampleHostedElementExtension Instance { get; } = new();

    public string Id => "MarkdownRenderer.Sample.HostedElements";

    public void Configure(MarkdownExtensionBuilder builder)
        => builder
            .AddFeature("sample-hosted-elements")
            .RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, RenderFence);

    private static void RenderFence(
        MarkdownExtensionContext context,
        MarkdownContentBuilder content)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!context.Node.Attributes.TryGetValue("info", out string? info))
            return;

        if (info.StartsWith("styled:", StringComparison.OrdinalIgnoreCase))
        {
            string styledText = NormalizeLabel(
                info["styled:".Length..],
                "Ancestor-scoped extension style");
            content.AddText(
                styledText,
                context.Node.SourceSpan,
                new MarkdownStyleRole(StyleRoleName),
                MarkdownAccessibilityRole.Paragraph);
            return;
        }

        string factoryKey;
        string label;
        string semanticText;
        double desiredHeight;
        if (info.StartsWith("button:", StringComparison.OrdinalIgnoreCase))
        {
            factoryKey = SampleHostedElementFactory.ButtonKey;
            label = NormalizeLabel(info["button:".Length..], "Native action");
            semanticText = label;
            desiredHeight = 48;
        }
        else if (info.StartsWith("panel:", StringComparison.OrdinalIgnoreCase))
        {
            factoryKey = SampleHostedElementFactory.PanelKey;
            label = NormalizeLabel(info["panel:".Length..], "Composite action");
            semanticText = $"{label}; editable sample value; apply";
            desiredHeight = 56;
        }
        else if (info.StartsWith("unsupported:", StringComparison.OrdinalIgnoreCase))
        {
            factoryKey = SampleHostedElementFactory.UnsupportedKey;
            label = NormalizeLabel(info["unsupported:".Length..], "Fallback probe");
            semanticText = label;
            desiredHeight = 72;
        }
        else
        {
            return;
        }

        content.AddHostedElement(
            factoryKey,
            context.Node.SourceSpan,
            MarkdownStyleRole.Body,
            MarkdownAccessibilityRole.Group,
            new Dictionary<string, string>
            {
                [MarkdownHostedElementAttributes.DesiredHeight] =
                    desiredHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["label"] = label,
            },
            accessibilityName: label,
            accessibilityDescription: "Native viewport-realized Markdown sample content",
            semanticText: semanticText);
    }

    private static string NormalizeLabel(string value, string fallback)
        => string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().Replace('_', ' ');
}

internal sealed class SampleHostedElementFactory : IMarkdownHostedElementFactory
{
    internal const string ButtonKey = "MarkdownRenderer.Sample.Button";
    internal const string PanelKey = "MarkdownRenderer.Sample.Panel";
    internal const string UnsupportedKey = "MarkdownRenderer.Sample.Unsupported";
    internal static SampleHostedElementFactory Instance { get; } = new();

    public ValueTask<FrameworkElement?> CreateAsync(
        MarkdownHostedElementRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Attributes.TryGetValue("label", out string? label);
        label = string.IsNullOrWhiteSpace(label) ? "Native action" : label;

        FrameworkElement? element = request.FactoryKey switch
        {
            ButtonKey => CreateButton(request, label),
            PanelKey => CreatePanel(request, label),
            _ => null,
        };
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
