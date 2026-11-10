using Microsoft.UI.Xaml.Automation.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// The UIA class name, control type and heading level of a semantic node peer,
/// from the node's role, hosted element, vector semantics and task state.
/// </summary>
internal static class MarkdownNodeAutomationDescriptor
{
    internal static string GetClassName(MarkdownSemanticNode node, InlineEmbedAutomationMetadata? task)
    {
        if (task is not null)
            return task.CanToggle ? "MarkdownTaskCheckBox" : "MarkdownTaskState";

        if (node.HostedElementBox is not null)
        {
            return node.AccessibilityRole switch
            {
                MarkdownAccessibilityRole.Text => "MarkdownHostedText",
                MarkdownAccessibilityRole.Group => "MarkdownHostedGroup",
                MarkdownAccessibilityRole.Paragraph => "MarkdownHostedParagraph",
                MarkdownAccessibilityRole.Heading => "MarkdownHostedHeading",
                MarkdownAccessibilityRole.Link => "MarkdownHostedLink",
                MarkdownAccessibilityRole.Image => "MarkdownHostedImage",
                MarkdownAccessibilityRole.Code => "MarkdownHostedCode",
                MarkdownAccessibilityRole.List => "MarkdownHostedList",
                MarkdownAccessibilityRole.ListItem => "MarkdownHostedListItem",
                MarkdownAccessibilityRole.Table => "MarkdownHostedTable",
                MarkdownAccessibilityRole.Row => "MarkdownHostedRow",
                MarkdownAccessibilityRole.Cell => "MarkdownHostedCell",
                MarkdownAccessibilityRole.Math => "MarkdownHostedMath",
                MarkdownAccessibilityRole.Diagram => "MarkdownHostedDiagram",
                MarkdownAccessibilityRole.Status => "MarkdownHostedStatus",
                _ => "MarkdownHostedElement",
            };
        }

        if (node.VectorSceneBox is not null && node.VectorSemanticIndex >= 0)
        {
            return node.Role == MarkdownSemanticRole.Link
                ? "MarkdownDiagramLink"
                : node.VectorSemanticRole switch
                {
                    MarkdownVectorSemanticRole.Node => "MarkdownDiagramNode",
                    MarkdownVectorSemanticRole.Edge => "MarkdownDiagramEdge",
                    MarkdownVectorSemanticRole.Label => "MarkdownDiagramLabel",
                    MarkdownVectorSemanticRole.Legend => "MarkdownDiagramLegend",
                    _ => "MarkdownDiagramGroup",
                };
        }

        return node.Role switch
        {
            MarkdownSemanticRole.Heading => "MarkdownHeading",
            MarkdownSemanticRole.CodeBlock => "MarkdownCodeBlock",
            MarkdownSemanticRole.List => "MarkdownList",
            MarkdownSemanticRole.ListItem => "MarkdownListItem",
            MarkdownSemanticRole.Table => "MarkdownTable",
            MarkdownSemanticRole.TableCell => "MarkdownTableCell",
            MarkdownSemanticRole.Image => "MarkdownImage",
            MarkdownSemanticRole.Math => "MarkdownMath",
            MarkdownSemanticRole.Diagram => "MarkdownDiagram",
            MarkdownSemanticRole.Embed => "MarkdownEmbed",
            MarkdownSemanticRole.Abbreviation => "MarkdownAbbreviation",
            _ => "MarkdownGroup",
        };
    }

    internal static AutomationControlType GetControlType(
        MarkdownSemanticNode node,
        InlineEmbedAutomationMetadata? task)
    {
        if (task is not null)
            return task.CanToggle ? AutomationControlType.CheckBox : AutomationControlType.Text;

        if (node.HostedElementBox is not null)
        {
            return node.AccessibilityRole switch
            {
                MarkdownAccessibilityRole.Text or MarkdownAccessibilityRole.Paragraph or
                MarkdownAccessibilityRole.Status => AutomationControlType.Text,
                MarkdownAccessibilityRole.Heading => AutomationControlType.Header,
                MarkdownAccessibilityRole.Link => AutomationControlType.Hyperlink,
                MarkdownAccessibilityRole.Image or MarkdownAccessibilityRole.Math or
                MarkdownAccessibilityRole.Diagram => AutomationControlType.Image,
                MarkdownAccessibilityRole.List => AutomationControlType.List,
                MarkdownAccessibilityRole.ListItem => AutomationControlType.ListItem,
                MarkdownAccessibilityRole.Table => AutomationControlType.Table,
                MarkdownAccessibilityRole.Cell => AutomationControlType.DataItem,
                MarkdownAccessibilityRole.Row or MarkdownAccessibilityRole.Group => AutomationControlType.Group,
                _ => AutomationControlType.Custom,
            };
        }

        if (IsLinkedInlineVectorScene(node))
            return AutomationControlType.Hyperlink;

        if (node.VectorSceneBox is not null && node.VectorSemanticIndex >= 0)
        {
            if (node.Role == MarkdownSemanticRole.Link)
                return AutomationControlType.Hyperlink;
            return node.VectorSemanticRole == MarkdownVectorSemanticRole.Label
                ? AutomationControlType.Text
                : AutomationControlType.Group;
        }

        return node.Role switch
        {
            MarkdownSemanticRole.Heading => AutomationControlType.Header,
            MarkdownSemanticRole.CodeBlock => AutomationControlType.Group,
            MarkdownSemanticRole.List => AutomationControlType.List,
            MarkdownSemanticRole.ListItem => AutomationControlType.ListItem,
            MarkdownSemanticRole.Table => AutomationControlType.Table,
            MarkdownSemanticRole.TableCell => AutomationControlType.DataItem,
            MarkdownSemanticRole.Image => AutomationControlType.Image,
            MarkdownSemanticRole.Math => AutomationControlType.Image,
            MarkdownSemanticRole.Diagram => AutomationControlType.Image,
            MarkdownSemanticRole.Link when node.VectorSceneBox is not null => AutomationControlType.Hyperlink,
            MarkdownSemanticRole.Embed => AutomationControlType.Custom,
            MarkdownSemanticRole.Abbreviation => AutomationControlType.Text,
            _ => AutomationControlType.Group,
        };
    }

    internal static AutomationHeadingLevel GetHeadingLevel(MarkdownSemanticNode node)
    {
        return node.HeadingLevel switch
        {
            1 => AutomationHeadingLevel.Level1,
            2 => AutomationHeadingLevel.Level2,
            3 => AutomationHeadingLevel.Level3,
            4 => AutomationHeadingLevel.Level4,
            5 => AutomationHeadingLevel.Level5,
            6 => AutomationHeadingLevel.Level6,
            _ => AutomationHeadingLevel.None,
        };
    }

    // The peer checks document currency before calling, so only the run shape remains.
    private static bool IsLinkedInlineVectorScene(MarkdownSemanticNode node) =>
        node.InlineRun is InlineVectorSceneRun { IsLinked: true };
}
