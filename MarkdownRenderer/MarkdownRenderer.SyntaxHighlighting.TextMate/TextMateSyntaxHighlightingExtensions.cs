using System;
using System.Diagnostics.CodeAnalysis;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Fluent helpers for enabling TextMate grammar based code-block highlighting.
/// </summary>
public static class TextMateSyntaxHighlightingExtensions
{
    /// <summary>
    /// Configures a builder to create and own one discovered highlighter per
    /// built control. Disposing each control deterministically disposes its
    /// highlighter and discovered provider.
    /// </summary>
    [Obsolete("Construct and dispose a TextMateCodeBlockSyntaxHighlighter explicitly. This compatibility overload transfers ownership to each built control.")]
    [RequiresUnreferencedCode(
        "Default TextMate grammar discovery loads optional provider types by name. " +
        "Trimmed and NativeAOT applications must construct and pass an explicit provider.")]
    public static MarkdownRendererControlBuilder UseTextMateSyntaxHighlighting(
        this MarkdownRendererControlBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
#pragma warning disable CS0618 // Source-compatible ownership-managed convenience path.
        return builder
            .WithCodeBlockSyntaxHighlightingEnabled(true)
            .WithOwnedCodeHighlighterFactory(static () => new TextMateCodeBlockSyntaxHighlighter());
#pragma warning restore CS0618
    }

    /// <summary>
    /// Configures a builder to create one borrowing adapter per built control.
    /// The caller retains ownership of <paramref name="provider"/> and must keep
    /// it alive until every built control is disposed.
    /// </summary>
    [Obsolete("Construct a TextMateCodeBlockSyntaxHighlighter explicitly to make provider ownership visible.")]
    public static MarkdownRendererControlBuilder UseTextMateSyntaxHighlighting(
        this MarkdownRendererControlBuilder builder,
        ITextMateGrammarProvider provider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(provider);
        return builder
            .WithCodeBlockSyntaxHighlightingEnabled(true)
            .WithOwnedCodeHighlighterFactory(
                () => new TextMateCodeBlockSyntaxHighlighter(provider));
    }

    /// <summary>Configures a builder to use an explicitly owned or borrowed TextMate highlighter.</summary>
    public static MarkdownRendererControlBuilder UseTextMateSyntaxHighlighting(
        this MarkdownRendererControlBuilder builder,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        if (builder is null) throw new System.ArgumentNullException(nameof(builder));
        if (highlighter is null) throw new System.ArgumentNullException(nameof(highlighter));
        return builder
            .WithCodeBlockSyntaxHighlightingEnabled(true)
            .WithCodeHighlighter(highlighter);
    }

    /// <summary>Configures a viewport-owning view with an explicit TextMate highlighter.</summary>
    public static MarkdownScrollView UseTextMateSyntaxHighlighting(
        this MarkdownScrollView view,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        if (view is null) throw new System.ArgumentNullException(nameof(view));
        if (highlighter is null) throw new System.ArgumentNullException(nameof(highlighter));
        ConfigureView(view, highlighter);
        return view;
    }

    /// <summary>
    /// Configures a viewport-owning view with an owned discovered highlighter.
    /// Disposing the view disposes both the highlighter and discovered provider.
    /// </summary>
    [Obsolete("Construct and dispose a TextMateCodeBlockSyntaxHighlighter explicitly. This compatibility overload transfers ownership to the view.")]
    [RequiresUnreferencedCode(
        "Default TextMate grammar discovery loads optional provider types by name. " +
        "Trimmed and NativeAOT applications must construct and pass an explicit provider.")]
    public static MarkdownScrollView UseTextMateSyntaxHighlighting(this MarkdownScrollView view)
    {
        ArgumentNullException.ThrowIfNull(view);
#pragma warning disable CS0618 // Source-compatible ownership-managed convenience path.
        ConfigureOwnedView(view, new TextMateCodeBlockSyntaxHighlighter());
#pragma warning restore CS0618
        return view;
    }

