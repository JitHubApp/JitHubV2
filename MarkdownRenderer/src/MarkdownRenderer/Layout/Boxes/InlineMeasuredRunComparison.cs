using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The stateless run comparisons an <see cref="InlineMeasuredRunState"/>
/// capture relies on: which runs may transfer, whether a source run and its
/// private measured counterpart carry the same semantics, text contract and
/// vector metadata, and whether a run is a direct immutable source slice.
/// </summary>
internal static class InlineMeasuredRunComparison
{
    internal static bool RunSemanticsMatch(
        InlineRun source,
        InlineRun prepared,
        bool allowMeasuredVectorPlacement)
    {
        if (source.InlineIndex != prepared.InlineIndex ||
            source.RenderedLength != prepared.RenderedLength ||
            !source.SourceSpan.Equals(prepared.SourceSpan) ||
            !string.Equals(source.ElementKey, prepared.ElementKey, StringComparison.Ordinal) ||
            !string.Equals(source.SemanticHeadingKey, prepared.SemanticHeadingKey, StringComparison.Ordinal) ||
            !RunTextContractsMatch(source, prepared) ||
            !SameKeys(source.StyleAliases, prepared.StyleAliases) ||
            !SameKeys(source.StyleModifierKeys, prepared.StyleModifierKeys) ||
            !SameKeys(source.ScriptStyleAncestryKeys, prepared.ScriptStyleAncestryKeys) ||
            source.SafeHtmlScriptRoleIsRunElement != prepared.SafeHtmlScriptRoleIsRunElement)
        {
            return false;
        }

        if (source is LinkRun sourceLink && prepared is LinkRun preparedLink)
        {
            return !sourceLink.IsDisclosure && !sourceLink.IsExpanded &&
                !preparedLink.IsDisclosure && !preparedLink.IsExpanded &&
                string.Equals(sourceLink.Url, preparedLink.Url, StringComparison.Ordinal) &&
                string.Equals(sourceLink.Title, preparedLink.Title, StringComparison.Ordinal) &&
                string.Equals(sourceLink.AccessibilityName, preparedLink.AccessibilityName, StringComparison.Ordinal) &&
                string.Equals(sourceLink.DisclosureId, preparedLink.DisclosureId, StringComparison.Ordinal) &&
                sourceLink.IsSuperscript == preparedLink.IsSuperscript;
        }

        if (source is InlineImageRun sourceImage && prepared is InlineImageRun preparedImage)
        {
            return !ReferenceEquals(sourceImage.Image, preparedImage.Image) &&
                string.Equals(sourceImage.Url, preparedImage.Url, StringComparison.Ordinal) &&
                string.Equals(sourceImage.Title, preparedImage.Title, StringComparison.Ordinal) &&
                string.Equals(sourceImage.LinkUrl, preparedImage.LinkUrl, StringComparison.Ordinal) &&
                string.Equals(sourceImage.LinkTitle, preparedImage.LinkTitle, StringComparison.Ordinal) &&
                string.Equals(sourceImage.AltText, preparedImage.AltText, StringComparison.Ordinal);
        }

        if (source is InlineVectorSceneRun sourceVector &&
            prepared is InlineVectorSceneRun preparedVector)
        {
            return allowMeasuredVectorPlacement && !sourceVector.IsDisposed &&
                !sourceVector.IsMeasuredPlacementOnly && !preparedVector.IsDisposed &&
                preparedVector.IsMeasuredPlacementOnly &&
                sourceVector.DeferredSceneMarker is null && preparedVector.DeferredSceneMarker is null &&
                ReferenceEquals(sourceVector.Scene, preparedVector.Scene) &&
                SameVectorMetadata(sourceVector, preparedVector);
        }

        return true;
    }

    internal static bool HasValidTextLengths(InlineRun run)
    {
        if (TryGetDirectSourceSlice(run, out SourceSliceTextRun slice))
        {
            return slice.SliceLength > 0 &&
                slice.RenderedLength == slice.SliceLength &&
                slice.TextLength == slice.SliceLength &&
                slice.LayoutTextLength == slice.SliceLength;
        }

        return run.RenderedLength == run.Text.Length &&
            run.LayoutText.Length == run.Text.Length;
    }

