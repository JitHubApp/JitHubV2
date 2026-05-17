using System;
using Markdig;
using Windows.ApplicationModel.DataTransfer;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Builds a markdown-source slice from a selection range and places it on the
/// system clipboard.
/// </summary>
internal static class MarkdownClipboardWriter
{
    private static readonly Lazy<MarkdownPipeline> _clipboardPipeline = new(() =>
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());

    public static bool Copy(
        MarkdownSourceMap sourceMap,
        DocumentRange range,
        MarkdownCopyOptions? options = null,
        string? renderedText = null)
    {
        options ??= MarkdownCopyOptions.Default;
        var sourceSlice = sourceMap.Slice(range);
        if (string.IsNullOrEmpty(sourceSlice)) return false;

        string plainText = ChoosePlainTextPayload(sourceSlice, renderedText, options);

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(plainText);
        if (options.IncludeHtml)
        {
            var html = BuildHtmlFragment(sourceSlice);
            if (!string.IsNullOrWhiteSpace(html))
                package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        }

        try { Clipboard.SetContent(package); }
        catch (Exception) { return false; }
        return true;
    }

    internal static string ChoosePlainTextPayload(
        string sourceMarkdown,
        string? renderedText,
        MarkdownCopyOptions options)
        => options.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText
            ? renderedText ?? sourceMarkdown
            : sourceMarkdown;

    internal static string BuildHtmlFragment(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        try
        {
            return Markdown.ToHtml(markdown, _clipboardPipeline.Value);
        }
        catch
        {
            return System.Net.WebUtility.HtmlEncode(markdown).Replace("\n", "<br />", StringComparison.Ordinal);
        }
    }
}
