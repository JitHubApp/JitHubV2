using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Identifies a markdown element category for theme lookups. Custom extensions
/// may use any string key.
/// </summary>
public static class MarkdownElementKeys
{
    /// <summary>Body paragraph text.</summary>
    public const string Body = "Body";
    /// <summary>Level-one heading text.</summary>
    public const string Heading1 = "Heading1";
    /// <summary>Level-two heading text.</summary>
    public const string Heading2 = "Heading2";
    /// <summary>Level-three heading text.</summary>
    public const string Heading3 = "Heading3";
    /// <summary>Level-four heading text.</summary>
    public const string Heading4 = "Heading4";
    /// <summary>Level-five heading text.</summary>
    public const string Heading5 = "Heading5";
    /// <summary>Level-six heading text.</summary>
    public const string Heading6 = "Heading6";
    /// <summary>Inline code text.</summary>
    public const string CodeInline = "CodeInline";
    /// <summary>Fenced or indented code block text.</summary>
    public const string CodeBlock = "CodeBlock";
    /// <summary>Fenced or indented code block header surface.</summary>
    public const string CodeBlockHeader = "CodeBlockHeader";
    /// <summary>Fenced or indented code block language label.</summary>
    public const string CodeBlockLanguage = "CodeBlockLanguage";
    /// <summary>Fenced or indented code block gutter surface.</summary>
    public const string CodeBlockGutter = "CodeBlockGutter";
    /// <summary>Fenced or indented code block line-number text.</summary>
    public const string CodeBlockLineNumber = "CodeBlockLineNumber";
    /// <summary>Block quote container.</summary>
    public const string Quote = "Quote";
    /// <summary>Inline link text.</summary>
    public const string Link = "Link";
    /// <summary>Strong emphasis text.</summary>
    public const string Strong = "Strong";
    /// <summary>Emphasis text.</summary>
    public const string Emphasis = "Emphasis";
    /// <summary>Strikethrough text.</summary>
    public const string Strikethrough = "Strikethrough";
    /// <summary>Subscript text from Markdig emphasis extras.</summary>
    public const string Subscript = "Subscript";
    /// <summary>Superscript text from Markdig emphasis extras.</summary>
    public const string Superscript = "Superscript";
    /// <summary>Inserted text from Markdig emphasis extras.</summary>
    public const string Inserted = "Inserted";
    /// <summary>Marked/highlighted text from Markdig emphasis extras.</summary>
    public const string Marked = "Marked";
    /// <summary>Abbreviation text from Markdown Extra.</summary>
    public const string Abbreviation = "Abbreviation";
    /// <summary>Definition-list term.</summary>
    public const string DefinitionTerm = "DefinitionTerm";
    /// <summary>Definition-list description.</summary>
    public const string DefinitionDescription = "DefinitionDescription";
    /// <summary>Figure container.</summary>
    public const string Figure = "Figure";
    /// <summary>Figure caption.</summary>
    public const string FigureCaption = "FigureCaption";
    /// <summary>Sample or extension-provided diagram block.</summary>
    public const string Diagram = "Diagram";
    /// <summary>Inline or display mathematical expression.</summary>
    public const string Math = "Math";
    /// <summary>List bullet or ordinal marker.</summary>
    public const string ListMarker = "ListMarker";
    /// <summary>Unordered or ordered list container.</summary>
    public const string List = "List";
    /// <summary>Thematic break separator.</summary>
    public const string ThematicBreak = "ThematicBreak";
    /// <summary>Image caption text.</summary>
    public const string ImageCaption = "ImageCaption";

    // GFM extension keys
    /// <summary>GitHub-flavored markdown table container.</summary>
    public const string Table = "Table";
    /// <summary>GitHub-flavored markdown table header cell.</summary>
    public const string TableHeader = "TableHeader";
    /// <summary>GitHub-flavored markdown table body cell.</summary>
    public const string TableCell = "TableCell";
    /// <summary>GitHub alert note block.</summary>
    public const string AlertNote = "AlertNote";
    /// <summary>GitHub alert tip block.</summary>
    public const string AlertTip = "AlertTip";
    /// <summary>GitHub alert important block.</summary>
    public const string AlertImportant = "AlertImportant";
    /// <summary>GitHub alert warning block.</summary>
    public const string AlertWarning = "AlertWarning";
    /// <summary>GitHub alert caution block.</summary>
    public const string AlertCaution = "AlertCaution";

    /// <summary>
    /// Returns a context-aware override key such as <c>Quote &gt; Link</c>.
    /// </summary>
    public static string Context(string ancestorKey, string elementKey)
        => string.IsNullOrWhiteSpace(ancestorKey)
            ? elementKey
            : $"{ancestorKey} > {elementKey}";

    /// <summary>
    /// Returns the override key for a generic markdown/HTML class attribute.
    /// </summary>
    public static string Class(string className)
        => "." + NormalizeAlias(className);

    /// <summary>
    /// Returns the override key for a generic markdown/HTML id attribute.
    /// </summary>
    public static string Id(string id)
        => "#" + NormalizeAlias(id);

    /// <summary>
    /// Returns the override key for a one-based list nesting depth.
    /// </summary>
    public static string ListDepth(int depth)
        => $"ListDepth{System.Math.Max(1, depth)}";

    private static string NormalizeAlias(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return string.Empty;
        return value[0] is '.' or '#'
            ? value.Substring(1)
            : value;
    }
}
