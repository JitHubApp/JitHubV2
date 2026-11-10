using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Creates the semantic nodes for inline runs and vector scene items from
/// their layout objects and already-appended text ranges. Registration and
/// parenting stay with <see cref="MarkdownSemanticDocumentBuilder"/>.
/// </summary>
internal static class MarkdownSemanticNodeFactory
{
    /// <summary>The semantic child an interactive or described inline run adds, if any.</summary>
    internal static MarkdownSemanticNode? CreateInlineRunNode(
        InlineContainerBox inline,
        InlineRun run,
        int runStart,
        int runEnd) => run switch
    {
        LinkRun link => new MarkdownSemanticNode(MarkdownSemanticRole.Link, inline)
        {
            InlineBox = inline,
            InlineRun = link,
            TextStart = runStart,
            TextEnd = runEnd,
            HelpText = link.Url,
        },
        InlineEmbedRun embedRun => new MarkdownSemanticNode(MarkdownSemanticRole.Embed, inline)
        {
            InlineBox = inline,
            InlineRun = embedRun,
            TextStart = runStart,
            TextEnd = runEnd,
            AccessibilityName = embedRun.AutomationMetadata?.CurrentName,
            AccessibilityDescription = embedRun.AutomationMetadata?.ReadOnlyHelpText,
            AutomationId = embedRun.AutomationMetadata?.AutomationId,
        },
        InlineImageRun imageRun => CreateInlineImageNode(inline, imageRun, runStart, runEnd),
        InlineVectorSceneRun vectorRun => CreateInlineMathNode(inline, vectorRun, runStart, runEnd),
        AbbreviationRun abbreviationRun => new MarkdownSemanticNode(MarkdownSemanticRole.Abbreviation, inline)
        {
            InlineBox = inline,
            InlineRun = abbreviationRun,
            TextStart = runStart,
            TextEnd = runEnd,
            HelpText = abbreviationRun.Expansion,
        },
        _ => null,
    };

    internal static MarkdownSemanticNode CreateInlineImageNode(
        InlineContainerBox inline,
        InlineImageRun imageRun,
        int runStart,
        int runEnd) => new(MarkdownSemanticRole.Image, inline)
    {
        InlineBox = inline,
        ImageBox = imageRun.Image,
        InlineRun = imageRun,
        TextStart = runStart,
        TextEnd = runEnd,
        HelpText = imageRun.IsLinked
            ? !string.IsNullOrWhiteSpace(imageRun.LinkTitle)
                ? imageRun.LinkTitle
                : imageRun.LinkUrl
            : !string.IsNullOrWhiteSpace(imageRun.Title)
                ? imageRun.Title
            : imageRun.Url,
    };

    internal static MarkdownSemanticNode CreateInlineMathNode(
        InlineContainerBox inline,
        InlineVectorSceneRun vectorRun,
        int runStart,
        int runEnd) => new(MarkdownSemanticRole.Math, inline)
    {
        InlineBox = inline,
        InlineRun = vectorRun,
        TextStart = runStart,
        TextEnd = runEnd,
        AccessibilityRole = MarkdownAccessibilityRole.Math,
        AccessibilityName = vectorRun.AccessibilityName,
        AccessibilityDescription = vectorRun.AccessibilityDescription,
        HelpText = vectorRun.IsLinked
            ? !string.IsNullOrWhiteSpace(vectorRun.LinkTitle)
                ? vectorRun.LinkTitle
                : vectorRun.LinkUrl
            : vectorRun.AccessibilityDescription,
        AutomationId = string.Concat(
            "MarkdownMath_",
            vectorRun.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "_",
            vectorRun.SourceSpan.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };

    /// <summary>
    /// The scene's own name, or else the name of its exposed root diagram item.
    /// </summary>
    internal static string? ResolveVectorSceneAccessibilityName(
        VectorSceneBox vector,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        string? accessibilityName = vector.AccessibilityName;
        if (string.IsNullOrWhiteSpace(accessibilityName))
        {
            for (int i = 0; i < semantics.Count; i++)
            {
                vector.ThrowIfCancellationRequested();
                MarkdownVectorSemanticItem candidate = semantics[i];
                if (MarkdownVectorSemanticPolicy.IsExposed(candidate.Flags) &&
                    candidate.ParentIndex < 0 &&
                    candidate.Role == MarkdownVectorSemanticRole.Diagram &&
                    !string.IsNullOrWhiteSpace(candidate.Name))
                {
                    accessibilityName = candidate.Name;
                    break;
                }
            }
        }

        return accessibilityName;
    }

    internal static MarkdownSemanticNode CreateVectorSceneNode(
        VectorSceneBox vector,
        int start,
        int end,
        string? accessibilityName) => new(
            vector.AccessibilityRole == MarkdownAccessibilityRole.Math
                ? MarkdownSemanticRole.Math
                : MarkdownSemanticRole.Diagram,
            vector)
        {
            VectorSceneBox = vector,
            TextStart = start,
            TextEnd = end,
            AccessibilityRole = vector.AccessibilityRole,
            AccessibilityName = accessibilityName,
            AccessibilityDescription = vector.AccessibilityDescription,
            AutomationId = string.Concat(
                vector.AccessibilityRole == MarkdownAccessibilityRole.Math ? "MarkdownMath_" : "MarkdownDiagram_",
                vector.SourceRange.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "_",
                vector.SourceRange.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

    internal static MarkdownSemanticNode CreateVectorSemanticNode(
        VectorSceneBox vector,
        int semanticIndex,
        MarkdownVectorSemanticItem item,
        (int Start, int End)? range,
        int start)
    {
        MarkdownVectorLinkAction? link = vector.GetSemanticLinkAction(semanticIndex);
        bool invokable = MarkdownVectorSemanticPolicy.IsInvokable(item.Flags, link is not null);
        return new MarkdownSemanticNode(invokable
            ? MarkdownSemanticRole.Link
            : MarkdownSemanticRole.Group, vector)
        {
            VectorSceneBox = vector,
            VectorSemanticIndex = semanticIndex,
            VectorSemanticRole = item.Role,
            VectorSemanticFlags = item.Flags,
            TextStart = range?.Start ?? start,
            TextEnd = range?.End ?? start,
            AccessibilityRole = invokable
                ? MarkdownAccessibilityRole.Link
                : MarkdownAccessibilityRole.Group,
            AccessibilityName = item.Name,
            AccessibilityDescription = item.Description,
            HelpText = link?.Target ?? link?.Action,
            AutomationId = string.Concat(
                "MarkdownDiagramItem_",
                vector.SourceRange.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "_",
                item.SourceId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
        };
    }
}
