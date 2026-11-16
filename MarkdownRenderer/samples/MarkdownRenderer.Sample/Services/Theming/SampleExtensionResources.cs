using MarkdownRenderer.Sample.Services.Providers;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Sample.Services.Theming;

/// <summary>
/// Theme resources for the sample extension's style role and the diagram
/// role, set on an ancestor of the renderer.
/// </summary>
/// <remarks>
/// This exercises the public resource contract from an ancestor rather than
/// assigning a MarkdownTheme override directly to the renderer. The
/// non-built-in role must be discovered from both the Default theme
/// dictionary and the selected High Contrast theme dictionary.
/// </remarks>
internal static class SampleExtensionResources
{
    internal static void ApplyTo(FrameworkElement ancestor)
    {
        var extensionStyleRole = new MarkdownStyleRole(SampleHostedElementExtension.StyleRoleName);
        var extensionResources = new ResourceDictionary
        {
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.ForegroundBrush)] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x2B, 0xE2)),
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.FontSize)] = 19d,
            [MarkdownResourceKeys.ForRole(MarkdownStyleRole.Diagram, MarkdownStyleProperty.FontFamily)] =
                new FontFamily("Cascadia Mono"),
        };
        var extensionHighContrastResources = new ResourceDictionary
        {
            // Deliberately non-system authored color: the renderer must map it
            // back to WindowText while retaining the HC-specific typography.
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.ForegroundBrush)] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x00, 0xFF)),
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.FontSize)] = 23d,
            [MarkdownResourceKeys.ForRole(MarkdownStyleRole.Diagram, MarkdownStyleProperty.FontFamily)] =
                new FontFamily("Cascadia Mono"),
        };
        ancestor.Resources.ThemeDictionaries["Default"] = extensionResources;
        ancestor.Resources.ThemeDictionaries["HighContrast"] = extensionHighContrastResources;
    }
}
