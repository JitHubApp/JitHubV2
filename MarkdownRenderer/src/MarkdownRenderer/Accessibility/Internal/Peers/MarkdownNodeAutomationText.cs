using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// The localized UIA name, help text and item status of a semantic node peer.
/// </summary>
internal static class MarkdownNodeAutomationText
{
    internal static string GetName(
        RendererLocalization localization,
        MarkdownSemanticDocument document,
        MarkdownSemanticNode node,
        InlineEmbedAutomationMetadata? task)
    {
        if (task is not null)
            return task.CurrentName;

        if (!string.IsNullOrWhiteSpace(node.AccessibilityName))
            return node.AccessibilityName;

        if (node.Role == MarkdownSemanticRole.Diagram)
        {
            return localization.Resolve(
                MarkdownStringKeys.DiagramName,
                MarkdownLocalizedStrings.DiagramName);
        }
        if (node.VectorSceneBox is not null && node.VectorSemanticIndex >= 0)
            return node.HelpText ?? string.Empty;

        // A code block's text can run to thousands of characters. Its name only
        // says what it is; the peer's TextPattern reads the code itself.
        if (node.Role == MarkdownSemanticRole.CodeBlock)
            return GetCodeBlockName(localization, node.CodeLanguage);

        var text = document.GetText(node);
        if (node.Role == MarkdownSemanticRole.Image)
            return GetImageName(localization, text, node.ImageBox?.AccessibilityState);

        if (!string.IsNullOrWhiteSpace(text) &&
            !string.Equals(text, MarkdownRenderer.Layout.Internal.Inlines.Runs.InlineEmbedRun.PlaceholderChar, System.StringComparison.Ordinal))
            return text;
        return node.Role switch
        {
            MarkdownSemanticRole.Image => localization.Resolve(
                MarkdownStringKeys.ImageName,
                MarkdownLocalizedStrings.ImageName),
            MarkdownSemanticRole.Table => localization.Resolve(
                MarkdownStringKeys.TableName,
                MarkdownLocalizedStrings.TableName),
            MarkdownSemanticRole.List => localization.Resolve(
                MarkdownStringKeys.ListName,
                MarkdownLocalizedStrings.ListName),
            MarkdownSemanticRole.Embed => localization.Resolve(
                MarkdownStringKeys.EmbeddedContentName,
                MarkdownLocalizedStrings.EmbeddedContentName),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// "TypeScript code block" when the language is known, otherwise "Code block".
    /// </summary>
    private static string GetCodeBlockName(RendererLocalization localization, string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return localization.Resolve(
                MarkdownStringKeys.StyleCodeBlock,
                MarkdownLocalizedStrings.StyleName(MarkdownElementKeys.CodeBlock));
        }

        return localization.ResolveFormatted(
            MarkdownStringKeys.CodeBlockLanguageName,
            MarkdownLocalizedStrings.CodeBlockLanguageNameFormat,
            language.Trim());
    }

    internal static string GetHelpText(
        MarkdownRendererControl owner,
        MarkdownSemanticNode node,
        InlineEmbedAutomationMetadata? task)
    {
        if (task is not null)
            return task.CurrentHelpText;

        if (node.Role == MarkdownSemanticRole.CodeBlock &&
            !string.IsNullOrWhiteSpace(node.CodeLanguage))
        {
            return owner.Localization.ResolveFormatted(
                MarkdownStringKeys.CodeLanguageHelp,
                MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                node.CodeLanguage);
        }

        return node.AccessibilityDescription ??
               (node.Role == MarkdownSemanticRole.Image ? node.ImageBox?.SvgDesc : null) ??
               node.HelpText ??
               string.Empty;
    }

    internal static string GetItemStatus(
        MarkdownRendererControl owner,
        MarkdownAutomationPeer root,
        MarkdownSemanticNode node)
    {
        if (node.Box is VectorSceneBox { GraphRebindFailed: true })
            return owner.Localization.Resolve(MarkdownStringKeys.EmbeddedContentUnavailable,
                MarkdownLocalizedStrings.EmbeddedContentUnavailable);
        if (node.Role == MarkdownSemanticRole.Image &&
            node.ImageBox?.AccessibilityState == MarkdownImageAccessibilityState.Loading)
        {
            string text = root.GetSemanticDocument().GetText(node);
            return GetImageName(owner.Localization, text, MarkdownImageAccessibilityState.Loading);
        }

        return string.Empty;
    }

    internal static string GetImageName(
        RendererLocalization localization,
        string text,
        MarkdownImageAccessibilityState? state)
    {
        string description = string.IsNullOrWhiteSpace(text)
            ? localization.Resolve(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName)
            : text;
        return state switch
        {
            MarkdownImageAccessibilityState.Loading => localization.ResolveFormatted(
                MarkdownStringKeys.ImageLoading,
                MarkdownLocalizedStrings.ImageLoadingFormat,
                description),
            MarkdownImageAccessibilityState.Error => localization.ResolveFormatted(
                MarkdownStringKeys.ImageError,
                MarkdownLocalizedStrings.ImageErrorFormat,
                description),
            _ => description,
        };
    }
}
