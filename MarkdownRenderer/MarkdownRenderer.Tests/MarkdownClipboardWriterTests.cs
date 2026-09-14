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
}
