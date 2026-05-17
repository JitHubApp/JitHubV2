using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Fluent helpers for enabling TextMate grammar based code-block highlighting.
/// </summary>
public static class TextMateSyntaxHighlightingExtensions
{
    /// <summary>Configures a builder to use the default TextMate code-block highlighter.</summary>
    public static MarkdownRendererControlBuilder UseTextMateSyntaxHighlighting(
        this MarkdownRendererControlBuilder builder,
        TextMateCodeBlockSyntaxHighlighter? highlighter = null)
    {
        if (builder is null) throw new System.ArgumentNullException(nameof(builder));
        return builder
            .WithCodeBlockSyntaxHighlightingEnabled(true)
            .WithCodeBlockSyntaxHighlighter(highlighter ?? new TextMateCodeBlockSyntaxHighlighter());
    }

    /// <summary>Configures a control to use the default TextMate code-block highlighter.</summary>
    public static MarkdownRendererControl UseTextMateSyntaxHighlighting(
        this MarkdownRendererControl control,
        TextMateCodeBlockSyntaxHighlighter? highlighter = null)
    {
        if (control is null) throw new System.ArgumentNullException(nameof(control));
        control.IsCodeBlockSyntaxHighlightingEnabled = true;
        control.CodeBlockSyntaxHighlighter = highlighter ?? new TextMateCodeBlockSyntaxHighlighter();
        return control;
    }
}
