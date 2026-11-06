using System;
using System.Collections.Generic;
using System.Text;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// One inline segment inside a paragraph or heading. Inline runs do not lay
/// themselves out individually; the parent block joins them into a single
/// CanvasTextLayout with per-range style spans.
/// </summary>
internal abstract class InlineRun
{
    protected InlineRun()
    {
    }

    // Copies logical/style metadata only; native layout and ownership stay
    // generation-local.
    protected InlineRun(InlineRun source)
    {
        InlineIndex = source.InlineIndex;
        RenderedLength = source.RenderedLength;
        SourceSpan = source.SourceSpan;
        ElementKey = source.ElementKey;
        StyleAliases = CopyStrings(source.StyleAliases);
        StyleModifierKeys = CopyStrings(source.StyleModifierKeys);
        ScriptStyleAncestryKeys = CopyStrings(source.ScriptStyleAncestryKeys);
        SafeHtmlScriptRoleIsRunElement = source.SafeHtmlScriptRoleIsRunElement;
        SemanticHeadingKey = source.SemanticHeadingKey;
    }

    private static string[] CopyStrings(IReadOnlyList<string> source)
    {
        if (source.Count == 0)
            return Array.Empty<string>();

        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    public int InlineIndex { get; internal set; }
    public int RenderedLength { get; protected set; }
    public SourceSpan SourceSpan { get; internal set; }
    /// <summary>
    /// Empty string means "inherit container's style". Set to a specific key
    /// (e.g. <see cref="MarkdownElementKeys.Strong"/>) to apply a delta override.
    /// </summary>
    public string ElementKey { get; init; } = string.Empty;

    /// <summary>
    /// Attribute-derived style aliases (for example <c>.warning</c> or
    /// <c>#intro</c>) captured from the layout context when this run is built.
    /// </summary>
    public IReadOnlyList<string> StyleAliases { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Inline element styles inherited from a formatting container that had to
    /// be split into multiple runs (for example, <c>**before [link](...) after**</c>).
    /// Keeping these modifiers separate preserves the interactive link run while
    /// retaining the surrounding emphasis.
    /// </summary>
    internal IReadOnlyList<string> StyleModifierKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Source-ordered safe-HTML and Markdown script ancestry. This is kept
    /// separately from the style modifiers because HTML and Markdown scopes
    /// can interleave around a single interactive run.
    /// </summary>
    internal IReadOnlyList<string> ScriptStyleAncestryKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True when a typed script run or superscript link is the run emitted by
    /// the innermost safe-HTML script element, rather than a second nested
    /// Markdown/footnote script inside that element.
    /// </summary>
    internal bool SafeHtmlScriptRoleIsRunElement { get; set; }

    /// <summary>
    /// Heading style/semantics inherited from safe HTML that is flattened into
    /// an inline-only host such as a table cell. Ordinary Markdown headings
    /// remain block containers and leave this empty.
    /// </summary>
    internal string SemanticHeadingKey { get; set; } = string.Empty;

    public void SetStyleAliases(IReadOnlyList<string> styleAliases)
        => StyleAliases = styleAliases ?? Array.Empty<string>();

    internal bool HasStyleModifier(string elementKey)
    {
        foreach (string key in StyleModifierKeys)
        {
            if (string.Equals(key, elementKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The text contributed by this run to the inline buffer.</summary>
    public abstract string Text { get; }

    /// <summary>
    /// Logical UTF-16 length without requiring a direct-source literal to
    /// allocate its string representation.
    /// </summary>
    internal int TextLength => this is SourceSliceTextRun sourceSlice
        ? sourceSlice.SliceLength
        : Text.Length;

    /// <summary>Length of the native-layout representation without materializing a direct literal.</summary>
    internal int LayoutTextLength => this is SourceSliceTextRun sourceSlice
        ? sourceSlice.SliceLength
        : LayoutText.Length;

    internal void AppendLayoutTextTo(StringBuilder builder)
    {
        if (this is SourceSliceTextRun sourceSlice)
            sourceSlice.AppendLayoutTextTo(builder);
        else
            builder.Append(LayoutText);
    }

    /// <summary>
    /// The same-length text used only by the native text layout. Most runs use
    /// their semantic text directly; a standalone safe-HTML line break uses a
    /// blank layout character so DirectWrite retains one empty line box rather
    /// than creating an extra trailing line for the terminal newline.
    /// </summary>
    internal virtual string LayoutText => Text;

    /// <summary>
    /// Text exposed through UI Automation. Most runs expose their rendered text;
    /// visual atomic runs such as images can expose richer fallback text.
    /// </summary>
    public virtual string AccessibleText => Text;
}
