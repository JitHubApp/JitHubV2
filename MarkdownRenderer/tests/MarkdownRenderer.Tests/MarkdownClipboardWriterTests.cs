using MarkdownRenderer.Selection;
using Xunit;

namespace MarkdownRenderer.Tests;

public class MarkdownClipboardWriterTests
{
    [Fact]
    public void BuildHtmlFragment_EncodesRenderedTextWithoutReparsingMarkup()
    {
        string html = MarkdownClipboardWriter.BuildHtmlFragment("<script>alert(1)</script>\nnext");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<br />next", html);
    }

    [Fact]
    public void BuildHtmlFragment_EmptyMarkdown_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownClipboardWriter.BuildHtmlFragment(string.Empty));
    }

    [Fact]
    public void ChoosePlainTextPayload_CanUseSourceMarkdownExplicitly()
    {
        var options = new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
        };

        string text = MarkdownClipboardWriter.ChoosePlainTextPayload("**bold**", "bold", options);

        Assert.Equal("**bold**", text);
    }

    [Fact]
    public void ChoosePlainTextPayload_DefaultsToRenderedText()
    {
        string text = MarkdownClipboardWriter.ChoosePlainTextPayload(
            "**bold**",
            "bold",
            MarkdownCopyOptions.Default);

        Assert.Equal("bold", text);
        Assert.True(MarkdownCopyOptions.Default.IncludeHtml);
    }

    [Fact]
    public void ChoosePlainTextPayload_CanUseRenderedText()
    {
        var options = new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.RenderedText,
        };

        string text = MarkdownClipboardWriter.ChoosePlainTextPayload("**bold**", "bold", options);

        Assert.Equal("bold", text);
    }

    [Fact]
    public void ChoosePlainTextPayload_RenderedModeFallsBackToSource()
    {
        var options = new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.RenderedText,
        };

        string text = MarkdownClipboardWriter.ChoosePlainTextPayload("**bold**", null, options);

        Assert.Equal("**bold**", text);
    }

    // A selection inside a GitHub alert title ("Note", "Warning", "Important")
    // has rendered text but no source text; Ctrl+C must still copy it.
    [Fact]
    public void ResolvePlainTextPayload_RenderedTextWithoutSourceIsCopied()
    {
        Assert.Equal("Note", MarkdownClipboardWriter.ResolvePlainTextPayload(
            string.Empty, "Note", MarkdownCopyOptions.Default));
        Assert.Equal("Note", MarkdownClipboardWriter.ResolvePlainTextPayload(
            null, "Note", MarkdownCopyOptions.Default));
    }

    [Fact]
    public void ResolvePlainTextPayload_NothingToCopyReturnsNull()
    {
        Assert.Null(MarkdownClipboardWriter.ResolvePlainTextPayload(string.Empty, string.Empty, MarkdownCopyOptions.Default));
        Assert.Null(MarkdownClipboardWriter.ResolvePlainTextPayload(null, null, MarkdownCopyOptions.Default));
        // Source mode copies source only; a source-less title has none.
        var source = new MarkdownCopyOptions { PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown };
        Assert.Null(MarkdownClipboardWriter.ResolvePlainTextPayload(string.Empty, "Note", source));
        Assert.Equal("**bold**", MarkdownClipboardWriter.ResolvePlainTextPayload("**bold**", "bold", source));
    }

    [Fact]
    public void ResolvePlainTextPayload_RenderedModeKeepsExistingChoices()
    {
        Assert.Equal("bold", MarkdownClipboardWriter.ResolvePlainTextPayload("**bold**", "bold", MarkdownCopyOptions.Default));
        Assert.Equal("**bold**", MarkdownClipboardWriter.ResolvePlainTextPayload("**bold**", null, MarkdownCopyOptions.Default));
    }
}
