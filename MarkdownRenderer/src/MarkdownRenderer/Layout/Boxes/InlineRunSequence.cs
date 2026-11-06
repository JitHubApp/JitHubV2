using System;
using System.Collections.Generic;
using System.Text;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The ordered runs of one <see cref="InlineContainerBox"/>, the image and
/// embed indexes and text length derived from them, and the concatenated
/// layout buffer built from their text.
/// </summary>
/// <remarks>
/// The box owns this by value in a private field and mutates it in place, so
/// it adds no allocation beyond the run list itself. It is never copied out:
/// a copy would share the run list while carrying stale indexes.
/// </remarks>
internal struct InlineRunSequence
{
    private readonly SingletonMutableList<InlineRun> _runs;
    private List<(InlineImageRun Run, int Offset)>? _inlineImageRuns;
    private List<(InlineEmbedRun Run, int Offset)>? _inlineEmbedRuns;
    private bool _hasAtomicInlineObject;
    private int _runTextLength;
    private string _buffer;
    private bool _bufferDirty;

    public InlineRunSequence()
    {
        _runs = new();
        _buffer = string.Empty;
    }

    internal readonly SingletonMutableList<InlineRun> Runs => _runs;

    internal readonly List<(InlineImageRun Run, int Offset)>? ImageEntries => _inlineImageRuns;

    internal readonly List<(InlineEmbedRun Run, int Offset)>? EmbedEntries => _inlineEmbedRuns;

    internal readonly bool HasAtomicInlineObject => _hasAtomicInlineObject;

    internal readonly int TextLength => _runTextLength;

    internal readonly string Buffer => _buffer;

    internal readonly bool IsBufferDirty => _bufferDirty;

    /// <summary>
    /// Appends <paramref name="run"/> at the next ordinal and records its
    /// offset in the image or embed index.
    /// </summary>
    /// <param name="run">The run to append.</param>
    /// <param name="blockIndex">The owning box's logical block index.</param>
    internal void Append(InlineRun run, int blockIndex)
    {
        run.InlineIndex = _runs.Count;
        int runOffset = _runTextLength;
        _runTextLength += run.TextLength;
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            _hasAtomicInlineObject = true;
        if (run is InlineImageRun imageRun)
        {
            (_inlineImageRuns ??= new()).Add((imageRun, runOffset));
            // The image participates in asynchronous relayout and UIA using
            // the owning paragraph's logical block. Keep that identity on the
            // nested ImageBox so a late load can invalidate only this owner.
            imageRun.Image.BlockIndex = blockIndex;
        }
        else if (run is InlineEmbedRun embedRun)
        {
            (_inlineEmbedRuns ??= new()).Add((embedRun, runOffset));
        }
        _runs.Add(run);
    }

    /// <summary>Marks the layout buffer stale until it is next built.</summary>
    internal void MarkBufferDirty() => _bufferDirty = true;

    /// <summary>Records a copied image run of a private placement candidate.</summary>
    /// <param name="imageRun">The copied image run.</param>
    /// <param name="offset">Its buffer offset.</param>
    internal void AddImageEntry(InlineImageRun imageRun, int offset)
        => (_inlineImageRuns ??= new()).Add((imageRun, offset));

    /// <summary>Sets a private placement candidate's copied text length.</summary>
    /// <param name="textLength">The source's text length.</param>
    internal void SetTextLength(int textLength) => _runTextLength = textLength;

    /// <summary>Shares a placement source's immutable buffer and its staleness.</summary>
    /// <param name="source">The placement source's sequence.</param>
    internal void CopyBufferFrom(in InlineRunSequence source)
    {
        _buffer = source._buffer;
        _bufferDirty = source._bufferDirty;
    }

    /// <summary>Copies a placement source's atomic-object flag.</summary>
    /// <param name="source">The placement source's sequence.</param>
    internal void CopyAtomicObjectFlagFrom(in InlineRunSequence source)
        => _hasAtomicInlineObject = source._hasAtomicInlineObject;

