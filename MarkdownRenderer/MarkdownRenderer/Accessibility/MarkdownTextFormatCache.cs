using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Immutable effective formatting for one committed semantic-document/theme
/// identity. It intentionally stores only value-like style projections: it
/// does not retain a range provider, automation peer, owner control, or inline
/// run that could in turn retain a realized XAML element.
/// </summary>
internal sealed class MarkdownTextFormatCache
{
    private static readonly ElementStyle DefaultStyle = new();
    private readonly MarkdownTextStyleRun[] _runs;
    private readonly int[] _boundaries;

    private MarkdownTextFormatCache(
        MarkdownSemanticDocument document,
        ThemeSnapshot? theme,
        MarkdownTextStyleRun[] runs,
        int[] boundaries)
    {
        Document = document;
        Theme = theme;
        _runs = runs;
        _boundaries = boundaries;
    }

    internal MarkdownSemanticDocument Document { get; }

    internal ThemeSnapshot? Theme { get; }

    internal int RunCount => _runs.Length;

    internal MarkdownTextStyleRun GetRun(int index) => _runs[index];

    internal bool Matches(MarkdownSemanticDocument document, ThemeSnapshot? theme) =>
        ReferenceEquals(Document, document) && ReferenceEquals(Theme, theme);

    internal int MoveAcrossBoundaries(int current, int count, out int moved) =>
        MarkdownTextRangeProvider.MoveAcrossSortedBoundaries(
            _boundaries,
            current,
            count,
            out moved);

    internal static MarkdownTextFormatCache Create(
        MarkdownSemanticDocument document,
        ThemeSnapshot? theme)
    {
        ArgumentNullException.ThrowIfNull(document);

        var raw = new List<MarkdownTextStyleRun>(document.TextSpans.Count);
        AppendRawRuns(raw, document, theme);
        raw.Sort(static (left, right) =>
        {
            int byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : left.End.CompareTo(right.End);
        });

        var normalized = new List<MarkdownTextStyleRun>(Math.Max(1, raw.Count + 1));
        ElementStyle bodyStyle = ResolveStyle(theme, MarkdownElementKeys.Body);
        int documentLength = document.Text.Length;
        int cursor = 0;
        foreach (MarkdownTextStyleRun candidate in raw)
        {
            int start = Math.Clamp(candidate.Start, cursor, documentLength);
            int end = Math.Clamp(candidate.End, start, documentLength);
            if (start > cursor)
            {
                AppendNormalizedRun(normalized, new MarkdownTextStyleRun(
                    cursor,
                    start,
                    MarkdownElementKeys.Body,
                    IsSubscript: false,
                    IsSuperscript: false,
                    bodyStyle));
            }

            if (end > start || (documentLength == 0 && normalized.Count == 0))
                AppendNormalizedRun(normalized, candidate with { Start = start, End = end });
            cursor = Math.Max(cursor, end);
        }

        if (cursor < documentLength || normalized.Count == 0)
        {
            AppendNormalizedRun(normalized, new MarkdownTextStyleRun(
                cursor,
                documentLength,
                MarkdownElementKeys.Body,
                IsSubscript: false,
                IsSuperscript: false,
                bodyStyle));
        }

        var boundaries = new List<int>(Math.Max(2, (normalized.Count * 2) + 1));
        AppendBoundary(boundaries, 0);
        foreach (MarkdownTextStyleRun run in normalized)
        {
            AppendBoundary(boundaries, run.Start);
            AppendBoundary(boundaries, run.End);
        }
        AppendBoundary(boundaries, documentLength);

        return new MarkdownTextFormatCache(
            document,
            theme,
            normalized.ToArray(),
            boundaries.ToArray());
    }

    private static void AppendRawRuns(
        List<MarkdownTextStyleRun> result,
        MarkdownSemanticDocument document,
        ThemeSnapshot? theme)
    {
        int documentLength = document.Text.Length;
        foreach (MarkdownTextSpan span in document.TextSpans)
        {
            if (span.TextEnd < 0 || span.TextStart > documentLength)
                continue;

            if (span.InlineBox is { } inline)
            {
                if (span.InlineRun is { } run)
                {
                    string elementKey = string.IsNullOrEmpty(run.ElementKey)
                        ? inline.ElementKey
                        : run.ElementKey;
                    result.Add(CreateRun(
                        span.TextStart,
                        span.TextEnd,
                        elementKey,
                        run,
                        ResolveStyle(theme, elementKey)));
                }
                else
                {
                    AppendInlineRuns(result, inline, span.TextStart, theme);
                }

                continue;
            }

            if (span.ImageBox is null &&
                span.EmbedBox is null &&
                span.HostedElementBox is null &&
                span.VectorSceneBox is null)
            {
                continue;
            }

            string atomicElementKey = span.VectorSceneBox?.ElementKey ?? MarkdownElementKeys.Body;
            ElementStyle atomicStyle = span.VectorSceneBox is { } vector
                ? vector.ResolveSemanticTextStyle(span.VectorSemanticIndex)
                : ResolveStyle(theme, MarkdownElementKeys.Body);
            result.Add(new MarkdownTextStyleRun(
                span.TextStart,
                span.TextEnd,
                atomicElementKey,
                IsSubscript: false,
                IsSuperscript: false,
                atomicStyle));
        }
    }

