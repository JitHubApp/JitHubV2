using System;
using System.IO;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MarkdownRenderer.Gfm.Internal.Compatibility;

/// <summary>
/// Serializes a syntax tree the way the cmark-gfm 0.29 reference does, for the
/// specification conformance oracle only, including the GFM tag filter.
/// </summary>
internal static class Gfm029ReferenceHtml
{
    private static readonly string[] DisallowedRawHtmlTags =
    [
        "title",
        "textarea",
        "style",
        "xmp",
        "iframe",
        "noembed",
        "noframes",
        "script",
        "plaintext",
    ];

    internal static string Render(
        Markdig.Syntax.MarkdownDocument document,
        MarkdownPipeline pipeline,
        bool applyTagFilter)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<ListRenderer>(new GfmReferenceListRenderer());
        renderer.ObjectRenderers.Replace<HtmlTableRenderer>(new GfmReferenceTableRenderer());
        renderer.Render(document);
        string html = writer.ToString();
        return applyTagFilter ? FilterDisallowedRawHtml(html) : html;
    }

    private static string FilterDisallowedRawHtml(string html)
    {
        int firstTag = html.IndexOf('<');
        if (firstTag < 0)
            return html;

        var output = new System.Text.StringBuilder(html.Length + 16);
        int copiedThrough = 0;
        for (int index = firstTag; index < html.Length; index++)
        {
            if (html[index] != '<' || !StartsDisallowedTag(html, index + 1))
                continue;

            output.Append(html, copiedThrough, index - copiedThrough);
            output.Append("&lt;");
            copiedThrough = index + 1;
        }

        if (copiedThrough == 0)
            return html;
        output.Append(html, copiedThrough, html.Length - copiedThrough);
        return output.ToString();
    }

    private static bool StartsDisallowedTag(string html, int offset)
    {
        if (offset < html.Length && html[offset] == '/')
            offset++;

        foreach (string tag in DisallowedRawHtmlTags)
        {
            if (offset <= html.Length - tag.Length &&
                html.AsSpan(offset, tag.Length).Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                int end = offset + tag.Length;
                return end == html.Length ||
                    char.IsWhiteSpace(html[end]) ||
                    html[end] is '>' or '/';
            }
        }

        return false;
    }
}
