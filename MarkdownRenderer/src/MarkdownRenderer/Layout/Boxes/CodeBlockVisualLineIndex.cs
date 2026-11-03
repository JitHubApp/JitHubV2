using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Maps every wrapped visual row of a code block to its logical line and
/// vertical extent. The index reuses its template while chunk inputs are
/// unchanged and only refreshes origins after a scroll or re-arrange, so
/// gutter painting stays proportional to the visible rows.
/// </summary>
internal sealed class CodeBlockVisualLineIndex
{
    private readonly List<InlineContainerBox> _chunks;
    private readonly List<CodeLineInfo> _lines;
    private readonly string _codeText;
    private float[] _suppressedTrailingEmptyLineHeights = Array.Empty<float>();
    private CodeVisualLineInfo[] _visualLines = Array.Empty<CodeVisualLineInfo>();
    private double[] _visualLineBottomEdges = Array.Empty<double>();
    private VisualLineChunkCache[] _visualLineChunkCache = Array.Empty<VisualLineChunkCache>();
    private bool _visualLineTemplateValid;
    private int _visualLineCacheBuildCount;

    internal CodeBlockVisualLineIndex(List<InlineContainerBox> chunks, List<CodeLineInfo> lines, string codeText)
    {
        _chunks = chunks;
        _lines = lines;
        _codeText = codeText;
    }

    internal int Count => _visualLines.Length;

    internal int CacheBuildCount => _visualLineCacheBuildCount;

    /// <summary>Visual rows in document order; replaced, never mutated in place, on a template rebuild.</summary>
    internal CodeVisualLineInfo[] Lines => _visualLines;

    /// <summary>Bottom edge of each row in <see cref="Lines"/>, for viewport search.</summary>
    internal double[] BottomEdges => _visualLineBottomEdges;

    /// <summary>Per-chunk template inputs; entries are cleared whenever the index falls back.</summary>
    internal VisualLineChunkCache[] ChunkCache => _visualLineChunkCache;

    /// <summary>Forces the next rebuild to recompute the template after a chunk change.</summary>
    internal void InvalidateTemplate() => _visualLineTemplateValid = false;

    /// <summary>Clears per-chunk duplicate-row suppression before a measure pass.</summary>
    internal void ResetSuppressedTrailingEmptyLines()
    {
        if (_suppressedTrailingEmptyLineHeights.Length != _chunks.Count)
            _suppressedTrailingEmptyLineHeights = new float[_chunks.Count];
        else
            Array.Clear(_suppressedTrailingEmptyLineHeights);
    }

    internal void SuppressTrailingEmptyLine(int chunkIndex, float height) =>
        _suppressedTrailingEmptyLineHeights[chunkIndex] = height;

    /// <summary>Drops the index and its per-chunk cache when the box is disposed.</summary>
    internal void Release()
    {
        ClearVisualLineIndex();
        _visualLineChunkCache = Array.Empty<VisualLineChunkCache>();
    }

    internal void RebuildVisualLineIndex()
    {
        if (_chunks.Count == 0 || _lines.Count == 0)
        {
            ClearVisualLineIndex();
            return;
        }

        if (_visualLineChunkCache.Length != _chunks.Count)
        {
            _visualLineChunkCache = new VisualLineChunkCache[_chunks.Count];
            _visualLineTemplateValid = false;
        }

        bool previousTemplateValid = _visualLineTemplateValid &&
                                     _visualLines.Length == _visualLineBottomEdges.Length;
        _visualLineTemplateValid = false;
        if (!TryCaptureChunkInputs(previousTemplateValid, out bool inputsMatch, out bool allOriginsUnchanged))
        {
            ClearVisualLineIndex();
            return;
        }

        if (inputsMatch && _visualLines.Length > 0)
        {
            if (allOriginsUnchanged)
            {
                _visualLineTemplateValid = true;
                return;
            }

            if (!TryRefreshVisualLineOrigins())
            {
                ClearVisualLineIndex();
                return;
            }

            _visualLineTemplateValid = true;
            return;
        }

        if (!TryBuildVisualLineIndex())
        {
            ClearVisualLineIndex();
            return;
        }

        _visualLineTemplateValid = true;
        if (_visualLineCacheBuildCount < int.MaxValue)
            _visualLineCacheBuildCount++;
    }

