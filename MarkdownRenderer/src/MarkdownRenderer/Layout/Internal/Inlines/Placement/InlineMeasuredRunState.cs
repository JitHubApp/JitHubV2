using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Inlines.Placement;

/// <summary>
/// The immutable identity, semantics and measured scalars of one run in an
/// inline measured-placement transfer, captured from the source run and its
/// private measured counterpart so the final publication gate can revalidate
/// them without native calls.
/// </summary>
internal readonly struct InlineMeasuredRunState
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

    // Kept whole: it only assigns the readonly capture fields, which must be
    // set in the constructor.
    private InlineMeasuredRunState(
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
        if (InlineMeasuredRunComparison.TryGetDirectSourceSlice(sourceRun, out SourceSliceTextRun sourceSlice) &&
            InlineMeasuredRunComparison.TryGetDirectSourceSlice(preparedRun, out SourceSliceTextRun preparedSlice) &&
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
        out InlineMeasuredRunState state)
    {
        state = default;
        if (sourceRun is null || preparedRun is null ||
            ReferenceEquals(sourceRun, preparedRun) ||
            sourceRun.GetType() != preparedRun.GetType() ||
            sourceRun.InlineIndex != index || preparedRun.InlineIndex != index ||
            !InlineMeasuredRunComparison.HasValidTextLengths(sourceRun) ||
            !InlineMeasuredRunComparison.HasValidTextLengths(preparedRun) ||
            !InlineMeasuredRunComparison.IsSupportedRun(sourceRun, allowMeasuredVectorPlacement) ||
            !InlineMeasuredRunComparison.IsSupportedRun(preparedRun, allowMeasuredVectorPlacement) ||
            !InlineMeasuredRunComparison.RunSemanticsMatch(sourceRun, preparedRun, allowMeasuredVectorPlacement))
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
                !InlineMeasuredRunComparison.SameVectorMetadata(sourceVectorRun, preparedVectorRun) ||
                !sourceVectorRun.HasExpectedMeasuredDimensions(vectorFontSize) ||
                sourceVectorRun.DesiredWidth != preparedVectorRun.DesiredWidth ||
                sourceVectorRun.DesiredHeight != preparedVectorRun.DesiredHeight ||
                sourceVectorRun.DesiredBaseline != preparedVectorRun.DesiredBaseline ||
                !InlineMeasuredPlacementCapture.SameRect(sourceVectorRun.LocalBounds, preparedVectorRun.LocalBounds) ||
                !InlineMeasuredPlacementCapture.IsFiniteNonnegative(sourceVectorRun.LocalBounds) ||
                !float.IsFinite(vectorFontSize) || vectorFontSize <= 0f)
            {
                return false;
            }
        }

        state = new InlineMeasuredRunState(
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
            !InlineMeasuredRunComparison.SameVectorMetadata(SourceVectorRun, PreparedVectorRun) ||
            SourceVectorRun.DesiredWidth != _sourceVectorWidth ||
            SourceVectorRun.DesiredHeight != _sourceVectorHeight ||
            SourceVectorRun.DesiredBaseline != _sourceVectorBaseline ||
            !InlineMeasuredPlacementCapture.SameRect(SourceVectorRun.LocalBounds, _sourceVectorLocalBounds) ||
            !InlineMeasuredPlacementCapture.IsFiniteNonnegative(SourceVectorRun.LocalBounds) ||
            !PreparedVectorRun.HasExpectedMeasuredDimensions(_vectorFontSize) ||
            !InlineMeasuredPlacementCapture.IsFiniteNonnegative(PreparedVectorRun.LocalBounds))
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
            !InlineMeasuredRunComparison.MatchesKeys(run.StyleAliases, _styleAliases) ||
            !InlineMeasuredRunComparison.MatchesKeys(run.StyleModifierKeys, _styleModifiers) ||
            !InlineMeasuredRunComparison.MatchesKeys(run.ScriptStyleAncestryKeys, _scriptStyleAncestryKeys) ||
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

    private bool MatchesTextFence(InlineRun run)
    {
        if (_usesDirectSourceSliceFence)
        {
            return InlineMeasuredRunComparison.TryGetDirectSourceSlice(run, out SourceSliceTextRun slice) &&
                ReferenceEquals(slice.SourceText, _text) &&
                slice.SourceStart == _sourceSpan.Start &&
                slice.SliceLength == _renderedLength;
        }

        return string.Equals(run.Text, _text, StringComparison.Ordinal) &&
            string.Equals(run.LayoutText, _layoutText, StringComparison.Ordinal) &&
            string.Equals(run.AccessibleText, _accessibleText, StringComparison.Ordinal);
    }
}
