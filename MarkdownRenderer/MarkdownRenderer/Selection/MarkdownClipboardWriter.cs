using System;
using Windows.ApplicationModel.DataTransfer;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Builds a markdown-source slice from a selection range and places it on the
/// system clipboard.
/// </summary>
internal static class MarkdownClipboardWriter
{
    public static bool Copy(
        MarkdownSourceMap sourceMap,
        DocumentRange range,
        MarkdownCopyOptions? options = null,
        string? renderedText = null,
        string? renderedHtml = null)
    {
        options ??= MarkdownCopyOptions.Default;
        var sourceSlice = sourceMap.Slice(range);
        if (string.IsNullOrEmpty(sourceSlice)) return false;

        string plainText = ChoosePlainTextPayload(sourceSlice, renderedText, options);

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(plainText);
        if (options.IncludeHtml)
        {
            // HTML is derived from the renderer's committed semantic display
            // plan. Never reparse source with an unrelated Markdown profile:
            // doing so can turn strict-profile literal HTML into executable
            // clipboard markup and bypass host policies.
            var html = string.IsNullOrEmpty(renderedHtml)
                ? BuildHtmlFragment(plainText)
                : renderedHtml;
            if (!string.IsNullOrWhiteSpace(html))
                package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        }

        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
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

    internal static string BuildHtmlFragment(string renderedText)
    {
        if (string.IsNullOrEmpty(renderedText))
            return string.Empty;

        return System.Net.WebUtility.HtmlEncode(renderedText)
            .Replace("\r\n", "<br />", StringComparison.Ordinal)
            .Replace("\r", "<br />", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);
    }
}
