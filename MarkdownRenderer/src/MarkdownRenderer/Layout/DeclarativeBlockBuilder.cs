using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds blocks for declarative hosted-element content (lists, tables,
/// images, code and materialized scenes) when the host enables it.
/// </summary>
internal sealed class DeclarativeBlockBuilder
{
    private readonly MarkdownLayoutContext _context;
    private readonly bool _enableDeclarativeHostedElements;
    private readonly LayoutConstructionTracker _tracker;
    private readonly DeclarativeInlineBuilder _declarativeInlines;
    private readonly CodeBlockLayoutBuilder _codeBlocks;
    private readonly MaterializedSceneScope _scenes;

    internal DeclarativeBlockBuilder(
        MarkdownLayoutContext context,
        bool enableDeclarativeHostedElements,
        LayoutConstructionTracker tracker,
        DeclarativeInlineBuilder declarativeInlines,
        CodeBlockLayoutBuilder codeBlocks,
        MaterializedSceneScope scenes)
    {
        _context = context;
        _enableDeclarativeHostedElements = enableDeclarativeHostedElements;
        _tracker = tracker;
        _declarativeInlines = declarativeInlines;
        _codeBlocks = codeBlocks;
        _scenes = scenes;
    }

    internal BlockBox? TryBuildDeclarativeBlock(MarkdownContentFragment fragment)
    {
        // Validate the complete fragment before allocating block indices or
        // recording source-map entries. Unsupported output must fall back to
        // the built-in renderer atomically, without leaving partial layout
        // state behind.
        if (fragment.Items.Count == 0)
            return null;
        foreach (MarkdownContent content in fragment.Items)
        {
            if (!CanBuildDeclarativeContent(content))
                return null;
        }

        var fallbackKey = new HostedElementFallbackKey(fragment);

        if (fragment.Items.Count == 1)
            return TryBuildDeclarativeContent(fragment.Items[0], fallbackKey);

        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(stack);
        foreach (MarkdownContent content in fragment.Items)
        {
            var child = TryBuildDeclarativeContent(content, fallbackKey);
            if (child is null)
            {
                _tracker.DiscardUnpublishedBlock(stack);
                return null;
            }
            _tracker.AddStackChild(stack, child);
        }

        if (stack.Children.Count > 0)
            return stack;

        _tracker.DiscardUnpublishedBlock(stack);
        return null;
    }

    private bool CanBuildDeclarativeContent(MarkdownContent content) => content.Kind switch
    {
        MarkdownContentKind.Text => content.Text is not null,
        MarkdownContentKind.CodeBlock => content.Text is not null,
        MarkdownContentKind.Image => !string.IsNullOrWhiteSpace(content.Destination),
        MarkdownContentKind.VectorScene => content.VectorScene is not null,
        MarkdownContentKind.HostedElement =>
            _enableDeclarativeHostedElements && !string.IsNullOrWhiteSpace(content.FactoryKey),
        MarkdownContentKind.Link =>
            !string.IsNullOrWhiteSpace(content.Destination) &&
            content.Children.Count > 0 &&
            DeclarativeContentSyntax.AllChildren(content, _declarativeInlines.CanBuildDeclarativeInlineContent),
        MarkdownContentKind.Container =>
            content.Children.Count > 0 &&
            (DeclarativeContentSyntax.AllChildren(content, _declarativeInlines.CanBuildDeclarativeInlineContent) ||
             DeclarativeContentSyntax.AllChildren(content, CanBuildDeclarativeContent)),
        MarkdownContentKind.List =>
            content.Children.Count > 0 &&
            DeclarativeContentSyntax.AllChildren(content, static child => child.Kind == MarkdownContentKind.ListItem) &&
            DeclarativeContentSyntax.AllChildren(content, CanBuildDeclarativeListItem),
        MarkdownContentKind.ListItem => CanBuildDeclarativeListItem(content),
        MarkdownContentKind.Table => CanBuildDeclarativeTable(content),
        _ => false,
    };

    private bool CanBuildDeclarativeListItem(MarkdownContent content) =>
        content.Kind == MarkdownContentKind.ListItem &&
        content.Children.Count > 0 &&
        DeclarativeContentSyntax.AllChildren(content, CanBuildDeclarativeContent);

    private bool CanBuildDeclarativeTable(MarkdownContent content)
    {
        if (content.Children.Count == 0 ||
            !DeclarativeContentSyntax.AllChildren(content, static child => child.Kind == MarkdownContentKind.TableRow))
        {
            return false;
        }

        int columnCount = content.Children[0].Children.Count;
        if (columnCount == 0)
            return false;

        foreach (MarkdownContent row in content.Children)
        {
            if (row.Children.Count != columnCount ||
                !DeclarativeContentSyntax.AllChildren(row, static child => child.Kind == MarkdownContentKind.TableCell))
            {
                return false;
            }

            foreach (MarkdownContent cell in row.Children)
            {
                if (!DeclarativeContentSyntax.AllChildren(cell, _declarativeInlines.CanBuildDeclarativeInlineContent))
                    return false;
            }
        }

        return true;
    }

    private BlockBox? TryBuildDeclarativeContent(
        MarkdownContent content,
        HostedElementFallbackKey fallbackKey)
    {
        if (content.Kind == MarkdownContentKind.HostedElement)
            return BuildDeclarativeHostedElement(content, fallbackKey);

        if (content.Kind == MarkdownContentKind.VectorScene && content.VectorScene is not null)
            return BuildDeclarativeVectorScene(content);

        if (content.Kind == MarkdownContentKind.Text ||
            content.Kind == MarkdownContentKind.Link ||
            (content.Kind == MarkdownContentKind.Container &&
             DeclarativeContentSyntax.AllChildren(content, _declarativeInlines.CanBuildDeclarativeInlineContent)))
        {
            return BuildDeclarativeInlineBlock(content);
        }

        if (content.Kind == MarkdownContentKind.Image)
            return BuildDeclarativeImage(content);

        if (content.Kind == MarkdownContentKind.CodeBlock)
            return BuildDeclarativeCodeOrMaterializedScene(content);

        if (content.Kind == MarkdownContentKind.List)
            return BuildDeclarativeList(content, fallbackKey);

        if (content.Kind == MarkdownContentKind.Table)
            return BuildDeclarativeTable(content);

        if ((content.Kind == MarkdownContentKind.Container ||
             content.Kind == MarkdownContentKind.ListItem) &&
            content.Children.Count > 0)
        {
            return BuildDeclarativeStack(content, fallbackKey);
        }

        return null;
    }

    private DeclarativeHostedElementBox BuildDeclarativeHostedElement(
        MarkdownContent content,
        HostedElementFallbackKey fallbackKey)
    {
        var box = new DeclarativeHostedElementBox(_context, content, fallbackKey)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(box);
        if (!content.SourceSpan.IsEmpty)
            _context.SourceMap.Add(box.BlockIndex, 0, 1, content.SourceSpan);
        return box;
    }

    private VectorSceneBox BuildDeclarativeVectorScene(MarkdownContent content)
    {
        var vector = new VectorSceneBox(
            _context,
            content,
            DeclarativeContentSyntax.GetElementKey(
                content,
                content.AccessibilityRole == MarkdownAccessibilityRole.Math
                    ? MarkdownElementKeys.Math
                    : MarkdownElementKeys.Diagram))
        {
            BlockIndex = _context.NextBlockIndex(),
            DeferredSceneBinding = _scenes.NextBinding(),
        };
        _tracker.TrackBlock(vector);
        if (!content.SourceSpan.IsEmpty)
            _context.SourceMap.Add(vector.BlockIndex, 0, 1, content.SourceSpan);
        return vector;
    }

    // A code block carrying a deferred-scene marker renders the session's
    // materialized scene when one is ready; otherwise it renders as code.
    private BlockBox? BuildDeclarativeCodeOrMaterializedScene(MarkdownContent content)
    {
        if (content.Attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? deferredSceneMarker) &&
            !string.IsNullOrWhiteSpace(deferredSceneMarker) &&
            _context.PerformanceSession is IMarkdownPerformanceSessionInternal session &&
            _context.PerformanceDocumentOwner is { } documentOwner &&
            session.TryGetDeferredSceneResult(documentOwner, deferredSceneMarker,
                out MarkdownContentFragment? result, out object? projectionIdentity) &&
            TryBuildMaterializedSceneBlock(deferredSceneMarker, result!, projectionIdentity) is { } materialized)
            return materialized;

        return _codeBlocks.BuildDeclarativeCodeBlock(content);
    }

    // All children must build; a failed or empty container is discarded.
    private StackBox? BuildDeclarativeStack(MarkdownContent content, HostedElementFallbackKey fallbackKey)
    {
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(stack);
        foreach (MarkdownContent childContent in content.Children)
        {
            var child = TryBuildDeclarativeContent(childContent, fallbackKey);
            if (child is null)
            {
                _tracker.DiscardUnpublishedBlock(stack);
                return null;
            }
            _tracker.AddStackChild(stack, child);
        }
        if (stack.Children.Count == 0)
        {
            _tracker.DiscardUnpublishedBlock(stack);
            return null;
        }
        return stack;
    }

    private InlineContainerBox BuildDeclarativeInlineBlock(MarkdownContent content)
    {
        var box = new InlineContainerBox(_context, DeclarativeContentSyntax.GetElementKey(content, MarkdownElementKeys.Body))
        {
            BlockIndex = _context.NextBlockIndex(),
            TextAlignment = content.AccessibilityRole == MarkdownAccessibilityRole.Math
                ? CanvasHorizontalAlignment.Center
                : CanvasHorizontalAlignment.Left,
        };
        _tracker.TrackBlock(box);
        var runs = new List<InlineRun>();
        _declarativeInlines.AppendDeclarativeInline(content, runs);
        foreach (InlineRun run in runs)
            _tracker.AddInlineRun(box, run);
        return box;
    }

    private ImageBox BuildDeclarativeImage(MarkdownContent content)
    {
        DeclarativeContentSyntax.TryGetDeclarativeLength(content, MarkdownContentAttributes.ImageWidth, out SafeHtmlLength width);
        DeclarativeContentSyntax.TryGetDeclarativeLength(content, MarkdownContentAttributes.ImageHeight, out SafeHtmlLength height);
        var box = new ImageBox(
            _context, _context.ImageResources,
            content.Destination ?? string.Empty,
            content.Text ?? string.Empty,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(box);
        if (!content.SourceSpan.IsEmpty)
            _context.SourceMap.Add(box.BlockIndex, 0, 1, content.SourceSpan);
        return box;
    }

    private BlockBox? BuildDeclarativeList(
        MarkdownContent content,
        HostedElementFallbackKey fallbackKey)
    {
        using var listScope = _context.Styles.PushListDepth();
        var stack = new StackBox
        {
            BlockIndex = _context.NextBlockIndex(),
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(stack);
        bool ordered = DeclarativeContentSyntax.GetBooleanAttribute(content, MarkdownContentAttributes.ListOrdered);
        int ordinal = DeclarativeContentSyntax.GetPositiveIntegerAttribute(content, MarkdownContentAttributes.ListStart, 1);
        foreach (MarkdownContent item in content.Children)
        {
            ListItemBox? listItem = BuildDeclarativeListItem(item, fallbackKey, ordered, ordinal);
            if (listItem is null)
            {
                _tracker.DiscardUnpublishedBlock(stack);
                return null;
            }

            _tracker.AddStackChild(stack, listItem);
            ordinal++;
        }

        return stack;
    }

    private ListItemBox? BuildDeclarativeListItem(
        MarkdownContent item,
        HostedElementFallbackKey fallbackKey,
        bool ordered,
        int ordinal)
    {
        ElementStyle listStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(
            1f,
            listStyle.ListIndent + Math.Max(0, _context.Styles.ListDepth - 1) * listStyle.NestedListIndent);
        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker)
        {
            BlockIndex = _context.NextBlockIndex(),
            TextAlignment = ordered
                ? CanvasHorizontalAlignment.Left
                : CanvasHorizontalAlignment.Center,
        };
        _tracker.TrackBlock(marker);
        marker.Add(new TextRun(ordered ? $"{ordinal}." : "\u2022")
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(item.SourceSpan.Start, 0),
        });

        var body = new StackBox
        {
            BlockIndex = _context.NextBlockIndex(),
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(body);
        foreach (MarkdownContent childContent in item.Children)
        {
            BlockBox? child = TryBuildDeclarativeContent(childContent, fallbackKey);
            if (child is null)
            {
                _tracker.DiscardUnpublishedBlock(body);
                _tracker.DiscardUnpublishedBlock(marker);
                return null;
            }

            _tracker.AddStackChild(body, child);
        }

        var listItem = new ListItemBox(marker, body, markerWidth)
        {
            BlockIndex = _context.NextBlockIndex(),
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(listItem);
        _tracker.AdoptBlock(marker);
        _tracker.AdoptBlock(body);
        return listItem;
    }

    private TableBox BuildDeclarativeTable(MarkdownContent content)
    {
        var headerRows = new List<InlineContainerBox[]>();
        var bodyRows = new List<InlineContainerBox[]>();
        foreach (MarkdownContent row in content.Children)
        {
            bool isHeader = DeclarativeContentSyntax.GetBooleanAttribute(row, MarkdownContentAttributes.TableHeaderRow) ||
                row.Children.Count > 0 &&
                DeclarativeContentSyntax.AllChildren(row, static cell => cell.StyleRole == MarkdownStyleRole.TableHeader);
            var cells = new InlineContainerBox[row.Children.Count];
            for (int index = 0; index < cells.Length; index++)
                cells[index] = BuildDeclarativeTableCell(row.Children[index], isHeader);

            (isHeader ? headerRows : bodyRows).Add(cells);
        }

        var table = new TableBox(_context, headerRows.ToArray(), bodyRows.ToArray())
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(table);
        foreach (InlineContainerBox[] row in headerRows)
            foreach (InlineContainerBox cell in row)
                _tracker.AdoptBlock(cell);
        foreach (InlineContainerBox[] row in bodyRows)
            foreach (InlineContainerBox cell in row)
                _tracker.AdoptBlock(cell);
        return table;
    }

    private InlineContainerBox BuildDeclarativeTableCell(MarkdownContent cell, bool isHeader)
    {
        string elementKey = DeclarativeContentSyntax.GetElementKey(
            cell,
            isHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell);
        var box = new InlineContainerBox(_context, elementKey)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(box);
        var runs = new List<InlineRun>();
        foreach (MarkdownContent child in cell.Children)
            _declarativeInlines.AppendDeclarativeInline(child, runs);
        foreach (InlineRun run in runs)
            _tracker.AddInlineRun(box, run);
        return box;
    }

    internal BlockBox? TryBuildMaterializedSceneBlock(
        string marker,
        MarkdownContentFragment fragment,
        object? projectionIdentity)
    {
        string? previousMarker = _scenes.Marker;
        int previousOrdinal = _scenes.Ordinal;
        object? previousIdentity = _scenes.ProjectionIdentity;
        _scenes.Marker = marker;
        _scenes.Ordinal = 0;
        _scenes.ProjectionIdentity = projectionIdentity;
        try
        {
            return TryBuildDeclarativeBlock(fragment);
        }
        finally
        {
            _scenes.Marker = previousMarker;
            _scenes.Ordinal = previousOrdinal;
            _scenes.ProjectionIdentity = previousIdentity;
        }
    }
}
