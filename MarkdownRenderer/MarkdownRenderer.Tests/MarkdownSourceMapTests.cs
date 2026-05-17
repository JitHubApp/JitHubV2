using Xunit;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Tests;

public class MarkdownSourceMapTests
{
    private static MarkdownSourceMap BuildMap(string source, params (int block, int inline, int renderLen, int spanStart, int spanLen)[] entries)
    {
        var map = new MarkdownSourceMap(source);
        foreach (var (block, inline, renderLen, spanStart, spanLen) in entries)
            map.Add(block, inline, renderLen, new SourceSpan(spanStart, spanLen));
        return map;
    }

    [Fact]
    public void SourceText_ReturnsConstructorValue()
    {
        var map = new MarkdownSourceMap("hello");
        Assert.Equal("hello", map.SourceText);
    }

    [Fact]
    public void SourceText_NullBecomesEmpty()
    {
        var map = new MarkdownSourceMap(null!);
        Assert.Equal(string.Empty, map.SourceText);
    }

    [Fact]
    public void Entries_EmptyInitially()
    {
        var map = new MarkdownSourceMap("hello");
        Assert.Empty(map.Entries);
    }

    [Fact]
    public void Add_IncreasesEntryCount()
    {
        var map = new MarkdownSourceMap("hello world");
        map.Add(0, 0, 5, new SourceSpan(0, 5));
        map.Add(0, 1, 5, new SourceSpan(6, 5));
        Assert.Equal(2, map.Entries.Count);
    }

    [Fact]
    public void Slice_EmptyRange_ReturnsEmptyString()
    {
        var map = BuildMap("hello", (0, 0, 5, 0, 5));
        var slice = map.Slice(DocumentRange.Empty);
        Assert.Equal(string.Empty, slice);
    }

    [Fact]
    public void Slice_FullSpan_WhenRenderLengthEqualsSpanLength()
    {
        // block 0, inline 0, renderLen=5, span [0..5)
        var map = BuildMap("hello", (0, 0, 5, 0, 5));
        var start = new DocumentPosition(0, 0, 0);
        var end = new DocumentPosition(0, 0, 5);
        var range = new DocumentRange(start, end);
        Assert.Equal("hello", map.Slice(range));
    }

    [Fact]
    public void Slice_PartialRange_ReturnsSubstring()
    {
        var map = BuildMap("hello world", (0, 0, 5, 0, 5));
        // Slice chars 1..3 of block 0 inline 0
        var start = new DocumentPosition(0, 0, 1);
        var end = new DocumentPosition(0, 0, 4);
        var slice = map.Slice(new DocumentRange(start, end));
        Assert.Equal("ell", slice);
    }

    [Fact]
    public void Slice_AtomicInlineImageSlot_ReturnsFullMarkdownImage()
    {
        const string source = "before ![alt](image.png) after";
        int imageStart = source.IndexOf("![", System.StringComparison.Ordinal);
        var map = BuildMap(source, (0, 1, 1, imageStart, "![alt](image.png)".Length));

        var slice = map.Slice(new DocumentRange(
            new DocumentPosition(0, 1, 0),
            new DocumentPosition(0, 1, 1)));

        Assert.Equal("![alt](image.png)", slice);
    }

    [Fact]
    public void Slice_ReversedRange_NormalizesFirst()
    {
        var map = BuildMap("hello", (0, 0, 5, 0, 5));
        var a = new DocumentPosition(0, 0, 0);
        var b = new DocumentPosition(0, 0, 5);
        // Pass reversed
        var reversed = new DocumentRange(b, a);
        Assert.Equal("hello", map.Slice(reversed));
    }

    [Fact]
    public void Slice_MultiBlock_InsertsParagraphSeparator()
    {
        var source = "foo\n\nbar";
        var map = new MarkdownSourceMap(source);
        map.Add(0, 0, 3, new SourceSpan(0, 3)); // "foo"
        map.Add(1, 0, 3, new SourceSpan(5, 3)); // "bar"

        var start = new DocumentPosition(0, 0, 0);
        var end = new DocumentPosition(1, 0, 3);
        var slice = map.Slice(new DocumentRange(start, end));
        Assert.Contains("foo", slice);
        Assert.Contains("bar", slice);
        Assert.Contains("\n\n", slice);
    }

    [Fact]
    public void Slice_RangeBeforeAllEntries_ReturnsEmpty()
    {
        var map = BuildMap("hello", (2, 0, 5, 0, 5));
        // Range is at block 0, but all entries are at block 2
        var start = DocumentPosition.Zero;
        var end = new DocumentPosition(0, 0, 3);
        Assert.Equal(string.Empty, map.Slice(new DocumentRange(start, end)));
    }

    [Fact]
    public void Entry_StoresCorrectValues()
    {
        var map = new MarkdownSourceMap("test");
        map.Add(3, 7, 10, new SourceSpan(1, 4));
        var entry = map.Entries[0];
        Assert.Equal(3, entry.BlockIndex);
        Assert.Equal(7, entry.InlineIndex);
        Assert.Equal(10, entry.RenderedLength);
        Assert.Equal(new SourceSpan(1, 4), entry.Span);
    }
}
