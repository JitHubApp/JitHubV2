using System;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Fluent helpers for enabling TextMate grammar based code-block highlighting.
/// The caller owns the highlighter and must keep it alive, then dispose it,
/// after every control that uses it.
/// </summary>
public static class TextMateSyntaxHighlightingExtensions
{
    /// <summary>Configures a builder to use the TextMate highlighter.</summary>
    public static MarkdownRendererControlBuilder UseTextMateSyntaxHighlighting(
        this MarkdownRendererControlBuilder builder,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(highlighter);
        return builder
            .WithCodeBlockSyntaxHighlightingEnabled(true)
            .WithCodeHighlighter(highlighter);
    }

    /// <summary>Configures a viewport-owning view to use the TextMate highlighter.</summary>
    public static MarkdownScrollView UseTextMateSyntaxHighlighting(
        this MarkdownScrollView view,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(highlighter);
        view.IsCodeBlockSyntaxHighlightingEnabled = true;
        view.CodeHighlighter = highlighter;
        return view;
    }

    /// <summary>Configures an ancestor-viewport view to use the TextMate highlighter.</summary>
    public static MarkdownDocumentView UseTextMateSyntaxHighlighting(
        this MarkdownDocumentView view,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(highlighter);
        view.IsCodeBlockSyntaxHighlightingEnabled = true;
        view.CodeHighlighter = highlighter;
        return view;
    }
}
