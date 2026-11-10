using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Creates text, link and literal-markup runs for safe-HTML inline content.
/// </summary>
internal static class HtmlTextRuns
{
    internal static void AddTextRun(
        InlineContainerBox box,
        SafeHtmlText text,
        int sourceOffset,
        HtmlInlineContext context) =>
        AddDecodedTextRun(
            box,
            text.DecodedText,
            context.MapSourceRange(sourceOffset + text.SourceStart, text.SourceLength),
            context);

    internal static void AddDecodedTextRun(
        InlineContainerBox box,
        string decoded,
        SourceSpan span,
        HtmlInlineContext context)
    {
        string value = SafeHtmlParser.CollapseDecodedWhitespace(decoded);
        bool leadingWhitespace = decoded.Length > 0 && IsCollapsibleWhitespace(decoded[0]);
        bool trailingWhitespace = decoded.Length > 0 && IsCollapsibleWhitespace(decoded[^1]);
        bool atLineStart = box.Runs.Count == 0 || box.Runs[^1] is LineBreakRun;
        bool previousEndsWithWhitespace = !atLineStart &&
            box.Runs[^1].Text.Length > 0 &&
            char.IsWhiteSpace(box.Runs[^1].Text[^1]);

        // HTML collapses whitespace across element boundaries, not separately
        // inside each text node. Retain one boundary space around styled/link
        // elements while still discarding indentation at the start of a block.
        if (leadingWhitespace && !atLineStart && !previousEndsWithWhitespace)
            value = " " + value;
        if (trailingWhitespace && value.Length > 0 && !char.IsWhiteSpace(value[^1]))
            value += " ";

        if (value.Length == 0)
        {
            return;
        }

        // Indentation around an image inside an anchor is layout whitespace,
        // not a second hyperlink. Keeping it as a LinkRun creates a blank UIA
        // link beside the linked-image peer and inflates link navigation.
        if (context.LinkUrl is { Length: > 0 } && string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        InlineRun run = context.LinkUrl is { Length: > 0 } href
            ? new LinkRun(value, href, context.LinkTitle)
            {
                IsSuperscript = context.StyleKey == MarkdownElementKeys.Superscript,
            }
            : CreateStyledTextRun(value, context.StyleKey);
        run.SourceSpan = span;
        run.SemanticHeadingKey = context.HeadingKey ?? string.Empty;
        if (run is LinkRun && !string.IsNullOrEmpty(context.StyleKey))
            run.StyleModifierKeys = new[] { context.StyleKey };
        run.SetStyleAliases(context.StyleAliases);
        HtmlBoxes.AddRun(box, run);
    }

    private static bool IsCollapsibleWhitespace(char value) =>
        value != '\u00A0' && char.IsWhiteSpace(value);

    private static InlineRun CreateStyledTextRun(string text, string? styleKey) => styleKey switch
    {
        MarkdownElementKeys.CodeInline => new CodeInlineRun(text),
        MarkdownElementKeys.Strong => new StrongRun(text),
        MarkdownElementKeys.Emphasis => new EmphasisRun(text),
        MarkdownElementKeys.Strikethrough => new StrikethroughRun(text),
        MarkdownElementKeys.Subscript => new SubscriptRun(text),
        MarkdownElementKeys.Superscript => new SuperscriptRun(text),
        MarkdownElementKeys.Inserted => new InsertedRun(text),
        MarkdownElementKeys.Marked => new MarkedRun(text),
        _ => new TextRun(text),
    };

    internal static void AddLiteralElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        int sourceOffset,
        HtmlInlineContext context)
    {
        AddLiteralText(
            box,
            element.RawOpeningTag,
            sourceOffset + element.SourceStart,
            context);

        if (element.RawTrailingMarkup.Length > 0)
        {
            AddLiteralText(
                box,
                element.RawTrailingMarkup,
                sourceOffset + element.RawTrailingStart,
                context);
        }
        else
        {
            foreach (SafeHtmlNode child in element.Children)
            {
                if (child is SafeHtmlText text)
                {
                    AddLiteralText(
                        box,
                        text.RawText,
                        sourceOffset + text.SourceStart,
                        context);
                }
                else if (child is SafeHtmlElement childElement)
                {
                    AddLiteralElement(box, childElement, sourceOffset, context);
                }
            }

            AddLiteralText(
                box,
                element.RawClosingTag,
                sourceOffset + element.RawClosingStart,
                context);
        }
    }

    private static void AddLiteralText(
        InlineContainerBox box,
        string text,
        int sourceStart,
        HtmlInlineContext context)
    {
        if (text.Length == 0)
            return;

        InlineRun run = context.LinkUrl is { Length: > 0 } href
            ? new LinkRun(text, href, context.LinkTitle)
            : new TextRun(text);
        run.SourceSpan = context.MapSourceRange(sourceStart, text.Length);
        run.SetStyleAliases(context.StyleAliases);
        HtmlBoxes.AddRun(box, run);
    }
}
