using System.Globalization;
using System.Resources;

namespace MarkdownRenderer.Accessibility;

internal static class MarkdownLocalizedStrings
{
    private static readonly ResourceManager Resources = new(
        "MarkdownRenderer.Properties.Resources",
        typeof(MarkdownLocalizedStrings).Assembly);

    public static string MarkdownDocumentName => Get("MarkdownDocumentName", "Markdown document");
    public static string ImageName => Get("ImageName", "Image");
    public static string TableName => Get("TableName", "Table");
    public static string ListName => Get("ListName", "List");
    public static string EmbeddedContentName => Get("EmbeddedContentName", "Embedded content");
    public static string ContextMenuCopy => Get("ContextMenuCopy", "Copy");
    public static string ContextMenuSelectAll => Get("ContextMenuSelectAll", "Select All");

    public static string CodeLanguageHelp(string language) =>
        string.Format(CultureInfo.CurrentUICulture, Get("CodeLanguageHelpFormat", "Language: {0}"), language);

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
        Theming.MarkdownElementKeys.ListMarker => Get("StyleListMarker", "List marker"),
        Theming.MarkdownElementKeys.TableHeader => Get("StyleTableHeader", "Table header"),
        Theming.MarkdownElementKeys.TableCell => Get("StyleTableCell", "Table cell"),
        _ => elementKey,
    };

    private static string Get(string key, string fallback)
    {
        try
        {
            return Resources.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