    private static bool RunTextContractsMatch(InlineRun source, InlineRun prepared)
    {
        if (TryGetDirectSourceSlice(source, out SourceSliceTextRun sourceSlice) &&
            TryGetDirectSourceSlice(prepared, out SourceSliceTextRun preparedSlice))
        {
            if (ReferenceEquals(sourceSlice.SourceText, preparedSlice.SourceText) &&
                sourceSlice.SourceStart == preparedSlice.SourceStart &&
                sourceSlice.SliceLength == preparedSlice.SliceLength)
            {
                return true;
            }
        }

        // Different backing strings/ranges keep the previous exact text
        // contract, including equivalent slices from independently-owned
        // source buffers. Only the shared immutable slice can skip strings.
        return string.Equals(source.Text, prepared.Text, StringComparison.Ordinal) &&
            string.Equals(source.LayoutText, prepared.LayoutText, StringComparison.Ordinal) &&
            string.Equals(source.AccessibleText, prepared.AccessibleText, StringComparison.Ordinal);
    }

    internal static bool TryGetDirectSourceSlice(
        InlineRun run,
        out SourceSliceTextRun sourceSlice)
    {
        if (run is SourceSliceTextRun slice &&
            slice.SliceLength > 0 &&
            slice.RenderedLength == slice.SliceLength &&
            slice.TextLength == slice.SliceLength &&
            slice.LayoutTextLength == slice.SliceLength &&
            slice.SourceSpan.Start == slice.SourceStart &&
            slice.SourceSpan.Length == slice.SliceLength)
        {
            sourceSlice = slice;
            return true;
        }

        sourceSlice = null!;
        return false;
    }

    internal static bool IsSupportedRun(InlineRun run, bool allowMeasuredVectorPlacement) =>
        PrivateTextRunPlacement.IsSupported(run) ||
        run is LinkRun { IsDisclosure: false, IsExpanded: false } ||
        run is InlineImageRun ||
        allowMeasuredVectorPlacement && run is InlineVectorSceneRun;

    internal static bool SameVectorMetadata(
        InlineVectorSceneRun first,
        InlineVectorSceneRun second) =>
        string.Equals(first.SemanticText, second.SemanticText, StringComparison.Ordinal) &&
        string.Equals(first.AccessibilityName, second.AccessibilityName, StringComparison.Ordinal) &&
        string.Equals(first.AccessibilityDescription, second.AccessibilityDescription, StringComparison.Ordinal) &&
        string.Equals(first.DeferredSceneMarker, second.DeferredSceneMarker, StringComparison.Ordinal) &&
        SameDeferredBinding(first.DeferredSceneBinding, second.DeferredSceneBinding) &&
        string.Equals(first.LinkUrl, second.LinkUrl, StringComparison.Ordinal) &&
        string.Equals(first.LinkTitle, second.LinkTitle, StringComparison.Ordinal);

    private static bool SameDeferredBinding(
        MarkdownDeferredSceneBinding? first,
        MarkdownDeferredSceneBinding? second)
    {
        if (first.HasValue != second.HasValue)
            return false;
        if (!first.HasValue)
            return true;
        MarkdownDeferredSceneBinding left = first.Value;
        MarkdownDeferredSceneBinding right = second!.Value;
        return string.Equals(left.Marker, right.Marker, StringComparison.Ordinal) &&
            left.SceneOrdinal == right.SceneOrdinal &&
            ReferenceEquals(left.ProjectionIdentity, right.ProjectionIdentity);
    }

    internal static bool MatchesKeys(IReadOnlyList<string>? current, IReadOnlyList<string> expected)
    {
        if (current is null || current.Count != expected.Count)
            return false;
        for (int index = 0; index < expected.Count; index++)
            if (!string.Equals(current[index], expected[index], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static bool SameKeys(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        return true;
    }
}
