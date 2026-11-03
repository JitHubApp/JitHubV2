using System;
using System.Threading;
using Markdig.Syntax;
using MarkdownSourceMap = MarkdownRenderer.Document.MarkdownSourceMap;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds fenced, indented and declarative code blocks, chunking long blocks
/// into bounded text layouts with their source projections.
/// </summary>
internal sealed class CodeBlockLayoutBuilder
{
    private const int MaxMonolithicTextLayoutLength = 32_768;
    private const int MaxCodeBlockLinesPerChunk = 128;

    private readonly MarkdownLayoutContext _context;
    private readonly LayoutConstructionTracker _tracker;
    private readonly CodeBlockSourceProjector _sourceProjector;

    internal CodeBlockLayoutBuilder(
        MarkdownLayoutContext context,
        LayoutConstructionTracker tracker,
        CodeBlockSourceProjector sourceProjector)
    {
        _context = context;
        _tracker = tracker;
        _sourceProjector = sourceProjector;
    }

    internal BlockBox BuildCodeBlock(LeafBlock block, string text)
    {
        text = CodeBlockMetadata.NormalizeCodeLineEndings(text);
        var metadata = CodeBlockMetadata.FromBlock(block, text);
        SourceSpan sourceSpan = block.Span.Start >= 0 && block.Span.Length > 0
            ? new SourceSpan(block.Span.Start, block.Span.Length)
            : SourceSpan.Empty;
        return BuildCodeBlock(metadata, text, sourceSpan, block);
    }

