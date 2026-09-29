using System;
using System.Globalization;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Hosting;

namespace JitHub.Services.Markdown;

/// <summary>
/// Bridges MarkdownRenderer's stable localization keys to JitHub's MRT
/// resources. The provider is stateless; the underlying lookup serializes
/// access to culture-qualified ResourceContext instances.
/// </summary>
internal sealed class JitHubMarkdownStringProvider : IMarkdownStringProvider
{
    public static JitHubMarkdownStringProvider Instance { get; } = new();

    private JitHubMarkdownStringProvider()
    {
    }

    public string? GetString(string key, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(culture);

        string? fallback = GetFallback(key);
        return fallback is null
            ? null
            : LocalizedResourceText.GetString(
                "MarkdownRenderer." + key,
                fallback,
                culture);
    }

    private static string? GetFallback(string key) => key switch
    {
        MarkdownStringKeys.DocumentName => "Markdown document",
        MarkdownStringKeys.Copy => "Copy",
        MarkdownStringKeys.CopyMarkdown => "Copy as Markdown",
        MarkdownStringKeys.CopyLink => "Copy link",
        MarkdownStringKeys.CopyImage => "Copy image",
        MarkdownStringKeys.CopyTable => "Copy table",
        MarkdownStringKeys.SelectAll => "Select All",
        MarkdownStringKeys.SelectionStartHandle => "Selection start handle",
        MarkdownStringKeys.SelectionEndHandle => "Selection end handle",
        MarkdownStringKeys.SelectionHandleHelp =>
            "Drag to adjust the selection. Use the arrow keys for precise adjustment.",
        MarkdownStringKeys.CopyCode => "Copy code",
        MarkdownStringKeys.CodeCopied => "Copied",
        MarkdownStringKeys.ImageLoading => "Loading {0}...",
        MarkdownStringKeys.ImageError => "Image unavailable: {0}",
        MarkdownStringKeys.TaskCompleted => "Completed task",
        MarkdownStringKeys.TaskIncomplete => "Incomplete task",
        MarkdownStringKeys.TaskReadOnly => "Read-only task state",
        MarkdownStringKeys.TaskToggle => "Toggle task state",
        MarkdownStringKeys.ImageName => "Image",
        MarkdownStringKeys.TableName => "Table",
        MarkdownStringKeys.ListName => "List",
        MarkdownStringKeys.EmbeddedContentName => "Embedded content",
        MarkdownStringKeys.DiagramName => "Diagram",
        MarkdownStringKeys.CodeLanguageHelp => "Language: {0}",
        MarkdownStringKeys.HtmlDetails => "Details",
        MarkdownStringKeys.HtmlBudgetExceeded =>
            "Additional HTML content was omitted because it exceeded the renderer safety limit.",
        MarkdownStringKeys.StyleBody => "Body",
        MarkdownStringKeys.StyleHeading1 => "Heading 1",
        MarkdownStringKeys.StyleHeading2 => "Heading 2",
        MarkdownStringKeys.StyleHeading3 => "Heading 3",
        MarkdownStringKeys.StyleHeading4 => "Heading 4",
        MarkdownStringKeys.StyleHeading5 => "Heading 5",
        MarkdownStringKeys.StyleHeading6 => "Heading 6",
        MarkdownStringKeys.StyleCodeBlock => "Code block",
        MarkdownStringKeys.StyleInlineCode => "Inline code",
        MarkdownStringKeys.StyleQuote => "Quote",
        MarkdownStringKeys.StyleLink => "Link",
        MarkdownStringKeys.StyleStrong => "Strong",
        MarkdownStringKeys.StyleEmphasis => "Emphasis",
        MarkdownStringKeys.StyleStrikethrough => "Strikethrough",
        MarkdownStringKeys.StyleSubscript => "Subscript",
        MarkdownStringKeys.StyleSuperscript => "Superscript",
        MarkdownStringKeys.StyleInserted => "Inserted",
        MarkdownStringKeys.StyleMarked => "Marked",
        MarkdownStringKeys.StyleAbbreviation => "Abbreviation",
        MarkdownStringKeys.StyleDefinitionTerm => "Definition term",
        MarkdownStringKeys.StyleDefinitionDescription => "Definition",
        MarkdownStringKeys.StyleFigure => "Figure",
        MarkdownStringKeys.StyleFigureCaption => "Figure caption",
        MarkdownStringKeys.StyleDiagram => "Diagram",
        MarkdownStringKeys.StyleListMarker => "List marker",
        MarkdownStringKeys.StyleTable => "Table",
        MarkdownStringKeys.StyleTableHeader => "Table header",
        MarkdownStringKeys.StyleTableCell => "Table cell",
        _ => null,
    };
}
