using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
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

/// <summary>
/// Protects Builder-time semantic lookup-index construction against the former
/// post-build depth-first index builder. The independent oracle deliberately
/// uses the old traversal and first-wins rules.
/// </summary>
public sealed class SemanticNodeIndexConstructionTests
{
    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void BuilderIndexesMatchLegacyPreorderAndPreserveTextAndRangeFingerprints(
        FlowDirection flowDirection)
    {
        using LayoutSnapshot snapshot = CreateMixedSnapshot(flowDirection);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        OracleIndexes expected = BuildLegacyIndexes(document.Root);

        AssertIndexesMatch(document, expected);
        Assert.Equal(
            "Title linked photo \uFFFC abbr (expanded) x\n" +
            "paragraph text\n" +
            "var x = 1;\n" +
            "poster\n" +
            "nested group\n" +
            "1.\n" +
            "list item\n" +
            "table head\n" +
            "table cell\n" +
            "diagram\nnode label",
            document.Text);
        Assert.Equal(
            MarkdownAutomationTextFingerprint.Compute(document.Text),
            document.TextProjectionFingerprint);
        Assert.Equal(
            ComputeLegacyRangeFingerprint(document.Root, document.TextSpans, document.Text.Length),
            document.SemanticRangeFingerprint);

        MarkdownSemanticRole[] roles = MarkdownSemanticDocument.EnumerateDepthFirst(document.Root)
            .Select(node => node.Role)
            .Distinct()
            .ToArray();
        Assert.Contains(MarkdownSemanticRole.Document, roles);
        Assert.Contains(MarkdownSemanticRole.Group, roles);
        Assert.Contains(MarkdownSemanticRole.Paragraph, roles);
        Assert.Contains(MarkdownSemanticRole.Heading, roles);
        Assert.Contains(MarkdownSemanticRole.CodeBlock, roles);
        Assert.Contains(MarkdownSemanticRole.Link, roles);
        Assert.Contains(MarkdownSemanticRole.List, roles);
        Assert.Contains(MarkdownSemanticRole.ListItem, roles);
        Assert.Contains(MarkdownSemanticRole.Table, roles);
        Assert.Contains(MarkdownSemanticRole.TableCell, roles);
        Assert.Contains(MarkdownSemanticRole.Image, roles);
        Assert.Contains(MarkdownSemanticRole.Math, roles);
        Assert.Contains(MarkdownSemanticRole.Diagram, roles);
        Assert.Contains(MarkdownSemanticRole.Embed, roles);
        Assert.Contains(MarkdownSemanticRole.Abbreviation, roles);

        // A cell wrapper aliases its paragraph's inline box but must not own
        // that paragraph's text index. Horizontal overflow belongs to the
        // table itself; InlineContainerBox does not implement that interface.
        MarkdownSemanticNode table = Assert.Single(document.Root.Children,
            node => node.Role == MarkdownSemanticRole.Table);
        MarkdownSemanticNode cell = table.Children[0];
        InlineContainerBox cellBox = Assert.IsType<InlineContainerBox>(cell.Box);
        MarkdownSemanticNode paragraph = Assert.Single(cell.Children);
        Assert.Equal(MarkdownSemanticRole.Paragraph, paragraph.Role);
        TableBox tableBox = Assert.IsType<TableBox>(table.Box);
        Assert.Same(table, expected.ByHorizontalOverflow[tableBox]);
        Assert.True(document.TryGetHorizontalOverflowNode(tableBox, out MarkdownSemanticNode indexedTable));
        Assert.Same(table, indexedTable);
        Assert.True(document.TryGetInlineContainerNode(cellBox, out MarkdownSemanticNode indexedParagraph));
        Assert.Same(paragraph, indexedParagraph);

        // Code blocks, tables and vector scenes implement horizontal overflow;
        // oracle parity above verifies every exact first-wins identity.
        Assert.Contains(expected.ByHorizontalOverflow.Keys, box => box is CodeBlockBox);
        Assert.Contains(expected.ByHorizontalOverflow.Keys, box => box is TableBox);
        Assert.Contains(expected.ByHorizontalOverflow.Keys, box => box is VectorSceneBox);
    }

    [Fact]
    public void RepeatedInlineBoxOccurrenceDoesNotMakeItsBlockIndexAmbiguous()
    {
        MarkdownLayoutContext context = CreateContext(new string('x', 64), FlowDirection.LeftToRight);
        var sharedCell = new InlineContainerBox(context, MarkdownElementKeys.TableCell)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        sharedCell.Add(new TextRun("shared cell"));
        var table = new TableBox(
            context,
            [[sharedCell]],
            [[sharedCell]])
        {
            BlockIndex = context.NextBlockIndex(),
        };

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { table },
            context.SourceMap,
            width: 320,
            height: 120);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        OracleIndexes expected = BuildLegacyIndexes(document.Root);
        AssertIndexesMatch(document, expected);