    /// <summary>Restores the buffer captured before a failed measure.</summary>
    /// <param name="buffer">The captured buffer.</param>
    /// <param name="bufferDirty">The captured staleness.</param>
    internal void RestoreBuffer(string buffer, bool bufferDirty)
    {
        _buffer = buffer;
        _bufferDirty = bufferDirty;
    }

    /// <summary>
    /// Removes every image run in order and returns them; nothing changes
    /// when there are none. The caller rebuilds the indexes.
    /// </summary>
    internal InlineImageRun[] RemoveImageRuns()
    {
        List<(InlineImageRun Run, int Offset)>? imageEntries = _inlineImageRuns;
        if (imageEntries is null || imageEntries.Count == 0)
            return Array.Empty<InlineImageRun>();

        var detached = new InlineImageRun[imageEntries.Count];
        for (int index = 0; index < imageEntries.Count; index++)
            detached[index] = imageEntries[index].Run;

        for (int index = _runs.Count - 1; index >= 0; index--)
        {
            if (_runs[index] is InlineImageRun)
                _runs.RemoveAt(index);
        }

        return detached;
    }

    /// <summary>
    /// Removes every image and spacer run, preserving their visual order;
    /// nothing changes when there are none. The caller rebuilds the indexes.
    /// </summary>
    internal InlineRun[] RemoveImageAndSpacerRuns()
    {
        int detachedCount = 0;
        foreach (InlineRun run in _runs)
        {
            if (run is InlineImageRun or InlineSpacerRun)
                detachedCount++;
        }

        if (detachedCount == 0)
            return Array.Empty<InlineRun>();

        var detached = new InlineRun[detachedCount];
        int writeIndex = detached.Length;
        for (int index = _runs.Count - 1; index >= 0; index--)
        {
            InlineRun run = _runs[index];
            if (run is InlineImageRun or InlineSpacerRun)
            {
                detached[--writeIndex] = run;
                _runs.RemoveAt(index);
            }
        }

        return detached;
    }

    /// <summary>
    /// Re-derives ordinals, indexes, text length, the owner's vector-run
    /// counters and alias overrides after runs were detached, and marks the
    /// buffer stale.
    /// </summary>
    /// <param name="styleCache">The owner's run-style cache, cleared and refilled.</param>
    /// <param name="vectorState">The owner's vector-scene state, created on the first vector run.</param>
    /// <param name="containerAliases">The owner's container style aliases.</param>
    internal void RebuildAfterTransfer(
        ref InlineRunStyleCache styleCache,
        ref InlineVectorSceneState? vectorState,
        IReadOnlyList<string> containerAliases)
    {
        _inlineImageRuns = null;
        _inlineEmbedRuns = null;
        _hasAtomicInlineObject = false;
        _runTextLength = 0;
        styleCache.Clear();

        for (int index = 0; index < _runs.Count; index++)
        {
            InlineRun run = _runs[index];
            run.InlineIndex = index;
            int runOffset = _runTextLength;
            int runLength = run.TextLength;
            _runTextLength += runLength;

            if (run is InlineEmbedRun embedRun)
            {
                (_inlineEmbedRuns ??= new()).Add((embedRun, runOffset));
                _hasAtomicInlineObject = true;
            }
            else if (run is InlineVectorSceneRun vectorScene)
            {
                _hasAtomicInlineObject = true;
                InlineVectorSceneState state = vectorState ??= new InlineVectorSceneState();
                state.VectorSceneRunCount++;
                state.HasDeferredSceneMarker |= vectorScene.DeferredSceneMarker is not null;
            }
            else if (run is InlineSpacerRun)
            {
                _hasAtomicInlineObject = true;
            }

            styleCache.AppendRebuiltAliasOverride(
                index,
                _runs.Count,
                run.StyleAliases.Count == 0 ? null : InlineRunStyleCache.CombineAliases(containerAliases, run.StyleAliases));
        }

        _buffer = string.Empty;
        _bufferDirty = true;
    }

