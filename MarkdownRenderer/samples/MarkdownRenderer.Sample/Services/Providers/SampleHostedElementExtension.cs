using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample.Services.Providers;

internal sealed class SampleHostedElementExtension : IMarkdownExtension
{
    internal const string StyleRoleName = "SampleExtensionCallout";

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

        if (DescribeHostedFence(info) is not { } fence)
            return;

        content.AddHostedElement(
            fence.FactoryKey,
            context.Node.SourceSpan,
            MarkdownStyleRole.Body,
            MarkdownAccessibilityRole.Group,
            new Dictionary<string, string>
            {
                [MarkdownHostedElementAttributes.DesiredHeight] =
                    fence.DesiredHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["label"] = fence.Label,
            },
            accessibilityName: fence.Label,
            accessibilityDescription: "Native viewport-realized Markdown sample content",
            semanticText: fence.SemanticText);
    }

    private static HostedFence? DescribeHostedFence(string info)
    {
        if (info.StartsWith("button:", StringComparison.OrdinalIgnoreCase))
        {
            string label = NormalizeLabel(info["button:".Length..], "Native action");
            return new(SampleHostedElementFactory.ButtonKey, label, label, 48);
        }

        if (info.StartsWith("panel:", StringComparison.OrdinalIgnoreCase))
        {
            string label = NormalizeLabel(info["panel:".Length..], "Composite action");
            return new(SampleHostedElementFactory.PanelKey, label, $"{label}; editable sample value; apply", 56);
        }

        if (info.StartsWith("unsupported:", StringComparison.OrdinalIgnoreCase))
        {
            string label = NormalizeLabel(info["unsupported:".Length..], "Fallback probe");
            return new(SampleHostedElementFactory.UnsupportedKey, label, label, 72);
        }

        if (info.StartsWith("throw:", StringComparison.OrdinalIgnoreCase))
        {
            // Hosted-element failure probe: the factory throws.
            string label = NormalizeLabel(info["throw:".Length..], "Throwing factory probe");
            return new(SampleHostedElementFactory.ThrowingKey, label, label, 48);
        }

        if (info.StartsWith("attach-failed:", StringComparison.OrdinalIgnoreCase))
        {
            // Hosted-element failure probe: the factory returns an element that
            // already has a parent, so attaching it fails.
            string label = NormalizeLabel(info["attach-failed:".Length..], "Attach-failed factory probe");
            return new(SampleHostedElementFactory.AttachFailedKey, label, label, 48);
        }

        return null;
    }

    private static string NormalizeLabel(string value, string fallback)
        => string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().Replace('_', ' ');

    private readonly record struct HostedFence(
        string FactoryKey,
        string Label,
        string SemanticText,
        double DesiredHeight);
}
