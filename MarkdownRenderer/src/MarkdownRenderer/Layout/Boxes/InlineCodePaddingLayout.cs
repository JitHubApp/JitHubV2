using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Applies inline-code horizontal padding to a measured text layout.
/// Endpoint caret directions and visual anchors are captured before any
/// DirectWrite spacing mutation, so padding attaches to the visual outer
/// edges of mixed-direction code spans.
/// </summary>
internal static class InlineCodePaddingLayout
{
    internal const int InlineCodeGraphemeCancellationCheckInterval = 64;

    internal static void QueueLinkInlineCodePadding(
        InlineContainerBox box,
        ref List<InlineCodePaddingRange>? ranges,
        int linkStart,
        LinkInlineStyleMap styleMap,
        ElementStyle[] spanStyles,
        bool observeCancellation)
    {
        int groupStart = -1;
        int groupEnd = -1;
        ElementStyle groupStyle = default!;
        LinkInlineStylePath? groupCodePath = null;
        for (int index = 0; index < styleMap.Count; index++)
        {
            if (observeCancellation &&
                (index % InlineCodeGraphemeCancellationCheckInterval) == 0)
            {
                box.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            LinkInlineStyleSpan span = styleMap.GetSpan(index);
            LinkInlineStylePath? codePath = FindInlineCodeStylePath(span.Path);
            bool hasCodeStyle = codePath is not null;
            int spanStart = linkStart + span.Start;
            int spanEnd = spanStart + span.Length;
            if (!hasCodeStyle || (groupStart >= 0 &&
                (spanStart != groupEnd || !ReferenceEquals(codePath, groupCodePath))))
            {
                if (groupStart >= 0)
                    QueueInlineCodePaddingRange(
                        box,
                        ref ranges, groupStart, groupEnd - groupStart, groupStyle.Padding,
                        observeCancellation);
                groupStart = -1;
                groupEnd = -1;
                groupCodePath = null;
            }

            if (!hasCodeStyle)
                continue;

            if (groupStart < 0)
            {
                groupStart = spanStart;
                groupStyle = spanStyles[index];
                groupCodePath = codePath;
            }
            groupEnd = spanEnd;
        }

        if (groupStart >= 0)
        {
            if (observeCancellation)
                box.Context.CancellationToken.ThrowIfCancellationRequested();
            QueueInlineCodePaddingRange(
                box,
                ref ranges, groupStart, groupEnd - groupStart, groupStyle.Padding,
                observeCancellation);
        }
    }

    internal static bool HasInlineCodeStylePath(LinkInlineStylePath path)
        => FindInlineCodeStylePath(path) is not null;

    private static LinkInlineStylePath? FindInlineCodeStylePath(LinkInlineStylePath path)
    {
        for (LinkInlineStylePath? current = path; current is not null; current = current.Parent)
        {
            if (current.StyleKey == MarkdownElementKeys.CodeInline)
                return current;
        }

        return null;
    }

    internal static void QueueInlineCodePaddingRange(
        InlineContainerBox box,
        ref List<InlineCodePaddingRange>? ranges,
        int start,
        int length,
        Thickness padding,
        bool observeCancellation)
    {
        if (observeCancellation)
            box.Context.CancellationToken.ThrowIfCancellationRequested();

        if (length <= 0 || start < 0 || start > box.LayoutBuffer.Length - length)
            return;

        float leftPadding = ToInlineCodeAdvance(padding.Left);
        float rightPadding = ToInlineCodeAdvance(padding.Right);
        if (leftPadding == 0f && rightPadding == 0f)
            return;

        (ranges ??= new List<InlineCodePaddingRange>()).Add(
            new InlineCodePaddingRange(start, length, leftPadding, rightPadding));
    }

    internal static void ApplyInlineCodePaddingRanges(
        InlineContainerBox box,
        CanvasTextLayout layout,
        List<InlineCodePaddingRange>? ranges,
        bool observeCancellation)
    {
        if (ranges is null)
            return;

        if (observeCancellation)
            box.Context.CancellationToken.ThrowIfCancellationRequested();

        CancellationToken cancellationToken = observeCancellation
            ? box.Context.CancellationToken
            : CancellationToken.None;

        // Resolve every endpoint while the layout still has its final text
        // styles but no code-padding mutations. SetCharacterSpacing can
        // invalidate DirectWrite's layout; do not interleave caret queries
        // with those mutations for code-heavy documents.
        for (int index = 0; index < ranges.Count; index++)
        {
            if (observeCancellation)
                cancellationToken.ThrowIfCancellationRequested();

            InlineCodePaddingRange range = ranges[index];
            // DirectWrite spacing must cover the complete Unicode text element.
            // A code-unit-only update can be reported by the getter while
            // contributing no advance for a joined or surrogate grapheme.
            range.FirstTextElementLength = FindFirstTextElementLength(
                box.LayoutBuffer, range.Start, range.Length, cancellationToken);
            range.LastTextElementStart = FindLastTextElementStart(
                box.LayoutBuffer, range.Start, range.Length, cancellationToken);
            range.LastTextElementLength = range.Start + range.Length - range.LastTextElementStart;
            range.FirstLeadingIsLeft = IsLeadingCaretOnLeft(
                box,
                layout, range.Start, observeCancellation);
            range.LastLeadingIsLeft = range.LastTextElementStart == range.Start
                ? range.FirstLeadingIsLeft
                : IsLeadingCaretOnLeft(box, layout, range.LastTextElementStart, observeCancellation);

            // Logical start/end characters are not necessarily the visual
            // edges of a mixed-direction run. When the two endpoint caret
            // orders disagree, use DirectWrite's visual text regions to find
            // the physical outer edges and attach each padding advance there.
            if (range.FirstLeadingIsLeft != range.LastLeadingIsLeft &&
                TryCaptureSingleLineVisualPaddingAnchors(
                    box,
                    layout, range, cancellationToken, observeCancellation,
                    out InlineCodePaddingAnchor leftAnchor,
                    out InlineCodePaddingAnchor rightAnchor))
            {
                range.HasVisualPaddingAnchors = true;
                range.VisualLeftAnchor = leftAnchor;
                range.VisualRightAnchor = rightAnchor;
            }
            ranges[index] = range;
        }

        // Only after all reading directions have been captured do we mutate
        // DirectWrite's advance metadata.
        foreach (InlineCodePaddingRange range in ranges)
        {
            if (observeCancellation)
                cancellationToken.ThrowIfCancellationRequested();

            if (range.HasVisualPaddingAnchors)
            {
                ApplyVisualPaddingAnchors(layout, range, cancellationToken, observeCancellation);
                continue;
            }

            float leading = range.FirstLeadingIsLeft ? range.LeftPadding : range.RightPadding;
            float trailing = range.LastLeadingIsLeft ? range.RightPadding : range.LeftPadding;
            if (range.LastTextElementStart == range.Start)
            {
                if (observeCancellation)
                    cancellationToken.ThrowIfCancellationRequested();
                layout.SetCharacterSpacing(
                    range.Start, range.FirstTextElementLength, leading, trailing, 0f);
                continue;
            }

            if (observeCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            layout.SetCharacterSpacing(range.Start, range.FirstTextElementLength, leading, 0f, 0f);
            if (observeCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            layout.SetCharacterSpacing(
                range.LastTextElementStart, range.LastTextElementLength, 0f, trailing, 0f);
        }
    }

    private static bool TryCaptureSingleLineVisualPaddingAnchors(
        InlineContainerBox box,
        CanvasTextLayout layout,
        InlineCodePaddingRange range,
        CancellationToken cancellationToken,
        bool observeCancellation,
        out InlineCodePaddingAnchor leftAnchor,
        out InlineCodePaddingAnchor rightAnchor)
    {
        leftAnchor = default;
        rightAnchor = default;
        if (observeCancellation)
            cancellationToken.ThrowIfCancellationRequested();

        CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(range.Start, range.Length);
        if (observeCancellation)
            cancellationToken.ThrowIfCancellationRequested();
        if (regions is not { Length: > 1 })
            return false;

        Rect firstBounds = regions[0].LayoutBounds;
        if (!double.IsFinite(firstBounds.Top) || !double.IsFinite(firstBounds.Height))
            return false;

        double lineTop = firstBounds.Top;
        double lineBottom = firstBounds.Bottom;
        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        bool hasCandidate = false;
        int rangeEnd = range.Start + range.Length;

        foreach (CanvasTextLayoutRegion region in regions)
        {
            if (observeCancellation)
                cancellationToken.ThrowIfCancellationRequested();

            Rect bounds = region.LayoutBounds;
            // A wrapped inline code span keeps the existing logical endpoint
            // behavior. Visual-run edge selection is only valid on one line.
            if (!double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Bottom) ||
                Math.Abs(bounds.Top - lineTop) > 0.01 || Math.Abs(bounds.Bottom - lineBottom) > 0.01)
            {
                return false;
            }

            int regionStart = Math.Max(range.Start, region.CharacterIndex);
            int regionEnd = Math.Min(rangeEnd, region.CharacterIndex + region.CharacterCount);
            if (regionStart >= regionEnd)
                continue;

            int regionFirstElementLength = FindFirstTextElementLength(
                box.LayoutBuffer, regionStart, regionEnd - regionStart, cancellationToken);
            int regionLastElement = FindLastTextElementStart(
                box.LayoutBuffer, regionStart, regionEnd - regionStart, cancellationToken);
            int regionLastElementLength = regionEnd - regionLastElement;

            ConsiderVisualPaddingCaret(
                box,
                layout, regionStart, regionFirstElementLength, isLeading: true, ref minX, ref maxX,
                ref leftAnchor, ref rightAnchor, ref hasCandidate, observeCancellation);
            ConsiderVisualPaddingCaret(
                box,
                layout, regionStart, regionFirstElementLength, isLeading: false, ref minX, ref maxX,
                ref leftAnchor, ref rightAnchor, ref hasCandidate, observeCancellation);
            if (regionLastElement != regionStart)
            {
                ConsiderVisualPaddingCaret(
                    box,
                    layout, regionLastElement, regionLastElementLength, isLeading: true, ref minX, ref maxX,
                    ref leftAnchor, ref rightAnchor, ref hasCandidate, observeCancellation);
                ConsiderVisualPaddingCaret(
                    box,
                    layout, regionLastElement, regionLastElementLength, isLeading: false, ref minX, ref maxX,
                    ref leftAnchor, ref rightAnchor, ref hasCandidate, observeCancellation);
            }
        }

        return hasCandidate && float.IsFinite(minX) && float.IsFinite(maxX) && maxX - minX > 0.001f;
    }

    private static void ConsiderVisualPaddingCaret(
        InlineContainerBox box,
        CanvasTextLayout layout,
        int characterIndex,
        int characterCount,
        bool isLeading,
        ref float minX,
        ref float maxX,
        ref InlineCodePaddingAnchor leftAnchor,
        ref InlineCodePaddingAnchor rightAnchor,
        ref bool hasCandidate,
        bool observeCancellation)
    {
        if (observeCancellation)
            box.Context.CancellationToken.ThrowIfCancellationRequested();

        Vector2 caret = layout.GetCaretPosition(characterIndex, trailingSideOfCharacter: !isLeading);
        if (observeCancellation)
            box.Context.CancellationToken.ThrowIfCancellationRequested();
        if (!float.IsFinite(caret.X))
            return;

        hasCandidate = true;
        if (caret.X < minX)
        {
            minX = caret.X;
            leftAnchor = new InlineCodePaddingAnchor(characterIndex, characterCount, isLeading);
        }
        if (caret.X > maxX)
        {
            maxX = caret.X;
            rightAnchor = new InlineCodePaddingAnchor(characterIndex, characterCount, isLeading);
        }
    }

    private static void ApplyVisualPaddingAnchors(
        CanvasTextLayout layout,
        InlineCodePaddingRange range,
        CancellationToken cancellationToken,
        bool observeCancellation)
    {
        InlineCodePaddingAnchor left = range.VisualLeftAnchor;
        InlineCodePaddingAnchor right = range.VisualRightAnchor;
        if (left.CharacterIndex == right.CharacterIndex && left.CharacterCount == right.CharacterCount)
        {
            float leading = 0f;
            float trailing = 0f;
            AddVisualPadding(ref leading, ref trailing, left.IsLeading, range.LeftPadding);
            AddVisualPadding(ref leading, ref trailing, right.IsLeading, range.RightPadding);
            if (observeCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            layout.SetCharacterSpacing(left.CharacterIndex, left.CharacterCount, leading, trailing, 0f);
            return;
        }

        if (observeCancellation)
            cancellationToken.ThrowIfCancellationRequested();
        layout.SetCharacterSpacing(
            left.CharacterIndex,
            left.CharacterCount,
            left.IsLeading ? range.LeftPadding : 0f,
            left.IsLeading ? 0f : range.LeftPadding,
            0f);
        if (observeCancellation)
            cancellationToken.ThrowIfCancellationRequested();
        layout.SetCharacterSpacing(
            right.CharacterIndex,
            right.CharacterCount,
            right.IsLeading ? range.RightPadding : 0f,
            right.IsLeading ? 0f : range.RightPadding,
            0f);
    }

    private static void AddVisualPadding(
        ref float leading,
        ref float trailing,
        bool isLeading,
        float value)
    {
        if (isLeading)
            leading += value;
        else
            trailing += value;
    }

    internal static int FindLastTextElementStart(
        string text,
        int start,
        int length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int end = start + length;
        int lastElementStart = start;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text, start);
        int scannedElements = 0;
        while (elements.MoveNext())
        {
            if ((scannedElements++ % InlineCodeGraphemeCancellationCheckInterval) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int elementStart = start + elements.ElementIndex;
            if (elementStart >= end)
                break;
            lastElementStart = elementStart;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return lastElementStart;
    }

    private static int FindFirstTextElementLength(
        string text,
        int start,
        int length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (length <= 0)
            return 0;

        int end = start + length;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text, start);
        if (!elements.MoveNext())
            return 0;

        int firstElementStart = start + elements.ElementIndex;
        cancellationToken.ThrowIfCancellationRequested();
        if (!elements.MoveNext())
            return Math.Max(0, end - firstElementStart);

        int nextElementStart = start + elements.ElementIndex;
        cancellationToken.ThrowIfCancellationRequested();
        return Math.Max(0, Math.Min(end, nextElementStart) - firstElementStart);
    }

    private static bool IsLeadingCaretOnLeft(
        InlineContainerBox box,
        CanvasTextLayout layout,
        int characterIndex,
        bool observeCancellation)
    {
        if (observeCancellation)
            box.Context.CancellationToken.ThrowIfCancellationRequested();
        Vector2 leading = layout.GetCaretPosition(characterIndex, trailingSideOfCharacter: false);
        if (observeCancellation)
            box.Context.CancellationToken.ThrowIfCancellationRequested();
        Vector2 trailing = layout.GetCaretPosition(characterIndex, trailingSideOfCharacter: true);
        if (float.IsFinite(leading.X) && float.IsFinite(trailing.X) &&
            Math.Abs(leading.X - trailing.X) > 0.001f)
        {
            return leading.X < trailing.X;
        }

        // A zero-advance endpoint (for example a combining mark) may have
        // coincident carets. The paragraph direction is the deterministic
        // fallback; ordinary strong-direction characters use the measured
        // caret order above, including embedded RTL/LTR runs.
        return box.Context.FlowDirection != FlowDirection.RightToLeft;
    }

    internal static float ToInlineCodeAdvance(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0f;

        // Keep a custom style from passing an unbounded value to DirectWrite.
        return (float)Math.Min(value, 1_000_000d);
    }
}