    /// <summary>Builds the layout buffer if needed; an empty sequence has an empty, current buffer.</summary>
    /// <param name="context">The owner's layout context, whose cancellation is observed per run.</param>
    internal void EnsureBuilt(MarkdownLayoutContext context)
    {
        if (_runs.Count == 0) { _buffer = string.Empty; _bufferDirty = false; return; }
        // Rely solely on _bufferDirty (set by Add()) — the _buffer.Length == 0 fallback
        // would cause repeated BuildBuffer() calls for boxes whose runs all produce empty
        // text (e.g. embed-placeholder runs), burning CPU without changing anything.
        if (_bufferDirty) Build(context);
    }

    /// <summary>Rebuilds the layout buffer only when a run changed since the last build.</summary>
    /// <param name="context">The owner's layout context, whose cancellation is observed per run.</param>
    internal void BuildIfDirty(MarkdownLayoutContext context)
    {
        if (_bufferDirty) Build(context);
    }

    private void Build(MarkdownLayoutContext context)
    {
        // Size the builder to the runs' known total so the buffer is built in
        // one character allocation without a per-thread pooled builder.
        var sb = new StringBuilder(Math.Max(0, _runTextLength));
        foreach (var run in _runs)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            run.AppendLayoutTextTo(sb);
        }
        _buffer = sb.ToString();
        _bufferDirty = false;
    }

    internal readonly int GetBufferCharOffset(DocumentPosition pos)
    {
        int offset = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            if (i == pos.InlineIndex) return offset + Math.Clamp(pos.CharacterOffset, 0, _runs[i].TextLength);
            offset += _runs[i].TextLength;
        }
        return offset;
    }

    internal readonly DocumentPosition GetPositionFromBufferOffset(int blockIndex, int bufOffset)
    {
        bufOffset = Math.Max(0, bufOffset);
        int offset = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            int len = _runs[i].TextLength;
            if (bufOffset < offset + len)
                return new DocumentPosition(blockIndex, _runs[i].InlineIndex, bufOffset - offset);
            // At exact run boundary: prefer start of next run (continue loop).
            offset += len;
        }
        // bufOffset >= total length: clamp to end of last run.
        if (_runs.Count == 0) return new DocumentPosition(blockIndex, 0, 0);
        int last = _runs.Count - 1;
        return new DocumentPosition(blockIndex, last, _runs[last].TextLength);
    }

    /// <summary>
    /// Returns the start and end positions that span the word containing
    /// <paramref name="pos"/>, building the buffer first if needed.
    /// </summary>
    /// <param name="context">The owner's layout context.</param>
    /// <param name="blockIndex">The owner's logical block index.</param>
    /// <param name="pos">A position inside the owner.</param>
    internal (DocumentPosition Start, DocumentPosition End) GetWordBoundaries(
        MarkdownLayoutContext context,
        int blockIndex,
        DocumentPosition pos)
    {
        EnsureBuilt(context);
        if (_buffer.Length == 0) return (pos, pos);
        int idx = Math.Clamp(GetBufferCharOffset(pos), 0, Math.Max(0, _buffer.Length - 1));
        var (start, end) = TextBoundaryHelper.FindWordBoundaries(_buffer, idx);
        return (GetPositionFromBufferOffset(blockIndex, start), GetPositionFromBufferOffset(blockIndex, end));
    }

    internal readonly (DocumentPosition Start, DocumentPosition End) GetBlockBoundaries(int blockIndex)
    {
        var start = new DocumentPosition(blockIndex, 0, 0);
        DocumentPosition end;
        if (_runs.Count == 0)
        {
            end = start;
        }
        else
        {
            int last = _runs.Count - 1;
            end = new DocumentPosition(blockIndex, last, _runs[last].TextLength);
        }
        return (start, end);
    }
}
