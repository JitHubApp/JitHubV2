using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SemanticRangeFingerprintBatchingTests
{
    [Fact]
    public void BatchingPreservesTheLegacyNodeThenSpanSha256ByteStreamAcrossFlushBoundaries()
    {
        const int nodeCount = 131;
        const int spanCount = 160;
        string text = new('x', spanCount * 8 + 16);
        MarkdownLayoutContext context = CreateContext(text);
        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = 37,
        };

        MarkdownSemanticNode root = CreateSemanticTree(inline, text, nodeCount);
        List<MarkdownTextSpan> spans = CreateSpans(inline, spanCount);

        MarkdownSemanticDocument document = CreateDocument(text, root, spans);

        // The pre-batching implementation emitted one 64-byte node record or
        // one 52-byte span record per AppendData call. Compare its exact byte
        // stream rather than only checking repeatability of the new code.
        MarkdownAutomationTextFingerprint legacyFingerprint = LegacySemanticRangeFingerprint.Compute(
            root,
            spans,
            text.Length);

        Assert.Equal(legacyFingerprint, document.SemanticRangeFingerprint);
        Assert.True(nodeCount * 64 > 2 * 4 * 1024);
        Assert.True(spanCount * 52 > 2 * 4 * 1024);
        Assert.NotEqual(0, nodeCount * 64 % (4 * 1024));
    }

    private static MarkdownSemanticNode CreateSemanticTree(InlineContainerBox inline, string text, int nodeCount)
    {
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document, inline)
        {
            InlineBox = inline,
            TextStart = 0,
            TextEnd = text.Length,
        };
        MarkdownSemanticRole[] roles =
        [
            MarkdownSemanticRole.Group,
            MarkdownSemanticRole.Paragraph,
            MarkdownSemanticRole.Heading,
            MarkdownSemanticRole.CodeBlock,
            MarkdownSemanticRole.Link,
            MarkdownSemanticRole.List,
            MarkdownSemanticRole.ListItem,
            MarkdownSemanticRole.Table,
            MarkdownSemanticRole.TableCell,
            MarkdownSemanticRole.Image,
            MarkdownSemanticRole.Math,
            MarkdownSemanticRole.Diagram,
            MarkdownSemanticRole.Abbreviation,
        ];

        for (int index = 1; index < nodeCount; index++)
        {
            int start = index * 3 % text.Length;
            int end = System.Math.Min(text.Length, start + index % 19);
            var run = new TextRun($"node-{index}")
            {
                InlineIndex = index,
                SourceSpan = new SourceSpan(start, end - start),
            };
            var node = new MarkdownSemanticNode(roles[index % roles.Length], index % 3 == 0 ? inline : null)
            {
                InlineBox = index % 2 == 0 ? inline : null,
                InlineRun = index % 4 == 0 ? run : null,
                TextStart = start,
                TextEnd = end,
                HeadingLevel = index % 7,
                Row = index % 11,
                Column = index % 5,
                AccessibilityRole = (MarkdownAccessibilityRole)(index % 16),
            };

            if ((index & 1) == 0)
            {
                root.Add(node);
                if (index % 6 == 0)
                {
                    node.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Link, inline)
                    {
                        InlineBox = inline,
                        InlineRun = run,
                        TextStart = start,
                        TextEnd = end,
                        Row = index,
                        Column = index % 9,
                    });
                }
            }
            else
            {
                MarkdownSemanticNode parent = root.Children.Count == 0
                    ? root
                    : root.Children[^1];
                parent.Add(node);
            }
        }

        return root;
    }

    private static List<MarkdownTextSpan> CreateSpans(InlineContainerBox inline, int spanCount)
    {
        var spans = new List<MarkdownTextSpan>(spanCount);
        for (int index = 0; index < spanCount; index++)
        {
            int start = index * 8;
            var run = new TextRun($"span-{index}")
            {
                InlineIndex = index,
                SourceSpan = new SourceSpan(start, 5),
            };
            spans.Add(new MarkdownTextSpan(
                start,
                start + 5,
                index % 4 == 0 ? inline : null,
                index % 3 == 0 ? run : null,
                null,
                null,
                null,
                VectorSceneBox: null,
                VectorSemanticIndex: index % 7 == 0 ? index : -1));
        }

        return spans;
    }

    private static MarkdownSemanticDocument CreateDocument(
        string text,
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans)
        => new(root, text, spans);

    private static MarkdownLayoutContext CreateContext(string source)
    {
        Color color = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle> { [MarkdownElementKeys.Body] = new ElementStyle() },
            new Dictionary<string, ElementStyleOverride>(),
            color,
            color,
            color,
            color,
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
