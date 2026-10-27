using System;
using System.Globalization;
using System.Resources;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Accessibility;

internal static class MarkdownLocalizedStrings
{
    private static readonly ResourceManager Resources = new(
        "MarkdownRenderer.Properties.Resources",
        typeof(MarkdownLocalizedStrings).Assembly);

    public static string MarkdownDocumentName => Get("MarkdownDocumentName", "Markdown document");
    public static string ImageName => Get("ImageName", "Image");
    public static string ImageLoadingFormat => Get("ImageLoadingFormat", "Loading {0}...");
    public static string ImageErrorFormat => Get("ImageErrorFormat", "Image unavailable: {0}");
    public static string TableName => Get("TableName", "Table");
    public static string ListName => Get("ListName", "List");
    public static string EmbeddedContentName => Get("EmbeddedContentName", "Embedded content");
    public static string EmbeddedContentUnavailable => Get(
        MarkdownStringKeys.EmbeddedContentUnavailable,
        "Embedded content unavailable.");
    public static string ContextMenuCopy => Get("ContextMenuCopy", "Copy");
    public static string ContextMenuCopyMarkdown => Get("ContextMenuCopyMarkdown", "Copy as Markdown");
    public static string ContextMenuCopyLink => Get("ContextMenuCopyLink", "Copy link");
    public static string ContextMenuCopyImage => Get("ContextMenuCopyImage", "Copy image");
    public static string ContextMenuCopyCode => Get("ContextMenuCopyCode", "Copy code");
    public static string ContextMenuCopyTable => Get("ContextMenuCopyTable", "Copy table");
    public static string ContextMenuSelectAll => Get("ContextMenuSelectAll", "Select All");
    public static string SelectionStartHandle => Get("SelectionStartHandle", "Selection start handle");
    public static string SelectionEndHandle => Get("SelectionEndHandle", "Selection end handle");
    public static string SelectionHandleHelp => Get(
        "SelectionHandleHelp",
        "Drag to adjust the selection. Use the arrow keys for precise adjustment.");
    public static string CodeBlockCopy => Get("CodeBlockCopy", "Copy");
    public static string CodeBlockCopied => Get("CodeBlockCopied", "Copied");
    public static string CodeBlockCopyAutomationName => Get("CodeBlockCopyAutomationName", "Copy code");
    public static string TaskCompleted => Get("TaskCompleted", "Completed task");
    public static string TaskIncomplete => Get("TaskIncomplete", "Incomplete task");
    public static string TaskReadOnly => Get("TaskReadOnly", "Read-only task state");
    public static string TaskToggle => Get("TaskToggle", "Toggle task state");
    public static string HtmlDetails => Get("HtmlDetails", "Details");
    public static string DiagramName => Get("DiagramName", "Diagram");
    public static string HtmlBudgetExceeded => Get(
        "HtmlBudgetExceeded",
        "Additional HTML content was omitted because it exceeded the renderer safety limit.");

    public static string CodeLanguageHelp(string language) =>
        string.Format(CultureInfo.CurrentUICulture, Get("CodeLanguageHelpFormat", "Language: {0}"), language);

    public static string CodeLanguageHelpFormat => Get("CodeLanguageHelpFormat", "Language: {0}");

    public static string StyleName(string elementKey)
    {
        var definition = GetStyleNameDefinition(elementKey);
        return definition.Key is null
            ? definition.Fallback
            : Get(definition.Key, definition.Fallback);
    }

    public static string? StyleNameKey(string elementKey) =>
        GetStyleNameDefinition(elementKey).Key;

    // Keep the resource key and fallback in one value mapping so localized UIA
    // names and host string-provider keys cannot drift. The tuple is a value
    // type; this does not introduce a dictionary or allocate per lookup.
    private static (string? Key, string Fallback) GetStyleNameDefinition(string elementKey) =>
        elementKey switch
        {
            Theming.MarkdownElementKeys.Body => (MarkdownStringKeys.StyleBody, "Body"),
            Theming.MarkdownElementKeys.Heading1 => (MarkdownStringKeys.StyleHeading1, "Heading 1"),
            Theming.MarkdownElementKeys.Heading2 => (MarkdownStringKeys.StyleHeading2, "Heading 2"),
            Theming.MarkdownElementKeys.Heading3 => (MarkdownStringKeys.StyleHeading3, "Heading 3"),
            Theming.MarkdownElementKeys.Heading4 => (MarkdownStringKeys.StyleHeading4, "Heading 4"),
            Theming.MarkdownElementKeys.Heading5 => (MarkdownStringKeys.StyleHeading5, "Heading 5"),
            Theming.MarkdownElementKeys.Heading6 => (MarkdownStringKeys.StyleHeading6, "Heading 6"),
            Theming.MarkdownElementKeys.CodeBlock => (MarkdownStringKeys.StyleCodeBlock, "Code block"),
            Theming.MarkdownElementKeys.CodeInline => (MarkdownStringKeys.StyleInlineCode, "Inline code"),
            Theming.MarkdownElementKeys.Quote => (MarkdownStringKeys.StyleQuote, "Quote"),
            Theming.MarkdownElementKeys.Link => (MarkdownStringKeys.StyleLink, "Link"),
            Theming.MarkdownElementKeys.Strong => (MarkdownStringKeys.StyleStrong, "Strong"),
            Theming.MarkdownElementKeys.Emphasis => (MarkdownStringKeys.StyleEmphasis, "Emphasis"),
            Theming.MarkdownElementKeys.Strikethrough => (MarkdownStringKeys.StyleStrikethrough, "Strikethrough"),
            Theming.MarkdownElementKeys.Subscript => (MarkdownStringKeys.StyleSubscript, "Subscript"),
            Theming.MarkdownElementKeys.Superscript => (MarkdownStringKeys.StyleSuperscript, "Superscript"),
            Theming.MarkdownElementKeys.Inserted => (MarkdownStringKeys.StyleInserted, "Inserted"),
            Theming.MarkdownElementKeys.Marked => (MarkdownStringKeys.StyleMarked, "Marked"),
            Theming.MarkdownElementKeys.Abbreviation => (MarkdownStringKeys.StyleAbbreviation, "Abbreviation"),
            Theming.MarkdownElementKeys.DefinitionTerm => (MarkdownStringKeys.StyleDefinitionTerm, "Definition term"),
            Theming.MarkdownElementKeys.DefinitionDescription => (MarkdownStringKeys.StyleDefinitionDescription, "Definition"),
            Theming.MarkdownElementKeys.Figure => (MarkdownStringKeys.StyleFigure, "Figure"),
            Theming.MarkdownElementKeys.FigureCaption => (MarkdownStringKeys.StyleFigureCaption, "Figure caption"),
            Theming.MarkdownElementKeys.Diagram => (MarkdownStringKeys.StyleDiagram, "Diagram"),
            Theming.MarkdownElementKeys.ListMarker => (MarkdownStringKeys.StyleListMarker, "List marker"),
            Theming.MarkdownElementKeys.Table => (MarkdownStringKeys.StyleTable, "Table"),
            Theming.MarkdownElementKeys.TableHeader => (MarkdownStringKeys.StyleTableHeader, "Table header"),
            Theming.MarkdownElementKeys.TableCell => (MarkdownStringKeys.StyleTableCell, "Table cell"),
            _ => (null, elementKey),
        };

    internal static string Resolve(string key, string fallback, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            return Resources.GetString(key, culture) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string Get(string key, string fallback)
        => Resolve(key, fallback, CultureInfo.CurrentUICulture);
}
