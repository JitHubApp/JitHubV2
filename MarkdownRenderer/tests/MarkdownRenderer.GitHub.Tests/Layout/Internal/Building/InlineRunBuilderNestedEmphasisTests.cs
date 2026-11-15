using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.GitHubReadmeEngineLayoutFixture;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.InlineRoleStyleFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Building;

/// <summary>
/// Emphasis nested in emphasis combines both roles, as GitHub renders
/// <c>&lt;strong&gt;a &lt;em&gt;b&lt;/em&gt;&lt;/strong&gt;</c>: italic inside bold is
/// bold and italic, and the outer text keeps only the outer role.
/// </summary>
public sealed class InlineRunBuilderNestedEmphasisTests
{
    // Each expectation is "<unique text>:<flags>", flags from B (bold),
    // I (italic), S (strikethrough), C (code face), U (link underline).
    public static TheoryData<string, string, string[]> Cases => new()
    {
        { "**bold *italic* tail**", "bold italic tail", ["bold:B", "italic:BI", "tail:B"] },
        { "*italic **bold** tail*", "italic bold tail", ["italic:I", "bold:BI", "tail:I"] },
        { "__bold _italic_ tail__", "bold italic tail", ["bold:B", "italic:BI", "tail:B"] },
        { "~~**bold *italic***~~", "bold italic", ["bold:BS", "italic:BIS"] },
        { "***both***", "both", ["both:BI"] },
        { "**a *b***", "a b", ["a:B", "b:BI"] },
        {
            "**bold *italic `code`* tail**",
            "bold italic code tail", ["bold:B", "italic:BI", "code:BIC", "tail:B"]
        },
        {
            "**bold *italic [link](https://example.test/)* tail**",
            "bold italic link tail", ["bold:B", "italic:BI", "link:BIU", "tail:B"]
        },
        { "*one* plain **two**", "one plain two", ["one:I", "plain:", "two:B"] },
        { "- **bold *italic* tail**", "bold italic tail", ["bold:B", "italic:BI", "tail:B"] },
        { "| head |\n|------|\n| **bold *italic* tail** |", "bold italic tail", ["bold:B", "italic:BI", "tail:B"] },
        { "> [!NOTE]\n> **bold *italic* tail**", "bold italic tail", ["bold:B", "italic:BI", "tail:B"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NestedEmphasisCombinesRolesInRunStylesAndTextLayout(
        string source,
        string paragraphText,
        string[] expectations)
    {
        using LayoutSnapshot snapshot = await BuildAsync(source, CreateStyles());

        InlineContainerBox paragraph = Assert.Single(
            EnumerateInlineBoxes(snapshot),
            box => GetText(box) == paragraphText);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(paragraph.TextLayout);
        Assert.Equal(paragraphText, paragraph.LayoutBuffer);

        foreach (string expectation in expectations)
        {
            int separator = expectation.LastIndexOf(':');
            string text = expectation[..separator];
            string flags = expectation[(separator + 1)..];
            int start = paragraphText.IndexOf(text, StringComparison.Ordinal);
            Assert.True(start >= 0 && paragraphText.IndexOf(text, start + 1, StringComparison.Ordinal) < 0);

            for (int index = start; index < start + text.Length; index++)
            {
                ElementStyle style = ResolveStyleAt(paragraph, index);
                ushort weight = flags.Contains('B') ? BoldWeight : NormalWeight;
                Windows.UI.Text.FontStyle fontStyle = flags.Contains('I')
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal;
                string family = flags.Contains('C') ? CodeFamily : BodyFamily;
                string where = $"'{text}' character {index - start}";

                Assert.True(weight == style.FontWeight.Weight, $"{where}: run weight {style.FontWeight.Weight}");
                Assert.True(fontStyle == style.FontStyle, $"{where}: run style {style.FontStyle}");
                Assert.True(flags.Contains('S') == style.Strikethrough, $"{where}: strikethrough");
                Assert.True(flags.Contains('U') == style.Underline, $"{where}: underline");
                Assert.Equal(family, style.FontFamily);

                Assert.True(weight == layout.GetFontWeight(index).Weight, $"{where}: layout weight {layout.GetFontWeight(index).Weight}");
                Assert.True(fontStyle == layout.GetFontStyle(index), $"{where}: layout style {layout.GetFontStyle(index)}");
                Assert.Equal(CanvasFontFamilyReference.ForCanvas(family), layout.GetFontFamily(index));
            }
        }

        // Copy, UIA and caret text are unchanged by splitting the runs.
        Assert.Contains(paragraphText, snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        AssertEveryCharacterMapsToItsSource(snapshot, paragraph);
    }

    // Selection, hit testing and copy map each rendered character back to its
    // authored character. A character at a run edge may also take the
    // adjacent Markdown delimiters (and a link's destination), so source
    // copy keeps the syntax whole.
    private static void AssertEveryCharacterMapsToItsSource(LayoutSnapshot snapshot, InlineContainerBox box)
    {
        foreach (InlineRun run in box.Runs)
        {
            for (int offset = 0; offset < run.RenderedLength; offset++)
            {
                var range = new DocumentRange(
                    new DocumentPosition(box.BlockIndex, run.InlineIndex, offset),
                    new DocumentPosition(box.BlockIndex, run.InlineIndex, offset + 1));
                Assert.True(snapshot.SourceMap.TryMapRange(range, out _));
                string slice = snapshot.SourceMap.Slice(range);
                int destination = slice.IndexOf("](", StringComparison.Ordinal);
                if (destination >= 0)
                    slice = slice[..destination];
                Assert.Equal(run.Text[offset].ToString(), slice.Trim('*', '_', '~', '`', '[', ']'));
            }
        }
    }
}
