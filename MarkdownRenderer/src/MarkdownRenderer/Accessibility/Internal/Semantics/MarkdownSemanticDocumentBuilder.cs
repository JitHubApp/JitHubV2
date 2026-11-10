using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Document.Internal;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Theming;
using static MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticInlineText;
using static MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticNodeFactory;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>
/// Builds the semantic document of a layout snapshot in one depth-first pass
/// that appends each node's accessible text and text spans in document order.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because each box kind's node, text and
/// spans are appended in the same pass, against the one text projection and
/// span buffer whose offsets every later node depends on. Node index
/// registration lives in <see cref="MarkdownSemanticNodeIndexRegistry"/> and
/// vector scene semantics in <see cref="MarkdownSemanticVectorSceneBuilder"/>.
/// </remarks>
internal sealed class MarkdownSemanticDocumentBuilder
{
    private readonly SemanticTextProjectionAccumulator _text;
    private readonly LayoutSnapshotIndexBuilder? _layoutIndexes;
    private readonly CancellationToken _cancellationToken;
    // Spans are now inline values rather than per-span objects. Use paged
    // growth to avoid repeatedly copying a large value array as the semantic
    // graph is built, then freeze once into the snapshot's stable span list.
    private readonly PagedEntryBuffer<MarkdownTextSpan> _spans = new();
    private readonly MarkdownSemanticNodeIndexRegistry _nodeIndexes = new();
    private readonly MarkdownSemanticVectorSceneBuilder _vectorScenes;
    private int _listDepth;

    public MarkdownSemanticDocumentBuilder(
        string? expectedTextProjection,
        LayoutSnapshotIndexBuilder? layoutIndexes,
        CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _text = new SemanticTextProjectionAccumulator(expectedTextProjection, cancellationToken);
        _layoutIndexes = layoutIndexes;
        _vectorScenes = new MarkdownSemanticVectorSceneBuilder(_text, _spans, layoutIndexes, _nodeIndexes);
    }

    public MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
    {
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 0
        };
        _nodeIndexes.Register(root);

        for (int topLevelOrdinal = 0; topLevelOrdinal < snapshot.Blocks.Count; topLevelOrdinal++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _layoutIndexes?.BeginTopLevel(topLevelOrdinal);
            try
            {
                var node = BuildBlock(snapshot.Blocks[topLevelOrdinal]);
                if (node is not null) root.Add(node);
            }
            finally
            {
                _layoutIndexes?.EndTopLevel();
            }
        }