    private static void AppendInlineRuns(
        List<MarkdownTextStyleRun> result,
        InlineContainerBox inline,
        int textSpanStart,
        ThemeSnapshot? theme)
    {
        int cumulative = 0;
        foreach (InlineRun run in inline.Runs)
        {
            int length = run.Text.Length;
            if (length <= 0)
                continue;

            int runStart = textSpanStart + cumulative;
            int runEnd = runStart + length;
            cumulative += length;
            string elementKey = string.IsNullOrEmpty(run.ElementKey)
                ? inline.ElementKey
                : run.ElementKey;
            result.Add(CreateRun(
                runStart,
                runEnd,
                elementKey,
                run,
                ResolveStyle(theme, elementKey)));
        }
    }

    private static MarkdownTextStyleRun CreateRun(
        int start,
        int end,
        string elementKey,
        InlineRun run,
        ElementStyle style) =>
        new(
            start,
            end,
            elementKey,
            run is SubscriptRun,
            run is SuperscriptRun or LinkRun { IsSuperscript: true },
            style);

    private static ElementStyle ResolveStyle(ThemeSnapshot? theme, string elementKey) =>
        theme?.GetStyle(elementKey) ?? DefaultStyle;

    private static void AppendBoundary(List<int> boundaries, int value)
    {
        if (boundaries.Count == 0 || boundaries[^1] != value)
            boundaries.Add(value);
    }

    private static void AppendNormalizedRun(
        List<MarkdownTextStyleRun> result,
        MarkdownTextStyleRun next)
    {
        if (result.Count > 0 &&
            result[^1].End == next.Start &&
            HaveEquivalentFormat(result[^1], next))
        {
            result[^1] = result[^1] with { End = next.End };
            return;
        }

        result.Add(next);
    }

    private static bool HaveEquivalentFormat(
        MarkdownTextStyleRun left,
        MarkdownTextStyleRun right)
    {
        ElementStyle a = left.Style;
        ElementStyle b = right.Style;
        return string.Equals(left.ElementKey, right.ElementKey, StringComparison.Ordinal) &&
               a.Background == b.Background &&
               string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal) &&
               Math.Abs(a.FontSize - b.FontSize) < 0.001f &&
               a.FontWeight.Weight == b.FontWeight.Weight &&
               a.Foreground == b.Foreground &&
               a.FontStyle == b.FontStyle &&
               a.Strikethrough == b.Strikethrough &&
               a.Underline == b.Underline &&
               left.IsSubscript == right.IsSubscript &&
               left.IsSuperscript == right.IsSuperscript;
    }
}

/// <summary>
/// Owns the single format cache associated with an automation peer. Cache hits
/// are lock-free; identity changes rebuild once and publish an immutable value.
/// </summary>
internal sealed class MarkdownTextFormatCacheStore
{
    private readonly object _gate = new();
    private MarkdownTextFormatCache? _cache;
    private int _buildCount;

    internal int BuildCount => Volatile.Read(ref _buildCount);

    internal MarkdownTextFormatCache GetOrCreate(
        MarkdownSemanticDocument document,
        ThemeSnapshot? theme)
    {
        ArgumentNullException.ThrowIfNull(document);

        MarkdownTextFormatCache? cache = Volatile.Read(ref _cache);
        if (cache is not null && cache.Matches(document, theme))
            return cache;

        lock (_gate)
        {
            cache = _cache;
            if (cache is not null && cache.Matches(document, theme))
                return cache;

            cache = MarkdownTextFormatCache.Create(document, theme);
            Interlocked.Increment(ref _buildCount);
            Volatile.Write(ref _cache, cache);
            return cache;
        }
    }

    internal void Invalidate() => Volatile.Write(ref _cache, null);
}

internal readonly record struct MarkdownTextStyleRun(
    int Start,
    int End,
    string ElementKey,
    bool IsSubscript,
    bool IsSuperscript,
    ElementStyle Style);
