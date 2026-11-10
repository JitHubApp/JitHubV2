using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Renders the TouchSelection sample's <c>button:</c> fences as hosted elements and
/// records which fence info strings the parser handed to it.
/// </summary>
internal sealed class TouchSelectionHostedElementExtension : MarkdownRenderer.Extensions.IMarkdownExtension
{
    internal static TouchSelectionHostedElementExtension Instance { get; } = new();
    private static int _invocationCount;
    private static string[] _observedInfo = [];

    internal static int InvocationCount => Volatile.Read(ref _invocationCount);
    internal static IReadOnlyList<string> ObservedInfo => Volatile.Read(ref _observedInfo);

    internal static void Reset()
    {
        Volatile.Write(ref _invocationCount, 0);
        Volatile.Write(ref _observedInfo, []);
    }

    public string Id => "MarkdownRenderer.GitHub.Tests.TouchSelectionHostedElement";

    public void Configure(MarkdownExtensionBuilder builder)
        => builder
            .AddFeature("test-touch-selection-hosted-element")
            .RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, RenderFence);

    private static void RenderFence(MarkdownExtensionContext context, MarkdownContentBuilder content)
    {
        Interlocked.Increment(ref _invocationCount);
        string info = context.Node.Attributes.TryGetValue("info", out string? observed)
            ? observed
            : "<missing-info>";
        while (true)
        {
            string[] current = Volatile.Read(ref _observedInfo);
            string[] updated = [.. current, info];
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _observedInfo, updated, current),
                current))
            {
                break;
            }
        }

        if (!info.StartsWith("button:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string label = info["button:".Length..].Trim().Replace('_', ' ');
        content.AddHostedElement(
            factoryKey: "MarkdownRenderer.Sample.Button",
            sourceSpan: context.Node.SourceSpan,
            styleRole: MarkdownStyleRole.Body,
            accessibilityRole: MarkdownAccessibilityRole.Group,
            attributes: new Dictionary<string, string>
            {
                [MarkdownHostedElementAttributes.DesiredHeight] = "48",
                ["label"] = label,
            },
            accessibilityName: label,
            accessibilityDescription: "Native viewport-realized Markdown sample content",
            semanticText: label);
    }
}
