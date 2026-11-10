using System;
using Windows.Foundation;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Builds and releases the context menu target for the content under a pointer:
/// link, image, code, table and source range.
/// </summary>
internal static class ContextMenuTargets
{
    internal static void DisposeContextMenuTarget(ContextMenuTarget target)
    {
        try
        {
            target.Dispose();
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] context-menu target cleanup failed: {exception.Message}");
        }
    }

    internal static bool PopulateContextMenuTarget(
        BlockBox box,
        Point point,
        ContextMenuTarget target)
    {
        if (!box.Bounds.Contains(point))
            return false;

        switch (box)
        {
            case CodeBlockBox code:
                target.CodeText = code.CodeText;
                target.CodeLanguage = code.CodeLanguage;
                target.CodeSourceRange = GetSourceRange(code);
                return true;
            case ImageBox image:
                target.ImageUrl = image.Url;
                target.ImageSourceRange = GetSourceRange(image);
                target.SetImageBitmapLease(image.AcquireBitmapLease());
                return true;
            case VectorSceneBox vector:
                if (vector.TryGetLinkAt(point, out var vectorLink) && vectorLink is not null)
                {
                    var semantic = vector.GetSemanticItem(vectorLink.SemanticIndex);
                    target.LinkUrl = vectorLink.Target ?? string.Empty;
                    target.LinkSourceRange = semantic?.SourceSpan ?? vector.SourceRange;
                }
                return true;
            case InlineContainerBox inline:
                PopulateInlineContextMenuTarget(inline, point, target);
                return true;
            case TableBox table:
                target.TableSourceRange = GetSourceRange(table);
                target.TableSnapshot = SelectionClipboard.BuildTableClipboardSnapshot(table);
                foreach (var cell in table.GetCellBoxes())
                {
                    if (PopulateContextMenuTarget(cell, point, target))
                        break;
                }
                return true;
            case ListItemBox listItem:
                if (PopulateContextMenuTarget(listItem.Marker, point, target))
                    return true;
                PopulateContextMenuTarget(listItem.Content, point, target);
                return true;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (PopulateContextMenuTarget(child, point, target))
                        return true;
                }
                return true;
            default:
                return true;
        }
    }

    internal static void PopulateInlineContextMenuTarget(
        InlineContainerBox inline,
        Point point,
        ContextMenuTarget target)
    {
        InlineRun? run = inline.RunAtVisualPoint(point);
        if (run is InlineImageRun image)
        {
            target.ImageUrl = image.Image.Url;
            target.ImageSourceRange = image.SourceSpan;
            target.SetImageBitmapLease(image.Image.AcquireBitmapLease());
        }

        if (run is not null && LinkTargets.TryCreateLinkedAtomicTarget(run, out LinkTarget link))
        {
            target.LinkUrl = link.Url;
            target.LinkSourceRange = link.SourceSpan;
        }
    }

    internal static SourceSpan GetSourceRange(BlockBox box)
    {
        int start = int.MaxValue;
        int end = 0;
        CollectSourceRange(box, ref start, ref end);
        return start == int.MaxValue || end <= start
            ? SourceSpan.Empty
            : new SourceSpan(start, end - start);
    }

    internal static void CollectSourceRange(BlockBox box, ref int start, ref int end)
    {
        switch (box)
        {
            case InlineContainerBox inline:
                foreach (InlineRun run in inline.Runs)
                    IncludeSourceSpan(run.SourceSpan, ref start, ref end);
                return;
            case CodeBlockBox code:
                foreach (var chunk in code.Chunks)
                    CollectSourceRange(chunk, ref start, ref end);
                return;
            case TableBox table:
                foreach (var cell in table.GetCellBoxes())
                    CollectSourceRange(cell, ref start, ref end);
                return;
            case ListItemBox listItem:
                CollectSourceRange(listItem.Marker, ref start, ref end);
                CollectSourceRange(listItem.Content, ref start, ref end);
                return;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    CollectSourceRange(child, ref start, ref end);
                return;
            case VectorSceneBox vector:
                IncludeSourceSpan(vector.SourceRange, ref start, ref end);
                return;
        }

        if (box is ImageBox image)
        {
            foreach (var entry in image.Context.SourceMap.Entries)
            {
                if (entry.BlockIndex == image.BlockIndex)
                    IncludeSourceSpan(entry.Span, ref start, ref end);
            }
        }
    }

    internal static void IncludeSourceSpan(SourceSpan span, ref int start, ref int end)
    {
        if (span.IsEmpty)
            return;
        start = Math.Min(start, span.Start);
        end = Math.Max(end, span.End);
    }
}
