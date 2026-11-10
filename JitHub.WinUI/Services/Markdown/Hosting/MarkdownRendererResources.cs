using System.Collections.Immutable;
using JitHub.Markdown.Hosting;
using MarkdownRenderer;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JitHub.Services.Markdown.Hosting;

/// <summary>
/// Maps JitHub's app resources onto a renderer's local resource overrides.
/// Every color comes from theme resources, so real High Contrast keeps working
/// through the renderer's own environment monitor.
/// </summary>
internal static class MarkdownRendererResources
{
    /// <summary>Roles whose background follows the host surface.</summary>
    public static readonly ImmutableArray<string> HostSurfaceRoles =
    [
        MarkdownElementKeys.Body,
        MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Figure,
        MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];

    /// <summary>Roles whose local overrides the host owns and clears before reapplying.</summary>
    public static readonly ImmutableArray<string> StyledRoles =
    [
        MarkdownElementKeys.Body,
        MarkdownElementKeys.Heading1,
        MarkdownElementKeys.Heading2,
        MarkdownElementKeys.Heading3,
        MarkdownElementKeys.Heading4,
        MarkdownElementKeys.Heading5,
        MarkdownElementKeys.Heading6,
        MarkdownElementKeys.Link,
        MarkdownElementKeys.Strong,
        MarkdownElementKeys.Emphasis,
        MarkdownElementKeys.Strikethrough,
        MarkdownElementKeys.Subscript,
        MarkdownElementKeys.Superscript,
        MarkdownElementKeys.Inserted,
        MarkdownElementKeys.Marked,
        MarkdownElementKeys.Abbreviation,
        MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock,
        MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockLanguage,
        MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber,
        MarkdownElementKeys.Quote,
        MarkdownElementKeys.ListMarker,
        MarkdownElementKeys.ThematicBreak,
        MarkdownElementKeys.ImageCaption,
        MarkdownElementKeys.DefinitionTerm,
        MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Figure,
        MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.Diagram,
        MarkdownElementKeys.Math,
        MarkdownElementKeys.Table,
        MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell,
        MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];

    /// <summary>Removes every local override the host may have written.</summary>
    /// <param name="resources">The renderer's resource dictionary.</param>
    public static void RemoveLocalOverrides(ResourceDictionary resources)
    {
        resources.Remove(MarkdownResourceKeys.DocumentSurfaceBrush);
        resources.Remove(MarkdownResourceKeys.SelectionBackgroundBrush);
        resources.Remove(MarkdownResourceKeys.SelectionForegroundBrush);
        resources.Remove(MarkdownResourceKeys.FocusVisualBrush);
        resources.Remove(MarkdownResourceKeys.OverflowIndicatorBrush);

        foreach (string roleName in StyledRoles)
        {
            MarkdownStyleRole role = MarkdownStyleRole.FromElementKey(roleName);
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.ForegroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.HoverForegroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FocusForegroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.BackgroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.AccentBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.BorderBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontFamily));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontSize));
        }
    }

    /// <summary>Paints the document and surface-following roles with the host surface brush.</summary>
    /// <param name="resources">The renderer's resource dictionary.</param>
    /// <param name="surfaceColorToken">The host's explicit surface token, if any.</param>
    /// <param name="hostKind">The host kind, which supplies the default surface token.</param>
    public static void ApplyHostSurface(ResourceDictionary resources, string? surfaceColorToken, string? hostKind)
    {
        if (TryResolveHostSurfaceBrush(surfaceColorToken, hostKind, out SolidColorBrush? hostSurface) && hostSurface is not null)
        {
            resources[MarkdownResourceKeys.DocumentSurfaceBrush] = hostSurface;
            foreach (string role in HostSurfaceRoles)
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(role),
                    MarkdownStyleProperty.BackgroundBrush)] = hostSurface;
            }
        }
    }

    /// <summary>Resolves a thickness app design token.</summary>
    /// <param name="tokenName">The token's resource key.</param>
    /// <param name="fallback">The value to use when the token is missing.</param>
    /// <returns>The token's value.</returns>
    public static Thickness ResolveThickness(string tokenName, Thickness fallback)
        => TryResolveAppResource(tokenName, out object? value) && value is Thickness thickness
            ? thickness
            : fallback;

    /// <summary>Looks up an application resource.</summary>
    /// <param name="tokenName">The resource key.</param>
    /// <param name="value">The resource, when found.</param>
    /// <returns><see langword="true"/> when the application defines the resource.</returns>
    public static bool TryResolveAppResource(string tokenName, out object? value)
    {
        value = null;
        return Application.Current?.Resources is { } resources &&
            resources.TryGetValue(tokenName, out value);
    }

    private static bool TryResolveHostSurfaceBrush(string? surfaceColorToken, string? hostKind, out SolidColorBrush? brush)
    {
        string token = string.IsNullOrWhiteSpace(surfaceColorToken)
            ? MarkdownHostContract.GetSurfaceColorToken(hostKind)
            : surfaceColorToken.Trim();

        if (TryResolveAppResource(token + "Brush", out object? value) &&
            value is SolidColorBrush resolvedBrush)
        {
            brush = resolvedBrush;
            return true;
        }

        if (TryResolveAppResource(token + "Color", out value) &&
            value is Windows.UI.Color color)
        {
            brush = new SolidColorBrush(color);
            return true;
        }

        brush = null;
        return false;
    }
}
