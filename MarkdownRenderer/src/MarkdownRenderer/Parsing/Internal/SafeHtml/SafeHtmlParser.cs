using System;
using System.Collections.Generic;
using System.Threading;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

/// <summary>
/// Entry points for the bounded safe HTML subset: whole-fragment trees, tag
/// sequences, and the attribute, URL and whitespace policies renderers share.
/// The tokenizer, tree builder and policies live in the SafeHtml* helpers.
/// </summary>
internal static class SafeHtmlParser
{
    internal const int MaxInputLength = 4 * 1024 * 1024;
    // GitHub's rendered form of large but ordinary READMEs can exceed 20,000
    // nodes after syntax highlighting (airbnb/javascript is one example).
    // Input length, tag length, depth, and attribute ceilings remain the
    // primary allocation bounds; this ceiling prevents valid documents from
    // being cut off merely because GitHub emitted many small <span> nodes.
    internal const int MaxNodeCount = 100_000;
    internal const int MaxNestingDepth = 64;
    internal const int MaxAttributeCount = 32;
    internal const int MaxAttributeValueLength = 16 * 1024;
    internal const int MaxTagLength = 64 * 1024;
    internal const float MaximumAbsoluteLength = 8192f;

    public static SafeHtmlDocument Parse(string? html) =>
        Parse(html, SafeHtmlParseLimits.Default, CancellationToken.None);

    internal static SafeHtmlDocument Parse(string? html, SafeHtmlParseLimits limits) =>
        Parse(html, limits, CancellationToken.None);

    internal static SafeHtmlDocument Parse(
        string? html,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken) =>
        SafeHtmlTreeBuilder.Build(html, limits, cancellationToken);

    public static bool TryParseSingleTag(string? text, out SafeHtmlTag tag)
        => TryParseSingleTag(
            text,
            SafeHtmlParseLimits.Default,
            CancellationToken.None,
            out tag,
            out _);

    internal static bool TryParseSingleTag(string? text, SafeHtmlParseLimits limits, out SafeHtmlTag tag)
        => TryParseSingleTag(text, limits, CancellationToken.None, out tag, out _);

    internal static bool TryParseSingleTag(
        string? text,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken,
        out SafeHtmlTag tag,
        out bool scanBudgetExceeded)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scanBudgetExceeded = false;
        string source = text?.Trim() ?? string.Empty;
        if (source.Length > limits.MaxInputLength || source.Length > limits.MaxTagLength)
        {
            tag = default;
            return false;
        }
        var scanBudget = new SafeHtmlScanBudget(source.Length);
        if (!SafeHtmlTagReader.TryReadTag(
                source,
                0,
                source.Length,
                limits,
                cancellationToken,
                ref scanBudget,
                out tag,
                out int endExclusive))
        {
            scanBudgetExceeded = scanBudget.IsExhausted;
            return false;
        }

        for (int index = endExclusive; index < source.Length; index++)
        {
            SafeHtmlScanner.CheckCancellation(cancellationToken, index);
            if (!char.IsWhiteSpace(source[index]))
            {
                tag = default;
                return false;
            }
        }

