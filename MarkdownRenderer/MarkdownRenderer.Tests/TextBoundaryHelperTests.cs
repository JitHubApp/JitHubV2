using MarkdownRenderer.Layout;
using System.Globalization;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Unit tests for <see cref="TextBoundaryHelper.FindWordBoundaries"/>.
/// Exercises the word-boundary detection algorithm used by double-click and
/// the InlineContainerBox word-selection helper.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
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
    public void Punctuation_SeparatesWords()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("hello,world", 6);
        Assert.Equal(6, start);
        Assert.Equal(11, end);

        // Punctuation keeps the established nearest-word behavior; only
        // Unicode symbol/emoji graphemes are selected as independent units.
        Assert.Equal((0, 5), TextBoundaryHelper.FindWordBoundaries("hello,world", 5));
    }

    [Fact]
    public void CombiningMark_StaysWithBaseCharacter()
    {
        var (start, end) = TextBoundaryHelper.FindWordBoundaries("cafe\u0301 noir", 4);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void SymbolAndEmojiGraphemesAreSelectableWithoutSelectingNeighboringWords()
    {
        Assert.Equal((0, 2), TextBoundaryHelper.FindWordBoundaries("😀", 1));
        Assert.Equal((2, 4), TextBoundaryHelper.FindWordBoundaries("a 😀 b", 3));
        Assert.Equal((2, 9), TextBoundaryHelper.FindWordBoundaries("a 👩🏽‍💻 b", 5));
        Assert.Equal((2, 5), TextBoundaryHelper.FindWordBoundaries("a 1️⃣ b", 3));
        Assert.Equal((2, 3), TextBoundaryHelper.FindWordBoundaries("a ∑ b", 2));
        Assert.Equal((2, 3), TextBoundaryHelper.FindWordBoundaries("a $ b", 2));
        Assert.Equal((0, 3), TextBoundaryHelper.FindWordBoundaries("a\uFE0Fb", 1));
        Assert.Equal((0, 3), TextBoundaryHelper.FindWordBoundaries("1\uFE0F2", 1));
    }

    [Fact]
    public void WordMovement_SkipsPunctuation()
    {
        Assert.Equal(7, TextBoundaryHelper.FindNextWordStart("hello, world", 0));
        Assert.Equal(0, TextBoundaryHelper.FindPreviousWordStart("hello, world", 7));
    }

    [Theory]
    [InlineData("left1\uFE0F\u20E3right", 2, 0, 4)]
    [InlineData("left1\uFE0F\u20E3right", 8, 7, 12)]
    [InlineData("left1\uFE0F\u20E3right", 4, 4, 7)]
    [InlineData("left1\uFE0F\u20E3right", 5, 4, 7)]
    [InlineData("left1\uFE0F\u20E3right", 6, 4, 7)]
    [InlineData("left1\u20E3right", 2, 0, 4)]
    [InlineData("left1\u20E3right", 7, 6, 11)]
    [InlineData("مرحبا1\uFE0F\u20E3عالم", 2, 0, 5)]
    [InlineData("مرحبا1\uFE0F\u20E3عالم", 9, 8, 12)]
    [InlineData("مرحبا1\uFE0F\u20E3عالم", 6, 5, 8)]
    [InlineData("e\u03011\uFE0F\u20E3cafe\u0301", 1, 0, 2)]
    [InlineData("e\u03011\uFE0F\u20E3cafe\u0301", 6, 5, 10)]
    [InlineData("e\u03011\uFE0F\u20E3cafe\u0301", 3, 2, 5)]
    [InlineData("121\uFE0F\u20E334", 0, 0, 2)]
    [InlineData("121\uFE0F\u20E334", 6, 5, 7)]
    [InlineData("121\uFE0F\u20E334", 3, 2, 5)]
    public void AdjacentKeycapEmojiDoesNotJoinTheWordsOnEitherSide(
        string text, int offset, int expectedStart, int expectedEnd)
    {
        // Fixed literal UTF-16 expectations are independent of the
        // boundary-index representation and its mirrored segmentation oracle.
        Assert.Equal((expectedStart, expectedEnd), TextBoundaryHelper.FindWordBoundaries(text, offset));
    }

    [Fact]
    public void WordMovementDoesNotSkipTheWordAfterAnAdjacentKeycapEmoji()
    {
        const string text = "left1\uFE0F\u20E3right";
        Assert.Equal(7, TextBoundaryHelper.FindNextWordStart(text, 0));
        Assert.Equal(7, TextBoundaryHelper.FindPreviousWordStart(text, text.Length));
        Assert.Equal(0, TextBoundaryHelper.FindPreviousWordStart(text, 7));
    }

    [Theory]
    [InlineData("left\u20E3right", 2)]
    [InlineData("left\u20E3right", 3)]
    [InlineData("left\u20E3right", 4)]
    [InlineData("left\u20E3right", 8)]
    [InlineData("left\u0661\u20E3right", 2)]
    [InlineData("left\u0661\u20E3right", 4)]
    [InlineData("left\u0661\u20E3right", 5)]
    [InlineData("left\u0661\u20E3right", 8)]
    public void EnclosingKeycapMarkOnAnIneligibleBaseRemainsInTheOrdinaryWord(string text, int offset)
    {
        // U+20E3 is a combining enclosing mark. Only ASCII 0-9, # and *
        // are emoji keycap bases; letters/non-ASCII digits remain ordinary
        // word graphemes instead of becoming a spurious word separator.
        Assert.Equal((0, text.Length), TextBoundaryHelper.FindWordBoundaries(text, offset));
        Assert.Equal(text.Length, TextBoundaryHelper.FindNextWordStart(text, 0));
        Assert.Equal(0, TextBoundaryHelper.FindPreviousWordStart(text, text.Length));
    }

    [Theory]
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('2')]
    [InlineData('3')]
    [InlineData('4')]
    [InlineData('5')]
    [InlineData('6')]
    [InlineData('7')]
    [InlineData('8')]
    [InlineData('9')]
    [InlineData('#')]
    [InlineData('*')]
    public void EveryKeycapBaseRemainsAtomicBetweenNeighboringWords(char keycapBase)
    {
        foreach (bool hasVariationSelector in new[] { false, true })
        {
            string text = "left" + keycapBase + (hasVariationSelector ? "\uFE0F" : string.Empty) + "\u20E3right";
            int symbolEnd = hasVariationSelector ? 7 : 6;
            Assert.Equal((0, 4), TextBoundaryHelper.FindWordBoundaries(text, 2));
            Assert.Equal((4, symbolEnd), TextBoundaryHelper.FindWordBoundaries(text, 5));
            Assert.Equal((symbolEnd, symbolEnd + 5), TextBoundaryHelper.FindWordBoundaries(text, symbolEnd + 1));
            Assert.Equal(symbolEnd, TextBoundaryHelper.FindNextWordStart(text, 0));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void AdjacentKeycapWordBoundariesRemainCorrectAcrossSparseAndDenseIndexes(int repetitions)
    {
        // One exceptional cluster per 13-code-unit phrase reaches both sides
        // of the bounded sparse representation's 4096-cluster threshold.
        const string phrase = "left1\uFE0F\u20E3right ";
        var index = new TextElementBoundaryIndex(string.Concat(Enumerable.Repeat(phrase, repetitions)));
        foreach (int occurrence in new[] { 0, repetitions / 2, repetitions - 1 }.Distinct())
        {
            int origin = occurrence * 13;
            Assert.Equal((origin, origin + 4), index.FindWordBoundaries(origin + 2));
            Assert.Equal((origin + 4, origin + 7), index.FindWordBoundaries(origin + 5));
            Assert.Equal((origin + 7, origin + 12), index.FindWordBoundaries(origin + 8));
            Assert.Equal(origin + 7, index.FindNextWordStart(origin));
            Assert.Equal(origin + 7, index.FindPreviousWordStart(origin + 12));
            Assert.Equal(origin, index.FindPreviousWordStart(origin + 7));
        }
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

    [Fact]
    public void TextElementBoundariesDoNotSplitEmojiOrCombiningSequences()
    {
        const string text = "A👩🏽‍💻e\u0301Z";
        int emojiStart = 1;
        int emojiEnd = TextBoundaryHelper.FindNextTextElementStart(text, emojiStart);
        int combiningStart = emojiEnd;
        int combiningEnd = TextBoundaryHelper.FindNextTextElementStart(text, combiningStart);

        Assert.Equal((emojiStart, emojiEnd), TextBoundaryHelper.FindTextElementBoundaries(text, emojiStart + 1));
        Assert.Equal((combiningStart, combiningEnd), TextBoundaryHelper.FindTextElementBoundaries(text, combiningStart + 1));
        Assert.Equal(emojiStart, TextBoundaryHelper.FindPreviousTextElementStart(text, emojiEnd));
        Assert.Equal(combiningStart, TextBoundaryHelper.FindPreviousTextElementStart(text, combiningEnd));

        Assert.Equal(combiningEnd, TextBoundaryHelper.MoveByTextElements(text, emojiStart, 2, out int movedForward));
        Assert.Equal(2, movedForward);
        Assert.Equal(emojiStart, TextBoundaryHelper.MoveByTextElements(text, combiningEnd, -2, out int movedBackward));
        Assert.Equal(-2, movedBackward);

        Assert.Equal(text.Length, TextBoundaryHelper.MoveByTextElements(text, 0, int.MaxValue, out int movedToEnd));
        Assert.Equal(StringInfo.ParseCombiningCharacters(text).Length, movedToEnd);
    }

    [Fact]
    public void CachedTextElementIndexSupportsAllocationFreeRepeatedNavigation()
    {
        string text = string.Concat(Enumerable.Repeat("A👩🏽‍💻e\u0301 ", 20_000));
        var index = new TextElementBoundaryIndex(text);
        int offset = 0;
        int wordOffset = 0;

        // Warm the JIT and all relevant binary-search paths before measuring.
        offset = index.Move(offset, 1, out _);
        _ = index.FindBoundaries(offset);
        _ = index.FindPreviousStart(offset);
        wordOffset = index.FindNextWordStart(wordOffset);
        _ = index.FindWordBoundaries(wordOffset);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            offset = index.Move(offset, 1, out _);
            _ = index.FindBoundaries(offset);
            _ = index.FindPreviousStart(offset);
            wordOffset = index.FindNextWordStart(wordOffset);
            if (wordOffset == text.Length)
                wordOffset = 0;
            _ = index.FindWordBoundaries(wordOffset);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.InRange(offset, 0, text.Length);
    }

    [Fact]
    public void ImplicitAsciiBoundariesMatchStringInfoOracleForEveryOffsetAndMove()
    {
        // The ordered ASCII repertoire contains every ASCII category while avoiding
        // an adjacent CRLF pair (which StringInfo treats as one text element).
        string allAscii = new(Enumerable.Range(0, 128).Select(static value => (char)value).ToArray());
        string readableAscii = "  one foo,bar\t33_  ";
        string bmpLettersAndSymbols = "ASCII ☑ CJK 中文✓";

        AssertMatchesStringInfoOracle(allAscii);
        AssertMatchesStringInfoOracle(readableAscii);
        AssertMatchesStringInfoOracle(bmpLettersAndSymbols);
    }

    [Fact]
    public void NonAsciiAndCrLfFallbackMatchStringInfoOracle()
    {
        string[] fallbackInputs =
        [
            string.Empty,
            "a\r\nb",
            "a\rb\nc",
            "e\u0301",
            "👩🏽‍💻",
            "🇺🇸",
            "क्‍ष",
            "A\u0301\r\n😀",
        ];

        foreach (string text in fallbackInputs)
            AssertMatchesStringInfoOracle(text);

        Assert.Equal((1, 3), new TextElementBoundaryIndex("a\r\nb").FindBoundaries(2));
        Assert.Equal(1, TextBoundaryHelper.FindPreviousTextElementStart("a\r\nb", 3));
        Assert.Equal(2, TextBoundaryHelper.FindPreviousTextElementStart("😀😃", 4));
        Assert.Equal(4, TextBoundaryHelper.FindPreviousWordStart("one two", 7));
    }

    [Fact]
    public void SparseMultiUnitBoundariesMatchStringInfoForEveryOffsetAndMove()
    {
        string[] sparseInputs =
        [
            "prefix e\u0301 middle A\u0308 suffix",
            "start \U0001F469\u200D\U0001F4BB middle \U0001F1FA\U0001F1F8 end",
            "left\r\nright\r\nend",
            "before \uD800 invalid-high \uDC00 invalid-low after",
            "RTL \u05D0\u05B0\u05D1 and \u0644\u0627",
            "a\u0301\U0001F600\r\nb\uD800c\uDC00d",
            "left1\uFE0F\u20E3right left\u20E3right",
        ];

        foreach (string text in sparseInputs)
            AssertMatchesStringInfoOracle(text);
    }

    [Fact]
    public void SparseMultiUnitMoveCrossesClustersWithoutLosingOrdinalAccounting()
    {
        const string text = "ab e\u0301 cd \U0001F600 ef\r\ngh";
        int[] starts = StringInfo.ParseCombiningCharacters(text);

        for (int ordinal = 0; ordinal < starts.Length; ordinal++)
        {
            int actual = TextBoundaryHelper.MoveByTextElements(text, 0, ordinal, out int moved);
            Assert.Equal(starts[ordinal], actual);
            Assert.Equal(ordinal, moved);

            int remaining = starts.Length - ordinal;
            Assert.Equal(text.Length, TextBoundaryHelper.MoveByTextElements(text, actual, remaining, out int movedToEnd));
            Assert.Equal(remaining, movedToEnd);
        }
    }

    [Fact]
    public void LongMostlySingleUnitTextWithSparseUnicodeDoesNotAllocatePerCharacterStorage()
    {
        string text = new string('a', 400_000) + "e\u0301" +
            new string('b', 400_000) + "\U0001F469\u200D\U0001F4BB" +
            new string('c', 199_000) + "\r\n";

        // Warm StringInfo and the sparse representation before measuring this
        // thread's allocations; the retained index must be bounded by the few
        // exceptional graphemes, not the million UTF-16 positions.
        _ = new TextElementBoundaryIndex("x e\u0301 \U0001F600\r\ny");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        TextElementBoundaryIndex index = new(text);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(text.Length, index.TextLength);
        Assert.InRange(allocated, 0, 4096);
        Assert.Equal((400_000, 400_002), index.FindBoundaries(400_001));
        Assert.Equal((800_002, 800_007), index.FindBoundaries(800_004));
        Assert.Equal((999_007, 999_009), index.FindBoundaries(999_008));
        Assert.Equal(text.Length, index.Move(0, int.MaxValue, out int movedForward));
        Assert.Equal(text.Length - (1 + 4 + 1), movedForward);
        Assert.Equal(0, index.Move(text.Length, int.MinValue, out int movedBackward));
        Assert.Equal(-movedForward, movedBackward);
        GC.KeepAlive(index);
    }

    [Fact]
    public void DenseMultiUnitInputFallsBackAndStillMatchesStringInfoOracle()
    {
        string text = string.Concat(Enumerable.Repeat("e\u0301!", 4_200));

        AssertMatchesStringInfoOracle(text);
    }

    [Fact]
    public void PreviousStartAtTextLengthReturnsTheLastGraphemeForAscii()
    {
        Assert.Equal(1, TextBoundaryHelper.FindPreviousTextElementStart("ab", 2));
    }

    [Fact]
    public void LargeSingleUnitTextIndexDoesNotAllocatePerCharacterBoundaryStorage()
    {
        string text = new string('a', 999_997) + "☑中語";
        _ = new TextElementBoundaryIndex("warmup");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        TextElementBoundaryIndex index = new(text);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(text.Length, index.TextLength);
        Assert.InRange(allocated, 0, 256);
        Assert.Equal((500_000, 500_001), index.FindBoundaries(500_000));
        Assert.Equal(text.Length - 1, index.FindPreviousStart(text.Length));
        Assert.Equal(text.Length, index.Move(0, int.MaxValue, out int movedForward));
        Assert.Equal(text.Length, movedForward);
        Assert.Equal(0, index.Move(text.Length, int.MinValue, out int movedBackward));
        Assert.Equal(-text.Length, movedBackward);
        GC.KeepAlive(index);
    }

    private static void AssertMatchesStringInfoOracle(string text)
    {
        var index = new TextElementBoundaryIndex(text);
        var boundaries = new StringInfoBoundaryOracle(text);

        for (int offset = -2; offset <= text.Length + 2; offset++)
        {
            Assert.Equal(boundaries.FindBoundaries(offset), index.FindBoundaries(offset));
            Assert.Equal(boundaries.FindNextStart(offset), index.FindNextStart(offset));
            Assert.Equal(boundaries.FindPreviousStart(offset), index.FindPreviousStart(offset));
            Assert.Equal(boundaries.FindWordBoundaries(offset), index.FindWordBoundaries(offset));
            Assert.Equal(boundaries.FindNextWordStart(offset), index.FindNextWordStart(offset));
            Assert.Equal(boundaries.FindPreviousWordStart(offset), index.FindPreviousWordStart(offset));

            foreach (int count in new[]
                     {
                         int.MinValue, -text.Length - 1, -3, -1, 0, 1, 3,
                         text.Length + 1, int.MaxValue,
                     })
            {
                int expected = boundaries.Move(offset, count, out int expectedMoved);
                int actual = index.Move(offset, count, out int actualMoved);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedMoved, actualMoved);
            }
        }
    }

    private sealed class StringInfoBoundaryOracle
    {
        private readonly string _text;
        private readonly int[] _starts;

        public StringInfoBoundaryOracle(string text)
        {
            _text = text;
            _starts = StringInfo.ParseCombiningCharacters(text);
        }

        public (int Start, int End) FindBoundaries(int charIndex)
        {
            if (_text.Length == 0) return (0, 0);
            int offset = Math.Clamp(charIndex, 0, _text.Length);
            if (offset == _text.Length) return (_text.Length, _text.Length);
            int start = StartAtOrBefore(offset);
            int next = Array.BinarySearch(_starts, start) + 1;
            int end = next < _starts.Length ? _starts[next] : _text.Length;
            return (start, end);
        }

        public int FindNextStart(int charIndex)
        {
            if (_text.Length == 0) return 0;
            int offset = Math.Clamp(charIndex, 0, _text.Length);
            if (offset >= _text.Length) return _text.Length;
            int current = StartAtOrBefore(offset);
            int next = Array.BinarySearch(_starts, current) + 1;
            return next < _starts.Length ? _starts[next] : _text.Length;
        }

        public int FindPreviousStart(int charIndex)
        {
            if (_text.Length == 0) return 0;
            int offset = Math.Clamp(charIndex, 0, _text.Length);
            if (offset <= 0) return 0;
            int current = offset == _text.Length ? _text.Length : StartAtOrBefore(offset);
            int previous = PreviousStart(current);
            return previous >= 0 ? previous : 0;
        }

        public int Move(int charIndex, int count, out int moved)
        {
            moved = 0;
            if (_text.Length == 0 || count == 0)
                return Math.Clamp(charIndex, 0, _text.Length);

            int offset = Math.Clamp(charIndex, 0, _text.Length);
            int found = Array.BinarySearch(_starts, offset);
            int currentIndex = offset == _text.Length
                ? _starts.Length
                : Math.Max(0, found >= 0 ? found : ~found - 1);
            long requested = (long)currentIndex + count;
            int target = (int)Math.Clamp(requested, 0L, _starts.Length);
            moved = target - currentIndex;
            return target == _starts.Length ? _text.Length : _starts[target];
        }

        public (int Start, int End) FindWordBoundaries(int charIndex)
        {
            if (_text.Length == 0) return (0, 0);
            int index = StartAtOrBefore(Math.Clamp(charIndex, 0, Math.Max(0, _text.Length - 1)));
            if (!IsWhitespaceTextElement(index) && IsSymbolTextElement(index))
                return (index, Math.Min(_text.Length, NextStart(index)));

            if (!IsWordTextElement(index))
            {
                int previous = PreviousStart(index);
                if (previous >= 0 && IsWordTextElement(previous))
                {
                    index = previous;
                }
                else
                {
                    int forward = Math.Min(_text.Length, NextStart(index));
                    while (forward < _text.Length && !IsWordTextElement(forward))
                        forward = Math.Min(_text.Length, NextStart(forward));
                    if (forward < _text.Length)
                        index = forward;
                    else
                        return (index, index);
                }
            }

            int start = index;
            while (true)
            {
                int previous = PreviousStart(start);
                if (previous < 0 || !IsWordTextElement(previous)) break;
                start = previous;
            }

            int end = Math.Min(_text.Length, NextStart(index));
            while (end < _text.Length && IsWordTextElement(end))
                end = Math.Min(_text.Length, NextStart(end));
            return (start, end);
        }

        public int FindNextWordStart(int charIndex)
        {
            if (_text.Length == 0) return 0;
            int index = Math.Clamp(charIndex, 0, _text.Length);
            if (index >= _text.Length) return _text.Length;
            index = StartAtOrBefore(index);
            if (IsWordTextElement(index))
                index = FindWordBoundaries(index).End;
            while (index < _text.Length && !IsWordTextElement(index))
                index = NextStart(index);
            return Math.Min(index, _text.Length);
        }

        public int FindPreviousWordStart(int charIndex)
        {
            if (_text.Length == 0) return 0;
            int index = Math.Clamp(charIndex, 0, _text.Length);
            if (index <= 0) return 0;
            index = PreviousStart(index);
            while (index >= 0 && !IsWordTextElement(index))
                index = PreviousStart(index);
            return index < 0 ? 0 : FindWordBoundaries(index).Start;
        }

        private int StartAtOrBefore(int offset)
        {
            int found = Array.BinarySearch(_starts, offset);
            if (found >= 0) return _starts[found];
            return _starts[Math.Clamp(~found - 1, 0, _starts.Length - 1)];
        }

        private int NextStart(int current)
        {
            int found = Array.BinarySearch(_starts, current);
            if (found < 0) found = ~found - 1;
            return found + 1 < _starts.Length ? _starts[found + 1] : int.MaxValue;
        }

        private int PreviousStart(int current)
        {
            if (current == _text.Length)
                return _starts.Length > 0 ? _starts[^1] : -1;
            int found = Array.BinarySearch(_starts, current);
            if (found < 0) found = ~found - 1;
            return found > 0 ? _starts[found - 1] : -1;
        }

        private bool IsWordTextElement(int start)
        {
            if (start < 0 || start >= _text.Length) return false;
            if (IsSymbolTextElement(start)) return false;
            return CharUnicodeInfo.GetUnicodeCategory(_text, start) switch
            {
                UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or
                UnicodeCategory.OtherNumber or
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.ConnectorPunctuation => true,
                _ => false,
            };
        }

        private bool IsWhitespaceTextElement(int start) =>
            start >= 0 && start < _text.Length && char.IsWhiteSpace(_text, start);

        private bool IsSymbolTextElement(int start)
        {
            if (start < 0 || start >= _text.Length)
                return false;

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(_text, start);
            if (category is UnicodeCategory.CurrencySymbol or
                UnicodeCategory.MathSymbol or
                UnicodeCategory.ModifierSymbol or
                UnicodeCategory.OtherSymbol)
            {
                return true;
            }

            int end = Math.Min(_text.Length, NextStart(start));
            if (_text[start] is not (>= '0' and <= '9' or '#' or '*'))
                return false;
            for (int index = start; index < end; index++)
            {
                if (_text[index] == '\u20E3')
                    return true;
            }

            return false;
        }
    }
}
