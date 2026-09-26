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

    public static string StyleName(string elementKey) => elementKey switch
    {
        Theming.MarkdownElementKeys.Body => Get("StyleBody", "Body"),
        Theming.MarkdownElementKeys.Heading1 => Get("StyleHeading1", "Heading 1"),
        Theming.MarkdownElementKeys.Heading2 => Get("StyleHeading2", "Heading 2"),
        Theming.MarkdownElementKeys.Heading3 => Get("StyleHeading3", "Heading 3"),
        Theming.MarkdownElementKeys.Heading4 => Get("StyleHeading4", "Heading 4"),
        Theming.MarkdownElementKeys.Heading5 => Get("StyleHeading5", "Heading 5"),
        Theming.MarkdownElementKeys.Heading6 => Get("StyleHeading6", "Heading 6"),
        Theming.MarkdownElementKeys.CodeBlock => Get("StyleCodeBlock", "Code block"),
        Theming.MarkdownElementKeys.CodeInline => Get("StyleInlineCode", "Inline code"),
        Theming.MarkdownElementKeys.Quote => Get("StyleQuote", "Quote"),
        Theming.MarkdownElementKeys.Link => Get("StyleLink", "Link"),
        Theming.MarkdownElementKeys.Strong => Get("StyleStrong", "Strong"),
        Theming.MarkdownElementKeys.Emphasis => Get("StyleEmphasis", "Emphasis"),
        Theming.MarkdownElementKeys.Strikethrough => Get("StyleStrikethrough", "Strikethrough"),
        Theming.MarkdownElementKeys.Subscript => Get("StyleSubscript", "Subscript"),
        Theming.MarkdownElementKeys.Superscript => Get("StyleSuperscript", "Superscript"),
        Theming.MarkdownElementKeys.Inserted => Get("StyleInserted", "Inserted"),
        Theming.MarkdownElementKeys.Marked => Get("StyleMarked", "Marked"),
        Theming.MarkdownElementKeys.Abbreviation => Get("StyleAbbreviation", "Abbreviation"),
        Theming.MarkdownElementKeys.DefinitionTerm => Get("StyleDefinitionTerm", "Definition term"),
        Theming.MarkdownElementKeys.DefinitionDescription => Get("StyleDefinitionDescription", "Definition"),
        Theming.MarkdownElementKeys.Figure => Get("StyleFigure", "Figure"),
        Theming.MarkdownElementKeys.FigureCaption => Get("StyleFigureCaption", "Figure caption"),
        Theming.MarkdownElementKeys.Diagram => Get("StyleDiagram", "Diagram"),
        Theming.MarkdownElementKeys.ListMarker => Get("StyleListMarker", "List marker"),
        Theming.MarkdownElementKeys.Table => Get("StyleTable", "Table"),
        Theming.MarkdownElementKeys.TableHeader => Get("StyleTableHeader", "Table header"),
        Theming.MarkdownElementKeys.TableCell => Get("StyleTableCell", "Table cell"),
        _ => elementKey,
    };

    public static string? StyleNameKey(string elementKey) => elementKey switch
    {
        Theming.MarkdownElementKeys.Body => MarkdownStringKeys.StyleBody,
        Theming.MarkdownElementKeys.Heading1 => MarkdownStringKeys.StyleHeading1,
        Theming.MarkdownElementKeys.Heading2 => MarkdownStringKeys.StyleHeading2,
        Theming.MarkdownElementKeys.Heading3 => MarkdownStringKeys.StyleHeading3,
        Theming.MarkdownElementKeys.Heading4 => MarkdownStringKeys.StyleHeading4,
        Theming.MarkdownElementKeys.Heading5 => MarkdownStringKeys.StyleHeading5,
        Theming.MarkdownElementKeys.Heading6 => MarkdownStringKeys.StyleHeading6,
        Theming.MarkdownElementKeys.CodeBlock => MarkdownStringKeys.StyleCodeBlock,
        Theming.MarkdownElementKeys.CodeInline => MarkdownStringKeys.StyleInlineCode,
        Theming.MarkdownElementKeys.Quote => MarkdownStringKeys.StyleQuote,
        Theming.MarkdownElementKeys.Link => MarkdownStringKeys.StyleLink,
        Theming.MarkdownElementKeys.Strong => MarkdownStringKeys.StyleStrong,
        Theming.MarkdownElementKeys.Emphasis => MarkdownStringKeys.StyleEmphasis,
        Theming.MarkdownElementKeys.Strikethrough => MarkdownStringKeys.StyleStrikethrough,
        Theming.MarkdownElementKeys.Subscript => MarkdownStringKeys.StyleSubscript,
        Theming.MarkdownElementKeys.Superscript => MarkdownStringKeys.StyleSuperscript,
        Theming.MarkdownElementKeys.Inserted => MarkdownStringKeys.StyleInserted,
        Theming.MarkdownElementKeys.Marked => MarkdownStringKeys.StyleMarked,
        Theming.MarkdownElementKeys.Abbreviation => MarkdownStringKeys.StyleAbbreviation,
        Theming.MarkdownElementKeys.DefinitionTerm => MarkdownStringKeys.StyleDefinitionTerm,
        Theming.MarkdownElementKeys.DefinitionDescription => MarkdownStringKeys.StyleDefinitionDescription,
        Theming.MarkdownElementKeys.Figure => MarkdownStringKeys.StyleFigure,
        Theming.MarkdownElementKeys.FigureCaption => MarkdownStringKeys.StyleFigureCaption,
        Theming.MarkdownElementKeys.Diagram => MarkdownStringKeys.StyleDiagram,
        Theming.MarkdownElementKeys.ListMarker => MarkdownStringKeys.StyleListMarker,
        Theming.MarkdownElementKeys.Table => MarkdownStringKeys.StyleTable,
        Theming.MarkdownElementKeys.TableHeader => MarkdownStringKeys.StyleTableHeader,
        Theming.MarkdownElementKeys.TableCell => MarkdownStringKeys.StyleTableCell,
        _ => null,
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
