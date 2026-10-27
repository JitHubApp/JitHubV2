namespace MarkdownRenderer.Extensions;

/// <summary>
/// Stable parser-independent syntax-kind identifiers accepted by
/// <see cref="MarkdownExtensionBuilder.RegisterBlock"/> and
/// <see cref="MarkdownExtensionBuilder.RegisterInline"/>.
/// </summary>
public static class MarkdownSyntaxKinds
{
    /// <summary>Built-in block syntax kinds.</summary>
    public static class Block
    {
        /// <summary>A CommonMark paragraph block.</summary>
        public const string Paragraph = "commonmark.block.paragraph";
        /// <summary>A CommonMark ATX or setext heading block.</summary>
        public const string Heading = "commonmark.block.heading";
        /// <summary>A CommonMark fenced code block.</summary>
        public const string FencedCode = "commonmark.block.fenced-code";
        /// <summary>A CommonMark indented code block.</summary>
        public const string IndentedCode = "commonmark.block.indented-code";
        /// <summary>A CommonMark block quote.</summary>
        public const string Quote = "commonmark.block.quote";
        /// <summary>A CommonMark ordered or unordered list.</summary>
        public const string List = "commonmark.block.list";
        /// <summary>A CommonMark list item.</summary>
        public const string ListItem = "commonmark.block.list-item";
        /// <summary>A CommonMark thematic break.</summary>
        public const string ThematicBreak = "commonmark.block.thematic-break";
        /// <summary>A CommonMark raw HTML block.</summary>
        public const string Html = "commonmark.block.html";
        /// <summary>A dollar-delimited display mathematical expression.</summary>
        public const string Math = "markdown.block.math";
    }

    /// <summary>Built-in inline syntax kinds.</summary>
    public static class Inline
    {
        /// <summary>Literal text.</summary>
        public const string Text = "commonmark.inline.text";
        /// <summary>An inline code span.</summary>
        public const string Code = "commonmark.inline.code";
        /// <summary>An emphasis or strong-emphasis span.</summary>
        public const string Emphasis = "commonmark.inline.emphasis";
        /// <summary>A link.</summary>
        public const string Link = "commonmark.inline.link";
        /// <summary>An image.</summary>
        public const string Image = "commonmark.inline.image";
        /// <summary>A soft or hard line break.</summary>
        public const string LineBreak = "commonmark.inline.line-break";
        /// <summary>An automatically detected link.</summary>
        public const string AutoLink = "commonmark.inline.autolink";
        /// <summary>Raw inline HTML.</summary>
        public const string Html = "commonmark.inline.html";
        /// <summary>An HTML character entity.</summary>
        public const string Entity = "commonmark.inline.entity";
        /// <summary>A Markdown Extra abbreviation.</summary>
        public const string Abbreviation = "markdown-extra.inline.abbreviation";
        /// <summary>A dollar-delimited inline mathematical expression.</summary>
        public const string Math = "markdown.inline.math";
    }
}