    internal BlockBox BuildDeclarativeCodeBlock(MarkdownContent content)
    {
        string text = CodeBlockMetadata.NormalizeCodeLineEndings(content.Text);
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            content.SourceSpan,
            text,
            content.Language,
            content.Attributes);
        return BuildCodeBlock(metadata, text, content.SourceSpan, parsedBlock: null);
    }

    internal BlockBox BuildCodeBlock(
        CodeBlockMetadata metadata,
        string text,
        SourceSpan sourceSpan,
        LeafBlock? parsedBlock)
    {
        bool hasExactParserSourceMap = parsedBlock is not null &&
            _sourceProjector.TryPrepareCodeBlockSourceMap(parsedBlock, text, out sourceSpan);
        if (parsedBlock is not null && !hasExactParserSourceMap)
            sourceSpan = SourceSpan.Empty;

        int lineCount = CountLogicalLines(text);
        bool showLineNumbers = metadata.ShowLineNumbers ?? _context.CodeBlockLineNumberMode switch
        {
            CodeBlockLineNumberMode.Always => true,
            CodeBlockLineNumberMode.Never => false,
            _ => lineCount > 1,
        };
        var codeBox = new CodeBlockBox(
            _context,
            metadata,
            text,
            _context.IsCodeBlockCopyEnabled,
            showLineNumbers)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(codeBox);

        if (text.Length <= MaxMonolithicTextLayoutLength &&
            lineCount <= MaxCodeBlockLinesPerChunk)
        {
            var monolithicSourceCursor = new CodeBlockSourceCursor();
            MarkdownSourceMap.SourceProjection? projection = hasExactParserSourceMap
                ? _sourceProjector.TryCreateCodeBlockChunkProjection(
                    parsedBlock!, text, 0, text.Length, sourceSpan,
                    includeSourcePrefix: true,
                    includeSourceSuffix: true,
                    ref monolithicSourceCursor)
                : null;
            SourceSpan chunkSourceSpan = hasExactParserSourceMap && projection is not null
                ? sourceSpan
                : parsedBlock is null ? SliceSourceSpan(sourceSpan, 0, text.Length, text.Length) : SourceSpan.Empty;
            _tracker.AddCodeBlockChunk(codeBox, BuildCodeBlockChunk(metadata, text, 0, chunkSourceSpan, projection));
            return codeBox;
        }

        int offset = 0;
        var chunkSourceCursor = new CodeBlockSourceCursor();
        while (offset < text.Length)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int length = GetCodeBlockChunkLength(text, offset, _context.CancellationToken);
            MarkdownSourceMap.SourceProjection? projection = hasExactParserSourceMap
                ? _sourceProjector.TryCreateCodeBlockChunkProjection(
                    parsedBlock!, text, offset, length, sourceSpan,
                    includeSourcePrefix: offset == 0,
                    includeSourceSuffix: offset + length == text.Length,
                    ref chunkSourceCursor)
                : null;
            SourceSpan chunkSourceSpan = parsedBlock is not null
                ? projection is not null ? sourceSpan : SourceSpan.Empty
                : SliceSourceSpan(sourceSpan, offset, length, text.Length);

            _tracker.AddCodeBlockChunk(codeBox, BuildCodeBlockChunk(
                metadata,
                text.Substring(offset, length),
                offset,
                chunkSourceSpan,
                projection));
            offset += length;
        }

        return codeBox;
    }

    private static int GetCodeBlockChunkLength(
        string text,
        int offset,
        CancellationToken cancellationToken)
    {
        int maximumLength = Math.Min(MaxMonolithicTextLayoutLength, text.Length - offset);
        int maximumEnd = offset + maximumLength;
        int searchStart = offset;
        int lineBreaks = 0;
        int preferredLength = maximumLength;
        while (searchStart < maximumEnd)
        {
            int newline = text.IndexOf('\n', searchStart, maximumEnd - searchStart);
            if (newline < 0)
                break;
            lineBreaks++;
            searchStart = newline + 1;
            if (lineBreaks >= MaxCodeBlockLinesPerChunk)
            {
                preferredLength = searchStart - offset;
                break;
            }
        }

        if (preferredLength == maximumLength && maximumEnd < text.Length)
        {
            int newline = text.LastIndexOf('\n', maximumEnd - 1, maximumLength);
            if (newline > offset)
                preferredLength = newline - offset + 1;
        }

        return CodeBlockChunkBoundary.GetLinePreservingLength(
            text,
            offset,
            preferredLength,
            maximumLength,
            cancellationToken);
    }

    private InlineContainerBox BuildCodeBlockChunk(
        CodeBlockMetadata metadata,
        string text,
        int textOffset,
        SourceSpan sourceSpan,
        MarkdownSourceMap.SourceProjection? sourceProjection)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.CodeBlock)
        {
            CodeLanguage = metadata.Language,
            CodeBlockTextOffset = textOffset,
            CodeBlockTextLength = text.Length,
            WordWrapping = _context.CodeBlockWrappingMode == CodeBlockWrappingMode.Wrap
                ? Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.Wrap
                : Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
        };
        _tracker.TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        // No ElementKey on the run: it inherits the container's CodeBlock style.
        // Setting ElementKey = CodeBlock would cause DrawDecorations to draw a
        // per-run background on top of the container-level background (double bg).
        var run = new TextRun(text)
        {
            SourceSpan = sourceSpan,
        };
        box.Add(run, sourceProjection);
        return box;
    }

    private static int CountLogicalLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int lines = 1;
        foreach (char ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return text.EndsWith("\n", StringComparison.Ordinal) && lines > 1 ? lines - 1 : lines;
    }

    private static SourceSpan SliceSourceSpan(
        SourceSpan sourceSpan,
        int textOffset,
        int textLength,
        int totalTextLength)
    {
        if (sourceSpan.IsEmpty || totalTextLength <= 0)
            return SourceSpan.Empty;

        double scale = sourceSpan.Length / (double)totalTextLength;
        int start = sourceSpan.Start + (int)Math.Round(textOffset * scale);
        int end = sourceSpan.Start + (int)Math.Round((textOffset + textLength) * scale);
        return new SourceSpan(start, Math.Max(0, end - start));
    }
}
