namespace MarkdownRenderer.Theming;

internal enum MarkdownHighContrastColorRole
{
    WindowText,
    Window,
    Hotlight,
    Highlight,
    HighlightText,
}

internal readonly record struct MarkdownHighContrastStyleRoles(
    MarkdownHighContrastColorRole Foreground,
    MarkdownHighContrastColorRole? Background = null,
    MarkdownHighContrastColorRole? AccentBar = null,
    bool Underline = false,
    bool Strikethrough = false);

internal static class MarkdownHighContrastDefaults
{
    public static MarkdownHighContrastStyleRoles Resolve(string elementKey) => elementKey switch
    {
        "Link" => new(
            MarkdownHighContrastColorRole.Hotlight,
            Underline: true),

        "CodeBlock" => new(
            MarkdownHighContrastColorRole.WindowText,
            MarkdownHighContrastColorRole.Window,
            MarkdownHighContrastColorRole.WindowText),

        "CodeInline" => new(
            MarkdownHighContrastColorRole.WindowText,
            MarkdownHighContrastColorRole.Window),

        "Quote" => new(
            MarkdownHighContrastColorRole.WindowText,
            AccentBar: MarkdownHighContrastColorRole.WindowText),

        "Strikethrough" => new(
            MarkdownHighContrastColorRole.WindowText,
            Strikethrough: true),

        "Inserted" or "Abbreviation" => new(
            MarkdownHighContrastColorRole.WindowText,
            Underline: true),

        "Marked" or "DefinitionDescription" or "Figure" or "FigureCaption" or "Diagram" => new(
            MarkdownHighContrastColorRole.WindowText,
            MarkdownHighContrastColorRole.Window),

        "TableHeader" => new(
            MarkdownHighContrastColorRole.HighlightText,
            MarkdownHighContrastColorRole.Highlight),

        "Body" or "TableCell" => new(
            MarkdownHighContrastColorRole.WindowText,
            MarkdownHighContrastColorRole.Window),

        "AlertNote" or "AlertTip" or "AlertImportant" or "AlertWarning" or "AlertCaution" => new(
            MarkdownHighContrastColorRole.WindowText,
            AccentBar: MarkdownHighContrastColorRole.Hotlight),

        _ => new(MarkdownHighContrastColorRole.WindowText),
    };
}
