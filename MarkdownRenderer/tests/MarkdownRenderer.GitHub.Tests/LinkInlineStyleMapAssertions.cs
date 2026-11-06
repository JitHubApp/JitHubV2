using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Reads nested link style maps, spans and uniform text layout typography for
/// <see cref="RelativeScriptFontSizeTests"/>.
/// </summary>
internal static class LinkInlineStyleMapAssertions
{
    /// <summary>
    /// Creates a sheet whose link rule overrides font, size and paint, with nested
    /// strong, emphasis and inline code rules inside the link.
    /// </summary>
    internal static MarkdownStyleSheet CreateNestedLinkSheet(Color linkColor, Color codeBackground)
    {
        return new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Link),
                new ElementStyleOverride
                {
                    FontFamily = "Arial",
                    FontSize = 20f,
                    Foreground = linkColor,
                    Underline = true,
                }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Strong)),
                new ElementStyleOverride { FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 } }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Emphasis)),
                new ElementStyleOverride { FontStyle = Windows.UI.Text.FontStyle.Italic }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.CodeInline)),
                new ElementStyleOverride
                {
                    // Deliberately equals the body style while differing
                    // from the overridden link base, to pin range-relative
                    // SetFontFamily/SetFontSize behavior.
                    FontFamily = "Segoe UI",
                    FontSize = 16f,
                    Background = codeBackground,
                }),
        ]);
    }

    /// <summary>
    /// Asserts that nested strong, emphasis and code spans keep their own typography
    /// while inheriting the link foreground and underline.
    /// </summary>
    internal static void AssertNestedSpanStylesKeepLinkPaint(
        LinkInlineStyleMap map,
        ElementStyle[] resolution,
        Color linkColor,
        Color codeBackground)
    {
        ElementStyle strong = GetSpanStyle(map, resolution, MarkdownElementKeys.Strong);
        Assert.Equal((ushort)700, strong.FontWeight.Weight);
        Assert.Equal(linkColor, strong.Foreground);
        Assert.True(strong.Underline);

        ElementStyle emphasis = GetSpanStyle(map, resolution, MarkdownElementKeys.Emphasis);
        Assert.Equal(Windows.UI.Text.FontStyle.Italic, emphasis.FontStyle);
        Assert.Equal(linkColor, emphasis.Foreground);
        Assert.True(emphasis.Underline);

        ElementStyle code = GetSpanStyle(map, resolution, MarkdownElementKeys.CodeInline);
        Assert.Equal("Segoe UI", code.FontFamily);
        Assert.Equal(16f, code.FontSize);
        Assert.Equal(codeBackground, code.Background);
        Assert.Equal(linkColor, code.Foreground);
        Assert.True(code.Underline);
    }

    internal static ElementStyle GetSpanStyle(
        LinkInlineStyleMap map,
        ElementStyle[] styles,
        string expectedStyleKey)
    {
        for (int index = 0; index < map.Count; index++)
        {
            LinkInlineStyleSpan span = map.GetSpan(index);
            for (LinkInlineStylePath? path = span.Path; path is not null; path = path.Parent)
            {
                if (string.Equals(path.StyleKey, expectedStyleKey, StringComparison.Ordinal))
                    return styles[index];
            }
        }

        throw new Xunit.Sdk.XunitException($"No nested link span carried style key '{expectedStyleKey}'.");
    }

    internal static LinkInlineStyleSpan GetSpan(LinkInlineStyleMap map, string expectedStyleKey)
    {
        for (int index = 0; index < map.Count; index++)
        {
            LinkInlineStyleSpan span = map.GetSpan(index);
            for (LinkInlineStylePath? path = span.Path; path is not null; path = path.Parent)
                if (string.Equals(path.StyleKey, expectedStyleKey, StringComparison.Ordinal))
                    return span;
        }

        throw new Xunit.Sdk.XunitException($"No nested link span carried style key '{expectedStyleKey}'.");
    }

    internal static void AssertSpanText(LinkRun link, LinkInlineStyleSpan span, string expected)
    {
        Assert.Equal(expected.Length, span.Length);
        Assert.Equal(link.Text.IndexOf(expected, StringComparison.Ordinal), span.Start);
        Assert.Equal(expected, link.Text.Substring(span.Start, span.Length));
    }

    internal static string[] GetPathKeys(LinkInlineStylePath path)
    {
        var keys = new List<string>();
        for (LinkInlineStylePath? current = path; current is not null; current = current.Parent)
            keys.Add(current.StyleKey);
        keys.Reverse();
        return [.. keys];
    }

    internal static int FindSpanIndex(LinkInlineStyleMap map, LinkInlineStyleSpan expected)
    {
        for (int index = 0; index < map.Count; index++)
        {
            LinkInlineStyleSpan actual = map.GetSpan(index);
            if (actual.Start == expected.Start && actual.Length == expected.Length &&
                ReferenceEquals(actual.Path, expected.Path))
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException("The nested link style map did not contain the expected span.");
    }

    internal static float ReadUniformFontSize(CanvasTextLayout layout, int start, int length)
    {
        Assert.True(start >= 0 && length > 0);
        float fontSize = layout.GetFontSize(start);
        for (int index = 1; index < length; index++)
            Assert.Equal(fontSize, layout.GetFontSize(start + index));
        return fontSize;
    }

    internal static int GetRunStart(InlineContainerBox box, InlineRun target)
    {
        int offset = 0;
        foreach (InlineRun run in box.Runs)
        {
            if (ReferenceEquals(run, target))
                return offset;
            offset += run.TextLength;
        }

        throw new Xunit.Sdk.XunitException("The inline run is not present in its container.");
    }

    internal static string ReadUniformFontFamily(CanvasTextLayout layout, int start, int length)
    {
        Assert.True(start >= 0 && length > 0);
        string fontFamily = layout.GetFontFamily(start);
        for (int index = 1; index < length; index++)
            Assert.Equal(fontFamily, layout.GetFontFamily(start + index));
        return fontFamily;
    }
}