        return true;
    }

    public static bool TryParseTagSequence(string? html, out IReadOnlyList<SafeHtmlTag> tags)
        => TryParseTagSequence(
            html,
            SafeHtmlParseLimits.Default,
            CancellationToken.None,
            out tags,
            out _);

    internal static bool TryParseTagSequence(string? html, SafeHtmlParseLimits limits, out IReadOnlyList<SafeHtmlTag> tags)
        => TryParseTagSequence(html, limits, CancellationToken.None, out tags, out _);

    internal static bool TryParseTagSequence(
        string? html,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken,
        out IReadOnlyList<SafeHtmlTag> tags,
        out bool scanBudgetExceeded)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scanBudgetExceeded = false;
        string source = html ?? string.Empty;
        if (source.Length > limits.MaxInputLength)
        {
            tags = Array.Empty<SafeHtmlTag>();
            return false;
        }
        List<SafeHtmlTag> parsed = [];
        int position = 0;
        var scanBudget = new SafeHtmlScanBudget(source.Length);

        while (position < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (position < source.Length && char.IsWhiteSpace(source[position]))
            {
                SafeHtmlScanner.CheckCancellation(cancellationToken, position);
                position++;
            }

            if (position >= source.Length)
            {
                break;
            }

            if (parsed.Count >= limits.MaxNodeCount ||
                !SafeHtmlTagReader.TryReadTag(
                    source,
                    position,
                    source.Length,
                    limits,
                    cancellationToken,
                    ref scanBudget,
                    out SafeHtmlTag tag,
                    out int endExclusive))
            {
                scanBudgetExceeded = scanBudget.IsExhausted;
                tags = Array.Empty<SafeHtmlTag>();
                return false;
            }

            if (tag.Kind is not (SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration))
            {
                parsed.Add(tag);
            }

            position = endExclusive;
        }

        tags = parsed;
        return parsed.Count > 0;
    }

    public static IReadOnlyList<SafeHtmlTag> ParseTags(string? html)
        => ParseTags(
            html,
            SafeHtmlParseLimits.Default,
            CancellationToken.None,
            out _);

    internal static IReadOnlyList<SafeHtmlTag> ParseTags(string? html, SafeHtmlParseLimits limits)
        => ParseTags(html, limits, CancellationToken.None, out _);

    internal static IReadOnlyList<SafeHtmlTag> ParseTags(
        string? html,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken,
        out bool scanBudgetExceeded)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scanBudgetExceeded = false;
        string source = html ?? string.Empty;
        int parseLength = Math.Min(source.Length, limits.MaxInputLength);
        List<SafeHtmlTag> parsed = [];
        int position = 0;
        var scanBudget = new SafeHtmlScanBudget(parseLength);
        while (position < parseLength && parsed.Count < limits.MaxNodeCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tagStart = SafeHtmlScanner.FindCharacter(source, '<', position, parseLength, cancellationToken);
            if (tagStart < 0)
            {
                break;
            }

            if (SafeHtmlTagReader.TryReadTag(
                    source,
                    tagStart,
                    parseLength,
                    limits,
                    cancellationToken,
                    ref scanBudget,
                    out SafeHtmlTag tag,
                    out int endExclusive))
            {
                if (tag.Kind is not (SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration))
                {
                    parsed.Add(tag);
                }

                position = endExclusive;
            }
            else
            {
                if (scanBudget.IsExhausted)
                {
                    scanBudgetExceeded = true;
                    break;
                }

                position = tagStart + 1;
            }
        }

        return parsed;
    }

    public static SafeHtmlAlignment GetAlignment(SafeHtmlElement element) =>
        SafeHtmlAttributeValues.GetAlignment(element);

    public static SafeHtmlAlignment GetAlignment(SafeHtmlTag tag) =>
        SafeHtmlAttributeValues.GetAlignment(tag);

    public static bool TryGetLength(SafeHtmlElement element, string name, out SafeHtmlLength length) =>
        SafeHtmlAttributeValues.TryGetLength(element, name, out length);

    public static bool TryGetLength(SafeHtmlTag tag, string name, out SafeHtmlLength length) =>
        SafeHtmlAttributeValues.TryGetLength(tag, name, out length);

    /// <inheritdoc cref="SafeHtmlInertImageSpacer.TryGetDimensions(SafeHtmlTag, bool, bool, bool, out float, out float)"/>
    internal static bool TryGetInertImageSpacerDimensions(
        SafeHtmlTag tag,
        bool imagesEnabled,
        bool insidePicture,
        bool insideLink,
        out float width,
        out float height) =>
        SafeHtmlInertImageSpacer.TryGetDimensions(tag, imagesEnabled, insidePicture, insideLink, out width, out height);

    internal static bool TryGetInertImageSpacerDimensions(
        SafeHtmlElement element,
        bool imagesEnabled,
        bool insidePicture,
        bool insideLink,
        out float width,
        out float height) =>
        SafeHtmlInertImageSpacer.TryGetDimensions(element, imagesEnabled, insidePicture, insideLink, out width, out height);

    public static bool TryGetSafeLink(SafeHtmlElement element, out string url) =>
        SafeHtmlUrlPolicy.TryGetSafeLink(element, out url);

    public static bool TryGetSafeLink(SafeHtmlTag tag, out string url) =>
        SafeHtmlUrlPolicy.TryGetSafeLink(tag, out url);

    public static bool TryGetSafeImageSource(SafeHtmlElement element, out string source) =>
        SafeHtmlUrlPolicy.TryGetSafeImageSource(element, out source);

    public static bool TryGetSafeImageSource(SafeHtmlTag tag, out string source) =>
        SafeHtmlUrlPolicy.TryGetSafeImageSource(tag, out source);

    public static bool TryNormalizeImageSource(string value, out string source) =>
        SafeHtmlUrlPolicy.TryNormalizeImageSource(value, out source);

    public static bool IsSuppressedElement(string name) => SafeHtmlElementCategories.IsRawContentElement(name);

    public static string CollapseWhitespace(string? text) =>
        SafeHtmlWhitespace.CollapseWhitespace(text);

    public static string CollapseDecodedWhitespace(string? decodedText) =>
        SafeHtmlWhitespace.CollapseDecodedWhitespace(decodedText);

    internal static string ResolveMissingImageAlternative(
        string source,
        string localizedImageName) =>
        SafeHtmlUrlPolicy.ResolveMissingImageAlternative(source, localizedImageName);
}
