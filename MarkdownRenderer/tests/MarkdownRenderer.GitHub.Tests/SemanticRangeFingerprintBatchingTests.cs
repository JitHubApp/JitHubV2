using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
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

        MarkdownSemanticDocument document = CreateDocument(text, root, spans);

        // The pre-batching implementation emitted one 64-byte node record or
        // one 52-byte span record per AppendData call. Compare its exact byte
        // stream rather than only checking repeatability of the new code.
        MarkdownAutomationTextFingerprint legacyFingerprint = ComputeLegacyFingerprint(
            root,
            spans,
            text.Length);

        Assert.Equal(legacyFingerprint, document.SemanticRangeFingerprint);
        Assert.True(nodeCount * 64 > 2 * 4 * 1024);
        Assert.True(spanCount * 52 > 2 * 4 * 1024);
        Assert.NotEqual(0, nodeCount * 64 % (4 * 1024));
    }

    private static MarkdownAutomationTextFingerprint ComputeLegacyFingerprint(
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans,
        int textLength)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[64];
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
            record.Clear();
            record[0] = 0x4E;
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], (int)node.Role);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], node.TextStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], node.TextEnd);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], node.Box?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], node.InlineBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], node.ImageBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], node.EmbedBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], node.HostedElementBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[36..], node.VectorSceneBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], node.InlineRun?.InlineIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], node.VectorSemanticIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], node.HeadingLevel);
            BinaryPrimitives.WriteInt32LittleEndian(record[52..], node.Row);
            BinaryPrimitives.WriteInt32LittleEndian(record[56..], node.Column);
            BinaryPrimitives.WriteInt32LittleEndian(record[60..], (int)node.AccessibilityRole);
            hash.AppendData(record);
        }

        foreach (MarkdownTextSpan span in spans)
        {
            record.Clear();
            record[0] = 0x53;
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], span.TextStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], span.TextEnd);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], span.InlineBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], span.InlineRun?.InlineIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], span.ImageBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], span.EmbedBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], span.HostedElementBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], span.VectorSceneBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[36..], span.VectorSemanticIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], span.InlineRun?.SourceSpan.Start ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], span.InlineRun?.SourceSpan.Length ?? -1);
            int kindFlags =
                (span.InlineBox is null ? 0 : 1) |
                (span.InlineRun is null ? 0 : 2) |
                (span.ImageBox is null ? 0 : 4) |
                (span.EmbedBox is null ? 0 : 8) |
                (span.HostedElementBox is null ? 0 : 16) |
                (span.VectorSceneBox is null ? 0 : 32);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], kindFlags);
            hash.AppendData(record[..52]);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return MarkdownAutomationTextFingerprint.FromSha256(textLength, digest);
    }

    private static MarkdownSemanticDocument CreateDocument(
        string text,
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans)
    {
        ConstructorInfo constructor = typeof(MarkdownSemanticDocument).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(MarkdownSemanticNode), typeof(string), typeof(IReadOnlyList<MarkdownTextSpan>)],
            modifiers: null)!;
        Assert.NotNull(constructor);
        return (MarkdownSemanticDocument)constructor.Invoke([root, text, spans]);
    }

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