    // Records each chunk's current metrics and text origin in the per-chunk
    // cache and reports whether every input, and every origin, still matches
    // the previous template. Returns false when a chunk has no usable metrics
    // or origin yet.
    private bool TryCaptureChunkInputs(
        bool previousTemplateValid,
        out bool inputsMatch,
        out bool allOriginsUnchanged)
    {
        inputsMatch = previousTemplateValid;
        allOriginsUnchanged = previousTemplateValid;

        for (int index = 0; index < _chunks.Count; index++)
        {
            InlineContainerBox chunk = _chunks[index];
            CanvasLineMetrics[]? metrics = chunk.GetLineMetricsSnapshot();
            if (metrics is null || metrics.Length == 0)
                return false;
            double originY = chunk.GetTextOriginY();
            if (!double.IsFinite(originY))
                return false;

            VisualLineChunkCache previous = _visualLineChunkCache[index];
            int lineCount = GetEffectiveNativeLineCount(index, chunk, metrics);
            bool inputMatches = previousTemplateValid &&
                                ReferenceEquals(previous.Chunk, chunk) &&
                                ReferenceEquals(previous.Metrics, metrics) &&
                                previous.TextOffset == chunk.CodeBlockTextOffset &&
                                previous.TextLength == chunk.CodeBlockTextLength &&
                                previous.LineCount == lineCount;
            bool originUnchanged = inputMatches && previous.OriginY == originY;
            inputsMatch &= inputMatches;
            allOriginsUnchanged &= originUnchanged;

            _visualLineChunkCache[index] = new VisualLineChunkCache
            {
                Chunk = chunk,
                Metrics = metrics,
                TextOffset = chunk.CodeBlockTextOffset,
                TextLength = chunk.CodeBlockTextLength,
                OriginY = originY,
                StartIndex = previous.StartIndex,
                LineCount = lineCount,
                OriginUnchanged = originUnchanged,
            };
        }

        return true;
    }

    private bool TryBuildVisualLineIndex()
    {
        int totalLineCount = 0;
        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            CanvasLineMetrics[] metrics = chunk.Metrics!;
            if (chunk.LineCount > int.MaxValue - totalLineCount)
                return false;
            totalLineCount += chunk.LineCount;

            double top = chunk.OriginY;
            long localOffset = 0;
            for (int metricIndex = 0; metricIndex < chunk.LineCount; metricIndex++)
            {
                CanvasLineMetrics metric = metrics[metricIndex];
                if (!double.IsFinite(metric.Height))
                    return false;

                double height = Math.Max(1, metric.Height);
                if (!double.IsFinite(top + height))
                    return false;
                top += height;

                int characterCount = Math.Max(0, metric.CharacterCount);
                if (characterCount > int.MaxValue - localOffset ||
                    (long)chunk.TextOffset + localOffset > int.MaxValue)
                    return false;
                localOffset += characterCount;
            }
        }

        if (totalLineCount == 0)
            return false;

        var visualLines = new CodeVisualLineInfo[totalLineCount];
        var bottomEdges = new double[totalLineCount];
        int visualIndex = 0;
        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            CanvasLineMetrics[] metrics = chunk.Metrics!;
            double top = chunk.OriginY;
            long localOffset = 0;
            int startIndex = visualIndex;
            for (int metricIndex = 0; metricIndex < chunk.LineCount; metricIndex++)
            {
                CanvasLineMetrics metric = metrics[metricIndex];
                int globalOffset = (int)((long)chunk.TextOffset + localOffset);
                int logicalLine = FindFirstLineEndingAtOrAfter(globalOffset);
                if (logicalLine >= _lines.Count)
                    logicalLine = _lines.Count - 1;

                double height = Math.Max(1, metric.Height);
                visualLines[visualIndex] = new CodeVisualLineInfo(logicalLine, top, height);
                bottomEdges[visualIndex] = top + height;
                visualIndex++;
                top += height;
                localOffset += Math.Max(0, metric.CharacterCount);
            }

