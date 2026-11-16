using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.RelativeScriptFontSizeFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines;

/// <summary>
/// Checks that a paragraph never wraps inside a symbol token attached to a
/// word, such as "C++", the way browsers lay it out, while the ordinary
/// space, hyphen, URL and CJK break opportunities stay available.
/// </summary>
public sealed class InlineGluedTokenWrappingTests
{
    private const float GuardNarrowestWidth = 40f;
    private const float WidestWidth = 640f;

    // microsoft/terminal README: DirectWrite broke this as "is a C+" / "+ application".
    private const string TerminalSentence =
        "Make sure to set your build platform to x64 or ARM64, because the Terminal doesn't " +
        "build for \"Any Cpu\" (because the Terminal is a C++ application, not a C# one).";

    [Fact]
    public void TerminalReadmeSentenceNeverWrapsInsideCpp()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(TerminalSentence, MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        int token = box.LayoutBuffer.IndexOf("C++", StringComparison.Ordinal);

        List<string> splits = FindWidthsThatSplit(box, token, "C++".Length);

        Assert.Empty(splits);
    }

    [Theory]
    [InlineData("C++")]
    [InlineData("a++")]
    [InlineData("x--")]
    [InlineData("C#")]
    [InlineData("->")]
    [InlineData("=>")]
    [InlineData("...")]
    [InlineData("-->")]
    [InlineData("->>")]
    [InlineData("x+y")]
    public void SymbolTokenAttachedToAWordNeverWraps(string token)
    {
        string source = "Several words lead up to the token " + token + " and several words follow it here.";
        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        int start = box.LayoutBuffer.IndexOf(" " + token + " ", StringComparison.Ordinal) + 1;

        List<string> splits = FindWidthsThatSplit(box, start, token.Length);

        Assert.Empty(splits);
    }

    [Theory]
    [InlineData("Several words before a well-known compound word and more words after it.", "well-", "known")]
    [InlineData("Read https://example.com/docs/getting-started/installation for the setup steps.", "example.com/", "docs")]
    [InlineData("中文文本应该在汉字之间换行而不需要空格分隔这一段落很长", "在", "汉")]
    public void OrdinaryBreakOpportunitiesStillWrap(string source, string before, string after)
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        int boundary = box.LayoutBuffer.IndexOf(before + after, StringComparison.Ordinal) + before.Length;

        bool wrapped = false;
        for (float width = GuardNarrowestWidth; width <= WidestWidth && !wrapped; width += 1f)
        {
            box.Measure(width);
            wrapped = LineBoundaries(Assert.IsType<CanvasTextLayout>(box.TextLayout)).Contains(boundary);
        }

        Assert.True(wrapped, $"No width in {GuardNarrowestWidth}..{WidestWidth} wrapped at '{before}|{after}'.");
    }

    [Fact]
    public void SelectionLayoutWrapsTheSameLinesAsThePaintedLayout()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(TerminalSentence, MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        int token = box.LayoutBuffer.IndexOf("C++", StringComparison.Ordinal);
        float repairedWidth = FindWidthWhereTokenStartsALine(box, token);

        box.Measure(repairedWidth);
        int[] painted = LineBoundaries(Assert.IsType<CanvasTextLayout>(box.TextLayout));
        CanvasTextLayout selection = box.EnsureSelectionLayout(
            Color.FromArgb(255, 255, 255, 255),
            box.GetContainerStyle());

        Assert.Contains(token, painted);
        Assert.Equal(painted, LineBoundaries(selection));
    }

    private static float FindWidthWhereTokenStartsALine(InlineContainerBox box, int token)
    {
        float narrowest = MinContentWidth(box);
        for (float width = WidestWidth; width >= narrowest; width -= 1f)
        {
            box.Measure(width);
            if (LineBoundaries(Assert.IsType<CanvasTextLayout>(box.TextLayout)).Contains(token))
                return width;
        }

        throw new InvalidOperationException("No width wrapped the paragraph before the token.");
    }

    private static List<string> FindWidthsThatSplit(InlineContainerBox box, int start, int length)
    {
        Assert.True(start >= 0);
        var splits = new List<string>();
        // Below min-content no line can hold the token whole: DirectWrite
        // must break a word somewhere where a browser would overflow instead.
        for (float width = MinContentWidth(box); width <= WidestWidth; width += 1f)
        {
            box.Measure(width);
            foreach (int boundary in LineBoundaries(Assert.IsType<CanvasTextLayout>(box.TextLayout)))
            {
                if (boundary > start && boundary < start + length)
                {
                    splits.Add(
                        $"width {width}: \"{box.LayoutBuffer[start..boundary]}\" | " +
                        $"\"{box.LayoutBuffer[boundary..(start + length)]}\"");
                }
            }
        }

        return splits;
    }

    private static float MinContentWidth(InlineContainerBox box)
        => MathF.Ceiling(box.MeasureIntrinsicWidths().Minimum);

    private static int[] LineBoundaries(CanvasTextLayout layout)
    {
        CanvasLineMetrics[] lines = layout.LineMetrics;
        var boundaries = new int[System.Math.Max(0, lines.Length - 1)];
        int position = 0;
        for (int index = 0; index < boundaries.Length; index++)
        {
            position += lines[index].CharacterCount;
            boundaries[index] = position;
        }

        return boundaries;
    }
}