    /// <summary>
    /// Configures a viewport-owning view with an owned borrowing adapter. The
    /// caller retains ownership of <paramref name="provider"/>.
    /// </summary>
    [Obsolete("Construct a TextMateCodeBlockSyntaxHighlighter explicitly to make provider ownership visible.")]
    public static MarkdownScrollView UseTextMateSyntaxHighlighting(
        this MarkdownScrollView view,
        ITextMateGrammarProvider provider)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(provider);
        ConfigureOwnedView(view, new TextMateCodeBlockSyntaxHighlighter(provider));
        return view;
    }

    /// <summary>Configures an ancestor-viewport view with an explicit TextMate highlighter.</summary>
    public static MarkdownDocumentView UseTextMateSyntaxHighlighting(
        this MarkdownDocumentView view,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        if (view is null) throw new System.ArgumentNullException(nameof(view));
        if (highlighter is null) throw new System.ArgumentNullException(nameof(highlighter));
        ConfigureView(view, highlighter);
        return view;
    }

    /// <summary>
    /// Configures an ancestor-viewport view with an owned discovered highlighter.
    /// Disposing the view disposes both the highlighter and discovered provider.
    /// </summary>
    [Obsolete("Construct and dispose a TextMateCodeBlockSyntaxHighlighter explicitly. This compatibility overload transfers ownership to the view.")]
    [RequiresUnreferencedCode(
        "Default TextMate grammar discovery loads optional provider types by name. " +
        "Trimmed and NativeAOT applications must construct and pass an explicit provider.")]
    public static MarkdownDocumentView UseTextMateSyntaxHighlighting(this MarkdownDocumentView view)
    {
        ArgumentNullException.ThrowIfNull(view);
#pragma warning disable CS0618 // Source-compatible ownership-managed convenience path.
        ConfigureOwnedView(view, new TextMateCodeBlockSyntaxHighlighter());
#pragma warning restore CS0618
        return view;
    }

    /// <summary>
    /// Configures an ancestor-viewport view with an owned borrowing adapter.
    /// The caller retains ownership of <paramref name="provider"/>.
    /// </summary>
    [Obsolete("Construct a TextMateCodeBlockSyntaxHighlighter explicitly to make provider ownership visible.")]
    public static MarkdownDocumentView UseTextMateSyntaxHighlighting(
        this MarkdownDocumentView view,
        ITextMateGrammarProvider provider)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(provider);
        ConfigureOwnedView(view, new TextMateCodeBlockSyntaxHighlighter(provider));
        return view;
    }

    private static void ConfigureView(object view, TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        switch (view)
        {
            case MarkdownScrollView scrollView:
                scrollView.IsCodeBlockSyntaxHighlightingEnabled = true;
                scrollView.CodeHighlighter = highlighter;
                break;
            case MarkdownDocumentView documentView:
                documentView.IsCodeBlockSyntaxHighlightingEnabled = true;
                documentView.CodeHighlighter = highlighter;
                break;
            default:
                throw new System.ArgumentException("The value must be a markdown view.", nameof(view));
        }
    }

    private static void ConfigureOwnedView(
        object view,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        bool ownershipTransferred = false;
        try
        {
            switch (view)
            {
                case MarkdownScrollView scrollView:
                    scrollView.IsCodeBlockSyntaxHighlightingEnabled = true;
                    scrollView.SetOwnedCodeHighlighter(highlighter, highlighter);
                    ownershipTransferred = true;
                    break;
                case MarkdownDocumentView documentView:
                    documentView.IsCodeBlockSyntaxHighlightingEnabled = true;
                    documentView.SetOwnedCodeHighlighter(highlighter, highlighter);
                    ownershipTransferred = true;
                    break;
                default:
                    throw new ArgumentException("The value must be a markdown view.", nameof(view));
            }
        }
        finally
        {
            if (!ownershipTransferred)
                highlighter.Dispose();
        }
    }
}
