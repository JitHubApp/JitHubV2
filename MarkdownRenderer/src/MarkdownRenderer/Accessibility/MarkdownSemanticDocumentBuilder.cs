using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

internal sealed class MarkdownSemanticDocumentBuilder
{
    private readonly SemanticTextProjectionAccumulator _text;
    private readonly LayoutSnapshotIndexBuilder? _layoutIndexes;
    // Spans are now inline values rather than per-span objects. Use paged
    // growth to avoid repeatedly copying a large value array as the semantic
    // graph is built, then freeze once into the snapshot's stable span list.
    private readonly PagedEntryBuffer<MarkdownTextSpan> _spans = new();
    private readonly Dictionary<InlineContainerBox, MarkdownSemanticNode> _nodeByInlineBox = new();
    private readonly Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> _nodeByHorizontalOverflow = new(
        ReferenceEqualityComparer.Instance);
    private int _listDepth;

    public MarkdownSemanticDocumentBuilder(
        string? expectedTextProjection,
        LayoutSnapshotIndexBuilder? layoutIndexes)
    {
        _text = new SemanticTextProjectionAccumulator(expectedTextProjection);
        _layoutIndexes = layoutIndexes;
    }

    public MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
    {
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 0
        };
        RegisterNodeIndexes(root);

        for (int topLevelOrdinal = 0; topLevelOrdinal < snapshot.Blocks.Count; topLevelOrdinal++)
        {
            LayoutPassCancellation.GetEffectiveToken(CancellationToken.None).ThrowIfCancellationRequested();
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
        // The graph indexes are collected in preorder during construction.
        // Build the compact integer index once at its exact final capacity;
        // growing it alongside the graph adds rehashes and oversized arrays.
        // This loop visits only unique indexed inline boxes, not the tree.
        var nodeByInlineBlockIndex = new Dictionary<int, MarkdownSemanticNode>(_nodeByInlineBox.Count);
        var ambiguousInlineBlockIndices = new HashSet<int>();
        foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in _nodeByInlineBox)
        {
            if (!nodeByInlineBlockIndex.TryAdd(entry.Key.BlockIndex, entry.Value))
                ambiguousInlineBlockIndices.Add(entry.Key.BlockIndex);
        }
        foreach (int blockIndex in ambiguousInlineBlockIndices)
            nodeByInlineBlockIndex.Remove(blockIndex);
        return new MarkdownSemanticDocument(
            root,
            text,
            _spans.Freeze(),
            matchedProjection,
            new MarkdownSemanticNodeIndexes(
                _nodeByInlineBox,
                nodeByInlineBlockIndex,
                _nodeByHorizontalOverflow));
    }

    private void RegisterNodeIndexes(MarkdownSemanticNode node)
    {
        // Match EnumerateDepthFirst's preorder/first-wins contracts. Register
        // parent wrappers before creating any semantic children.
        if (node.InlineBox is { } inline &&
            node.InlineRun is null &&
            node.Role is (MarkdownSemanticRole.Paragraph or
                MarkdownSemanticRole.Heading or
                MarkdownSemanticRole.CodeBlock))
        {
            _nodeByInlineBox.TryAdd(inline, node);
        }

        if (node.VectorSemanticIndex < 0 && node.Box is IHorizontalOverflowBox overflow)
            _nodeByHorizontalOverflow.TryAdd(overflow, node);
    }

    private void AddRegisteredChild(
        MarkdownSemanticNode parent,
        MarkdownSemanticNode child)
    {
        RegisterNodeIndexes(child);
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
        var role = inline.ElementKey == MarkdownElementKeys.CodeBlock
            ? MarkdownSemanticRole.CodeBlock
            : IsHeading(inline.ElementKey)
                ? MarkdownSemanticRole.Heading
                : MarkdownSemanticRole.Paragraph;
        var node = new MarkdownSemanticNode(role, inline)
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
        RegisterNodeIndexes(node);

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

            int runStart = _text.Length;
            if (sourceSlice is not null)
                _text.AppendSlice(sourceSlice.SourceText, sourceSlice.SourceStart, sourceSlice.SliceLength);
            else
                _text.Append(accessibleText);
            int runEnd = _text.Length;
            _spans.Add(new MarkdownTextSpan(runStart, runEnd, inline, run, null, null, null));
            int flattenedHeadingLevel = GetHeadingLevel(run.SemanticHeadingKey);
            bool isFlattenedCodeBlock =
                inline.ElementKey != MarkdownElementKeys.CodeBlock &&
                run.ElementKey == MarkdownElementKeys.CodeBlock;
            if (flattenedHeadingLevel > 0)
            {
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
            }
            else
            {
                flattenedHeading = null;
            }

            if (isFlattenedCodeBlock)
            {
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
            }
            else
            {
                flattenedCodeBlock = null;
            }

            MarkdownSemanticNode semanticParent = flattenedCodeBlock ?? flattenedHeading ?? node;
            if (run is LinkRun link)
            {
                AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Link, inline)
                {
                    InlineBox = inline,
                    InlineRun = link,
                    TextStart = runStart,
                    TextEnd = runEnd,
                    HelpText = link.Url,
                });
            }
            else if (run is InlineEmbedRun embedRun)
            {
                AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Embed, inline)
                {
                    InlineBox = inline,
                    InlineRun = embedRun,
                    TextStart = runStart,
                    TextEnd = runEnd,
                    AccessibilityName = embedRun.AutomationMetadata?.CurrentName,
                    AccessibilityDescription = embedRun.AutomationMetadata?.ReadOnlyHelpText,
                    AutomationId = embedRun.AutomationMetadata?.AutomationId,
                });
            }
            else if (run is InlineImageRun imageRun)
            {
                AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Image, inline)
                {
                    InlineBox = inline,
                    ImageBox = imageRun.Image,
                    InlineRun = imageRun,
                    TextStart = runStart,
                    TextEnd = runEnd,
                    HelpText = imageRun.IsLinked
                        ? !string.IsNullOrWhiteSpace(imageRun.LinkTitle)
                            ? imageRun.LinkTitle
                            : imageRun.LinkUrl
                        : !string.IsNullOrWhiteSpace(imageRun.Title)
                            ? imageRun.Title
                        : imageRun.Url,
                });
            }
            else if (run is InlineVectorSceneRun vectorRun)
            {
                AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Math, inline)
                {
                    InlineBox = inline,
                    InlineRun = vectorRun,
                    TextStart = runStart,
                    TextEnd = runEnd,
                    AccessibilityRole = MarkdownAccessibilityRole.Math,
                    AccessibilityName = vectorRun.AccessibilityName,
                    AccessibilityDescription = vectorRun.AccessibilityDescription,
                    HelpText = vectorRun.IsLinked
                        ? !string.IsNullOrWhiteSpace(vectorRun.LinkTitle)
                            ? vectorRun.LinkTitle
                            : vectorRun.LinkUrl
                        : vectorRun.AccessibilityDescription,
                    AutomationId = string.Concat(
                        "MarkdownMath_",
                        vectorRun.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "_",
                        vectorRun.SourceSpan.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                });
            }
            else if (run is AbbreviationRun abbreviationRun)
            {
                AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Abbreviation, inline)
                {
                    InlineBox = inline,
                    InlineRun = abbreviationRun,
                    TextStart = runStart,
                    TextEnd = runEnd,
                    HelpText = abbreviationRun.Expansion,
                });
            }

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

    private static string GetInlineAccessibleText(
        InlineRun run,
        MarkdownLayoutContext context) =>
        run is InlineImageRun imageRun
            ? MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                imageRun.AltText,
                imageRun.IsLinked,
                imageRun.LinkTitle,
                context.ResolveString(
                    MarkdownStringKeys.ImageName,
                    MarkdownLocalizedStrings.ImageName)) ?? string.Empty
            : run.AccessibleText;

    private static bool IsAtomicInline(InlineRun run) =>
        run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun;

    private static bool NeedsSemanticBoundary(char left, char right) =>
        char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);

    private static bool TryGetNextInlineFirstCharacter(
        InlineContainerBox inline,
        int startIndex,
        out char firstCharacter)
    {
        for (int index = startIndex; index < inline.Runs.Count; index++)
        {
            InlineRun run = inline.Runs[index];
            if (run is SourceSliceTextRun sourceSlice && IsExactSourceSlice(inline, run, sourceSlice))
            {
                if (sourceSlice.SliceLength == 0)
                    continue;

                firstCharacter = sourceSlice.SourceText[sourceSlice.SourceStart];
                return true;
            }

            string text = GetInlineAccessibleText(run, inline.Context);
            if (text.Length > 0)
            {
                firstCharacter = text[0];
                return true;
            }
        }

        firstCharacter = default;
        return false;
    }

    private static bool IsExactSourceSlice(
        InlineContainerBox inline,
        InlineRun run,
        SourceSliceTextRun sourceSlice) =>
        ReferenceEquals(sourceSlice.SourceText, inline.Context.SourceMap.SourceText) &&
        sourceSlice.SourceStart == run.SourceSpan.Start &&
        sourceSlice.SliceLength == run.SourceSpan.Length;

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
        RegisterNodeIndexes(node);

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
        RegisterNodeIndexes(node);

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
        RegisterNodeIndexes(node);
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
        IReadOnlyList<MarkdownVectorSemanticItem> semantics = vector.SemanticItems;
        int start = _text.Length;
        var semanticRanges = new (int Start, int End)?[semantics.Count];
        for (int i = 0; i < semantics.Count; i++)
        {
            vector.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem item = semantics[i];
            _layoutIndexes?.ObserveVectorSemantic(vector, i, item.Flags);
            if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram) ||
                string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            semanticRanges[i] = AppendVectorText(vector, i, item.Name!, start);
        }

        if (semantics.Count == 0 &&
            !string.IsNullOrWhiteSpace(vector.SemanticText))
        {
            // Math and other unstructured vector scenes deliberately expose
            // their semantic source as one exact range. Structured scenes expose
            // each named, non-decorative child as an exact range above; Selectable
            // independently controls application selection and pointer hit testing.
            AppendVectorText(vector, -1, vector.SemanticText, start);
        }

        if (semantics.Count == 0 &&
            _text.Length == start &&
            !string.IsNullOrWhiteSpace(vector.AccessibilityName))
        {
            AppendVectorText(vector, -1, vector.AccessibilityName!, start);
        }
        int end = _text.Length;

        string? accessibilityName = vector.AccessibilityName;
        if (string.IsNullOrWhiteSpace(accessibilityName))
        {
            for (int i = 0; i < semantics.Count; i++)
            {
                vector.ThrowIfCancellationRequested();
                MarkdownVectorSemanticItem candidate = semantics[i];
                if (MarkdownVectorSemanticPolicy.IsExposed(candidate.Flags) &&
                    candidate.ParentIndex < 0 &&
                    candidate.Role == MarkdownVectorSemanticRole.Diagram &&
                    !string.IsNullOrWhiteSpace(candidate.Name))
                {
                    accessibilityName = candidate.Name;
                    break;
                }
            }
        }

        var node = new MarkdownSemanticNode(
            vector.AccessibilityRole == MarkdownAccessibilityRole.Math
                ? MarkdownSemanticRole.Math
                : MarkdownSemanticRole.Diagram,
            vector)
        {
            VectorSceneBox = vector,
            TextStart = start,
            TextEnd = end,
            AccessibilityRole = vector.AccessibilityRole,
            AccessibilityName = accessibilityName,
            AccessibilityDescription = vector.AccessibilityDescription,
            AutomationId = string.Concat(
                vector.AccessibilityRole == MarkdownAccessibilityRole.Math ? "MarkdownMath_" : "MarkdownDiagram_",
                vector.SourceRange.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "_",
                vector.SourceRange.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        RegisterNodeIndexes(node);

        var semanticNodes = new MarkdownSemanticNode?[semantics.Count];
        for (int i = 0; i < semanticNodes.Length; i++)
        {
            vector.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem item = semantics[i];
            if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram))
            {
                continue;
            }
            MarkdownVectorLinkAction? link = vector.GetSemanticLinkAction(i);
            bool invokable = MarkdownVectorSemanticPolicy.IsInvokable(item.Flags, link is not null);
            var child = new MarkdownSemanticNode(invokable
                ? MarkdownSemanticRole.Link
                : MarkdownSemanticRole.Group, vector)
            {
                VectorSceneBox = vector,
                VectorSemanticIndex = i,
                VectorSemanticRole = item.Role,
                VectorSemanticFlags = item.Flags,
                TextStart = semanticRanges[i]?.Start ?? start,
                TextEnd = semanticRanges[i]?.End ?? start,
                AccessibilityRole = invokable
                    ? MarkdownAccessibilityRole.Link
                    : MarkdownAccessibilityRole.Group,
                AccessibilityName = item.Name,
                AccessibilityDescription = item.Description,
                HelpText = link?.Target ?? link?.Action,
                AutomationId = string.Concat(
                    "MarkdownDiagramItem_",
                    vector.SourceRange.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "_",
                    item.SourceId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
            };
            RegisterNodeIndexes(child);
            semanticNodes[i] = child;
        }

        MarkdownSemanticDocument.AttachVectorSemanticNodes(
            semantics,
            semanticNodes,
            node,
            vector.CancellationToken);

        AppendBlockSeparator();
        return node;
    }

    private (int Start, int End) AppendVectorText(
        VectorSceneBox vector,
        int semanticIndex,
        string value,
        int blockStart)
    {
        if (_text.Length > blockStart)
            _text.Append('\n');
        int start = _text.Length;
        _text.Append(value);
        int end = _text.Length;
        _spans.Add(new MarkdownTextSpan(
            start,
            end,
            null,
            null,
            null,
            null,
            null,
            vector,
            semanticIndex,
            vector.ResolveSemanticTextStyle(semanticIndex)));
        return (start, end);
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
        RegisterNodeIndexes(node);
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
        RegisterNodeIndexes(node);

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
        RegisterNodeIndexes(node);

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
        RegisterNodeIndexes(node);

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
            RegisterNodeIndexes(cellNode);

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
        RegisterNodeIndexes(node);

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
        CancellationToken cancellationToken = LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
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
        CancellationToken cancellationToken = LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
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