        Dictionary<InlineContainerBox, MarkdownSemanticNode> byInlineBox = GetInlineBoxMap(document);
        Dictionary<int, MarkdownSemanticNode> byBlockIndex = GetInlineBlockMap(document);
        Assert.Single(byInlineBox);
        Assert.True(byBlockIndex.TryGetValue(sharedCell.BlockIndex, out MarkdownSemanticNode? indexed));
        Assert.Same(expected.ByInlineBox[sharedCell], indexed);
        Assert.True(document.TryGetInlineContainerNode(sharedCell, out MarkdownSemanticNode queried));
        Assert.Same(indexed, queried);
    }

    [Fact]
    public void DistinctInlineBoxesWithDuplicateBlockIndicesRemainAmbiguous()
    {
        MarkdownLayoutContext context = CreateContext(new string('x', 64), FlowDirection.RightToLeft);
        int duplicateIndex = context.NextBlockIndex();
        InlineContainerBox first = Paragraph(context, "first paragraph", duplicateIndex);
        InlineContainerBox second = Paragraph(context, "second paragraph", duplicateIndex);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            context.SourceMap,
            width: 320,
            height: 120);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        OracleIndexes expected = BuildLegacyIndexes(document.Root);
        AssertIndexesMatch(document, expected);

        Dictionary<int, MarkdownSemanticNode> byBlockIndex = GetInlineBlockMap(document);
        Assert.False(byBlockIndex.ContainsKey(duplicateIndex));
        Assert.True(byBlockIndex.EnsureCapacity(0) >= 2);
        MarkdownSemanticNode firstNode = expected.ByInlineBox[first];
        MarkdownSemanticNode secondNode = expected.ByInlineBox[second];
        Assert.NotSame(firstNode, secondNode);
        Assert.Same(firstNode, document.GetEnclosingNodeForTextRangeByBlockIndex(
            firstNode.TextStart, firstNode.TextEnd, duplicateIndex));
        Assert.Same(secondNode, document.GetEnclosingNodeForTextRangeByBlockIndex(
            secondNode.TextStart, secondNode.TextEnd, duplicateIndex));
    }

    [Fact]
    public void ImageOnlyDocumentKeepsEverySemanticLookupIndexEmpty()
    {
        MarkdownLayoutContext context = CreateContext(new string('x', 64), FlowDirection.LeftToRight);
        var images = new ImageBox[32];
        for (int index = 0; index < images.Length; index++)
        {
            images[index] = new ImageBox(context, string.Empty, $"image {index}")
            {
                BlockIndex = context.NextBlockIndex(),
            };
        }

        using var snapshot = new LayoutSnapshot(images, context.SourceMap, width: 320, height: 240);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        Assert.Empty(GetInlineBoxMap(document));
        Assert.Empty(GetInlineBlockMap(document));
        Assert.Empty(GetHorizontalOverflowMap(document));
        Assert.Empty(GetSpanMap(document));
        Assert.Equal(0, GetInlineBoxMap(document).EnsureCapacity(0));
        Assert.Equal(0, GetInlineBlockMap(document).EnsureCapacity(0));
        Assert.Equal(0, GetHorizontalOverflowMap(document).EnsureCapacity(0));
        Assert.Equal(0, GetSpanMap(document).EnsureCapacity(0));
    }

    private static LayoutSnapshot CreateMixedSnapshot(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(new string('x', 1024), flowDirection);

        var heading = Paragraph(context, MarkdownElementKeys.Heading1, "Title ");
        heading.Add(new LinkRun("linked", "https://example.test/link"));
        heading.Add(new TextRun(" "));
        heading.Add(new InlineImageRun(context, "photo", "https://example.test/photo.png"));
        heading.Add(new TextRun(" "));
        heading.Add(new InlineEmbedRun(24, 18, static () => throw new InvalidOperationException()));
        heading.Add(new TextRun(" "));
        heading.Add(new AbbreviationRun("abbr", "expanded"));
        heading.Add(new TextRun(" "));
        var simpleMathScene = new MarkdownVectorScene(12, 8, 6, Array.Empty<MarkdownVectorCommand>());
        heading.Add(new InlineVectorSceneRun(context, simpleMathScene, "x", "x", null, MarkdownElementKeys.Math));

        InlineContainerBox paragraph = Paragraph(context, "paragraph text");

        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, 10),
            "var x = 1;",
            "csharp",
            new Dictionary<string, string>());
        var codeBlock = new CodeBlockBox(context, metadata, "var x = 1;", false, false)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var codeChunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        codeChunk.Add(new TextRun("var x = 1;"));
        codeBlock.AddChunk(codeChunk);

        var poster = new ImageBox(context, string.Empty, "poster")
        {
            BlockIndex = context.NextBlockIndex(),
        };

        var group = new StackBox();
        group.Add(Paragraph(context, "nested group"));

        var marker = Paragraph(context, MarkdownElementKeys.ListMarker, "1.");
        var listContent = new StackBox();
        listContent.Add(Paragraph(context, "list item"));
        var listItem = new ListItemBox(marker, listContent, markerWidth: 24)
        {
            FlowDirection = flowDirection,
            BlockIndex = context.NextBlockIndex(),
        };
        var list = new StackBox();
        list.Add(listItem);

        var headerCell = Paragraph(context, MarkdownElementKeys.TableCell, "table head");
        var bodyCell = Paragraph(context, MarkdownElementKeys.TableCell, "table cell");
        var table = new TableBox(context, [[headerCell]], [[bodyCell]])
        {
            BlockIndex = context.NextBlockIndex(),
        };

        var diagramScene = new MarkdownVectorScene(
            20,
            12,
            10,
            Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 12,
            viewport: new MarkdownVectorRectangle(0, 0, 20, 12),
            semantics:
            [
                new(1, MarkdownVectorSemanticRole.Node, "diagram", null, -1,
                    new SourceSpan(0, 0), new MarkdownVectorRectangle(0, 0, 12, 8)),
                new(2, MarkdownVectorSemanticRole.Label, "node label", null, 0,
                    new SourceSpan(0, 0), new MarkdownVectorRectangle(1, 1, 8, 4)),
            ],
            links: []);
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            diagramScene,
            new SourceSpan(0, 0),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "diagram",
            semanticText: "diagram");
        var vector = new VectorSceneBox(
            context,
            Assert.Single(content.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };

        return new LayoutSnapshot(
            new BlockBox[] { heading, paragraph, codeBlock, poster, group, list, table, vector },
            context.SourceMap,
            width: 420,
            height: 640);
    }

    private static InlineContainerBox Paragraph(
        MarkdownLayoutContext context,
        string text,
        int? blockIndex = null)
        => Paragraph(context, MarkdownElementKeys.Body, text, blockIndex);

    private static InlineContainerBox Paragraph(
        MarkdownLayoutContext context,
        string elementKey,
        string text,
        int? blockIndex = null)
    {
        var inline = new InlineContainerBox(context, elementKey)
        {
            BlockIndex = blockIndex ?? context.NextBlockIndex(),
        };
        inline.Add(new TextRun(text));
        return inline;
    }

    private static OracleIndexes BuildLegacyIndexes(MarkdownSemanticNode root)
    {
        var byInlineBox = new Dictionary<InlineContainerBox, MarkdownSemanticNode>();
        var byHorizontalOverflow = new Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode>(
            ReferenceEqualityComparer.Instance);
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
            if (node.InlineBox is { } inline &&
                node.InlineRun is null &&
                node.Role is MarkdownSemanticRole.Paragraph or
                    MarkdownSemanticRole.Heading or
                    MarkdownSemanticRole.CodeBlock)
            {
                byInlineBox.TryAdd(inline, node);
            }

            if (node.VectorSemanticIndex < 0 && node.Box is IHorizontalOverflowBox overflow)
                byHorizontalOverflow.TryAdd(overflow, node);
        }

        var byInlineBlockIndex = new Dictionary<int, MarkdownSemanticNode>(byInlineBox.Count);
        var ambiguousBlockIndices = new HashSet<int>();
        foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in byInlineBox)
        {
            if (!byInlineBlockIndex.TryAdd(entry.Key.BlockIndex, entry.Value))
                ambiguousBlockIndices.Add(entry.Key.BlockIndex);
        }
        foreach (int blockIndex in ambiguousBlockIndices)
            byInlineBlockIndex.Remove(blockIndex);

        return new OracleIndexes(byInlineBox, byInlineBlockIndex, byHorizontalOverflow);
    }

    private static void AssertIndexesMatch(MarkdownSemanticDocument document, OracleIndexes expected)
    {
        Dictionary<InlineContainerBox, MarkdownSemanticNode> actualInline = GetInlineBoxMap(document);
        Dictionary<int, MarkdownSemanticNode> actualBlocks = GetInlineBlockMap(document);
        Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> actualHorizontal = GetHorizontalOverflowMap(document);

        AssertDictionaryIdentityEquals(expected.ByInlineBox, actualInline);
        AssertDictionaryIdentityEquals(expected.ByInlineBlockIndex, actualBlocks);
        AssertDictionaryIdentityEquals(expected.ByHorizontalOverflow, actualHorizontal);

        foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in expected.ByInlineBox)
        {
            Assert.True(document.TryGetInlineContainerNode(entry.Key, out MarkdownSemanticNode actual));
            Assert.Same(entry.Value, actual);
        }

        foreach (KeyValuePair<IHorizontalOverflowBox, MarkdownSemanticNode> entry in expected.ByHorizontalOverflow)
        {
            Assert.True(document.TryGetHorizontalOverflowNode(entry.Key, out MarkdownSemanticNode actual));
            Assert.Same(entry.Value, actual);
        }

        int[] scopeBlockIndices = MarkdownSemanticDocument.EnumerateDepthFirst(document.Root)
            .Select(node => node.InlineBox?.BlockIndex ?? node.Box?.BlockIndex ?? -1)
            .Append(int.MinValue)
            .Distinct()
            .ToArray();
        foreach (MarkdownSemanticNode rangeNode in MarkdownSemanticDocument.EnumerateDepthFirst(document.Root))
        {
            int textStart = System.Math.Clamp(rangeNode.TextStart, 0, document.Text.Length);
            int textEnd = System.Math.Clamp(rangeNode.TextEnd, textStart, document.Text.Length);
            foreach (int scopeBlockIndex in scopeBlockIndices)
            {
                MarkdownSemanticNode expectedNode =
                    expected.ByInlineBlockIndex.TryGetValue(scopeBlockIndex, out MarkdownSemanticNode? scoped) &&
                    textStart == scoped.TextStart &&
                    textEnd == scoped.TextEnd
                        ? scoped
                        : LinearEnclosing(document.Root, textStart, textEnd, document.Text.Length);
                MarkdownSemanticNode actualNode = document.GetEnclosingNodeForTextRangeByBlockIndex(
                    rangeNode.TextStart,
                    rangeNode.TextEnd,
                    scopeBlockIndex);
                Assert.Same(expectedNode, actualNode);
            }
        }
    }

    private static void AssertDictionaryIdentityEquals<TKey>(
        Dictionary<TKey, MarkdownSemanticNode> expected,
        Dictionary<TKey, MarkdownSemanticNode> actual)
        where TKey : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<TKey, MarkdownSemanticNode> entry in expected)
        {
            Assert.True(actual.TryGetValue(entry.Key, out MarkdownSemanticNode? actualNode));
            Assert.Same(entry.Value, actualNode);
        }
    }

    private static MarkdownSemanticNode LinearEnclosing(
        MarkdownSemanticNode root,
        int start,
        int end,
        int textLength)
    {
        MarkdownSemanticNode current = root;
        while (true)
        {
            MarkdownSemanticNode? next = current.Children.FirstOrDefault(child => start == end
                ? child.TextStart <= start &&
                  (start < child.TextEnd || start == textLength && start == child.TextEnd)
                : child.TextStart <= start && end <= child.TextEnd);
            if (next is null)
                return current;
            current = next;
        }
    }

    private static MarkdownAutomationTextFingerprint ComputeLegacyRangeFingerprint(
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

    private static Dictionary<InlineContainerBox, MarkdownSemanticNode> GetInlineBoxMap(
        MarkdownSemanticDocument document)
        => GetMap<InlineContainerBox, MarkdownSemanticNode>(document, "_nodeByInlineBox");

    private static Dictionary<int, MarkdownSemanticNode> GetInlineBlockMap(
        MarkdownSemanticDocument document)
        => GetMap<int, MarkdownSemanticNode>(document, "_nodeByInlineBlockIndex");

    private static Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> GetHorizontalOverflowMap(
        MarkdownSemanticDocument document)
        => GetMap<IHorizontalOverflowBox, MarkdownSemanticNode>(document, "_nodeByHorizontalOverflow");

    private static Dictionary<(int BlockIndex, int InlineIndex), int> GetSpanMap(
        MarkdownSemanticDocument document)
        => GetMap<(int BlockIndex, int InlineIndex), int>(document, "_spanByPosition");

    private static Dictionary<TKey, TValue> GetMap<TKey, TValue>(
        MarkdownSemanticDocument document,
        string fieldName)
        where TKey : notnull
    {
        FieldInfo field = typeof(MarkdownSemanticDocument).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<Dictionary<TKey, TValue>>(field.GetValue(document));
    }

    private static MarkdownLayoutContext CreateContext(string source, FlowDirection flowDirection)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Heading1] = style,
            [MarkdownElementKeys.CodeBlock] = style,
            [MarkdownElementKeys.TableCell] = style,
            [MarkdownElementKeys.ListMarker] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Math] = style,
            [MarkdownElementKeys.Link] = style,
        };
        Color black = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
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
            flowDirection);
    }

    private sealed record OracleIndexes(
        Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox,
        Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex,
        Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow);
}