            chunk.StartIndex = startIndex;
            _visualLineChunkCache[chunkIndex] = chunk;
        }

        _visualLines = visualLines;
        _visualLineBottomEdges = bottomEdges;
        return true;
    }

    private int GetEffectiveNativeLineCount(
        int chunkIndex,
        InlineContainerBox chunk,
        CanvasLineMetrics[] metrics)
    {
        if (!TryGetInterChunkTrailingEmptyLineHeight(chunkIndex, chunk, metrics, out float lineHeight) ||
            chunkIndex >= _suppressedTrailingEmptyLineHeights.Length ||
            _suppressedTrailingEmptyLineHeights[chunkIndex] != lineHeight)
        {
            return metrics.Length;
        }

        return metrics.Length - 1;
    }

    internal bool TryGetInterChunkTrailingEmptyLineHeight(
        int chunkIndex,
        InlineContainerBox chunk,
        CanvasLineMetrics[]? metrics,
        out float height)
    {
        height = 0;
        if (chunkIndex < 0 ||
            chunkIndex >= _chunks.Count - 1 ||
            chunk.CodeBlockTextOffset < 0 ||
            chunk.CodeBlockTextLength <= 0 ||
            chunk.CodeBlockTextLength > _codeText.Length - chunk.CodeBlockTextOffset ||
            chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength !=
                _chunks[chunkIndex + 1].CodeBlockTextOffset ||
            _chunks[chunkIndex + 1].CodeBlockTextLength <= 0 ||
            metrics is not { Length: > 1 } ||
            _codeText[chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength - 1] != '\n')
        {
            return false;
        }

        CanvasLineMetrics trailing = metrics[^1];
        if (trailing.CharacterCount != 0 ||
            !float.IsFinite(trailing.Height) ||
            trailing.Height < 1f)
        {
            return false;
        }

        height = trailing.Height;
        return true;
    }

    private bool TryRefreshVisualLineOrigins()
    {
        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            if (chunk.OriginUnchanged)
                continue;

            double top = chunk.OriginY;
            if (chunk.StartIndex < 0 || chunk.StartIndex > _visualLines.Length ||
                chunk.LineCount > _visualLines.Length - chunk.StartIndex)
                return false;
            int endIndex = chunk.StartIndex + chunk.LineCount;
            for (int index = chunk.StartIndex; index < endIndex; index++)
            {
                double height = _visualLines[index].Height;
                if (!double.IsFinite(top + height))
                    return false;
                top += height;
            }
        }

        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            if (chunk.OriginUnchanged)
                continue;

            double top = chunk.OriginY;
            int endIndex = chunk.StartIndex + chunk.LineCount;
            for (int index = chunk.StartIndex; index < endIndex; index++)
            {
                CodeVisualLineInfo old = _visualLines[index];
                _visualLines[index] = new CodeVisualLineInfo(old.LogicalLineIndex, top, old.Height);
                _visualLineBottomEdges[index] = top + old.Height;
                top += old.Height;
            }
        }

        return true;
    }

    private void ClearVisualLineIndex()
    {
        _visualLineTemplateValid = false;
        _visualLines = Array.Empty<CodeVisualLineInfo>();
        _visualLineBottomEdges = Array.Empty<double>();
        Array.Clear(_visualLineChunkCache, 0, _visualLineChunkCache.Length);
    }

    internal int FindFirstLineEndingAtOrAfter(int textOffset)
    {
        int low = 0;
        int high = _lines.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            CodeLineInfo line = _lines[middle];
            int end = line.Start + Math.Max(1, line.Length);
            if (end <= textOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
