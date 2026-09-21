using System;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Lowers inert HTML media into a bounded atomic image. Markdown documents do
/// not execute HTML media, but they retain its authored footprint and an
/// accessible atomic representation without exposing signed markup as text.
/// </summary>
internal static class SafeHtmlMediaRunFactory
{
    private const string VideoGlyph =
        "<circle cx='320' cy='180' r='46' fill='currentColor' fill-opacity='.18'/><path d='M306 151l47 29-47 29z' fill='currentColor'/>";
    private const string AudioGlyph =
        "<circle cx='28' cy='24' r='16' fill='currentColor' fill-opacity='.18'/><path d='M23 15l14 9-14 9z' fill='currentColor'/><path d='M56 24h240' stroke='currentColor' stroke-opacity='.35' stroke-width='2'/>";
    private static readonly string VideoPlaceholder = CreatePlaceholder(
        "Video",
        VideoGlyph,
        640,
        360,
        12,
        identity: null);

    private static readonly string AudioPlaceholder = CreatePlaceholder(
        "Audio",
        AudioGlyph,
        320,
        48,
        8,
        identity: null);

    internal static InlineImageRun Create(
        MarkdownLayoutContext context,
        bool isVideo,
        string? accessibilityName,
        string? mediaSource,
        string? posterSource,
        SafeHtmlLength? width,
        SafeHtmlLength? height,
        SourceSpan sourceSpan,
        string? containingLinkUrl = null,
        string? containingLinkTitle = null)
    {
        string name = string.IsNullOrWhiteSpace(accessibilityName)
            ? context.ResolveString(
                MarkdownStringKeys.EmbeddedContentName,
                MarkdownLocalizedStrings.EmbeddedContentName)
            : accessibilityName.Trim();
        string imageSource = string.IsNullOrWhiteSpace(posterSource)
            ? CreateMediaPlaceholder(isVideo, mediaSource)
            : posterSource;
        return new InlineImageRun(
            context,
            name,
            imageSource,
            title: null,
            containingLinkUrl,
            containingLinkTitle,
            width ?? new SafeHtmlLength(isVideo ? 640 : 320, IsPercent: false),
            height ?? new SafeHtmlLength(isVideo ? 360 : 48, IsPercent: false))
        {
            SourceSpan = sourceSpan,
        };
    }

    private static string CreatePlaceholder(
        string label,
        string glyph,
        int width,
        int height,
        int radius,
        string? identity)
    {
        string metadata = string.IsNullOrEmpty(identity)
            ? string.Empty
            : $"<metadata>media-{identity}</metadata>";
        string svg = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {width} {height}'><title>{label}</title>{metadata}<rect width='{width}' height='{height}' rx='{radius}' fill='currentColor' fill-opacity='.08'/>{glyph}</svg>";
        return "data:image/svg+xml;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    private static string CreateMediaPlaceholder(bool isVideo, string? mediaSource)
    {
        if (string.IsNullOrWhiteSpace(mediaSource))
            return isVideo ? VideoPlaceholder : AudioPlaceholder;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(mediaSource));
        string identity = Convert.ToHexString(digest.AsSpan(0, 8));
        return isVideo
            ? CreatePlaceholder("Video", VideoGlyph, 640, 360, 12, identity)
            : CreatePlaceholder("Audio", AudioGlyph, 320, 48, 8, identity);
    }
}
