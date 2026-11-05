using JitHub.Services.Markdown.Hosting;
using MarkdownRenderer;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// Applies the text scale the lifecycle harness requests through its runtime
/// settings, so text-scale checks run without changing the machine setting.
/// </summary>
internal static class MarkdownLifecycleTextScaleResources
{
    private static readonly (string Role, string Token, double Fallback)[] ScalableRoleFonts =
    [
        (MarkdownElementKeys.Body, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Heading1, "AppMarkdownHeading1FontSize", 30),
        (MarkdownElementKeys.Heading2, "AppMarkdownHeading2FontSize", 24),
        (MarkdownElementKeys.Heading3, "AppMarkdownHeading3FontSize", 20),
        (MarkdownElementKeys.Heading4, "AppMarkdownHeading4FontSize", 17),
        (MarkdownElementKeys.Heading5, "AppMarkdownHeading5FontSize", 15),
        (MarkdownElementKeys.Heading6, "AppMarkdownHeading6FontSize", 14),
        (MarkdownElementKeys.Link, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Strong, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Emphasis, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Strikethrough, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Subscript, "AppMarkdownScriptFontSize", 12),
        (MarkdownElementKeys.Superscript, "AppMarkdownScriptFontSize", 12),
        (MarkdownElementKeys.Inserted, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Marked, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Abbreviation, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.CodeInline, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.CodeBlock, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.CodeBlockHeader, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockLanguage, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockGutter, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockLineNumber, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.Quote, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.ListMarker, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.ImageCaption, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.DefinitionTerm, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.DefinitionDescription, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Figure, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.FigureCaption, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.Diagram, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.Math, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Table, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.TableHeader, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.TableCell, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertNote, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertTip, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertImportant, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertWarning, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertCaution, "AppMarkdownBodyFontSize", 16),
    ];

    /// <summary>Scales the renderer's font-size overrides to the harness's requested text scale, if any.</summary>
    /// <param name="resources">The renderer's resource dictionary.</param>
    /// <param name="bridge">The app's lifecycle automation bridge, which reports the requested scale.</param>
    /// <param name="runtimeEvents">The app's best-effort Windows runtime event helper.</param>
    public static void Apply(
        ResourceDictionary resources,
        MarkdownLifecycleAutomationBridge bridge,
        RuntimeEventSubscription runtimeEvents)
    {
        if (bridge.GetTextScaleFactor() is double requestedScale)
        {
            double tokenScale = GetLifecycleTokenScaleFactor(requestedScale, runtimeEvents);
            foreach ((string role, string token, double fallback) in ScalableRoleFonts)
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(role),
                    MarkdownStyleProperty.FontSize)] =
                    MarkdownRendererResources.ResolveDouble(token, fallback) * tokenScale;
            }
        }
    }

    private static double GetLifecycleTokenScaleFactor(double requestedScale, RuntimeEventSubscription runtimeEvents)
    {
        UISettings? settings = runtimeEvents.TryCreate(
            static () => new UISettings(),
            nameof(UISettings));
        double? observedScale;
        try
        {
            observedScale = settings?.TextScaleFactor;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // An unreadable Windows scale counts as 1, as before.
            observedScale = null;
        }

        return MarkdownLifecycleTextScale.ToTokenScale(requestedScale, observedScale);
    }
}
