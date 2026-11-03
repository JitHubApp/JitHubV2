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

        foreach (string roleName in MarkdownRendererResources.StyledRoles)
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.ForegroundBrush)] = text;
        }

        foreach (string roleName in new[]
        {
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
        })
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.BackgroundBrush)] = window;
        }

        foreach (string roleName in new[]
        {
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.Diagram,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        })
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.BorderBrush)] = text;
        }

        if (MarkdownRendererResources.TryResolveAppResource("AppHighContrastMonoFontFamily", out object? monoFont))
        {
            foreach (string roleName in new[]
            {
                MarkdownElementKeys.CodeInline,
                MarkdownElementKeys.CodeBlock,
                MarkdownElementKeys.CodeBlockGutter,
                MarkdownElementKeys.CodeBlockLineNumber,
                MarkdownElementKeys.Diagram,
            })
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(roleName),
                    MarkdownStyleProperty.FontFamily)] = monoFont;
            }
        }

        MarkdownStyleRole linkRole = MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Link);
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.ForegroundBrush)] = link;
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.HoverForegroundBrush)] = link;
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.FocusForegroundBrush)] = focus;

        foreach (string roleName in new[]
        {
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.AlertNote,
            MarkdownElementKeys.AlertTip,
            MarkdownElementKeys.AlertImportant,
            MarkdownElementKeys.AlertWarning,
            MarkdownElementKeys.AlertCaution,
        })
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.AccentBrush)] = link;
        }
    }
}
