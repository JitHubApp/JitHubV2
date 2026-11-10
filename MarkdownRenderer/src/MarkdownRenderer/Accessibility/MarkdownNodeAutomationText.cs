using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The localized UIA name, help text and item status of a semantic node peer.
/// </summary>
internal static class MarkdownNodeAutomationText
{
    internal static string GetName(
        MarkdownRendererControl owner,
        MarkdownAutomationPeer root,
        MarkdownSemanticNode node,
        InlineEmbedAutomationMetadata? task)
    {
        if (task is not null)
            return task.CurrentName;

        if (!string.IsNullOrWhiteSpace(node.AccessibilityName))
            return node.AccessibilityName;

        if (node.Role == MarkdownSemanticRole.Diagram)
        {
            return owner.Localization.Resolve(
                MarkdownStringKeys.DiagramName,
                MarkdownLocalizedStrings.DiagramName);
        }
        if (node.VectorSceneBox is not null && node.VectorSemanticIndex >= 0)
            return node.HelpText ?? string.Empty;

        if (node.Role == MarkdownSemanticRole.CodeBlock && node.Box is CodeBlockBox)
        {
            return owner.Localization.Resolve(
                MarkdownStringKeys.StyleCodeBlock,
                MarkdownLocalizedStrings.StyleName(MarkdownElementKeys.CodeBlock));
        }

        var text = root.GetSemanticDocument().GetText(node);
        if (node.Role == MarkdownSemanticRole.Image)
            return GetImageName(owner, text, node.ImageBox?.AccessibilityState);

        if (!string.IsNullOrWhiteSpace(text) &&
            !string.Equals(text, MarkdownRenderer.Layout.InlineEmbedRun.PlaceholderChar, System.StringComparison.Ordinal))
            return text;
        return node.Role switch
        {
            MarkdownSemanticRole.Image => owner.Localization.Resolve(
                MarkdownStringKeys.ImageName,
                MarkdownLocalizedStrings.ImageName),
            MarkdownSemanticRole.Table => owner.Localization.Resolve(
                MarkdownStringKeys.TableName,
                MarkdownLocalizedStrings.TableName),
            MarkdownSemanticRole.List => owner.Localization.Resolve(
                MarkdownStringKeys.ListName,
                MarkdownLocalizedStrings.ListName),
            MarkdownSemanticRole.Embed => owner.Localization.Resolve(
                MarkdownStringKeys.EmbeddedContentName,
                MarkdownLocalizedStrings.EmbeddedContentName),
            _ => string.Empty,
        };
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
        if (node.Box is Layout.Boxes.VectorSceneBox { GraphRebindFailed: true })
            return owner.Localization.Resolve(MarkdownStringKeys.EmbeddedContentUnavailable,
                MarkdownLocalizedStrings.EmbeddedContentUnavailable);
        if (node.Role == MarkdownSemanticRole.Image &&
            node.ImageBox?.AccessibilityState == MarkdownImageAccessibilityState.Loading)
        {
            string text = root.GetSemanticDocument().GetText(node);
            return GetImageName(owner, text, MarkdownImageAccessibilityState.Loading);
        }

        return string.Empty;
    }

    internal static string GetImageName(
        MarkdownRendererControl owner,
        string text,
        MarkdownImageAccessibilityState? state)
    {
        string description = string.IsNullOrWhiteSpace(text)
            ? owner.Localization.Resolve(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName)
            : text;
        return state switch
        {
            MarkdownImageAccessibilityState.Loading => owner.Localization.ResolveFormatted(
                MarkdownStringKeys.ImageLoading,
                MarkdownLocalizedStrings.ImageLoadingFormat,
                description),
            MarkdownImageAccessibilityState.Error => owner.Localization.ResolveFormatted(
                MarkdownStringKeys.ImageError,
                MarkdownLocalizedStrings.ImageErrorFormat,
                description),
            _ => description,
        };
    }
}
