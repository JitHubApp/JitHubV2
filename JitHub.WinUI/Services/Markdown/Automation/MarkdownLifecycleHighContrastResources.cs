using System.Collections.Generic;
using JitHub.Services.Markdown.Hosting;
using MarkdownRenderer;
using MarkdownRenderer.Theming;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// A fixed High Contrast palette the lifecycle harness asks for so it can
/// verify contrast rendering without changing the machine's theme. Product
/// hosts never use it: real High Contrast flows through theme resources and the
/// renderer's own environment monitor.
/// </summary>
internal static class MarkdownLifecycleHighContrastResources
{
    private static readonly string[] WindowBackgroundRoles =
    [
        MarkdownElementKeys.Body,
        MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock,
        MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.Marked,
        MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Figure,
        MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.Diagram,
        MarkdownElementKeys.Table,
        MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell,
        MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];

    private static readonly string[] TextBorderRoles =
    [
        MarkdownElementKeys.CodeBlock,
        MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.Diagram,
        MarkdownElementKeys.Table,
        MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell,
    ];

    private static readonly string[] MonospaceRoles =
    [
        MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock,
        MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber,
        MarkdownElementKeys.Diagram,
    ];

    private static readonly string[] LinkAccentRoles =
    [
        MarkdownElementKeys.Quote,
        MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];

    /// <summary>Writes the fixed High Contrast palette into a renderer's local overrides.</summary>
    /// <param name="resources">The renderer's resource dictionary.</param>
    public static void Apply(ResourceDictionary resources)
    {
        SolidColorBrush window = new(Colors.Black);
        SolidColorBrush text = new(Colors.White);
        SolidColorBrush link = new(Colors.Yellow);
        SolidColorBrush focus = new(Colors.Cyan);

        resources[MarkdownResourceKeys.DocumentSurfaceBrush] = window;
        resources[MarkdownResourceKeys.SelectionBackgroundBrush] = link;
        resources[MarkdownResourceKeys.SelectionForegroundBrush] = window;
        resources[MarkdownResourceKeys.FocusVisualBrush] = focus;
        resources[MarkdownResourceKeys.OverflowIndicatorBrush] = text;

        SetForRoles(resources, MarkdownRendererResources.StyledRoles, MarkdownStyleProperty.ForegroundBrush, text);
        SetForRoles(resources, WindowBackgroundRoles, MarkdownStyleProperty.BackgroundBrush, window);
        SetForRoles(resources, TextBorderRoles, MarkdownStyleProperty.BorderBrush, text);
        if (MarkdownRendererResources.TryResolveAppResource("AppHighContrastMonoFontFamily", out object? monoFont))
        {
            SetForRoles(resources, MonospaceRoles, MarkdownStyleProperty.FontFamily, monoFont);
        }

        MarkdownStyleRole linkRole = MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Link);
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.ForegroundBrush)] = link;
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.HoverForegroundBrush)] = link;
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.FocusForegroundBrush)] = focus;

        SetForRoles(resources, LinkAccentRoles, MarkdownStyleProperty.AccentBrush, link);
    }

    private static void SetForRoles(
        ResourceDictionary resources,
        IEnumerable<string> roleNames,
        MarkdownStyleProperty property,
        object? value)
    {
        foreach (string roleName in roleNames)
        {
            resources[MarkdownResourceKeys.ForRole(MarkdownStyleRole.FromElementKey(roleName), property)] = value;
        }
    }
}
