using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class InlineContainerBox
{

    internal readonly struct RunState
    {
        internal readonly InlineRun SourceRun;
        internal readonly InlineRun PreparedRun;
        internal readonly InlineImageRun? SourceImageRun;
        internal readonly InlineImageRun? PreparedImageRun;
        internal readonly InlineVectorSceneRun? SourceVectorRun;
        internal readonly InlineVectorSceneRun? PreparedVectorRun;
        internal MarkdownVectorScene? VectorScene => _vectorScene;
        internal readonly float SourceRunImageWidth;
        internal readonly float SourceRunImageHeight;
        internal readonly float SourceImageAvailableWidth;
        internal readonly float SourceImageWidth;
        internal readonly float SourceImageHeight;
        private readonly Type _runType;
        private readonly int _inlineIndex;
        private readonly int _renderedLength;
        private readonly SourceSpan _sourceSpan;
        private readonly string _elementKey;
        private readonly string _semanticHeadingKey;
        private readonly bool _usesDirectSourceSliceFence;
        // In direct-slice mode `_text` holds the immutable backing source
        // string. `_sourceSpan.Start` and `_renderedLength` are the captured
        // slice bounds, so the fast fence adds only its discriminator per run.
        private readonly string? _text;
        private readonly string? _layoutText;
        private readonly string? _accessibleText;
        private readonly IReadOnlyList<string> _styleAliases;
        private readonly IReadOnlyList<string> _styleModifiers;
        private readonly IReadOnlyList<string> _scriptStyleAncestryKeys;
        private readonly bool _safeHtmlScriptRoleIsRunElement;
        private readonly string? _linkUrl;
        private readonly string? _linkTitle;
        private readonly string? _linkAccessibilityName;
        private readonly string? _linkDisclosureId;
        private readonly bool _linkExpanded;
        private readonly bool _linkSuperscript;
        private readonly string? _imageUrl;
        private readonly string? _imageTitle;
        private readonly string? _imageLinkUrl;
        private readonly string? _imageLinkTitle;
        private readonly string? _imageAltText;
        private readonly MarkdownVectorScene? _vectorScene;
        private readonly float _vectorFontSize;
        private readonly float _sourceVectorWidth;
        private readonly float _sourceVectorHeight;
        private readonly float _sourceVectorBaseline;
        private readonly Rect _sourceVectorLocalBounds;
        private readonly string? _vectorSemanticText;
        private readonly string? _vectorAccessibilityName;
        private readonly string? _vectorAccessibilityDescription;
        private readonly string? _vectorDeferredMarker;
        private readonly bool _vectorHasDeferredBinding;
        private readonly string? _vectorDeferredBindingMarker;
        private readonly int _vectorDeferredBindingOrdinal;
        private readonly object? _vectorDeferredProjectionIdentity;
        private readonly string? _vectorLinkUrl;
        private readonly string? _vectorLinkTitle;
        private readonly int _offset;

        private RunState(
            InlineRun sourceRun,
            InlineRun preparedRun,
            int offset,
            float sourceImageAvailableWidth,
            float sourceImageWidth,
            float sourceImageHeight,
            float vectorFontSize)
        {
            SourceRun = sourceRun;
            PreparedRun = preparedRun;
            SourceImageRun = sourceRun as InlineImageRun;
            PreparedImageRun = preparedRun as InlineImageRun;
            SourceVectorRun = sourceRun as InlineVectorSceneRun;
            PreparedVectorRun = preparedRun as InlineVectorSceneRun;
            SourceRunImageWidth = SourceImageRun?.DesiredWidth ?? 0f;
            SourceRunImageHeight = SourceImageRun?.DesiredHeight ?? 0f;
            SourceImageAvailableWidth = sourceImageAvailableWidth;
            SourceImageWidth = sourceImageWidth;
            SourceImageHeight = sourceImageHeight;
            _vectorFontSize = vectorFontSize;
            _runType = sourceRun.GetType();
            _inlineIndex = sourceRun.InlineIndex;
            _renderedLength = sourceRun.RenderedLength;
            _sourceSpan = sourceRun.SourceSpan;
            _elementKey = sourceRun.ElementKey;
            _semanticHeadingKey = sourceRun.SemanticHeadingKey;
            if (TryGetDirectSourceSlice(sourceRun, out SourceSliceTextRun sourceSlice) &&
                TryGetDirectSourceSlice(preparedRun, out SourceSliceTextRun preparedSlice) &&
                ReferenceEquals(sourceSlice.SourceText, preparedSlice.SourceText) &&
                sourceSlice.SourceStart == preparedSlice.SourceStart &&
                sourceSlice.SliceLength == preparedSlice.SliceLength)
            {
                _usesDirectSourceSliceFence = true;
                _text = sourceSlice.SourceText;
                _layoutText = null;
                _accessibleText = null;
            }
            else
            {
                _usesDirectSourceSliceFence = false;
                _text = sourceRun.Text;
                _layoutText = sourceRun.LayoutText;
                _accessibleText = sourceRun.AccessibleText;
            }
            // The prepared run is private to this candidate. Its style-key
            // lists are immutable for the candidate lifetime and already
            // contain the independent copies needed by publication.
            _styleAliases = preparedRun.StyleAliases;
            _styleModifiers = preparedRun.StyleModifierKeys;
            _scriptStyleAncestryKeys = preparedRun.ScriptStyleAncestryKeys;
            _safeHtmlScriptRoleIsRunElement = preparedRun.SafeHtmlScriptRoleIsRunElement;
            _offset = offset;

            if (sourceRun is LinkRun link)
            {
                _linkUrl = link.Url;
                _linkTitle = link.Title;
                _linkAccessibilityName = link.AccessibilityName;
                _linkDisclosureId = link.DisclosureId;
                _linkExpanded = link.IsExpanded;
                _linkSuperscript = link.IsSuperscript;
            }
            else
            {
                _linkUrl = null;
                _linkTitle = null;
                _linkAccessibilityName = null;
                _linkDisclosureId = null;
                _linkExpanded = false;
                _linkSuperscript = false;
            }

            if (sourceRun is InlineImageRun image)
            {
                _imageUrl = image.Url;
                _imageTitle = image.Title;
                _imageLinkUrl = image.LinkUrl;
                _imageLinkTitle = image.LinkTitle;
                _imageAltText = image.AltText;
            }
            else
            {
                _imageUrl = null;
                _imageTitle = null;
                _imageLinkUrl = null;
                _imageLinkTitle = null;
                _imageAltText = null;
            }

            if (SourceVectorRun is { } vector)
            {
                _vectorScene = vector.Scene;
                _sourceVectorWidth = vector.DesiredWidth;
                _sourceVectorHeight = vector.DesiredHeight;
                _sourceVectorBaseline = vector.DesiredBaseline;
                _sourceVectorLocalBounds = vector.LocalBounds;
                _vectorSemanticText = vector.SemanticText;
                _vectorAccessibilityName = vector.AccessibilityName;
                _vectorAccessibilityDescription = vector.AccessibilityDescription;
                _vectorDeferredMarker = vector.DeferredSceneMarker;
                if (vector.DeferredSceneBinding is { } binding)
                {
                    _vectorHasDeferredBinding = true;
                    _vectorDeferredBindingMarker = binding.Marker;
                    _vectorDeferredBindingOrdinal = binding.SceneOrdinal;
                    _vectorDeferredProjectionIdentity = binding.ProjectionIdentity;
                }
                else
                {
                    _vectorHasDeferredBinding = false;
                    _vectorDeferredBindingMarker = null;
                    _vectorDeferredBindingOrdinal = 0;
                    _vectorDeferredProjectionIdentity = null;
                }
                _vectorLinkUrl = vector.LinkUrl;
                _vectorLinkTitle = vector.LinkTitle;
            }
            else
            {
                _vectorScene = null;
                _sourceVectorWidth = 0f;
                _sourceVectorHeight = 0f;
                _sourceVectorBaseline = 0f;
                _sourceVectorLocalBounds = default;
                _vectorSemanticText = null;
                _vectorAccessibilityName = null;
                _vectorAccessibilityDescription = null;
                _vectorDeferredMarker = null;
                _vectorHasDeferredBinding = false;
                _vectorDeferredBindingMarker = null;
                _vectorDeferredBindingOrdinal = 0;
                _vectorDeferredProjectionIdentity = null;
                _vectorLinkUrl = null;
                _vectorLinkTitle = null;
            }
        }

        internal static bool TryCreate(
            InlineRun sourceRun,
            InlineRun preparedRun,
            int index,
            int offset,
            bool allowMeasuredVectorPlacement,
            float vectorFontSize,
            out RunState state)
        {
            state = default;
            if (sourceRun is null || preparedRun is null ||
                ReferenceEquals(sourceRun, preparedRun) ||
                sourceRun.GetType() != preparedRun.GetType() ||
                sourceRun.InlineIndex != index || preparedRun.InlineIndex != index ||
                !HasValidTextLengths(sourceRun) ||
                !HasValidTextLengths(preparedRun) ||
                !IsSupportedRun(sourceRun, allowMeasuredVectorPlacement) ||
                !IsSupportedRun(preparedRun, allowMeasuredVectorPlacement) ||
                !RunSemanticsMatch(sourceRun, preparedRun, allowMeasuredVectorPlacement))
            {
                return false;
            }

            float imageAvailableWidth = 0f;
            float imageWidth = 0f;
            float imageHeight = 0f;
            if (sourceRun is InlineImageRun sourceImageRun &&
                preparedRun is InlineImageRun preparedImageRun)
            {
                if (ReferenceEquals(sourceImageRun.Image, preparedImageRun.Image) ||
                    !float.IsFinite(sourceImageRun.DesiredWidth) || sourceImageRun.DesiredWidth <= 0f ||
                    !float.IsFinite(sourceImageRun.DesiredHeight) || sourceImageRun.DesiredHeight <= 0f ||
                    sourceImageRun.DesiredWidth != preparedImageRun.DesiredWidth ||
                    sourceImageRun.DesiredHeight != preparedImageRun.DesiredHeight ||
                    !sourceImageRun.Image.TryGetCompletedInlinePlacementState(
                        out imageAvailableWidth, out imageWidth, out imageHeight) ||
                    !float.IsFinite(imageWidth) || imageWidth <= 0f ||
                    imageWidth > sourceImageRun.DesiredWidth ||
                    sourceImageRun.DesiredHeight != imageHeight ||
                    !float.IsFinite(imageAvailableWidth) || imageAvailableWidth <= 0f)
                {
                    return false;
                }
            }

            if (sourceRun is InlineVectorSceneRun sourceVectorRun &&
                preparedRun is InlineVectorSceneRun preparedVectorRun)
            {
                if (!allowMeasuredVectorPlacement || sourceVectorRun.IsDisposed ||
                    sourceVectorRun.IsMeasuredPlacementOnly || preparedVectorRun.IsDisposed ||
                    !preparedVectorRun.IsMeasuredPlacementOnly ||
                    sourceVectorRun.DeferredSceneMarker is not null ||
                    preparedVectorRun.DeferredSceneMarker is not null ||
                    !ReferenceEquals(sourceVectorRun.Scene, preparedVectorRun.Scene) ||
                    !SameVectorMetadata(sourceVectorRun, preparedVectorRun) ||
                    !sourceVectorRun.HasExpectedMeasuredDimensions(vectorFontSize) ||
                    sourceVectorRun.DesiredWidth != preparedVectorRun.DesiredWidth ||
                    sourceVectorRun.DesiredHeight != preparedVectorRun.DesiredHeight ||
                    sourceVectorRun.DesiredBaseline != preparedVectorRun.DesiredBaseline ||
                    !SameRect(sourceVectorRun.LocalBounds, preparedVectorRun.LocalBounds) ||
                    !IsFiniteNonnegative(sourceVectorRun.LocalBounds) ||
                    !float.IsFinite(vectorFontSize) || vectorFontSize <= 0f)
                {
                    return false;
                }
            }

            state = new RunState(
                sourceRun, preparedRun, offset, imageAvailableWidth, imageWidth, imageHeight,
                vectorFontSize);
            return state.Matches(sourceRun) && state.Matches(preparedRun);
        }

        internal bool MatchesSourceAndPrepared()
        {
            if (!Matches(SourceRun) || !Matches(PreparedRun))
                return false;

            if (SourceImageRun is { } sourceImageRun && PreparedImageRun is { } preparedImageRun)
            {
                if (sourceImageRun.DesiredWidth != SourceRunImageWidth ||
                    sourceImageRun.DesiredHeight != SourceRunImageHeight ||
                    !float.IsFinite(preparedImageRun.DesiredWidth) || preparedImageRun.DesiredWidth <= 0f ||
                    !float.IsFinite(preparedImageRun.DesiredHeight) || preparedImageRun.DesiredHeight <= 0f)
                {
                    return false;
                }
            }

            return true;
        }

        internal bool MatchesSourceAndPreparedVectorState(bool allowMeasuredVectorPlacement)
        {
            if (SourceVectorRun is null)
                return PreparedVectorRun is null;
            if (!allowMeasuredVectorPlacement || PreparedVectorRun is null ||
                SourceVectorRun.IsDisposed || SourceVectorRun.IsMeasuredPlacementOnly ||
                PreparedVectorRun.IsDisposed || !PreparedVectorRun.IsMeasuredPlacementOnly ||
                !MatchesCapturedVectorMetadata(SourceVectorRun) ||
                !MatchesCapturedVectorMetadata(PreparedVectorRun) ||
                !SameVectorMetadata(SourceVectorRun, PreparedVectorRun) ||
                SourceVectorRun.DesiredWidth != _sourceVectorWidth ||
                SourceVectorRun.DesiredHeight != _sourceVectorHeight ||
                SourceVectorRun.DesiredBaseline != _sourceVectorBaseline ||
                !SameRect(SourceVectorRun.LocalBounds, _sourceVectorLocalBounds) ||
                !IsFiniteNonnegative(SourceVectorRun.LocalBounds) ||
                !PreparedVectorRun.HasExpectedMeasuredDimensions(_vectorFontSize) ||
                !IsFiniteNonnegative(PreparedVectorRun.LocalBounds))
            {
                return false;
            }

            return true;
        }

        internal bool MatchesImageEntries(
            (InlineImageRun Run, int Offset) sourceEntry,
            (InlineImageRun Run, int Offset) preparedEntry) =>
            SourceImageRun is not null && PreparedImageRun is not null &&
            ReferenceEquals(sourceEntry.Run, SourceImageRun) && sourceEntry.Offset == _offset &&
            ReferenceEquals(preparedEntry.Run, PreparedImageRun) && preparedEntry.Offset == _offset;

        internal bool MatchesSourceImageState()
        {
            if (SourceImageRun is not { } image)
                return true;
            return image.Image.TryGetCompletedInlinePlacementState(
                    out float availableWidth, out float width, out float height) &&
                availableWidth == SourceImageAvailableWidth &&
                width == SourceImageWidth && height == SourceImageHeight &&
                float.IsFinite(width) && width > 0f && width <= SourceRunImageWidth &&
                float.IsFinite(SourceRunImageWidth) && SourceRunImageWidth > 0f &&
                float.IsFinite(SourceRunImageHeight) && SourceRunImageHeight > 0f &&
                image.DesiredWidth == SourceRunImageWidth && image.DesiredHeight == SourceRunImageHeight;
        }

        internal bool MatchesPreparedImageState(bool allowWidthChange)
        {
            if (PreparedImageRun is not { } image)
                return true;
            return image.Image.TryGetCompletedInlinePlacementState(
                    out float availableWidth, out float width, out float height) &&
                float.IsFinite(availableWidth) && availableWidth > 0f &&
                (allowWidthChange || availableWidth == SourceImageAvailableWidth) &&
                width == image.DesiredWidth && height == image.DesiredHeight &&
                float.IsFinite(width) && width > 0f &&
                float.IsFinite(image.DesiredWidth) && image.DesiredWidth > 0f &&
                float.IsFinite(image.DesiredHeight) && image.DesiredHeight > 0f;
        }

        private bool Matches(InlineRun run)
        {
            if (run.GetType() != _runType || run.InlineIndex != _inlineIndex ||
                run.RenderedLength != _renderedLength || !run.SourceSpan.Equals(_sourceSpan) ||
                !string.Equals(run.ElementKey, _elementKey, StringComparison.Ordinal) ||
                !string.Equals(run.SemanticHeadingKey, _semanticHeadingKey, StringComparison.Ordinal) ||
                !MatchesTextFence(run) ||
                !MatchesKeys(run.StyleAliases, _styleAliases) ||
                !MatchesKeys(run.StyleModifierKeys, _styleModifiers) ||
                !MatchesKeys(run.ScriptStyleAncestryKeys, _scriptStyleAncestryKeys) ||
                run.SafeHtmlScriptRoleIsRunElement != _safeHtmlScriptRoleIsRunElement)
            {
                return false;
            }

            if (run is LinkRun link &&
                (!string.Equals(link.Url, _linkUrl, StringComparison.Ordinal) ||
                 !string.Equals(link.Title, _linkTitle, StringComparison.Ordinal) ||
                 !string.Equals(link.AccessibilityName, _linkAccessibilityName, StringComparison.Ordinal) ||
                 !string.Equals(link.DisclosureId, _linkDisclosureId, StringComparison.Ordinal) ||
                 link.IsExpanded != _linkExpanded || link.IsSuperscript != _linkSuperscript ||
                 link.IsDisclosure || link.IsExpanded))
            {
                return false;
            }

            if (run is InlineImageRun image &&
                (!string.Equals(image.Url, _imageUrl, StringComparison.Ordinal) ||
                 !string.Equals(image.Title, _imageTitle, StringComparison.Ordinal) ||
                 !string.Equals(image.LinkUrl, _imageLinkUrl, StringComparison.Ordinal) ||
                 !string.Equals(image.LinkTitle, _imageLinkTitle, StringComparison.Ordinal) ||
                 !string.Equals(image.AltText, _imageAltText, StringComparison.Ordinal) ||
                 !ReferenceEquals(image, SourceImageRun) && !ReferenceEquals(image, PreparedImageRun)))
            {
                return false;
            }

            if (run is InlineVectorSceneRun vector)
            {
                if ((!ReferenceEquals(vector, SourceVectorRun) &&
                     !ReferenceEquals(vector, PreparedVectorRun)) ||
                    vector.IsDisposed ||
                    !MatchesCapturedVectorMetadata(vector) ||
                    vector.IsMeasuredPlacementOnly != ReferenceEquals(vector, PreparedVectorRun))
                {
                    return false;
                }
            }

            return true;
        }

        private bool MatchesCapturedVectorMetadata(InlineVectorSceneRun vector)
        {
            if (!ReferenceEquals(vector.Scene, _vectorScene) ||
                !string.Equals(vector.SemanticText, _vectorSemanticText, StringComparison.Ordinal) ||
                !string.Equals(vector.AccessibilityName, _vectorAccessibilityName, StringComparison.Ordinal) ||
                !string.Equals(vector.AccessibilityDescription, _vectorAccessibilityDescription,
                    StringComparison.Ordinal) ||
                !string.Equals(vector.DeferredSceneMarker, _vectorDeferredMarker, StringComparison.Ordinal) ||
                !string.Equals(vector.LinkUrl, _vectorLinkUrl, StringComparison.Ordinal) ||
                !string.Equals(vector.LinkTitle, _vectorLinkTitle, StringComparison.Ordinal))
            {
                return false;
            }

            MarkdownDeferredSceneBinding? binding = vector.DeferredSceneBinding;
            if (binding.HasValue != _vectorHasDeferredBinding)
                return false;
            if (!binding.HasValue)
                return true;

            MarkdownDeferredSceneBinding value = binding.Value;
            return string.Equals(value.Marker, _vectorDeferredBindingMarker, StringComparison.Ordinal) &&
                value.SceneOrdinal == _vectorDeferredBindingOrdinal &&
                ReferenceEquals(value.ProjectionIdentity, _vectorDeferredProjectionIdentity);
        }

        private static bool RunSemanticsMatch(
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

        private bool MatchesTextFence(InlineRun run)
        {
            if (_usesDirectSourceSliceFence)
            {
                return TryGetDirectSourceSlice(run, out SourceSliceTextRun slice) &&
                    ReferenceEquals(slice.SourceText, _text) &&
                    slice.SourceStart == _sourceSpan.Start &&
                    slice.SliceLength == _renderedLength;
            }

            return string.Equals(run.Text, _text, StringComparison.Ordinal) &&
                string.Equals(run.LayoutText, _layoutText, StringComparison.Ordinal) &&
                string.Equals(run.AccessibleText, _accessibleText, StringComparison.Ordinal);
        }

        private static bool HasValidTextLengths(InlineRun run)
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

        private static bool TryGetDirectSourceSlice(
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

        private static bool IsSupportedRun(InlineRun run, bool allowMeasuredVectorPlacement) =>
            PrivateTextRunPlacement.IsSupported(run) ||
            run is LinkRun { IsDisclosure: false, IsExpanded: false } ||
            run is InlineImageRun ||
            allowMeasuredVectorPlacement && run is InlineVectorSceneRun;

        private static bool SameVectorMetadata(
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

        private static bool MatchesKeys(IReadOnlyList<string>? current, IReadOnlyList<string> expected)
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

    /// <summary>
    /// Captures a measured Body/Heading source and the exact private clone
    /// created from it. The clone must be measured and arranged outside the
    /// source gate before <see cref="InlineMeasuredPlacementTransfer.IsCurrentAndPrepared"/>
    /// succeeds. Unsupported roots fail closed.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransfer(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer);

    /// <summary>
    /// Root-local-only variant for the explicit measurement-only vector-run
    /// recipe. Do not call from whole-tree COW; the candidate has no painter.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            allowMeasuredVectorPlacement: true);

    /// <summary>
    /// Root-local image-completion variant for a table cell. The candidate is
    /// measurement-only and its vector geometry is remapped to stable source
    /// runs before the placement transaction commits.
    /// </summary>
    internal bool TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedSourceWidth, isTableCellTransfer: true, out transfer,
            allowMeasuredVectorPlacement: true);

    /// <summary>
    /// Captures a table-cell source and its private clone before the candidate
    /// table is measured. The source remains fenced at its exact current cell
    /// width; the prepared cell may later be measured at a different width as
    /// the candidate table rebalances its columns.
    /// </summary>
    internal bool TryCaptureTableCellMeasuredPlacementTransfer(
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedSourceWidth, isTableCellTransfer: true, out transfer);

    /// <summary>
    /// Captures a measured image-free Body, Heading, or ListMarker leaf inside
    /// an ordinary StackBox/ListItemBox placement tree. Both the published
    /// source and the completed private leaf remain fenced at the same exact
    /// width; unlike a table cell this mode never permits column rebalancing.
    /// </summary>
    internal bool TryCaptureContainerLeafMeasuredPlacementTransfer(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            isContainerLeafTransfer: true);

    /// <summary>
    /// Root-local image-completion variant for a vector-bearing leaf in an
    /// ordinary stack/list placement tree. It never publishes its measured-only
    /// candidate runs.
    /// </summary>
    internal bool TryCaptureContainerLeafMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            isContainerLeafTransfer: true, allowMeasuredVectorPlacement: true);

    private bool TryCaptureMeasuredPlacementTransferCore(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool isTableCellTransfer,
        out InlineMeasuredPlacementTransfer? transfer,
        bool isContainerLeafTransfer = false,
        bool allowMeasuredVectorPlacement = false)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        bool rootsSupported = isTableCellTransfer
            ? IsSupportedTableCellMeasuredPlacementRoot(this, allowMeasuredVectorPlacement) &&
                IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? IsSupportedContainerLeafMeasuredPlacementRoot(this, allowMeasuredVectorPlacement) &&
                IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : IsSupportedMeasuredPlacementRoot(this, allowMeasuredVectorPlacement) &&
                IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement);
        bool measuredSource = isTableCellTransfer
            ? IsMeasuredSourceForTableCellTransfer(expectedAvailableWidth, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? IsMeasuredSourceForContainerLeafTransfer(expectedAvailableWidth, allowMeasuredVectorPlacement)
            : IsMeasuredSourceForTransfer(expectedAvailableWidth, allowMeasuredVectorPlacement);
        int runCount = 0;
        int imageCount = 0;
        bool placementCountsValid = allowMeasuredVectorPlacement
            ? TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                prepared.Context, expectedAvailableWidth, out runCount,
                out imageCount, out _)
            : TryGetPrivateRasterPlacementCounts(
                prepared.Context, expectedAvailableWidth, out runCount,
                out imageCount, out _, allowUnmeasuredTextOnly: false,
                allowIdleImagePlacements: false, allowCodeForegroundSpans: false);
        if (ReferenceEquals(this, prepared) ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !rootsSupported ||
            !ArePrivateRasterPlacementContextsCompatible(Context, prepared.Context) ||
            !RawPlacementMargin.Equals(prepared.RawPlacementMargin) ||
            !placementCountsValid ||
            (!isTableCellTransfer && !isContainerLeafTransfer && imageCount == 0) ||
            (isContainerLeafTransfer && imageCount != 0) ||
            imageCount > MaximumPrivateRasterPlacementImages ||
            (!isTableCellTransfer && !isContainerLeafTransfer && runCount == 0) ||
            runCount > MaximumPrivateRasterPlacementRuns ||
            !measuredSource ||
            !IsUnmeasuredPrivateCloneOf(
                this, prepared, expectedAvailableWidth,
                allowImageFree: isTableCellTransfer || isContainerLeafTransfer,
                allowMeasuredVectorPlacement: allowMeasuredVectorPlacement) ||
            !NativeLayoutsAreDistinct(this) || !NativeLayoutsAreDistinct(prepared))
        {
            return false;
        }

        var runStates = new RunState[runCount];
        int offset = 0;
        int imageOrdinal = 0;
        var seenSourceImages = new ImageBox[imageCount];
        var seenPreparedImages = new ImageBox[imageCount];
        for (int index = 0; index < runCount; index++)
        {
            InlineRun sourceRun = _runs[index];
            InlineRun preparedRun = prepared._runs[index];
            if (!RunState.TryCreate(
                    sourceRun, preparedRun, index, offset,
                    allowMeasuredVectorPlacement, _resolvedContainerStyle!.FontSize,
                    out runStates[index]))
                return false;

            if (runStates[index].SourceImageRun is { } sourceImageRun &&
                runStates[index].PreparedImageRun is { } preparedImageRun)
            {
                for (int prior = 0; prior < imageOrdinal; prior++)
                {
                    if (ReferenceEquals(seenSourceImages[prior], sourceImageRun.Image) ||
                        ReferenceEquals(seenPreparedImages[prior], preparedImageRun.Image) ||
                        ReferenceEquals(seenSourceImages[prior], preparedImageRun.Image) ||
                        ReferenceEquals(seenPreparedImages[prior], sourceImageRun.Image))
                    {
                        return false;
                    }
                }
                seenSourceImages[imageOrdinal] = sourceImageRun.Image;
                seenPreparedImages[imageOrdinal] = preparedImageRun.Image;
                imageOrdinal++;
            }

            offset += sourceRun.TextLength;
        }

        if (imageOrdinal != imageCount || offset != _runTextLength ||
            (_inlineImageRuns?.Count ?? 0) != imageCount ||
            (prepared._inlineImageRuns?.Count ?? 0) != imageCount ||
            ((_inlineImageRuns is null) != (prepared._inlineImageRuns is null)))
        {
            return false;
        }

        imageOrdinal = 0;
        foreach (RunState run in runStates)
        {
            if (run.SourceImageRun is null || run.PreparedImageRun is null)
                continue;
            if (_inlineImageRuns is null || prepared._inlineImageRuns is null)
                return false;
            if (!run.MatchesImageEntries(
                    _inlineImageRuns[imageOrdinal], prepared._inlineImageRuns[imageOrdinal]))
            {
                return false;
            }
            imageOrdinal++;
        }

        var effectiveAliases = new IReadOnlyList<string>[runCount];
        var resolvedStyles = new ElementStyle?[runCount];
        for (int index = 0; index < runCount; index++)
        {
            string[] sourceAliases = GetEffectiveRunAliases(index);
            string[] preparedAliases = prepared.GetEffectiveRunAliases(index);
            if (ReferenceEquals(sourceAliases, preparedAliases) ||
                !MatchesKeys(sourceAliases, preparedAliases) ||
                !ReferenceEquals(GetCachedRunStyle(index), prepared.GetCachedRunStyle(index)))
            {
                return false;
            }
            effectiveAliases[index] = preparedAliases;
            resolvedStyles[index] = GetCachedRunStyle(index);
        }

        if (ReferenceEquals(_styleContextKeys, prepared._styleContextKeys) ||
            ReferenceEquals(_styleAliasKeys, prepared._styleAliasKeys) ||
            !MatchesKeys(_styleContextKeys, prepared._styleContextKeys) ||
            !MatchesKeys(_styleAliasKeys, prepared._styleAliasKeys))
        {
            return false;
        }

        transfer = new InlineMeasuredPlacementTransfer(
            this,
            prepared,
            expectedAvailableWidth,
            runStates,
            prepared._styleContextKeys,
            prepared._styleAliasKeys,
            effectiveAliases,
            resolvedStyles,
            isTableCellTransfer,
            isContainerLeafTransfer,
            allowMeasuredVectorPlacement);
        return true;
    }

    private static bool IsSupportedMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey == MarkdownElementKeys.Body || InlineRunStyleResolver.IsHeadingElement(box.ElementKey)) &&
        box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box._inlineEmbedRuns is null &&
        (allowMeasuredVectorPlacement || box._vectorSceneRunCount == 0) &&
        !box._hasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box._pendingSceneGeometry.Length == 0) &&
        box._foregroundSpans.Count == 0;

    private static bool IsSupportedTableCellMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box._inlineEmbedRuns is null &&
        (allowMeasuredVectorPlacement || box._vectorSceneRunCount == 0) &&
        !box._hasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box._pendingSceneGeometry.Length == 0) &&
        box._foregroundSpans.Count == 0;

    private static bool IsSupportedContainerLeafMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.Body or
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
            MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or
            MarkdownElementKeys.ListMarker) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box._inlineEmbedRuns is null &&
        (allowMeasuredVectorPlacement || box._vectorSceneRunCount == 0) &&
        !box._hasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box._pendingSceneGeometry.Length == 0) &&
        box._foregroundSpans.Count == 0;

    internal static bool AreCompatibleMeasuredRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false) =>
        float.IsFinite(expectedAvailableWidth) && expectedAvailableWidth > 0f &&
        IsSupportedMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    internal static bool AreCompatibleTableCellRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        bool allowMeasuredVectorPlacement = false) =>
        IsSupportedTableCellMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    internal static bool AreCompatibleContainerLeafRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        bool allowMeasuredVectorPlacement = false) =>
        IsSupportedContainerLeafMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    private static bool AreCompatiblePlacementRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared) =>
        ArePrivateRasterPlacementContextsCompatible(source.Context, prepared.Context) &&
        source.BlockIndex == prepared.BlockIndex &&
        string.Equals(source.ElementKey, prepared.ElementKey, StringComparison.Ordinal) &&
        string.Equals(source.CodeLanguage, prepared.CodeLanguage, StringComparison.Ordinal) &&
        string.Equals(source.StyleState, prepared.StyleState, StringComparison.Ordinal) &&
        source.CodeBlockTextOffset == prepared.CodeBlockTextOffset &&
        source.CodeBlockTextLength == prepared.CodeBlockTextLength &&
        source.WordWrapping == prepared.WordWrapping &&
        source.TextAlignment == prepared.TextAlignment &&
        source.DrawContainerChrome == prepared.DrawContainerChrome &&
        source.UseContainerPadding == prepared.UseContainerPadding &&
        source.UseContainerMargin == prepared.UseContainerMargin &&
        source.SafeHtmlInlineFlowScopeId == prepared.SafeHtmlInlineFlowScopeId &&
        source.Margin.Equals(prepared.Margin) &&
        source.CollapsedTopMargin == prepared.CollapsedTopMargin &&
        source.SuppressTopMargin == prepared.SuppressTopMargin &&
        source.SuppressBottomMargin == prepared.SuppressBottomMargin &&
        source._usesProgressiveNativeSceneResidency == prepared._usesProgressiveNativeSceneResidency &&
        source._hasAtomicInlineObject == prepared._hasAtomicInlineObject &&
        source._bufferDirty == prepared._bufferDirty &&
        source._runTextLength == prepared._runTextLength &&
        ReferenceEquals(source._buffer, prepared._buffer) &&
        ReferenceEquals(source._resolvedContainerStyle, prepared._resolvedContainerStyle) &&
        MatchesKeys(source._styleContextKeys, prepared._styleContextKeys) &&
        MatchesKeys(source._styleAliasKeys, prepared._styleAliasKeys);

    private bool IsMeasuredSourceForTransfer(
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        bool expectedAtomic = (_inlineImageRuns?.Count ?? 0) > 0 ||
            allowMeasuredVectorPlacement && _vectorSceneRunCount > 0;
        if (IsDirty || _layout is null || _bufferDirty || _resolvedContainerStyle is null ||
            !float.IsFinite(_lastWidth) || _lastWidth <= 0f ||
            Math.Abs(_lastWidth - expectedAvailableWidth) > 0.5f ||
            !IsFiniteNonnegative(Bounds) || Bounds.Width != expectedAvailableWidth ||
            _runs.Count == 0 || _runTextLength != _buffer.Length ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            _hasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(Margin) || !double.IsFinite(CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    private bool IsMeasuredSourceForTableCellTransfer(
        float expectedSourceWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = _inlineImageRuns?.Count ?? 0;
        bool expectedAtomic = imageCount > 0 ||
            allowMeasuredVectorPlacement && _vectorSceneRunCount > 0;
        if (IsDirty || _layout is null || _bufferDirty || _resolvedContainerStyle is null ||
            !float.IsFinite(expectedSourceWidth) || expectedSourceWidth <= 0f ||
            !float.IsFinite(_lastWidth) || _lastWidth != expectedSourceWidth ||
            !IsFiniteNonnegative(Bounds) || Bounds.Width != expectedSourceWidth ||
            _runTextLength != _buffer.Length ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            imageCount > MaximumPrivateRasterPlacementImages ||
            _hasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(Margin) || !double.IsFinite(CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    private bool IsMeasuredSourceForContainerLeafTransfer(
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = _inlineImageRuns?.Count ?? 0;
        bool expectedAtomic = allowMeasuredVectorPlacement && _vectorSceneRunCount > 0;
        if (IsDirty || _layout is null || _bufferDirty || _resolvedContainerStyle is null ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !float.IsFinite(_lastWidth) || _lastWidth != expectedAvailableWidth ||
            !IsFiniteNonnegative(Bounds) || Bounds.Width != expectedAvailableWidth ||
            _runTextLength != _buffer.Length ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            imageCount != 0 || _inlineImageRuns is not null || _hasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(RawPlacementMargin) || !IsFiniteThickness(Margin) ||
            !double.IsFinite(CollapsedTopMargin))
        {
            return false;
        }

        // Empty paragraphs may have no runs, but only the established clean
        // empty native-layout shape is admitted. Zero-length runs remain
        // restricted to the existing table-cell path.
        return _runs.Count != 0 ||
            (_buffer.Length == 0 && _runTextLength == 0 && _inlineEmbedRuns is null &&
             _vectorSceneRunCount == 0 && !_hasDeferredSceneMarker &&
             _pendingSceneGeometry.Length == 0 && _foregroundSpans.Count == 0);
    }

    private static bool IsUnmeasuredPrivateCloneOf(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowImageFree = false,
        bool allowMeasuredVectorPlacement = false)
    {
        bool imageIndexShapeMatches = allowImageFree
            ? (prepared._inlineImageRuns?.Count ?? 0) == (source._inlineImageRuns?.Count ?? 0) &&
                ((prepared._inlineImageRuns is null) == (source._inlineImageRuns is null))
            : prepared._inlineImageRuns is { Count: > 0 } &&
                prepared._inlineImageRuns.Count == source._inlineImageRuns?.Count;
        return prepared.IsDirty && prepared._layout is null && prepared._selectionLayout is null &&
            prepared._layoutRetirementRecord is null &&
            prepared._selectionLayoutRetirementRecord is null &&
            prepared._cachedLineMetrics is null && !prepared._lineMetricsRead &&
            !prepared._bufferDirty && ReferenceEquals(prepared._buffer, source._buffer) &&
            prepared._runTextLength == source._runTextLength &&
            prepared._runs.Count == source._runs.Count && imageIndexShapeMatches &&
            prepared._vectorSceneRunCount == source._vectorSceneRunCount &&
            (allowMeasuredVectorPlacement || source._vectorSceneRunCount == 0) &&
            !source._hasDeferredSceneMarker && !prepared._hasDeferredSceneMarker &&
            prepared._sceneGeometryGeneration == 0 &&
            prepared._pendingSceneGeometryGeneration == 0 &&
            prepared._pendingSceneGeometry.Length == 0 &&
            prepared._lastWidth == source._lastWidth &&
            ReferenceEquals(prepared._resolvedContainerStyle, source._resolvedContainerStyle) &&
            prepared.Bounds.Width == 0 && prepared.Bounds.Height == 0 &&
            float.IsFinite(expectedAvailableWidth);
    }

    internal static bool MatchesRuns(
        InlineContainerBox source,
        InlineContainerBox prepared,
        RunState[] runs,
        IReadOnlyList<string>[] effectiveAliases,
        ElementStyle?[] sourceResolvedStyles,
        bool allowMeasuredVectorPlacement)
    {
        int sourceImageCount = source._inlineImageRuns?.Count ?? 0;
        int preparedImageCount = prepared._inlineImageRuns?.Count ?? 0;
        int vectorCount = 0;
        foreach (RunState run in runs)
            if (run.SourceVectorRun is not null)
                vectorCount++;
        if (source._runs.Count != runs.Length || prepared._runs.Count != runs.Length ||
            source._runTextLength != prepared._runTextLength ||
            sourceImageCount != preparedImageCount ||
            ((source._inlineImageRuns is null) != (prepared._inlineImageRuns is null)) ||
            !source.HasWellFormedEffectiveRunAliases ||
            !prepared.HasWellFormedEffectiveRunAliases ||
            source.ResolvedRunStyleCount != runs.Length ||
            prepared.ResolvedRunStyleCount != runs.Length ||
            source._foregroundSpans.Count != 0 || prepared._foregroundSpans.Count != 0 ||
            source._inlineEmbedRuns is not null || prepared._inlineEmbedRuns is not null ||
            source._hasDeferredSceneMarker || prepared._hasDeferredSceneMarker ||
            source._vectorSceneRunCount != vectorCount || prepared._vectorSceneRunCount != vectorCount ||
            (!allowMeasuredVectorPlacement && vectorCount != 0) ||
            source._hasAtomicInlineObject != prepared._hasAtomicInlineObject)
        {
            return false;
        }

        for (int index = 0; index < runs.Length; index++)
        {
            RunState run = runs[index];
            if (!ReferenceEquals(source._runs[index], run.SourceRun) ||
                !ReferenceEquals(prepared._runs[index], run.PreparedRun) ||
                !run.MatchesSourceAndPrepared() ||
                !MatchesKeys(source.GetEffectiveRunAliases(index), effectiveAliases[index]) ||
                !MatchesKeys(prepared.GetEffectiveRunAliases(index), effectiveAliases[index]) ||
                !ReferenceEquals(source.GetCachedRunStyle(index), sourceResolvedStyles[index]))
            {
                return false;
            }
        }

        int imageOrdinal = 0;
        foreach (RunState run in runs)
        {
            if (run.SourceImageRun is null || run.PreparedImageRun is null)
                continue;
            if (source._inlineImageRuns is null || prepared._inlineImageRuns is null ||
                imageOrdinal >= sourceImageCount ||
                !run.MatchesImageEntries(
                    source._inlineImageRuns[imageOrdinal],
                    prepared._inlineImageRuns[imageOrdinal]))
            {
                return false;
            }
            imageOrdinal++;
        }
        return imageOrdinal == sourceImageCount;
    }

    internal static bool MatchesContainerKeys(
        InlineContainerBox box,
        IReadOnlyList<string> contextKeys,
        IReadOnlyList<string> aliasKeys) =>
        MatchesKeys(box._styleContextKeys, contextKeys) &&
        MatchesKeys(box._styleAliasKeys, aliasKeys);

    private static bool MatchesKeys(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static bool NativeLayoutsAreDistinct(InlineContainerBox box) =>
        HasValidNativeLayoutCustody(box);

    internal static bool HasValidNativeLayoutCustody(InlineContainerBox box) =>
        (box._layout is null) == (box._layoutRetirementRecord is null) &&
        (box._selectionLayout is null) == (box._selectionLayoutRetirementRecord is null) &&
        (box._layout is null || box._selectionLayout is null ||
            !ReferenceEquals(box._layout, box._selectionLayout)) &&
        (box._layoutRetirementRecord is null || box._selectionLayoutRetirementRecord is null ||
            !ReferenceEquals(box._layoutRetirementRecord, box._selectionLayoutRetirementRecord));

    internal static bool Aliases(CanvasTextLayout? first, CanvasTextLayout? second) =>
        first is not null && ReferenceEquals(first, second);

    internal static bool Aliases(
        NativeResourceRetirementRecord? first,
        NativeResourceRetirementRecord? second) =>
        first is not null && ReferenceEquals(first, second);

    internal static bool SameRect(Rect first, Rect second) =>
        first.X == second.X && first.Y == second.Y &&
        first.Width == second.Width && first.Height == second.Height;

    internal static bool IsFiniteNonnegative(Rect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && bounds.Width >= 0 &&
        double.IsFinite(bounds.Height) && bounds.Height >= 0;

    internal static bool HasUsableMeasuredVectorRegions(in InlineSceneGeometry geometry)
    {
        if (geometry.RegionCount <= 0)
            return false;

        for (int index = 0; index < geometry.RegionCount; index++)
        {
            Rect region = geometry.GetRegionBounds(index);
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
                !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
                !double.IsFinite(region.Right) || !double.IsFinite(region.Bottom) ||
                region.Width < 0d || region.Height < 0d)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteThickness(Thickness thickness) =>
        double.IsFinite(thickness.Left) && double.IsFinite(thickness.Top) &&
        double.IsFinite(thickness.Right) && double.IsFinite(thickness.Bottom);
}
