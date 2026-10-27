using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class FrozenSemanticSpanTests
{
    [Fact]
    public void SemanticDocumentUsesFrozenOrderedSpansAndPreservesFirstPositionWinner()
    {
        MarkdownLayoutContext context = CreateContext("firstsecond");
        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var first = new TextRun("first");
        var second = new TextRun("second");
        inline.Add(first);
        inline.Add(second);

        // A malformed duplicate position is intentionally retained as a
        // regression input: span order keeps both runs, while the position
        // lookup must continue to resolve to the first span.
        second.InlineIndex = first.InlineIndex;

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { inline },
            context.SourceMap,
            width: 320,
            height: 80);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        IReadOnlyList<MarkdownTextSpan> spans = document.TextSpans;

        Assert.Equal("firstsecond", document.Text);
        Assert.Equal(2, spans.Count);
        Assert.False(spans is MarkdownTextSpan[]);
        Assert.False(spans is IList<MarkdownTextSpan>);
        Assert.False(spans is ICollection<MarkdownTextSpan>);

        MarkdownTextSpan firstSpan = spans[0];
        MarkdownTextSpan secondSpan = spans[1];
        Assert.Equal((0, 5), (firstSpan.TextStart, firstSpan.TextEnd));
        Assert.Equal((5, 11), (secondSpan.TextStart, secondSpan.TextEnd));
        Assert.Same(inline, firstSpan.InlineBox);
        Assert.Same(first, firstSpan.InlineRun);
        Assert.Same(inline, secondSpan.InlineBox);
        Assert.Same(second, secondSpan.InlineRun);

        var enumerated = new List<MarkdownTextSpan>();
        foreach (MarkdownTextSpan span in spans)
            enumerated.Add(span);
        Assert.Equal(spans.Count, enumerated.Count);
        for (int index = 0; index < spans.Count; index++)
        {
            Assert.Equal(spans[index].TextStart, enumerated[index].TextStart);
            Assert.Equal(spans[index].TextEnd, enumerated[index].TextEnd);
            Assert.Same(spans[index].InlineBox, enumerated[index].InlineBox);
            Assert.Same(spans[index].InlineRun, enumerated[index].InlineRun);
        }

        Assert.Equal(
            firstSpan.TextStart + 2,
            document.TextOffsetFromDocumentPosition(new DocumentPosition(inline.BlockIndex, first.InlineIndex, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = spans[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = spans[spans.Count]);
    }

    private static MarkdownLayoutContext CreateContext(string source)
    {
        Color black = Color.FromArgb(255, 0, 0, 0);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            black,
            black,
            black,
            black,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);

        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
