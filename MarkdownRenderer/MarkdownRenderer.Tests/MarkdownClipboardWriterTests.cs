using MarkdownRenderer.Selection;
using Xunit;

namespace MarkdownRenderer.Tests;

public class MarkdownClipboardWriterTests
{
    [Fact]
    public void BuildHtmlFragment_RendersMarkdownFormatting()
    {
        string html = MarkdownClipboardWriter.BuildHtmlFragment("**bold** and [link](https://example.com)");

        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void BuildHtmlFragment_EmptyMarkdown_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownClipboardWriter.BuildHtmlFragment(string.Empty));
    }

    [Fact]
    public void ChoosePlainTextPayload_DefaultsToSourceMarkdown()
    {
        var options = new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
        };

        string text = MarkdownClipboardWriter.ChoosePlainTextPayload("**bold**", "bold", options);

        Assert.Equal("**bold**", text);
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