        int trimmedTrailingNewlines = TrimTrailingNewline();
        root.TextEnd = _text.Length;
        if (trimmedTrailingNewlines > 0)
            ClampTrailingTextSpans(_text.Length);
        string text = _text.GetText();
        MarkdownSemanticTextProjectionReuse? matchedProjection =
            textProjectionReuse is { } projection && ReferenceEquals(text, projection.Text)
                ? projection
                : null;
        return new MarkdownSemanticDocument(
            root,
            text,
            _spans.Freeze(),
            matchedProjection,
            _nodeIndexes.ToIndexes());
    }

    private void AddRegisteredChild(
        MarkdownSemanticNode parent,
        MarkdownSemanticNode child)
    {
        _nodeIndexes.Register(child);
        parent.Add(child);
    }

    private MarkdownSemanticNode? BuildBlock(BlockBox box)
    {
        if (_layoutIndexes is null)
            return BuildBlockCore(box);

        LayoutSnapshotIndexBuilder.Scope scope = _layoutIndexes.EnterBlock(box);
        try
        {
            return BuildBlockCore(box);
        }
        finally
        {
            _layoutIndexes.ExitBlock(box, scope);
        }
    }

    private MarkdownSemanticNode? BuildBlockCore(BlockBox box)
    {
        return box switch
        {
            InlineContainerBox inline => BuildInline(inline),
            CodeBlockBox codeBlock => BuildCodeBlock(codeBlock),
            ImageBox image => BuildImage(image),
            EmbedBox embed => BuildEmbed(embed),
            DeclarativeHostedElementBox hosted => BuildHostedElement(hosted),
            VectorSceneBox vector => BuildVectorScene(vector),
            ListItemBox listItem => BuildListItem(listItem),
            TableBox table => BuildTable(table),
            StackBox stack when IsListStack(stack) => BuildList(stack),
            StackBox stack => BuildGroup(stack),
            _ => null,
        };
    }

    private MarkdownSemanticNode BuildInline(InlineContainerBox inline)
    {
        MarkdownSemanticNode node = CreateInlineNode(inline);
        _nodeIndexes.Register(node);

        MarkdownSemanticNode? flattenedHeading = null;
        MarkdownSemanticNode? flattenedCodeBlock = null;

        for (int runIndex = 0; runIndex < inline.Runs.Count; runIndex++)
        {
            InlineRun run = inline.Runs[runIndex];
            // The layout image index includes decorative images even when
            // semantic/UIA construction intentionally omits their runs.
            _layoutIndexes?.ObserveInlineRun(inline, run);
            // Inert spacers keep a one-character private source/layout
            // mapping, but are neither selectable text nor UIA content.
            if (run is InlineSpacerRun)
                continue;

            // A plain literal that is still an exact view over this
            // document's immutable source contributes the same semantic
            // text without first allocating a per-run substring.
            SourceSliceTextRun? sourceSlice = run is SourceSliceTextRun candidateSlice &&
                IsExactSourceSlice(inline, run, candidateSlice)
                    ? candidateSlice
                    : null;
            string? accessibleText = sourceSlice is null
                ? GetInlineAccessibleText(run, inline.Context)
                : null;
            int accessibleLength = sourceSlice?.SliceLength ?? accessibleText!.Length;

            // An unlinked image with empty alternative text is decorative. It must
            // contribute neither a text span nor an element to the UIA document.
            if (run is InlineImageRun { IsLinked: false } && accessibleLength == 0)
                continue;

            bool atomic = IsAtomicInline(run);
            if (atomic &&
                accessibleLength > 0 &&
                _text.Length > node.TextStart &&
                NeedsSemanticBoundary(_text[^1], accessibleText![0]))
            {
                AppendInlineSemanticBoundary(inline, run);
            }

            (int runStart, int runEnd) = AppendInlineRunText(inline, run, sourceSlice, accessibleText);
            flattenedHeading = TrackFlattenedHeading(node, inline, run, runStart, runEnd, flattenedHeading);
            flattenedCodeBlock = TrackFlattenedCodeBlock(node, inline, run, runStart, runEnd, flattenedCodeBlock);

            MarkdownSemanticNode semanticParent = flattenedCodeBlock ?? flattenedHeading ?? node;
            if (CreateInlineRunNode(inline, run, runStart, runEnd) is { } child)
                AddRegisteredChild(semanticParent, child);

            if (atomic &&
                accessibleLength > 0 &&
                TryGetNextInlineFirstCharacter(inline, runIndex + 1, out char nextCharacter) &&
                NeedsSemanticBoundary(accessibleText![^1], nextCharacter))
            {
                AppendInlineSemanticBoundary(inline, run);
            }
        }
        node.TextEnd = _text.Length;

        AppendBlockSeparator();
        return node;
    }

    private MarkdownSemanticNode CreateInlineNode(InlineContainerBox inline)
    {
        var role = inline.ElementKey == MarkdownElementKeys.CodeBlock
            ? MarkdownSemanticRole.CodeBlock
            : IsHeading(inline.ElementKey)
                ? MarkdownSemanticRole.Heading
                : MarkdownSemanticRole.Paragraph;
        return new MarkdownSemanticNode(role, inline)
        {
            InlineBox = inline,
            TextStart = _text.Length,
            HeadingLevel = GetHeadingLevel(inline.ElementKey),
            CodeLanguage = inline.CodeLanguage,
            HelpText = inline.ElementKey == MarkdownElementKeys.CodeBlock && !string.IsNullOrWhiteSpace(inline.CodeLanguage)
                ? inline.Context.ResolveFormattedString(
                    MarkdownStringKeys.CodeLanguageHelp,
                    MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                    inline.CodeLanguage)
                : null,
        };
    }

    private (int Start, int End) AppendInlineRunText(
        InlineContainerBox inline,
        InlineRun run,
        SourceSliceTextRun? sourceSlice,
        string? accessibleText)
    {
        int runStart = _text.Length;
        if (sourceSlice is not null)
            _text.AppendSlice(sourceSlice.SourceText, sourceSlice.SourceStart, sourceSlice.SliceLength);
        else
            _text.Append(accessibleText);
        int runEnd = _text.Length;
        _spans.Add(new MarkdownTextSpan(runStart, runEnd, inline, run, null, null, null));
        return (runStart, runEnd);
    }

    /// <summary>
    /// Groups consecutive runs flattened from one heading level into a single
    /// heading child, and returns the open heading (or null after any other run).
    /// </summary>
    private MarkdownSemanticNode? TrackFlattenedHeading(
        MarkdownSemanticNode node,
        InlineContainerBox inline,
        InlineRun run,
        int runStart,
        int runEnd,
        MarkdownSemanticNode? flattenedHeading)
    {
        int flattenedHeadingLevel = GetHeadingLevel(run.SemanticHeadingKey);
        if (flattenedHeadingLevel <= 0)
            return null;

        if (flattenedHeading is null || flattenedHeading.HeadingLevel != flattenedHeadingLevel)
        {
            flattenedHeading = new MarkdownSemanticNode(MarkdownSemanticRole.Heading, inline)
            {
                InlineBox = inline,
                InlineRun = run,
                TextStart = runStart,
                TextEnd = runEnd,
                HeadingLevel = flattenedHeadingLevel,
            };
            AddRegisteredChild(node, flattenedHeading);
        }
        else
        {
            flattenedHeading.TextEnd = runEnd;
        }

        return flattenedHeading;
    }

    /// <summary>
    /// Groups consecutive code-block runs flattened into a non-code inline into
    /// a single code-block child, and returns it (or null after any other run).
    /// </summary>
    private MarkdownSemanticNode? TrackFlattenedCodeBlock(
        MarkdownSemanticNode node,
        InlineContainerBox inline,
        InlineRun run,
        int runStart,
        int runEnd,
        MarkdownSemanticNode? flattenedCodeBlock)
    {
        bool isFlattenedCodeBlock =
            inline.ElementKey != MarkdownElementKeys.CodeBlock &&
            run.ElementKey == MarkdownElementKeys.CodeBlock;
        if (!isFlattenedCodeBlock)
            return null;

        if (flattenedCodeBlock is null)
        {
            flattenedCodeBlock = new MarkdownSemanticNode(MarkdownSemanticRole.CodeBlock, inline)
            {
                InlineBox = inline,
                InlineRun = run,
                TextStart = runStart,
                TextEnd = runEnd,
            };
            AddRegisteredChild(node, flattenedCodeBlock);
        }
        else
        {
            flattenedCodeBlock.TextEnd = runEnd;
        }

        return flattenedCodeBlock;
    }

    private void AppendInlineSemanticBoundary(InlineContainerBox inline, InlineRun run)
    {
        int start = _text.Length;
        _text.Append(' ');
        _spans.Add(new MarkdownTextSpan(start, start + 1, inline, run, null, null, null));
    }

    private MarkdownSemanticNode BuildCodeBlock(CodeBlockBox codeBlock)
    {
        var hasLanguage = !string.IsNullOrWhiteSpace(codeBlock.CodeLanguage);
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.CodeBlock, codeBlock)
        {
            TextStart = _text.Length,
            CodeLanguage = hasLanguage ? codeBlock.LanguageDisplay : null,
            HelpText = hasLanguage
                ? codeBlock.Context.ResolveFormattedString(
                    MarkdownStringKeys.CodeLanguageHelp,
                    MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                    codeBlock.LanguageDisplay)
                : null,
        };
        _nodeIndexes.Register(node);

        foreach (var chunk in codeBlock.Chunks)
        {
            LayoutSnapshotIndexBuilder.Scope scope = default;
            if (_layoutIndexes is not null)
                scope = _layoutIndexes.EnterCodeChunk(chunk);
            try
            {
                foreach (var run in chunk.Runs)
                {
                    int runStart = _text.Length;
                    _text.Append(run.AccessibleText);
                    int runEnd = _text.Length;
                    _spans.Add(new MarkdownTextSpan(runStart, runEnd, chunk, run, null, null, null));
                }
            }
            finally
            {
                if (_layoutIndexes is not null)
                    _layoutIndexes.ExitCodeChunk(chunk, scope);
            }
        }

        node.TextEnd = _text.Length;
        AppendBlockSeparator();
        return node;
    }

    private MarkdownSemanticNode? BuildImage(ImageBox image)
    {
        if (MarkdownImageSemanticPolicy.IsDecorativeBlock(image.Alt))
            return null;

        var node = new MarkdownSemanticNode(MarkdownSemanticRole.Image, image)
        {
            ImageBox = image,
            TextStart = _text.Length,
            HelpText = image.SvgDesc,
        };
        _nodeIndexes.Register(node);

        string name = image.Alt;
        int start = _text.Length;
        _text.Append(name);
        int end = _text.Length;
        _spans.Add(new MarkdownTextSpan(start, end, null, null, image, null, null));
        node.TextEnd = _text.Length;
        AppendBlockSeparator();
        return node;
    }

    private MarkdownSemanticNode BuildEmbed(EmbedBox embed)
    {
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.Embed, embed)
        {
            EmbedBox = embed,
            TextStart = _text.Length,
        };
        _nodeIndexes.Register(node);
        int start = _text.Length;
        _text.Append(InlineEmbedRun.PlaceholderChar);
        int end = _text.Length;
        _spans.Add(new MarkdownTextSpan(start, end, null, null, null, embed, null));
        node.TextEnd = _text.Length;
        AppendBlockSeparator();
        return node;
    }

    private MarkdownSemanticNode BuildVectorScene(VectorSceneBox vector)
    {
        MarkdownSemanticNode node = _vectorScenes.Build(vector);
        AppendBlockSeparator();
        return node;
    }

    private MarkdownSemanticNode BuildHostedElement(DeclarativeHostedElementBox hosted)
    {
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.Embed, hosted)
        {
            HostedElementBox = hosted,
            TextStart = _text.Length,
            AccessibilityRole = hosted.AccessibilityRole,
            AccessibilityName = hosted.AccessibilityName,
            AccessibilityDescription = hosted.AccessibilityDescription,
            AutomationId = hosted.AutomationId,
        };
        _nodeIndexes.Register(node);
        int start = _text.Length;
        if (hosted.SemanticText is { } semanticText)
            _text.Append(semanticText);
        else
            _text.Append(InlineEmbedRun.PlaceholderChar);
        int end = _text.Length;
        _spans.Add(new MarkdownTextSpan(start, end, null, null, null, null, hosted));
        node.TextEnd = _text.Length;
        AppendBlockSeparator();
        return node;
    }

    private MarkdownSemanticNode BuildList(StackBox stack)
    {
        int level = ++_listDepth;
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.List, stack)
        {
            TextStart = _text.Length,
            Level = level,
        };
        _nodeIndexes.Register(node);

        int sizeOfSet = 0;
        IReadOnlyList<BlockBox> children = stack.Children;
        for (int index = 0; index < children.Count; index++)
        {
            BlockBox child = children[index];
            if (child is ListItemBox)
                sizeOfSet++;
        }

        int position = 0;
        try
        {
            for (int index = 0; index < children.Count; index++)
            {
                BlockBox child = children[index];
                var childNode = BuildBlock(child);
                if (childNode is null)
                    continue;

                if (childNode.Role == MarkdownSemanticRole.ListItem)
                {
                    childNode.Level = level;
                    childNode.PositionInSet = ++position;
                    childNode.SizeOfSet = sizeOfSet;
                }
                node.Add(childNode);
            }
        }
        finally
        {
            _listDepth--;
        }

        node.TextEnd = _text.Length;
        return node;
    }

    private MarkdownSemanticNode BuildListItem(ListItemBox listItem)
    {
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.ListItem, listItem)
        {
            TextStart = _text.Length,
        };
        _nodeIndexes.Register(node);

        if (BuildBlock(listItem.Marker) is { } marker) node.Add(marker);
        if (BuildBlock(listItem.Content) is { } content) node.Add(content);
        node.TextEnd = _text.Length;
        return node;
    }

    private MarkdownSemanticNode BuildTable(TableBox table)
    {
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.Table, table)
        {
            TableBox = table,
            TextStart = _text.Length,
            RowCount = table.RowCount,
            ColumnCount = table.ColumnCount,
        };
        _nodeIndexes.Register(node);

        foreach (var cellInfo in table.GetCellInfos())
        {
            var cellNode = new MarkdownSemanticNode(MarkdownSemanticRole.TableCell, cellInfo.Box)
            {
                InlineBox = cellInfo.Box,
                TextStart = _text.Length,
                Row = cellInfo.Row,
                Column = cellInfo.Column,
                IsHeader = cellInfo.IsHeader,
            };
            _nodeIndexes.Register(cellNode);

            if (BuildBlock(cellInfo.Box) is { } inlineNode)
                cellNode.Add(inlineNode);
            cellNode.TextEnd = _text.Length;
            node.Add(cellNode);
        }

        node.TextEnd = _text.Length;
        return node;
    }

    private MarkdownSemanticNode BuildGroup(StackBox stack)
    {
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.Group, stack)
        {
            TextStart = _text.Length,
        };
        _nodeIndexes.Register(node);

        IReadOnlyList<BlockBox> children = stack.Children;
        for (int index = 0; index < children.Count; index++)
        {
            BlockBox child = children[index];
            var childNode = BuildBlock(child);
            if (childNode is not null) node.Add(childNode);
        }

        node.TextEnd = _text.Length;
        return node;
    }

    private void AppendBlockSeparator()
    {
        // A separator divides two blocks' text. Before any text there is
        // nothing to divide: a leading block with no accessible text (an
        // alt="" logo, an anchor) must not leave an unowned offset 0.
        if (_text.Length > 0 && _text[^1] != '\n')
            _text.Append('\n');
    }

    private int TrimTrailingNewline()
    {
        CancellationToken cancellationToken = _cancellationToken;
        int lastContentEnd = 0;
        int visitedSpans = 0;

        // Positive-length spans own authored content, including a final LF
        // from a code block or explicit hard break. Any trailing LF beyond
        // the last such span is a synthetic block separator. Zero-length
        // spans at that boundary are metadata and do not move the trim bound.
        for (int index = _spans.Count - 1; index >= 0; index--)
        {
            if ((visitedSpans++ & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            MarkdownTextSpan span = _spans[index];
            if (span.TextEnd > span.TextStart)
            {
                lastContentEnd = span.TextEnd;
                break;
            }
        }

        int trimmed = 0;
        while (_text.Length > lastContentEnd && _text.Length > 0 && _text[^1] == '\n')
        {
            if ((trimmed++ & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            _text.Length--;
        }

        return trimmed;
    }

    private void ClampTrailingTextSpans(int textLength)
    {
        // Spans are appended in document/text order, and the semantic
        // position index relies on nondecreasing TextEnd values. Only the
        // suffix affected by trimming can be out of range.
        CancellationToken cancellationToken = _cancellationToken;
        int visited = 0;
        for (int index = _spans.Count - 1; index >= 0; index--)
        {
            if ((visited++ & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            MarkdownTextSpan span = _spans[index];
            if (span.TextStart <= textLength && span.TextEnd <= textLength)
                break;

            _spans[index] = span.ClampTextRange(textLength);
        }
    }

    private static bool IsListStack(StackBox stack)
    {
        IReadOnlyList<BlockBox> children = stack.Children;
        if (children.Count == 0) return false;
        for (int index = 0; index < children.Count; index++)
        {
            BlockBox child = children[index];
            if (child is not ListItemBox) return false;
        }
        return true;
    }

    private static bool IsHeading(string elementKey) =>
        GetHeadingLevel(elementKey) > 0;

    private static int GetHeadingLevel(string elementKey) => elementKey switch
    {
        MarkdownElementKeys.Heading1 => 1,
        MarkdownElementKeys.Heading2 => 2,
        MarkdownElementKeys.Heading3 => 3,
        MarkdownElementKeys.Heading4 => 4,
        MarkdownElementKeys.Heading5 => 5,
        MarkdownElementKeys.Heading6 => 6,
        _ => 0,
    };
}
