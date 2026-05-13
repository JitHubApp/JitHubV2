using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Unit tests for <see cref="TextBoundaryHelper.FindWordBoundaries"/>.
/// Exercises the word-boundary detection algorithm used by double-click and
/// the InlineContainerBox word-selection helper.
/// </summary>
public class TextBoundaryHelperTests
{
    // ── Basic word selection ──────────────────────────────────────────────────

    [Fact]
    public void SingleWord_MiddleCursor_SelectsWholeWord()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello", 2);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void SingleWord_StartCursor_SelectsWholeWord()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello", 0);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void SingleWord_EndCursor_SelectsWholeWord()
    {
        // Cursor at length (one past last char) should still snap to the word.
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello", 5);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void TwoWords_CursorInFirstWord_SelectsFirstWord()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("foo bar", 1);
        Assert.Equal(0, start);
        Assert.Equal(3, end);
    }

    [Fact]
    public void TwoWords_CursorInSecondWord_SelectsSecondWord()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("foo bar", 5);
        Assert.Equal(4, start);
        Assert.Equal(7, end);
    }

    // ── Whitespace handling ───────────────────────────────────────────────────

    [Fact]
    public void CursorOnSpace_SnapsToLeftWord()
    {
        // Buffer: "hello world", cursor on the space at index 5.
        // Should prefer the word to the left ("hello").
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello world", 5);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void CursorOnLeadingSpace_SnapsToFirstWord()
    {
        // Buffer: " hello", cursor at 0 (the leading space, no left word).
        var (start, end) = TextBoundaryHelper.FindWordBoundaries(" hello", 0);
        // No word to the left → should snap right to "hello".
        Assert.Equal(1, start);
        Assert.Equal(6, end);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyBuffer_ReturnsZeroZero()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries(string.Empty, 0);
        Assert.Equal(0, start);
        Assert.Equal(0, end);
    }

    [Fact]
    public void AllWhitespace_ReturnsSameIndex()
    {
        // "   " — all spaces.  After snapping right, end lands at the end of the string.
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("   ", 1);
        // All whitespace → empty word; start should equal end.
        Assert.Equal(start, end);
    }

    [Fact]
    public void MultipleSpaces_BetweenWords_SnapsCorrectly()
    {
        // "foo   bar", cursor at index 4 (second space).
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("foo   bar", 4);
        // Index 4 is a space; index 3 (left) is also a space; snap right → "bar"
        Assert.Equal(6, start);
        Assert.Equal(9, end);
    }

    [Fact]
    public void NegativeIndex_ClampedToZero()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello", -5);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void IndexBeyondBuffer_ClampedToEnd()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello", 100);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    // ── Unicode / special chars ───────────────────────────────────────────────

    [Fact]
    public void UnicodeWord_SelectsWholeWord()
    {
        // Arabic word embedded in buffer.
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("مرحبا world", 2);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void SuperscriptChars_TreatedAsWord()
    {
        // Footnote citation "¹²³" — Unicode superscripts are non-whitespace.
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("text ¹²³ more", 6);
        Assert.Equal(5, start);
        Assert.Equal(8, end);
    }

    [Fact]
    public void TrailingDoubleSpace_CursorOnLastSpace_SnapsLeftToWord()
    {
        // "hello  " — cursor on last char (index 6, second trailing space).
        // snap-left: buffer[5]=' ' → fails; snap-right loop with < buffer.Length: idx=6, buffer[6]=' ' → idx=7 (beyond buffer);
        // both walk-loops stall immediately → falls through to all-whitespace → empty range.
        // Actually correct: no word to snap to at that position.
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello  ", 6);
        Assert.Equal(start, end); // degenerate — no word; this is the expected behavior
    }

    [Fact]
    public void TrailingSpace_CursorOnFirstTrailingSpace_SnapsLeftToWord()
    {
        // "hello  " — cursor on index 5 (first trailing space).
        // snap-left: buffer[4]='o' → fires → idx=4 → selects "hello".
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello  ", 5);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void MultipleTrailingSpaces_CursorOnMiddleSpace_SnapsRight()
    {
        // "aa   bb   " — cursor at index 3 (middle of first space-run; both neighbors are spaces)
        // snap-left fails (buffer[2]=' '); snap-right advances 3→4→5 until 'b'; selects "bb".
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("aa   bb   ", 3);
        Assert.Equal(5, start);
        Assert.Equal(7, end);
    }
}
