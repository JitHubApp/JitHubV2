using System.Globalization;
using System.Numerics;
using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines;

/// <summary>
/// Text layout geometry helpers and shared assertions for
/// <see cref="InlineCodePaddingLayoutTests"/>.
/// </summary>
internal static class InlineCodePaddingTestGeometry
{
    /// <summary>
    /// Asserts that inline code padding leaves the code run's document and buffer
    /// offsets on the original UTF-16 range, and that the selection and selection
    /// layout cover exactly the padded code bounds.
    /// </summary>
    internal static void AssertCodeOffsetsAndSelectionKeepSourceRange(
        InlineContainerBox padded,
        InlineRun codeRun,
        int codeStart,
        int codeLength,
        Rect paddedCode,
        Color selectionColor,
        ElementStyle bodyStyle)
    {
        var codeStartPosition = new DocumentPosition(padded.BlockIndex, codeRun.InlineIndex, 0);
        var codeEndPosition = new DocumentPosition(padded.BlockIndex, codeRun.InlineIndex, codeRun.TextLength);
        Assert.Equal(codeStart, padded.GetBufferCharOffset(codeStartPosition));
        Assert.Equal(codeStartPosition, padded.GetPositionFromBufferOffset(codeStart));
        Assert.Equal(codeStart + codeLength, padded.GetBufferCharOffset(codeEndPosition));
        InlineRun suffixRun = padded.Runs[codeRun.InlineIndex + 1];
        Assert.Equal(
            new DocumentPosition(padded.BlockIndex, suffixRun.InlineIndex, 0),
            padded.GetPositionFromBufferOffset(codeStart + codeLength));

        var selectionRange = new DocumentRange(codeStartPosition, codeEndPosition);
        Rect selectedCode = UnionRects(padded.GetSelectionRects(selectionRange));
        Assert.Equal(paddedCode.Left, selectedCode.Left, precision: 3);
        Assert.Equal(paddedCode.Right, selectedCode.Right, precision: 3);
        CanvasTextLayout selectionLayout = padded.EnsureSelectionLayout(selectionColor, bodyStyle);
        Rect selectedLayoutCode = Union(selectionLayout.GetCharacterRegions(codeStart, codeLength));
        Assert.Equal(paddedCode.Left, selectedLayoutCode.Left, precision: 3);
        Assert.Equal(paddedCode.Right, selectedLayoutCode.Right, precision: 3);
    }

    internal static string Buffer(InlineContainerBox box)
        => Assert.IsType<string>(box.LayoutBuffer);

    internal static Rect Union(CanvasTextLayoutRegion[] regions)
    {
        Assert.NotNull(regions);
        Assert.NotEmpty(regions);
        double left = regions.Min(region => region.LayoutBounds.Left);
        double top = regions.Min(region => region.LayoutBounds.Top);
        double right = regions.Max(region => region.LayoutBounds.Right);
        double bottom = regions.Max(region => region.LayoutBounds.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    internal static string DescribeTextLayout(CanvasTextLayout layout, string text)
    {
        var description = new System.Text.StringBuilder();
        CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(0, text.Length);
        description.Append("regions=");
        foreach (CanvasTextLayoutRegion region in regions)
            description.Append($"[{region.CharacterIndex}+{region.CharacterCount}@{region.LayoutBounds.Left:0.##},{region.LayoutBounds.Top:0.##},{region.LayoutBounds.Width:0.##}]");

        description.Append(" carets=");
        for (int index = 0; index < text.Length; index++)
        {
            Vector2 leading = layout.GetCaretPosition(index, trailingSideOfCharacter: false);
            Vector2 trailing = layout.GetCaretPosition(index, trailingSideOfCharacter: true);
            description.Append($"{index}:{(int)text[index]}({leading.X:0.##},{trailing.X:0.##})s({layout.GetLeadingCharacterSpacing(index):0.##},{layout.GetTrailingCharacterSpacing(index):0.##});");
        }

        return description.ToString();
    }

    internal static int TextElementLength(string text, int start)
    {
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text, start);
        Assert.True(elements.MoveNext());
        int firstElementStart = start + elements.ElementIndex;
        if (!elements.MoveNext())
            return text.Length - firstElementStart;

        return start + elements.ElementIndex - firstElementStart;
    }

    internal static Rect UnionRects(IEnumerable<Rect> rects)
    {
        Rect[] values = rects.ToArray();
        Assert.NotEmpty(values);
        double left = values.Min(rect => rect.Left);
        double top = values.Min(rect => rect.Top);
        double right = values.Max(rect => rect.Right);
        double bottom = values.Max(rect => rect.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    internal static int LastTextElementStart(string text)
    {
        int lastStart = 0;
        TextElementEnumerator elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            lastStart = elements.ElementIndex;
        return lastStart;
    }

    internal static Rect ReadCanvasTextLayoutRect(CanvasTextLayout layout, string name)
    {
        object? value = typeof(CanvasTextLayout)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(layout);
        return Assert.IsType<Rect>(value);
    }

    internal static List<InlineCodePaddingRange> CreatePaddingRanges(
        int start, int length, float leftPadding, float rightPadding)
        => [new InlineCodePaddingRange(start, length, leftPadding, rightPadding)];
}
