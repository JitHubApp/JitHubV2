using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout.Boxes;

internal readonly struct IntrinsicWidthMetrics
{
    public readonly float Minimum;
    public readonly float Preferred;

    public IntrinsicWidthMetrics(float Minimum, float Preferred)
    {
        this.Minimum = Minimum;
        this.Preferred = Preferred;
    }
}

/// <summary>
/// Managed-only geometry captured from a text layout. This deliberately keeps
/// rectangles rather than CanvasTextLayoutRegion or CanvasTextLayout objects so
/// optional readiness indexes cannot extend native layout lifetimes.
/// </summary>
internal readonly struct InlineSceneGeometry
{
    private readonly Rect _firstRegion;
    private readonly Rect[]? _additionalRegions;
    private readonly int _additionalRegionOffset;

    internal InlineSceneGeometry(InlineVectorSceneRun run, CanvasTextLayoutRegion[]? regions)
    {
        Run = run;
        if (regions is { Length: > 0 })
        {
            _firstRegion = regions[0].LayoutBounds;
            if (regions.Length > 1)
            {
                _additionalRegions = new Rect[regions.Length - 1];
                for (int index = 1; index < regions.Length; index++)
                    _additionalRegions[index - 1] = regions[index].LayoutBounds;
                _additionalRegionOffset = 1;
            }
            RegionCount = regions.Length;
        }
        else
        {
            _firstRegion = default;
            _additionalRegions = null;
            RegionCount = 0;
        }
    }

    internal InlineSceneGeometry(InlineVectorSceneRun run, Rect[] regions)
    {
        Run = run;
        if (regions.Length > 0)
        {
            _firstRegion = regions[0];
            _additionalRegions = regions.Length > 1 ? regions : null;
            _additionalRegionOffset = 0;
            RegionCount = regions.Length;
        }
        else
        {
            _firstRegion = default;
            _additionalRegions = null;
            RegionCount = 0;
        }
    }

    internal readonly InlineVectorSceneRun Run;

    internal readonly int RegionCount;

    private InlineSceneGeometry(InlineVectorSceneRun run, in InlineSceneGeometry source)
    {
        Run = run;
        _firstRegion = source._firstRegion;
        _additionalRegions = source._additionalRegions;
        _additionalRegionOffset = source._additionalRegionOffset;
        RegionCount = source.RegionCount;
    }

    // Regions are immutable after capture. A root-local placement transfer
    // rebinds only semantic identity, without copying any per-region payload.
    internal InlineSceneGeometry WithRun(InlineVectorSceneRun run) => new(run, this);

    internal bool HasSameRegions(in InlineSceneGeometry other) =>
        _firstRegion.Equals(other._firstRegion) &&
        ReferenceEquals(_additionalRegions, other._additionalRegions) &&
        _additionalRegionOffset == other._additionalRegionOffset &&
        RegionCount == other.RegionCount;

    internal Rect GetRegionBounds(int index)
    {
        if ((uint)index >= (uint)RegionCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return index == 0 ? _firstRegion : _additionalRegions![index - _additionalRegionOffset];
    }
}

/// <summary>
/// Block hosting a sequence of inline runs (paragraph or heading body).
/// Owns a <see cref="CanvasTextLayout"/> built from a single concatenated buffer
/// with per-run style spans.
/// </summary>
internal sealed partial class InlineContainerBox : BlockBox
{
    private const float MinimumContentAwareLineScale = 0.1f;
    private const float MaximumContentAwareLineScale = 32f;
    private const float MaximumContentAwareLineHeight = 1_000_000f;
    private const float MinimumInverseInlineMetricHeight = 1f;
    // Keep descriptor scans under the snapshot lock and source-fence checks on
    // the publishing UI thread predictably bounded for unusual Markdown input.
    internal const int MaximumPrivateRasterPlacementRuns = 256;
    internal const int MaximumPrivateRasterPlacementImages = 64;
    internal const int MaximumPrivateRasterPlacementTextLength = 64 * 1024;
    // A clone copies context aliases, run aliases/modifiers, and only explicit
    // run-alias overrides. Inherited aliases use the clone's copied container
    // array, so count repeated references only when an override is retained.
    internal const int MaximumPrivateRasterPlacementStyleKeyCopyEntries = 8 * 1024;
    internal int PrivateRasterPlacementTextLength => _runTextLength;
    internal int PrivateRasterPlacementForegroundSpanCount => _foregroundSpans.Count;

    private readonly SingletonMutableList<InlineRun> _runs = new();
    private List<(InlineImageRun Run, int Offset)>? _inlineImageRuns;
    private List<(InlineEmbedRun Run, int Offset)>? _inlineEmbedRuns;
    private bool _hasAtomicInlineObject;
    private int _runTextLength;
    // Most runs inherit the container aliases. Keep that common case implicit;
    // once an override exists, null slots preserve the exact run ordinals.
    private List<string[]?>? _effectiveRunAliasOverrides;
    // Offscreen lazy boxes have not resolved any run style yet. Preserve the
    // ordinal cache contract without allocating a List and null slots for them.
    private List<ElementStyle?>? _resolvedRunStyles;
    private int ResolvedRunStyleCount => _resolvedRunStyles?.Count ?? _runs.Count;
    private ElementStyle? GetCachedRunStyle(int index)
    {
        if ((uint)index >= (uint)_runs.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _resolvedRunStyles?[index];
    }
    public readonly string ElementKey;
    private CanvasTextLayout? _layout;
    private NativeResourceRetirementRecord? _layoutRetirementRecord;
    private CanvasLineMetrics[]? _cachedLineMetrics;
    private bool _lineMetricsRead;
    private CanvasTextLayout? _selectionLayout;
    private NativeResourceRetirementRecord? _selectionLayoutRetirementRecord;
    private Color _selectionLayoutColor;
    private float _selectionLayoutWidth;
    private string _buffer = string.Empty;
    private bool _bufferDirty;
    private float _lastWidth;
    private sealed class VectorSceneState
    {
        internal InlineSceneGeometry[] PendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
        internal long SceneGeometryGeneration;
        internal long PendingSceneGeometryGeneration;
        internal int SceneCharacterRegionQueryCount;
        internal int VectorSceneRunCount;
        internal bool HasDeferredSceneMarker;
    }

    // Text-only inline boxes are the common case. Keep their vector-specific
    // counters and geometry in no per-box holder until a vector run is added.
    // This state is mutable and strictly box-owned; private placement clones
    // create their own holder rather than sharing it with the source box.
    private VectorSceneState? _vectorSceneState;
    private InlineSceneGeometry[] _pendingSceneGeometry
    {
        get => _vectorSceneState?.PendingSceneGeometry ?? Array.Empty<InlineSceneGeometry>();
        set
        {
            if (_vectorSceneState is { } state)
                state.PendingSceneGeometry = value;
            else if (value.Length != 0)
                throw new InvalidOperationException("Vector geometry requires vector-scene state.");
        }
    }
    private long _sceneGeometryGeneration
    {
        get => _vectorSceneState?.SceneGeometryGeneration ?? 0;
        set
        {
            if (_vectorSceneState is { } state)
                state.SceneGeometryGeneration = value;
            else if (value != 0)
                throw new InvalidOperationException("A scene generation requires vector-scene state.");
        }
    }
    private long _pendingSceneGeometryGeneration
    {
        get => _vectorSceneState?.PendingSceneGeometryGeneration ?? 0;
        set
        {
            if (_vectorSceneState is { } state)
                state.PendingSceneGeometryGeneration = value;
            else if (value != 0)
                throw new InvalidOperationException("Pending scene geometry requires vector-scene state.");
        }
    }
    private int _sceneCharacterRegionQueryCount => _vectorSceneState?.SceneCharacterRegionQueryCount ?? 0;
    private int _vectorSceneRunCount
    {
        get => _vectorSceneState?.VectorSceneRunCount ?? 0;
        set
        {
            if (_vectorSceneState is { } state)
                state.VectorSceneRunCount = value;
            else if (value != 0)
                throw new InvalidOperationException("Vector runs require vector-scene state.");
        }
    }
    private bool _hasDeferredSceneMarker
    {
        get => _vectorSceneState?.HasDeferredSceneMarker ?? false;
        set
        {
            if (_vectorSceneState is { } state)
                state.HasDeferredSceneMarker = value;
            else if (value)
                throw new InvalidOperationException("A deferred scene marker requires vector-scene state.");
        }
    }
    public readonly MarkdownLayoutContext Context;
    private readonly bool _usesProgressiveNativeSceneResidency;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    private ElementStyle? _resolvedContainerStyle;
    private IReadOnlyList<CodeBlockHighlightSpan> _foregroundSpans = Array.Empty<CodeBlockHighlightSpan>();
    private readonly NativeResourceRetirement _nativeRetirement;
    private readonly Action<IDisposable>? _nativeDisposer;

    private readonly struct InlineRollbackState
    {
        private readonly InlineImageRun? _imageRun;
        private readonly InlineVectorSceneRun? _vectorRun;
        private readonly float _desiredWidth;
        private readonly float _desiredHeight;
        private readonly float _desiredBaseline;
        private readonly Rect _localBounds;
        private readonly ImageBox.InlineMeasureCheckpoint _imageCheckpoint;

        internal InlineRollbackState(
            InlineImageRun? imageRun,
            InlineVectorSceneRun? vectorRun,
            float desiredWidth,
            float desiredHeight,
            float desiredBaseline,
            Rect localBounds,
            ImageBox.InlineMeasureCheckpoint imageCheckpoint = default)
        {
            _imageRun = imageRun;
            _vectorRun = vectorRun;
            _desiredWidth = desiredWidth;
            _desiredHeight = desiredHeight;
            _desiredBaseline = desiredBaseline;
            _localBounds = localBounds;
            _imageCheckpoint = imageCheckpoint;
        }

        internal void Commit() => _imageCheckpoint.Commit();

        internal void Restore()
        {
            _imageCheckpoint.Rollback();
            if (_imageRun is { } image)
                image.RestoreMeasuredSize(_desiredWidth, _desiredHeight);
            if (_vectorRun is { } vector)
            {
                vector.RestoreLayoutState(
                    _desiredWidth,
                    _desiredHeight,
                    _desiredBaseline,
                    _localBounds);
            }
        }
    }

    private readonly struct InlineScenePlacementRollbackState
    {
        internal InlineScenePlacementRollbackState(InlineVectorSceneRun run, Rect localBounds)
        {
            Run = run;
            LocalBounds = localBounds;
        }

        internal readonly InlineVectorSceneRun Run;
        internal readonly Rect LocalBounds;
    }

    /// <summary>
    /// Run currently being hovered by the pointer.  Retained for hit-test /
    /// click routing diagnostics only; we deliberately do NOT mutate any
    /// CanvasTextLayout state in response to hover changes because calling
    /// <c>CanvasTextLayout.SetColor</c> on every hover transition invalidates
    /// DirectWrite's cached glyph-run metrics, which causes visible glyph shake
    /// when the cursor moves over text or links.  Link hover affordance is
    /// communicated by the cursor shape (Hand) only — matching Win11 Settings,
    /// Notepad, Word, etc.  A future enhancement may render an underline
    /// accent on a XAML overlay (no canvas nnvalndatnon) for stronger
    /// affordance without re-introducnng shake.
    /// </summary>
    public InlineRun? HoveredRun { get; set; }

    public IReadOnlyList<InlineRun> Runs => _runs;
    internal bool HasInlineImages => _inlineImageRuns is not null;
    internal bool HasInlineEmbeds => _inlineEmbedRuns is not null;
    internal IReadOnlyList<(InlineImageRun Run, int Offset)> InlineImageRuns =>
        _inlineImageRuns is { } runs
            ? runs
            : Array.Empty<(InlineImageRun Run, int Offset)>();
    public string? CodeLanguage { get; init; }
    internal string? StyleState { get; init; }
    internal int CodeBlockTextOffset { get; init; }
    internal int CodeBlockTextLength { get; init; }
    internal CanvasWordWrapping WordWrapping { get; init; } = CanvasWordWrapping.Wrap;
    public CanvasHorizontalAlignment TextAlignment { get; set; } = CanvasHorizontalAlignment.Left;
    internal bool HasMeasuredLayout => _layout is not null;
    internal bool HasDeferredSceneMarker => _hasDeferredSceneMarker;
    internal int VectorSceneRunCount => _vectorSceneRunCount;
    internal long SceneGeometryGeneration =>
        _layout is not null && !_bufferDirty ? _sceneGeometryGeneration : 0;
    internal int SceneCharacterRegionQueryCount => _sceneCharacterRegionQueryCount;
    internal float ContentWidth => _layout is null ? (float)Bounds.Width : (float)Math.Max(_layout.LayoutBounds.Width, _layout.DrawBounds.Width);
    internal bool DrawContainerChrome { get; set; } = true;
    internal bool UseContainerPadding { get; set; } = true;
    internal bool UseContainerMargin { get; set; } = true;

    public InlineContainerBox(MarkdownLayoutContext context, string elementKey)
        : this(context, elementKey, null, null)
    {
    }

    internal InlineContainerBox(
        MarkdownLayoutContext context,
        string elementKey,
        NativeResourceRetirement? nativeRetirement,
        Action<IDisposable>? nativeDisposer)
    {
        Context = context;
        _nativeRetirement = nativeRetirement ?? NativeResourceRetirement.Shared;
        _nativeDisposer = nativeDisposer;
        // BuildLazy scopes the context hint only while constructing boxes.
        // Later offscreen measurements still need to retain their scalar
        // geometry handoff, so capture the mode on the box itself.
        _usesProgressiveNativeSceneResidency = context.UseProgressiveNativeSceneResidency;
        ElementKey = elementKey;
        _styleContextKeys = context.CreateStyleContextSnapshot();
        _styleAliasKeys = context.CreateStyleAliasSnapshot();
        // Object-initializer properties such as CodeLanguage and StyleState are
        // assigned after this constructor returns. Do not seed the contextual
        // style cache until Measure, when those immutable qualifiers are final.
        Margin = context.ThemeSnapshot.GetStyle(
            ElementKey,
            _styleContextKeys,
            _styleAliasKeys).Margin;
    }

    private InlineContainerBox(
        MarkdownLayoutContext context,
        InlineContainerBox source,
        bool copyCodeForegroundSpans)
    {
        Context = context;
        _nativeRetirement = source._nativeRetirement;
        _nativeDisposer = source._nativeDisposer;
        // BuildLazy's construction hint is scoped on the shared context. Its
        // current value may already have been restored when a placement clone
        // is captured; the source box holds the authoritative lifetime mode.
        _usesProgressiveNativeSceneResidency = source._usesProgressiveNativeSceneResidency;
        ElementKey = source.ElementKey;
        _styleContextKeys = CopyKeys(source._styleContextKeys);
        _styleAliasKeys = CopyKeys(source._styleAliasKeys);
        _resolvedContainerStyle = source._resolvedContainerStyle;
        CodeLanguage = source.CodeLanguage;
        StyleState = source.StyleState;
        CodeBlockTextOffset = source.CodeBlockTextOffset;
        CodeBlockTextLength = source.CodeBlockTextLength;
        WordWrapping = source.WordWrapping;
        TextAlignment = source.TextAlignment;
        DrawContainerChrome = source.DrawContainerChrome;
        UseContainerPadding = source.UseContainerPadding;
        UseContainerMargin = source.UseContainerMargin;
        _buffer = source._buffer;
        // The string is immutable and safe to share, but an unmeasured source
        // may have a stale buffer. Preserve that fact so the candidate builds
        // its own buffer later, outside the source snapshot gate.
        _bufferDirty = source._bufferDirty;
        _lastWidth = source._lastWidth;
        if (copyCodeForegroundSpans && source._foregroundSpans.Count != 0)
        {
            var spans = new CodeBlockHighlightSpan[source._foregroundSpans.Count];
            for (int index = 0; index < spans.Length; index++)
                spans[index] = source._foregroundSpans[index];
            _foregroundSpans = spans;
        }
        else
        {
            _foregroundSpans = Array.Empty<CodeBlockHighlightSpan>();
        }
        _hasAtomicInlineObject = source._hasAtomicInlineObject;
        BlockIndex = source.BlockIndex;
        SafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
        CopyPlacementMarginsFrom(source);
    }

    /// <summary>
    /// Builds a private, unmeasured candidate for a bounded inline sequence
    /// containing at least one completed raster. The caller must separately
    /// prove top-level/suffix ownership and rebuild semantic projections.
    /// Source image/lease outputs are borrowed identity fences; candidate
    /// cleanup owns only cloned ImageBoxes until the caller measures it.
    /// </summary>
    internal bool TryCreatePrivateRasterImageCandidates(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        candidate = null;
        expectedSources = null;
        if (!TryGetPrivateRasterPlacementCounts(
                targetContext, availableWidth, out int runCount, out int imageCount) || imageCount == 0)
            return false;

        return TryCreatePrivateInlinePlacementCloneCore(
            targetContext, runCount, imageCount, out candidate, out expectedSources);
    }

    /// <summary>
    /// Allocation-free preflight for a private inline root clone. This is used
    /// for every root before the snapshot acquires any candidate bitmap leases.
    /// It accepts text-only/raster-only roots but rejects stateful or unsupported
    /// run types and mismatched layout/source metadata.
    /// </summary>
    internal bool TryGetPrivateRasterPlacementCounts(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false)
        => TryGetPrivateRasterPlacementCounts(
            targetContext, availableWidth, out runCount, out imageCount, out _, allowUnmeasuredTextOnly, allowIdleImagePlacements);

    internal bool TryGetPrivateRasterPlacementCounts(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false)
        => TryGetPrivateRasterPlacementCountsCore(
            targetContext, availableWidth, out runCount, out imageCount, out styleKeyCopyEntries,
            allowUnmeasuredTextOnly, allowIdleImagePlacements, allowCodeForegroundSpans,
            allowMeasuredVectorPlacement: false);

    /// <summary>
    /// Root-local measurement may borrow immutable vector metrics while the
    /// published source retains its drawing. This recipe must never be used
    /// for a publishable whole-tree clone.
    /// </summary>
    internal bool TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries)
        => TryGetPrivateRasterPlacementCountsCore(
            targetContext, availableWidth, out runCount, out imageCount, out styleKeyCopyEntries,
            allowUnmeasuredTextOnly: false, allowIdleImagePlacements: false,
            allowCodeForegroundSpans: false, allowMeasuredVectorPlacement: true);

    private bool TryGetPrivateRasterPlacementCountsCore(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowUnmeasuredTextOnly,
        bool allowIdleImagePlacements,
        bool allowCodeForegroundSpans,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        runCount = 0;
        imageCount = 0;
        styleKeyCopyEntries = 0;
        targetContext.CancellationToken.ThrowIfCancellationRequested();
        if (!IsEligibleForPrivateInlinePlacementClone(
                targetContext, availableWidth, allowUnmeasuredTextOnly, allowIdleImagePlacements,
                allowCodeForegroundSpans, allowMeasuredVectorPlacement) ||
            !TryGetPrivateRasterPlacementShapeCounts(allowUnmeasuredTextOnly,
                out int candidateRunCount, out int candidateImageCount, out long candidateStyleKeyCopyEntries,
                allowIdleImagePlacements, allowCodeForegroundSpans, allowMeasuredVectorPlacement) ||
            (_layout is null && candidateImageCount != 0 && !allowIdleImagePlacements))
            return false;

        // Prove the complete source shape before any earlier root can acquire
        // candidate bitmap leases. The clone and final commit repeat these
        // lifecycle checks because image work can start after this inspection.
        if (_inlineImageRuns is { } imageEntries)
            foreach (var entry in imageEntries)
                if (!entry.Run.TryClassifyPrivateImagePlacement(
                        targetContext, allowIdleImagePlacements, out _, out _))
                    return false;

        runCount = candidateRunCount;
        imageCount = candidateImageCount;
        styleKeyCopyEntries = candidateStyleKeyCopyEntries;
        return true;
    }

    /// <summary>
    /// Allocation-free count of every key reference array copied by a private
    /// placement clone. The caller applies the same aggregate bound across
    /// document roots; this per-inline check also protects clone entry points.
    /// </summary>
    internal bool TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long entries)
    {
        entries = 0;
        if (_runs.Count > MaximumPrivateRasterPlacementRuns ||
            !HasWellFormedEffectiveRunAliases ||
            !TryAccumulateStyleKeyCopyEntries(ref entries, _styleContextKeys) ||
            !TryAccumulateStyleKeyCopyEntries(ref entries, _styleAliasKeys))
            return false;

        for (int index = 0; index < _runs.Count; index++)
        {
            InlineRun run = _runs[index];
            string[]? aliasOverride = GetEffectiveRunAliasOverride(index);
            if (run is null ||
                !TryAccumulateStyleKeyCopyEntries(ref entries, run.StyleAliases) ||
                !TryAccumulateStyleKeyCopyEntries(ref entries, run.StyleModifierKeys) ||
                aliasOverride is not null &&
                    !TryAccumulateStyleKeyCopyEntries(ref entries, aliasOverride))
                return false;
        }

        return true;
    }

    private static bool TryAccumulateStyleKeyCopyEntries(
        ref long total,
        IReadOnlyList<string>? keys)
    {
        if (keys is null)
            return false;

        int count = keys.Count;
        if (count < 0)
            return false;
        total = total > long.MaxValue - count ? long.MaxValue : total + count;
        return total <= MaximumPrivateRasterPlacementStyleKeyCopyEntries;
    }

    /// <summary>
    /// Builds an unmeasured private copy of a plain inline paragraph/heading.
    /// Text runs, ordinary links, and completed raster images are copied; all
    /// mutable layout/native state is rebuilt in the supplied generation context.
    /// </summary>
    internal bool TryCreatePrivateInlinePlacementClone(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        candidate = null;
        expectedSources = null;
        if (!TryGetPrivateRasterPlacementCounts(
                targetContext, availableWidth, out int runCount, out int imageCount, out _,
                allowUnmeasuredTextOnly, allowIdleImagePlacements, allowCodeForegroundSpans))
            return false;

        return TryCreatePrivateInlinePlacementCloneCore(
            targetContext, runCount, imageCount, out candidate, out expectedSources,
            allowIdleImagePlacements, allowCodeForegroundSpans);
    }

    /// <summary>
    /// Builds a non-publishable measurement candidate for a completed image
    /// root containing stable inline vector content. No vector drawing or
    /// native scene resources are created or borrowed by the candidate.
    /// </summary>
    internal bool TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources)
    {
        candidate = null;
        expectedSources = null;
        if (!TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                targetContext, availableWidth, out int runCount, out int imageCount, out _))
            return false;

        return TryCreatePrivateInlinePlacementCloneCore(
            targetContext, runCount, imageCount, out candidate, out expectedSources,
            allowMeasuredVectorPlacement: true);
    }

    private bool TryCreatePrivateInlinePlacementCloneCore(
        MarkdownLayoutContext targetContext,
        int runCount,
        int imageCount,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false,
        bool allowMeasuredVectorPlacement = false)
    {
        candidate = null;
        expectedSources = null;
        var copiedRuns = new InlineRun[runCount];
        var sourceFences = new List<RasterPlacementSourceFence>(imageCount);
        var unownedImageClones = new List<ImageBox>(imageCount);
        InlineContainerBox? privateCandidate = null;
        try
        {
            for (int index = 0; index < runCount; index++)
            {
                targetContext.CancellationToken.ThrowIfCancellationRequested();
                InlineRun sourceRun = _runs[index];
                if (PrivateTextRunPlacement.IsSupported(sourceRun))
                {
                    copiedRuns[index] = PrivateTextRunPlacement.Clone(sourceRun) ??
                        throw new InvalidOperationException("A supported built-in text run omitted its private placement recipe.");
                }
                else if (sourceRun is LinkRun linkRun)
                {
                    copiedRuns[index] = linkRun.CloneForPrivateRasterPlacement();
                }
                else if (allowMeasuredVectorPlacement && sourceRun is InlineVectorSceneRun vectorRun)
                {
                    copiedRuns[index] = vectorRun.CloneForMeasuredPlacement();
                }
                else if (sourceRun is InlineImageRun imageRun &&
                         TryCreateImageRunPlacement(imageRun, targetContext, allowIdleImagePlacements,
                             out InlineImageRun? copiedImageRun,
                             out RasterPlacementSourceFence sourceFence) && copiedImageRun is not null)
                {
                    copiedRuns[index] = copiedImageRun;
                    unownedImageClones.Add(copiedImageRun.Image);
                    sourceFences.Add(sourceFence);
                }
                else
                {
                    Exception? cleanupFailure = DiscardPrivateRasterCloneConstruction(
                        targetContext, privateCandidate, unownedImageClones);
                    privateCandidate = null;
                    unownedImageClones.Clear();
                    if (cleanupFailure is not null)
                        throw cleanupFailure;
                    return false;
                }

                targetContext.CancellationToken.ThrowIfCancellationRequested();
            }

            privateCandidate = new InlineContainerBox(targetContext, this, allowCodeForegroundSpans);
            int offset = 0;
            for (int index = 0; index < copiedRuns.Length; index++)
            {
                targetContext.CancellationToken.ThrowIfCancellationRequested();
                InlineRun copiedRun = copiedRuns[index];
                // Preflight required the source ordinals to be exact; preserve
                // rather than repair them so malformed identity never passes.
                privateCandidate._runs.Add(copiedRun);
                if (copiedRun is InlineImageRun copiedImage)
                    unownedImageClones.Remove(copiedImage.Image);
                string[]? sourceAliasOverride = GetEffectiveRunAliasOverride(index);
                privateCandidate.AppendEffectiveRunAliasOverride(
                    index,
                    sourceAliasOverride is null ? null : CopyKeys(sourceAliasOverride));
                if (_resolvedRunStyles is not null)
                    (privateCandidate._resolvedRunStyles ??= new(runCount)).Add(_resolvedRunStyles[index]);
                if (copiedRun is InlineImageRun imageRun)
                {
                    imageRun.Image.BlockIndex = BlockIndex;
                    (privateCandidate._inlineImageRuns ??= new()).Add((imageRun, offset));
                }
                else if (copiedRun is InlineVectorSceneRun)
                {
                    VectorSceneState vectorState = privateCandidate._vectorSceneState ??= new VectorSceneState();
                    vectorState.VectorSceneRunCount++;
                }
                offset += copiedRun.TextLength;
            }
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            privateCandidate._runTextLength = offset;

            if (unownedImageClones.Count != 0 || sourceFences.Count != imageCount)
                throw new InvalidOperationException("The private inline candidate did not take ownership of every cloned image.");

            expectedSources = sourceFences.ToArray();
            candidate = privateCandidate;
            privateCandidate = null;
            return true;
        }
        catch (Exception constructionFailure)
        {
            Exception? cleanupFailure = DiscardPrivateRasterCloneConstruction(
                targetContext, privateCandidate, unownedImageClones);
            if (cleanupFailure is not null)
                TryAttachCleanupFailure(constructionFailure, cleanupFailure);
            throw;
        }
    }

    private static bool TryCreateImageRunPlacement(
        InlineImageRun source,
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out InlineImageRun? clone,
        out RasterPlacementSourceFence fence)
    {
        fence = default;
        ImageBox? image;
        SharedCanvasBitmapCache.Lease? lease;
        ImageBox.CompletedSvgPlacementMetadataFence? metadata;
        bool isIdle = false;
        int idleGeneration = 0;
        bool created = allowIdleImagePlacements
            ? source.TryCreatePrivateImagePlacementClone(targetContext, out clone, out image,
                out lease, out metadata, out isIdle, out idleGeneration)
            : source.TryCreatePrivateRasterPlacementClone(targetContext, out clone, out image,
                out lease, out metadata);
        if (!created || clone is null)
        {
            if (clone is not null)
                targetContext.DiscardConstructionBlock(clone.Image);
            clone = null;
            return false;
        }
        if (image is null || (isIdle ? lease is not null || metadata is not null : lease is null))
        {
            var failure = new InvalidOperationException("A private image clone omitted its source identity fence.");
            try { targetContext.DiscardConstructionBlock(clone.Image); }
            catch (Exception cleanupFailure) { TryAttachCleanupFailure(failure, cleanupFailure); }
            clone = null;
            throw failure;
        }
        fence = new RasterPlacementSourceFence(image, lease, metadata, isIdle, idleGeneration);
        return true;
    }

    private bool TryGetPrivateRasterPlacementShapeCounts(
        bool allowUnmeasuredTextOnly,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false,
        bool allowMeasuredVectorPlacement = false)
    {
        runCount = 0;
        imageCount = 0;
        styleKeyCopyEntries = 0;
        if (_runs.Count > MaximumPrivateRasterPlacementRuns ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            _inlineImageRuns is { Count: > MaximumPrivateRasterPlacementImages } ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            (_inlineImageRuns?.Count ?? 0) > _runs.Count ||
            !CanCopyPrivateCodeForegroundSpans(allowCodeForegroundSpans) ||
            !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, _styleContextKeys) ||
            !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, _styleAliasKeys))
            return false;

        // Empty table cells have no logical inline runs but still own mutable
        // native layout state and must be privately reconstructed with their
        // table candidate. Admit only the exact clean empty shape; malformed
        // partial indexes, atomic runs, or stale text buffers remain rejected.
        if (_runs.Count == 0)
        {
            if (_runTextLength != 0 || _buffer.Length != 0 || _inlineImageRuns is not null ||
                _inlineEmbedRuns is not null || _hasAtomicInlineObject ||
                _vectorSceneRunCount != 0 || _hasDeferredSceneMarker ||
                _foregroundSpans.Count != 0 ||
                (_layout is null && _selectionLayout is not null))
                return false;

            return true;
        }

        IReadOnlyList<(InlineImageRun Run, int Offset)> imageEntries = _inlineImageRuns is { } entries
            ? entries
            : Array.Empty<(InlineImageRun Run, int Offset)>();
        int imageOrdinal = 0;
        int vectorOrdinal = 0;
        long offset = 0;
        for (int runIndex = 0; runIndex < _runs.Count; runIndex++)
        {
            InlineRun run = _runs[runIndex];
            if (run is null || run.InlineIndex != runIndex || run.RenderedLength != run.TextLength ||
                run.LayoutTextLength != run.TextLength)
                return false;

            string[]? aliasOverride = GetEffectiveRunAliasOverride(runIndex);
            if (!TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, run.StyleAliases) ||
                !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, run.StyleModifierKeys) ||
                aliasOverride is not null &&
                    !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, aliasOverride))
                return false;

            if (PrivateTextRunPlacement.IsSupported(run) || run is LinkRun)
            {
                if (run is LinkRun linkRun && (linkRun.IsDisclosure || linkRun.IsExpanded))
                    return false;
            }
            else if (run is InlineImageRun imageRun)
            {
                if (imageOrdinal >= imageEntries.Count)
                    return false;
                var entry = imageEntries[imageOrdinal++];
                if (!ReferenceEquals(entry.Run, imageRun) || offset > int.MaxValue || entry.Offset != (int)offset ||
                    imageRun.Image.BlockIndex != BlockIndex)
                    return false;

                // Bounding this duplicate check at 64 keeps it allocation-free
                // while rejecting aliases before acquiring any clone leases.
                for (int priorIndex = 0; priorIndex < imageOrdinal - 1; priorIndex++)
                {
                    if (ReferenceEquals(imageEntries[priorIndex].Run.Image, imageRun.Image))
                        return false;
                }
            }
            else if (allowMeasuredVectorPlacement && run is InlineVectorSceneRun vectorRun)
            {
                // Read only renderer-owned identity and scalar state. A
                // placeholder or another measurement-only run cannot become
                // a publishable drawing through this private recipe.
                if (vectorRun.IsDisposed || vectorRun.IsMeasuredPlacementOnly ||
                    vectorRun.DeferredSceneMarker is not null ||
                    !float.IsFinite(vectorRun.DesiredWidth) || vectorRun.DesiredWidth <= 0 ||
                    !float.IsFinite(vectorRun.DesiredHeight) || vectorRun.DesiredHeight <= 0 ||
                    !float.IsFinite(vectorRun.DesiredBaseline) || vectorRun.DesiredBaseline < 0 ||
                    vectorRun.DesiredBaseline > vectorRun.DesiredHeight ||
                    !double.IsFinite(vectorRun.LocalBounds.X) || !double.IsFinite(vectorRun.LocalBounds.Y) ||
                    !double.IsFinite(vectorRun.LocalBounds.Width) || vectorRun.LocalBounds.Width < 0 ||
                    !double.IsFinite(vectorRun.LocalBounds.Height) || vectorRun.LocalBounds.Height < 0)
                    return false;
                vectorOrdinal++;
            }
            else
            {
                return false;
            }

            offset += run.TextLength;
            if (offset > int.MaxValue)
                return false;
        }

        bool unmeasuredDirtyText = allowUnmeasuredTextOnly && _layout is null && _bufferDirty;
        if (imageOrdinal != imageEntries.Count || vectorOrdinal != _vectorSceneRunCount || offset != _runTextLength ||
            (!unmeasuredDirtyText && offset != _buffer.Length))
            return false;

        if (allowMeasuredVectorPlacement && _pendingSceneGeometry.Length != 0)
        {
            if (_pendingSceneGeometry.Length != vectorOrdinal || _sceneGeometryGeneration == 0 ||
                _pendingSceneGeometryGeneration !=
                    (_usesProgressiveNativeSceneResidency ? _sceneGeometryGeneration : 0))
                return false;
            int geometryIndex = 0;
            foreach (InlineRun run in _runs)
                if (run is InlineVectorSceneRun vectorRun &&
                    !ReferenceEquals(_pendingSceneGeometry[geometryIndex++].Run, vectorRun))
                    return false;
        }

        // Unmeasured images can carry only pristine descriptors, never an
        // active load, accepted payload, or shared mutable source owner.
        if (_layout is null && imageOrdinal != 0)
        {
            if (!allowIdleImagePlacements)
                return false;
            foreach (var entry in imageEntries)
                if (!ReferenceEquals(entry.Run.Image.Context, Context) ||
                    !entry.Run.Image.IsCurrentIdleImagePlacement(0))
                    return false;
        }

        runCount = _runs.Count;
        imageCount = imageOrdinal;
        return true;
    }

    private static Exception? DiscardPrivateRasterCloneConstruction(
        MarkdownLayoutContext targetContext,
        InlineContainerBox? privateCandidate,
        List<ImageBox> unownedImageClones)
    {
        Exception? firstFailure = null;
        if (privateCandidate is not null)
        {
            try
            {
                targetContext.DiscardConstructionBlock(privateCandidate);
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        }

        foreach (ImageBox image in unownedImageClones)
        {
            try
            {
                targetContext.DiscardConstructionBlock(image);
            }
            catch (Exception exception)
            {
                if (firstFailure is null)
                    firstFailure = exception;
                else
                {
                    try { firstFailure.Data["MarkdownRenderer.PrivateRasterAdditionalCleanupFailure"] = exception; }
                    catch { /* Cleanup diagnostics must not mask the primary cleanup failure. */ }
                }
            }
        }

        return firstFailure;
    }

    private static void TryAttachCleanupFailure(Exception primaryFailure, Exception cleanupFailure)
    {
        try
        {
            primaryFailure.Data["MarkdownRenderer.PrivateRasterCandidateCleanupFailure"] = cleanupFailure;
        }
        catch
        {
            // Cleanup diagnostics must not mask the candidate failure.
        }
    }

    private bool IsEligibleForPrivateInlinePlacementClone(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        bool allowUnmeasuredTextOnly,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false,
        bool allowMeasuredVectorPlacement = false)
    {
        bool unmeasuredTextOnly = allowUnmeasuredTextOnly && _layout is null &&
            _selectionLayout is null && (_inlineImageRuns is null || allowIdleImagePlacements) && _inlineEmbedRuns is null &&
            (!_hasAtomicInlineObject || (allowIdleImagePlacements && _inlineImageRuns is not null)) &&
            _vectorSceneRunCount == 0 && !_hasDeferredSceneMarker &&
            _pendingSceneGeometry.Length == 0 && CanCopyPrivateCodeForegroundSpans(allowCodeForegroundSpans);
        bool cleanEmptyCell = _runs.Count == 0 && _runTextLength == 0 &&
            _buffer.Length == 0 && _inlineImageRuns is null &&
            _inlineEmbedRuns is null && !_hasAtomicInlineObject;
        bool zeroLengthBuiltInTextTableCell = IsZeroLengthBuiltInTextTableCell();
        if ((!unmeasuredTextOnly &&
                (_layout is null || _bufferDirty ||
                 (_buffer.Length == 0 && !cleanEmptyCell && !zeroLengthBuiltInTextTableCell) ||
                 _resolvedContainerStyle is null)) ||
            !float.IsFinite(availableWidth) || availableWidth <= 0 ||
            _inlineEmbedRuns is not null ||
            (!allowMeasuredVectorPlacement && _vectorSceneRunCount != 0) ||
            _hasDeferredSceneMarker ||
            (!allowMeasuredVectorPlacement && _pendingSceneGeometry.Length != 0) ||
            !CanCopyPrivateCodeForegroundSpans(allowCodeForegroundSpans) ||
            !ArePrivateRasterPlacementContextsCompatible(Context, targetContext))
            return false;

        return true;
    }

    private bool IsZeroLengthBuiltInTextTableCell()
    {
        if (ElementKey is not (MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell) ||
            _runs.Count == 0 || _runs.Count > MaximumPrivateRasterPlacementRuns ||
            _runTextLength != 0 || _buffer.Length != 0 ||
            _inlineImageRuns is not null || _inlineEmbedRuns is not null || _hasAtomicInlineObject ||
            _vectorSceneRunCount != 0 || _hasDeferredSceneMarker ||
            _pendingSceneGeometry.Length != 0 || _foregroundSpans.Count != 0)
        {
            return false;
        }

        foreach (InlineRun run in _runs)
        {
            if (run is null || !PrivateTextRunPlacement.IsSupported(run) ||
                run.TextLength != 0 || run.RenderedLength != 0 || run.LayoutTextLength != 0)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool ArePrivateRasterPlacementContextsCompatible(
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext)
        => ReferenceEquals(sourceContext.ResourceCreator, targetContext.ResourceCreator) &&
           ReferenceEquals(sourceContext.ThemeSnapshot, targetContext.ThemeSnapshot) &&
           ReferenceEquals(sourceContext.Registry, targetContext.Registry) &&
           ReferenceEquals(sourceContext.Dispatcher, targetContext.Dispatcher) &&
           ReferenceEquals(sourceContext.ImageResolver, targetContext.ImageResolver) &&
           ReferenceEquals(sourceContext.SvgRenderer, targetContext.SvgRenderer) &&
           ReferenceEquals(sourceContext.PerformanceSession, targetContext.PerformanceSession) &&
           ReferenceEquals(sourceContext.ImagePreparationOwner, targetContext.ImagePreparationOwner) &&
           ReferenceEquals(sourceContext.ImageDocumentSource, targetContext.ImageDocumentSource) &&
           ReferenceEquals(sourceContext.SourceMap, targetContext.SourceMap) &&
           sourceContext.FlowDirection == targetContext.FlowDirection &&
           sourceContext.RasterizationScale == targetContext.RasterizationScale &&
           sourceContext.AllowThirdPartyRemoteImages == targetContext.AllowThirdPartyRemoteImages &&
           string.Equals(sourceContext.Language, targetContext.Language, StringComparison.Ordinal) &&
           string.Equals(sourceContext.ImageDocumentPath, targetContext.ImageDocumentPath, StringComparison.Ordinal) &&
           Equals(sourceContext.ImageBaseUri, targetContext.ImageBaseUri) &&
           ReferenceEquals(sourceContext.StringProvider, targetContext.StringProvider) &&
           ReferenceEquals(sourceContext.CommandProvider, targetContext.CommandProvider) &&
           ReferenceEquals(sourceContext.DisclosureStates, targetContext.DisclosureStates);

    private bool CanCopyPrivateCodeForegroundSpans(bool allowCodeForegroundSpans)
    {
        if (_foregroundSpans.Count == 0)
            return true;

        return allowCodeForegroundSpans &&
            ElementKey == MarkdownElementKeys.CodeBlock &&
            _foregroundSpans.Count <= MaximumPrivateRasterPlacementRuns;
    }

    private static string[] CopyKeys(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    public void Add(InlineRun run)
    {
        System.Diagnostics.Debug.Assert(BlockIndex != 0,
            "BlockIndex must be assigned before calling Add(); source-map entries wnll be registered under block 0 otherwise.");
        VectorSceneState? vectorStateForAddedRun = run is InlineVectorSceneRun
            ? _vectorSceneState ?? new VectorSceneState()
            : null;
        run.InlineIndex = _runs.Count;
        int runOffset = _runTextLength;
        _runTextLength += run.TextLength;
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun)
            _hasAtomicInlineObject = true;
        if (run is InlineImageRun imageRun)
        {
            (_inlineImageRuns ??= new()).Add((imageRun, runOffset));
            // The image participates in asynchronous relayout and UIA using
            // the owning paragraph's logical block. Keep that identity on the
            // nested ImageBox so a late load can invalidate only this owner.
            imageRun.Image.BlockIndex = BlockIndex;
        }
        else if (run is InlineEmbedRun embedRun)
        {
            (_inlineEmbedRuns ??= new()).Add((embedRun, runOffset));
        }
        _runs.Add(run);
        if (run is InlineVectorSceneRun vectorScene)
        {
            VectorSceneState vectorState = vectorStateForAddedRun!;
            _vectorSceneState = vectorState;
            vectorState.VectorSceneRunCount++;
            vectorState.HasDeferredSceneMarker |= vectorScene.DeferredSceneMarker is not null;
        }
        AppendEffectiveRunAliasOverride(
            run.InlineIndex,
            run.StyleAliases.Count == 0 ? null : CombineAliases(_styleAliasKeys, run.StyleAliases));
        _resolvedRunStyles?.Add(null);
        if (run is SourceSliceTextRun sourceSlice &&
            ReferenceEquals(sourceSlice.SourceText, Context.SourceMap.SourceText) &&
            sourceSlice.SourceStart == run.SourceSpan.Start &&
            sourceSlice.SliceLength == run.SourceSpan.Length)
        {
            Context.SourceMap.Add(BlockIndex, run.InlineIndex, run.RenderedLength, run.SourceSpan);
        }
        else
        {
            Context.SourceMap.Add(BlockIndex, run.InlineIndex, run.RenderedLength, run.SourceSpan, run.Text);
        }
        _bufferDirty = true; // buffer is stale untnl next BuildBuffer()
    }

    /// <summary>
    /// Detaches image runs from an unmeasured staging container so another
    /// inline container can become their sole owner. All retained-run indexes
    /// and lookup metadata are rebuilt before returning; measured text layouts
    /// cannot be transferred because their native ranges encode the old run
    /// sequence.
    /// </summary>
    internal InlineImageRun[] DetachInlineImagesForTransfer()
    {
        if (_layout is not null || _selectionLayout is not null)
        {
            throw new InvalidOperationException(
                "Inline image runs can only be transferred before their container is measured.");
        }

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

        _inlineImageRuns = null;
        _inlineEmbedRuns = null;
        _hasAtomicInlineObject = false;
        _runTextLength = 0;
        _vectorSceneRunCount = 0;
        _hasDeferredSceneMarker = false;
        _effectiveRunAliasOverrides = null;
        _resolvedRunStyles = null;

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
                _vectorSceneState ??= new VectorSceneState();
                _vectorSceneRunCount++;
                _hasDeferredSceneMarker |= vectorScene.DeferredSceneMarker is not null;
            }

            AppendRebuiltEffectiveRunAliasOverride(
                index,
                run.StyleAliases.Count == 0 ? null : CombineAliases(_styleAliasKeys, run.StyleAliases));
        }

        _buffer = string.Empty;
        _bufferDirty = true;
        _sceneGeometryGeneration = 0;
        _pendingSceneGeometryGeneration = 0;
        _pendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
        return detached;
    }

    internal void SetForegroundSpans(IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        _foregroundSpans = spans ?? Array.Empty<CodeBlockHighlightSpan>();
        if (_layout is not null)
            ApplyForegroundSpans(_layout);
    }

    /// <summary>
    /// Converts a <see cref="DocumentPosition"/> (which must target this box) to an
    /// absolute character index withnn the concatenated inline buffer.
    /// </summary>
    public int GetBufferCharOffset(DocumentPosition pos)
    {
        int offset = 0;
        for (int n = 0; n < _runs.Count; n++)
        {
            if (n == pos.InlineIndex) return offset + Math.Clamp(pos.CharacterOffset, 0, _runs[n].TextLength);
            offset += _runs[n].TextLength;
        }
        return offset;
    }

    /// <summary>
    /// Converts an absolute character index withnn the buffer back to a
    /// <see cref="DocumentPosition"/> targetnng this box.
    /// </summary>
    public DocumentPosition GetPositionFromBufferOffset(int bufOffset)
    {
        bufOffset = Math.Max(0, bufOffset);
        int offset = 0;
        for (int n = 0; n < _runs.Count; n++)
        {
            int len = _runs[n].TextLength;
            if (bufOffset < offset + len)
                return new DocumentPosition(BlockIndex, _runs[n].InlineIndex, bufOffset - offset);
            // At exact run boundary: prefer start of next run (continue loou).
            offset += len;
        }
        // bufOffset >= total length: clamp to end of last run.
        if (_runs.Count == 0) return new DocumentPosition(BlockIndex, 0, 0);
        int last = _runs.Count - 1;
        return new DocumentPosition(BlockIndex, last, _runs[last].TextLength);
    }

    private void EnsureBuffer()
    {
        if (_runs.Count == 0) { _buffer = string.Empty; _bufferDirty = false; return; }
        // Rely solely on _bufferDirty (set by Add()) — the _buffer.Length == 0 fallback
        // would cause repeated BuildBuffer() calls for boxes whose runs all produce empty
        // text (e.g. embed-placeholder runs), burning CPU without changing anythnng.
        if (_bufferDirty) BuildBuffer();
    }

    /// <summary>
    /// Returns the start and end positions that span the word containing
    /// <paramref name="pos"/>.  A "word" is a maximal sequence of non-whitespace
    /// characters in the buffer.
    /// </summary>
    public (DocumentPosition Start, DocumentPosition End) GetWordBoundaries(DocumentPosition pos)
    {
        EnsureBuffer();
        if (_buffer.Length == 0) return (pos, pos);
        int idx = Math.Clamp(GetBufferCharOffset(pos), 0, Math.Max(0, _buffer.Length - 1));
        var (start, end) = TextBoundaryHelper.FindWordBoundaries(_buffer, idx);
        return (GetPositionFromBufferOffset(start), GetPositionFromBufferOffset(end));
    }

    /// <summary>Returns positions for the very start and end of this inline container.</summary>
    public (DocumentPosition Start, DocumentPosition End) GetBlockBoundaries()
    {
        var start = new DocumentPosition(BlockIndex, 0, 0);
        DocumentPosition end;
        if (_runs.Count == 0)
        {
            end = start;
        }
        else
        {
            int last = _runs.Count - 1;
            end = new DocumentPosition(BlockIndex, last, _runs[last].TextLength);
        }
        return (start, end);
    }

    public override float Measure(float availableWidth)
    {
        ThrowIfCancellationRequested();
        if (!HasValidNativeLayoutCustody(this))
            throw new InvalidOperationException("An inline text layout is missing its reserved native-retirement owner.");
        var style = GetContainerStyle();
        var padding = GetEffectivePadding(style);
        CanvasTextLayout? previousLayout = _layout;
        NativeResourceRetirementRecord? previousLayoutRetirementRecord = _layoutRetirementRecord;
        CanvasLineMetrics[]? previousLineMetrics = _cachedLineMetrics;
        bool previousLineMetricsRead = _lineMetricsRead;
        CanvasTextLayout? previousSelectionLayout = _selectionLayout;
        NativeResourceRetirementRecord? previousSelectionLayoutRetirementRecord =
            _selectionLayoutRetirementRecord;
        Color previousSelectionLayoutColor = _selectionLayoutColor;
        float previousSelectionLayoutWidth = _selectionLayoutWidth;
        string previousBuffer = _buffer;
        bool previousBufferDirty = _bufferDirty;
        float previousWidth = _lastWidth;
        Rect previousBounds = Bounds;
        List<InlineRollbackState>? changedInlineMeasures = null;
        InlineScenePlacementRollbackState[]? previousScenePlacements = null;
        CanvasTextLayout? replacementLayout = null;
        NativeResourceRetirementRecord? replacementLayoutRetirementRecord = null;
        InlineSceneGeometry[]? replacementSceneGeometry = null;
        float measuredHeight;
        // Measure with the same effective margin that Arrange/paint will use.
        // Resolving from the current style keeps remeasurement correct when a
        // theme changes authored margins, without publishing state before the
        // measurement succeeds.
        Thickness margin = UseContainerMargin ? ResolvePlacementMargin(style.Margin) : Margin;
        float horizontalPadding = (float)(padding.Left + padding.Right);
        float layoutWidth = Math.Max(1f, availableWidth - horizontalPadding);

        try
        {
            bool atomicMetricsChanged = MeasureAtomicInlineRuns(
                layoutWidth,
                style.FontSize,
                out changedInlineMeasures);
            if (atomicMetricsChanged)
            {
                InvalidateLineMetrics();
            }

            if (_layout is null || atomicMetricsChanged || _bufferDirty ||
                Math.Abs(_lastWidth - availableWidth) > 0.5f)
            {
                // Only rebuild the run-content string when runs changed; skip on pure
                // width-change reflow so repeated window resizes don't re-allocate.
                if (_bufferDirty) BuildBuffer();
                InvalidateLineMetrics();
                int vectorCount = 0;
                if (previousLayout is not null)
                {
                    foreach (InlineRun run in _runs)
                    {
                        if (run is InlineVectorSceneRun)
                            vectorCount++;
                    }
                }

                if (vectorCount > 0)
                {
                    previousScenePlacements = new InlineScenePlacementRollbackState[vectorCount];
                    int vectorIndex = 0;
                    foreach (InlineRun run in _runs)
                    {
                        if (run is InlineVectorSceneRun vector)
                        {
                            previousScenePlacements[vectorIndex++] = new InlineScenePlacementRollbackState(
                                vector,
                                vector.LocalBounds);
                        }
                    }
                }

                using var formatLease = CreateTextFormatLease(style, WordWrapping, TextAlignment);
                NativeResourceRetirementRecord candidateRetirementRecord =
                    _nativeRetirement.Reserve(
                        resourceGroupCount: 0,
                        temporarySlotCount: 1,
                        disposer: _nativeDisposer);
                replacementLayoutRetirementRecord = candidateRetirementRecord;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                CanvasTextLayout candidateLayout = CanvasTextLayoutFactory.Create(
                    Context.ResourceCreator,
                    _buffer,
                    formatLease.Format,
                    layoutWidth,
                    float.MaxValue,
                    Context.LayoutDiagnostics);
#else
                CanvasTextLayout candidateLayout = new CanvasTextLayout(
                    Context.ResourceCreator,
                    _buffer,
                    formatLease.Format,
                    layoutWidth,
                    float.MaxValue);
#endif
                candidateRetirementRecord.SetTemporary(0, candidateLayout);
                replacementLayout = candidateLayout;
                _layout = candidateLayout;
                _layoutRetirementRecord = candidateRetirementRecord;
                // CanvasTextLayout construction is one native call and cannot
                // be interrupted safely. Observe a superseding lazy-band
                // request immediately afterward, before walking/styling runs.
                ThrowIfCancellationRequested();
                // Enable DirectWrite color font path (Segoe UI Emoji / COLR-CPAL glyphs).
                candidateLayout.Options = CanvasDrawTextOptions.EnableColorFont;
                ApplyRunStyles(candidateLayout, applyColors: true);
                ApplyForegroundSpans(candidateLayout);
                float inlineMetricScale = ApplyContentAwareLineSpacing(candidateLayout, style);
                ApplyEmbedSpacing(candidateLayout, inverseMetricScale: inlineMetricScale);
                replacementSceneGeometry = UpdateVectorScenePlacements(candidateLayout);
                _lastWidth = availableWidth;
            }

            var bounds = _layout!.LayoutBounds;
            float top = (float)(margin.Top + padding.Top);
            float bottom = (float)(margin.Bottom + padding.Bottom);
            measuredHeight = (float)bounds.Height + top + bottom;
            // Do not retire the previously published native layout if the
            // candidate was superseded while placement metrics were computed.
            ThrowIfCancellationRequested();
            Bounds = new Rect(0, 0, availableWidth, measuredHeight);
        }
        catch (Exception measureFailure)
        {
            Exception? cleanupFailure = null;
            _layout = previousLayout;
            _layoutRetirementRecord = previousLayoutRetirementRecord;
            _cachedLineMetrics = previousLineMetrics;
            _lineMetricsRead = previousLineMetricsRead;
            _selectionLayout = previousSelectionLayout;
            _selectionLayoutRetirementRecord = previousSelectionLayoutRetirementRecord;
            _selectionLayoutColor = previousSelectionLayoutColor;
            _selectionLayoutWidth = previousSelectionLayoutWidth;
            _buffer = previousBuffer;
            _bufferDirty = previousBufferDirty;
            _lastWidth = previousWidth;
            Bounds = previousBounds;

            try
            {
                if (changedInlineMeasures is not null)
                {
                    foreach (InlineRollbackState state in changedInlineMeasures)
                    {
                        try
                        {
                            state.Restore();
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure = CombineFailures(
                                "rolling back speculative inline measurements",
                                cleanupFailure,
                                exception);
                        }
                    }
                }

                if (previousScenePlacements is not null)
                {
                    foreach (InlineScenePlacementRollbackState state in previousScenePlacements)
                    {
                        try
                        {
                            state.Run.SetLocalBounds(state.LocalBounds);
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure = CombineFailures(
                                "rolling back inline scene placements",
                                cleanupFailure,
                                exception);
                        }
                    }
                }
            }
            finally
            {
                // Restore the published state before releasing the unpublished
                // native candidate. This finally is deliberately around all
                // rollback/aggregation work: the pre-reserved native owner is
                // handed off even if collecting a rollback failure runs out of
                // memory after a native Dispose failure.
                if (replacementLayout is not null && !ReferenceEquals(replacementLayout, previousLayout))
                {
                    Exception? layoutCleanupFailure = RetireNativeLayout(
                        ref replacementLayout,
                        replacementLayoutRetirementRecord,
                        "disposing a failed inline-container measure candidate");
                    cleanupFailure = CombineFailures(
                        "rolling back a failed inline-container measure",
                        cleanupFailure,
                        layoutCleanupFailure);
                }
                else if (replacementLayoutRetirementRecord is not null)
                {
                    replacementLayoutRetirementRecord.Publish();
                }
            }

            if (cleanupFailure is not null)
            {
                try
                {
                    measureFailure.Data["MarkdownRenderer.InlineContainerBox.MeasureCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Failure metadata must not replace the original exception.
                }
            }

            throw;
        }

        if (changedInlineMeasures is not null)
        {
            foreach (InlineRollbackState state in changedInlineMeasures)
                state.Commit();
        }

        if (UseContainerMargin)
            Margin = style.Margin;

        if (!ReferenceEquals(previousLayout, _layout))
        {
            if (_vectorSceneState is { } vectorState)
            {
                long nextGeneration = unchecked(vectorState.SceneGeometryGeneration + 1);
                vectorState.SceneGeometryGeneration = nextGeneration == 0 ? 1 : nextGeneration;
                vectorState.PendingSceneGeometry =
                    replacementSceneGeometry ?? Array.Empty<InlineSceneGeometry>();
                vectorState.PendingSceneGeometryGeneration = _usesProgressiveNativeSceneResidency
                    ? vectorState.SceneGeometryGeneration
                    : 0;
            }
            _selectionLayout = null;
            _selectionLayoutRetirementRecord = null;
            _selectionLayoutWidth = 0;

            bool previousLayoutsAlias = ReferenceEquals(previousLayout, previousSelectionLayout);
            Exception? layoutCleanupFailure = RetireNativeLayout(
                ref previousLayout,
                previousLayoutRetirementRecord,
                "retiring a superseded inline layout");
            Exception? selectionCleanupFailure = previousLayoutsAlias
                ? ReferenceEquals(previousLayoutRetirementRecord, previousSelectionLayoutRetirementRecord)
                    ? null
                    : ReleaseEmptyNativeRecord(previousSelectionLayoutRetirementRecord)
                : RetireNativeLayout(
                    ref previousSelectionLayout,
                    previousSelectionLayoutRetirementRecord,
                    "retiring a superseded inline selection layout");
            Exception? supersededCleanupFailure = CombineFailures(
                "retiring superseded inline layouts",
                layoutCleanupFailure,
                selectionCleanupFailure);
            if (supersededCleanupFailure is not null)
                Context.RegisterRetirement(Task.FromException(supersededCleanupFailure));
        }

        return measuredHeight;
    }

    /// <summary>
    /// Measures the natural single-line width and the widest unbreakable text
    /// segment without replacing the layout used for painting. Table layout
    /// uses these two values to implement min/preferred columns instead of
    /// assigning every column an equal share of the viewport.
    /// </summary>
    internal IntrinsicWidthMetrics MeasureIntrinsicWidths(float maximumPreferredWidth = 65_536f)
    {
        ThrowIfCancellationRequested();
        EnsureBuffer();

        var style = GetContainerStyle();
        var padding = GetEffectivePadding(style);
        float horizontalPadding = (float)(padding.Left + padding.Right);
        float measurementWidth = Math.Clamp(maximumPreferredWidth, 1f, 1_000_000f);
        List<InlineRollbackState>? candidateMeasures = null;
        NativeResourceRetirementRecord? layoutRetirementRecord = null;
        CanvasTextLayout? layout = null;
        IntrinsicWidthMetrics result = default;
        Exception? failure = null;
        try
        {
            _ = MeasureAtomicInlineRuns(
                measurementWidth,
                style.FontSize,
                out candidateMeasures);

            using var formatLease = CreateTextFormatLease(
                style,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left);
            layoutRetirementRecord = _nativeRetirement.Reserve(
                resourceGroupCount: 0,
                temporarySlotCount: 1,
                disposer: _nativeDisposer);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            layout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                _buffer,
                formatLease.Format,
                measurementWidth,
                float.MaxValue,
                Context.LayoutDiagnostics);
#else
            layout = new CanvasTextLayout(
                Context.ResourceCreator,
                _buffer,
                formatLease.Format,
                measurementWidth,
                float.MaxValue);
#endif
            layoutRetirementRecord.SetTemporary(0, layout);
            layout.Options = CanvasDrawTextOptions.EnableColorFont;
            ApplyRunStyles(layout, applyColors: false);
            ApplyEmbedSpacing(layout);

            var layoutBounds = layout.LayoutBounds;
            var drawBounds = layout.DrawBounds;
            float preferredContent = (float)Math.Max(
                Math.Max(layoutBounds.Width, drawBounds.Width),
                1d);
            preferredContent = Math.Min(preferredContent, measurementWidth);

            float minimumContent = MeasureWidestUnbreakableSegment(layout, style.FontSize);
            minimumContent = Math.Clamp(minimumContent, 1f, preferredContent);
            ThrowIfCancellationRequested();
            result = new IntrinsicWidthMetrics(
                minimumContent + horizontalPadding,
                preferredContent + horizontalPadding);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Intrinsic sizing is a query, not a committed image layout. Its
            // atomic measurements are speculative even when it succeeds, and
            // must not replace bounds or native placeholder/caption resources
            // belonging to the currently published painting layout.
            try
            {
                if (candidateMeasures is not null)
                {
                    foreach (InlineRollbackState state in candidateMeasures)
                    {
                        try
                        {
                            state.Restore();
                        }
                        catch (Exception exception)
                        {
                            failure = CombineFailures(
                                "restoring speculative inline measurements",
                                failure,
                                exception);
                        }
                    }
                }
            }
            finally
            {
                // This nested finally ensures a speculative CanvasTextLayout
                // reaches its pre-reserved owner even if restoring inline
                // dimensions or aggregating that failure itself throws.
                Exception? layoutCleanupFailure = RetireNativeLayout(
                    ref layout,
                    layoutRetirementRecord,
                    "disposing intrinsic inline measurement layout");
                failure = CombineFailures(
                    "measuring intrinsic inline widths",
                    failure,
                    layoutCleanupFailure);
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private float MeasureWidestUnbreakableSegment(CanvasTextLayout layout, float fallbackFontSize)
    {
        if (_buffer.Length == 0)
            return Math.Max(1f, fallbackFontSize);

        float widest = 0;
        int tokenStart = -1;
        int runIndex = 0;
        int runStart = 0;
        int runEnd = _runs.Count > 0 ? _runs[0].TextLength : 0;
        for (int index = 0; index <= _buffer.Length; index++)
        {
            while (runIndex < _runs.Count && index >= runEnd)
            {
                runStart = runEnd;
                runIndex++;
                if (runIndex < _runs.Count)
                    runEnd = Math.Min(_buffer.Length, runStart + _runs[runIndex].TextLength);
            }

            InlineRun? currentRun = index < _buffer.Length && runIndex < _runs.Count &&
                index >= runStart && index < runEnd
                    ? _runs[runIndex]
                    : null;
            bool responsiveImage = currentRun is InlineImageRun;
            bool boundary = index == _buffer.Length || responsiveImage || IsIntrinsicBreakOpportunity(_buffer[index]);
            if (!boundary)
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            if (tokenStart >= 0)
            {
                Context.CancellationToken.ThrowIfCancellationRequested();
                int length = index - tokenStart;
                var regions = layout.GetCharacterRegions(tokenStart, length);
                if (regions is not null)
                {
                    foreach (var region in regions)
                        widest = Math.Max(widest, (float)region.LayoutBounds.Width);
                }
                tokenStart = -1;
            }

            // Hosted controls and vector scenes are genuinely atomic, so they
            // contribute their full desired width. Images are also atomic for
            // selection and line layout, but their rendering contract is
            // max-width:100%: they may shrink while preserving aspect ratio.
            // Giving an image its full width as a min-content contribution
            // makes an image table column impossible to shrink and pushes its
            // sibling columns out of the viewport.
            if (index < _buffer.Length && _buffer[index] == InlineEmbedRun.PlaceholderChar[0])
            {
                if (currentRun is InlineImageRun image)
                {
                    widest = Math.Max(
                        widest,
                        Math.Min(image.DesiredWidth, Math.Max(1f, fallbackFontSize * 2f)));
                }
                else
                {
                    var regions = layout.GetCharacterRegions(index, 1);
                    if (regions is not null)
                    {
                        foreach (var region in regions)
                            widest = Math.Max(widest, (float)region.LayoutBounds.Width);
                    }
                }
            }
        }

        return Math.Max(widest, Math.Max(1f, fallbackFontSize * 2f));
    }

    private static bool IsIntrinsicBreakOpportunity(char value)
        => char.IsWhiteSpace(value) || value is '-' or '\u2010' or '\u2013' or '/';

    private SharedCanvasTextFormatCache.Lease CreateTextFormatLease(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment)
        => SharedCanvasTextFormatCache.Acquire(
            style,
            wordWrapping,
            horizontalAlignment,
            Context.FlowDirection,
            Context.Language,
            useContentAwareLineSpacing: _hasAtomicInlineObject);

    private ElementStyle GetContainerStyle()
        => _resolvedContainerStyle ??= Context.ThemeSnapshot.GetStyle(
                ElementKey,
                _styleContextKeys,
                _styleAliasKeys,
                CodeLanguage,
                StyleState);

    private ElementStyle GetRunStyle(InlineRun run)
    {
        ElementStyle? resolved = GetCachedRunStyle(run.InlineIndex);
        if (resolved is not null)
            return resolved;

        // A link or formatting run inside a heading inherits the heading's
        // typography. Its own role supplies only the nested decoration/color;
        // resolving Link as the base style would silently turn linked heading
        // text back into body-sized text.
        string headingKey = !string.IsNullOrEmpty(run.SemanticHeadingKey)
            ? run.SemanticHeadingKey
            : IsHeadingElement(ElementKey) ? ElementKey : string.Empty;
        bool inheritsHeading = headingKey.Length > 0;
        var key = inheritsHeading
            ? headingKey
            : string.IsNullOrEmpty(run.ElementKey) ? ElementKey : run.ElementKey;
        resolved = Context.ThemeSnapshot.GetStyle(
            key,
            _styleContextKeys,
            GetEffectiveRunAliases(run.InlineIndex),
            CodeLanguage,
            StyleState);
        int modifierStart = 0;
        if (inheritsHeading && !string.IsNullOrEmpty(run.ElementKey) &&
            !string.Equals(run.ElementKey, headingKey, StringComparison.Ordinal))
        {
            ElementStyle ownStyle = Context.ThemeSnapshot.GetStyle(
                run.ElementKey,
                _styleContextKeys,
                GetEffectiveRunAliases(run.InlineIndex),
                CodeLanguage,
                StyleState);
            resolved = ApplyNestedStyleModifier(resolved, ownStyle, run.ElementKey);
        }
        else if (!inheritsHeading && string.IsNullOrEmpty(run.ElementKey) &&
                 run.StyleModifierKeys.Count > 0)
        {
            resolved = Context.ThemeSnapshot.GetStyle(
                run.StyleModifierKeys[0],
                _styleContextKeys,
                GetEffectiveRunAliases(run.InlineIndex),
                CodeLanguage,
                StyleState);
            modifierStart = 1;
        }

        for (int index = modifierStart; index < run.StyleModifierKeys.Count; index++)
        {
            string modifierKey = run.StyleModifierKeys[index];
            ElementStyle modifier = Context.ThemeSnapshot.GetStyle(
                modifierKey,
                _styleContextKeys,
                GetEffectiveRunAliases(run.InlineIndex),
                CodeLanguage,
                StyleState);
            resolved = ApplyNestedStyleModifier(resolved, modifier, modifierKey);
        }
        if (_resolvedRunStyles is null)
        {
            var styles = new List<ElementStyle?>(_runs.Count);
            for (int index = 0; index < _runs.Count; index++)
                styles.Add(null);
            _resolvedRunStyles = styles;
        }
        _resolvedRunStyles[run.InlineIndex] = resolved;
        return resolved;
    }

    private static ElementStyle ApplyNestedStyleModifier(
        ElementStyle basis,
        ElementStyle modifier,
        string modifierKey)
    {
        Windows.UI.Text.FontWeight fontWeight = basis.FontWeight;
        Windows.UI.Text.FontStyle fontStyle = basis.FontStyle;
        float fontSize = basis.FontSize;
        bool underline = basis.Underline;
        bool strikethrough = basis.Strikethrough;
        Color? background = basis.Background;
        float cornerRadius = basis.CornerRadius;

        switch (modifierKey)
        {
            case MarkdownElementKeys.Link:
                return new ElementStyle
                {
                    FontFamily = basis.FontFamily,
                    FontSize = basis.FontSize,
                    FontWeight = basis.FontWeight,
                    FontStyle = basis.FontStyle,
                    Foreground = modifier.Foreground,
                    HoverForeground = modifier.HoverForeground,
                    FocusForeground = modifier.FocusForeground,
                    Background = basis.Background,
                    AccentBar = basis.AccentBar,
                    BorderBrush = basis.BorderBrush,
                    BorderThickness = basis.BorderThickness,
                    CornerRadius = basis.CornerRadius,
                    ListIndent = basis.ListIndent,
                    NestedListIndent = basis.NestedListIndent,
                    Underline = modifier.Underline,
                    Strikethrough = basis.Strikethrough,
                    Margin = basis.Margin,
                };
            case MarkdownElementKeys.Strong:
                fontWeight = modifier.FontWeight;
                break;
            case MarkdownElementKeys.Emphasis:
                fontStyle = modifier.FontStyle;
                break;
            case MarkdownElementKeys.Strikethrough:
                strikethrough |= modifier.Strikethrough;
                break;
            case MarkdownElementKeys.Subscript:
            case MarkdownElementKeys.Superscript:
                fontSize = modifier.FontSize;
                break;
            case MarkdownElementKeys.Inserted:
                underline |= modifier.Underline;
                break;
            case MarkdownElementKeys.Marked:
                background = modifier.Background;
                cornerRadius = modifier.CornerRadius;
                break;
        }

        return new ElementStyle
        {
            FontFamily = basis.FontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            FontStyle = fontStyle,
            Foreground = basis.Foreground,
            HoverForeground = basis.HoverForeground,
            FocusForeground = basis.FocusForeground,
            Background = background,
            AccentBar = basis.AccentBar,
            BorderBrush = basis.BorderBrush,
            BorderThickness = basis.BorderThickness,
            CornerRadius = cornerRadius,
            ListIndent = basis.ListIndent,
            NestedListIndent = basis.NestedListIndent,
            Underline = underline,
            Strikethrough = strikethrough,
            Margin = basis.Margin,
            Padding = basis.Padding,
            LineHeightMultiplier = basis.LineHeightMultiplier,
        };
    }

    private Thickness GetEffectivePadding(ElementStyle style)
        => UseContainerPadding ? style.Padding : default;

    internal override void ThrowIfCancellationRequested()
        => Context.CancellationToken.ThrowIfCancellationRequested();

    private bool HasWellFormedEffectiveRunAliases =>
        _effectiveRunAliasOverrides is null || _effectiveRunAliasOverrides.Count == _runs.Count;

    private string[] GetEffectiveRunAliases(int index)
    {
        if ((uint)index >= (uint)_runs.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        List<string[]?>? overrides = _effectiveRunAliasOverrides;
        if (overrides is not null)
        {
            if (overrides.Count != _runs.Count)
                throw new InvalidOperationException("Run alias override ordinals are inconsistent.");
            if (overrides[index] is { } aliases)
                return aliases;
        }

        return (string[])_styleAliasKeys;
    }

    private string[]? GetEffectiveRunAliasOverride(int index)
    {
        if ((uint)index >= (uint)_runs.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        List<string[]?>? overrides = _effectiveRunAliasOverrides;
        if (overrides is null)
            return null;
        if (overrides.Count != _runs.Count)
            throw new InvalidOperationException("Run alias override ordinals are inconsistent.");
        return overrides[index];
    }

    private void AppendEffectiveRunAliasOverride(int index, string[]? aliases)
    {
        // Add the run before recording its override so the logical ordinal and
        // run count remain the same fence used by style lookup and transfers.
        if ((uint)index >= (uint)_runs.Count || index != _runs.Count - 1)
            throw new InvalidOperationException("Run alias overrides must be appended in run order.");

        List<string[]?>? overrides = _effectiveRunAliasOverrides;
        if (overrides is null)
        {
            if (aliases is null)
                return;

            overrides = new List<string[]?>(_runs.Count);
            for (int prior = 0; prior < index; prior++)
                overrides.Add(null);
            overrides.Add(aliases);
            _effectiveRunAliasOverrides = overrides;
            return;
        }

        if (overrides.Count != index)
            throw new InvalidOperationException("Run alias overrides must be contiguous by ordinal.");
        overrides.Add(aliases);
    }

    private void AppendRebuiltEffectiveRunAliasOverride(int index, string[]? aliases)
    {
        // DetachInlineImagesForTransfer has already removed all image runs, so
        // the retained run list is complete before its effective aliases are
        // rebuilt. Keep normal Add/clone appends strict, but let this dedicated
        // ordered rebuild fill the null ordinals preceding the current run.
        if ((uint)index >= (uint)_runs.Count)
            throw new InvalidOperationException("Rebuilt run alias ordinals must belong to the retained runs.");

        List<string[]?>? overrides = _effectiveRunAliasOverrides;
        if (overrides is null)
        {
            if (aliases is null)
                return;

            overrides = new List<string[]?>(_runs.Count);
            for (int prior = 0; prior < index; prior++)
                overrides.Add(null);
            overrides.Add(aliases);
            _effectiveRunAliasOverrides = overrides;
            return;
        }

        if (overrides.Count > index)
            throw new InvalidOperationException("Rebuilt run alias overrides must be appended in run order.");
        while (overrides.Count < index)
            overrides.Add(null);
        overrides.Add(aliases);
    }

    private static string[] CombineAliases(
        IReadOnlyList<string> containerAliases,
        IReadOnlyList<string> runAliases)
    {
        if (runAliases.Count == 0)
            return containerAliases as string[] ?? CopyKeys(containerAliases);
        if (containerAliases.Count == 0)
            return runAliases as string[] ?? CopyKeys(runAliases);

        var aliases = new string[containerAliases.Count + runAliases.Count];
        for (int n = 0; n < containerAliases.Count; n++)
            aliases[n] = containerAliases[n];
        for (int n = 0; n < runAliases.Count; n++)
            aliases[containerAliases.Count + n] = runAliases[n];
        return aliases;
    }

    /// <summary>
    /// Returns the integer-pixel-snapped origin of the text layout in
    /// document coordinates.  Snappnng eliminates fractional sub-pixel
    /// positioning that otherwise causes DirectWrite glyphs straddlnng
    /// CanvasVnrtualControl tnle / dirty-rect boundarnes to be re-rasternsed
    /// at slightly dnfferent sub-pixel offsets when the dirty rect changes
    /// shape (e.g. as a selection-drag extends a highlight rect across the
    /// text), which reads as visible "shake" of the rendered glyphs.
    /// All paint / hit-test / selection-rect / embed-rect computations use
    /// this single snapped origin so they stay in agreement.
    /// </summary>
    private (float X, float Y) GetSnappedOrigin(Theming.ElementStyle style)
        => GetSnappedOrigin(style, out _, out _);

    private (float X, float Y) GetSnappedOrigin(
        Theming.ElementStyle style,
        out float finalOriginX,
        out float finalOriginY)
    {
        // Snau to *device pixels*, not DIPs. At a non-1x rasterization scale
        // (e.g., 1.25x / 1.5x / 2x), snappnng only to integer DIPs still leaves
        // glyph origins at fractional device-pixel positions. When the canvas
        // dirty-rect shape changes between frames (as it does whenever the
        // selection drag grows / shrinks adjacent tiles), DirectWrite's pixel
        // snappnng can resolve to a slightly dnfferent device-pixel column,
        // which manifests as the "selection shake" the user reports —
        // visually amplified on large heading glyphs.
        float scale = (float)Context.RasterizationScale;
        if (scale <= 0f) scale = 1f;
        var margin = Margin;
        var padding = GetEffectivePadding(style);
        float xDnu = (float)(Bounds.X + margin.Left + padding.Left);
        float yDnu = (float)(Bounds.Y + margin.Top + padding.Top);
        LayoutPlacementTransformContext.SnapOriginToDevicePixels(
            xDnu,
            yDnu,
            scale,
            out float x,
            out float y);
        LayoutPlacementTransformContext.SnapFinalOriginToDevicePixels(
            xDnu,
            yDnu,
            scale,
            out finalOriginX,
            out finalOriginY);
        return (x, y);
    }

    private static void DrawTextLayoutAtFinalOrigin(
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        float finalOriginX,
        float finalOriginY,
        Color color)
    {
        Matrix3x2 originalTransform = drawingSession.Transform;
        if (!LayoutPlacementTransformContext.TryGetPlacementFreeCanvasTransform(
                originalTransform,
                out Matrix3x2 placementFreeTransform))
        {
            drawingSession.DrawTextLayout(layout, finalOriginX, finalOriginY, color);
            return;
        }

        // Placement-only world translations perturb DirectWrite's glyph
        // rasterization even when the final logical origin is identical.
        // Draw text with the caller's exact matrix at the final snapped
        // coordinate; other box geometry retains its local-coordinate path.
        PlacementAwareTextDrawing.DrawAtPlacementFreeTransform(
            drawingSession,
            layout,
            finalOriginX,
            finalOriginY,
            color,
            originalTransform,
            placementFreeTransform);
    }

    private void DrawHeadingBottomBorder(
        CanvasDrawingSession ds,
        Color color,
        float thickness)
    {
        if (!float.IsFinite(thickness) || thickness <= 0)
            return;

        Thickness margin = Margin;
        float left = (float)(Bounds.X + margin.Left);
        float right = (float)(Bounds.Right - margin.Right);
        float bottom = (float)(Bounds.Bottom - margin.Bottom);
        if (!float.IsFinite(left) || !float.IsFinite(right) ||
            !float.IsFinite(bottom) || right <= left)
        {
            return;
        }

        float scale = (float)Context.RasterizationScale;
        if (!float.IsFinite(scale) || scale <= 0)
            scale = 1;
        // Align the outside edge to a device-pixel boundary, then center the
        // stroke inside its reserved final padding band. This keeps 1-DIP
        // heading rules crisp at integral DPI scales.
        float snappedBottom = MathF.Round(bottom * scale) / scale;
        float centerY = snappedBottom - thickness / 2f;
        ds.DrawLine(left, centerY, right, centerY, color, thickness);
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (_layout is null) return;
        var style = GetContainerStyle();

        if (DrawContainerChrome && style.Background is { } bg)
        {
            var margin = Margin;
            var rect = new Rect(
                Bounds.X + margin.Left,
                Bounds.Y + margin.Top,
                Bounds.Width - margin.Left - margin.Right,
                Bounds.Height - margin.Top - margin.Bottom);
            ds.FillRoundedRectangle(rect, style.CornerRadius, style.CornerRadius, bg);
        }

        bool drawHeadingBottomBorder =
            style.HeadingBorderBottomOnly &&
            (ElementKey is MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2) &&
            style.BorderBrush is not null &&
            float.IsFinite(style.BorderThickness) &&
            style.BorderThickness > 0;
        if (DrawContainerChrome && !drawHeadingBottomBorder &&
            style.BorderBrush is { } border && style.BorderThickness > 0)
        {
            var margin = Margin;
            var rect = new Rect(
                Bounds.X + margin.Left,
                Bounds.Y + margin.Top,
                Bounds.Width - margin.Left - margin.Right,
                Bounds.Height - margin.Top - margin.Bottom);
            float nnset = style.BorderThickness / 2f;
            ds.DrawRoundedRectangle(
                new Rect(
                    rect.X + nnset,
                    rect.Y + nnset,
                    Math.Max(0, rect.Width - style.BorderThickness),
                    Math.Max(0, rect.Height - style.BorderThickness)),
                style.CornerRadius,
                style.CornerRadius,
                border,
                style.BorderThickness);
        }
        if (drawHeadingBottomBorder && style.BorderBrush is { } headingBorder)
            DrawHeadingBottomBorder(ds, headingBorder, style.BorderThickness);

        // Hover state is intentnonally NOT applned to the text layout —
        // see HoveredRun docs for why.  Link hover affordance is conveyed by
        // the cursor shape change in MarkdownRendererControl.OnPointerMoved.
        // Snau to integer pixels at the draw snte.  See GetSnappedOrigin
        // for the ratnonale; the same snapped origin is used for hit-test,
        // selection rects and embed ulacement so they stay in sync.
        var (sx, sy) = GetSnappedOrigin(style, out float textOriginX, out float textOriginY);
        if (ShakeLogger.IsEnabled)
            ShakeLogger.LogPaint(
                "inline-paint",
                BlockIndex,
                sx,
                sy,
                _layout.LayoutBounds.Width,
                _layout.LayoutBounds.Height);
        DrawRunBackgrounds(ds, sx, sy);
        DrawTextLayoutAtFinalOrigin(ds, _layout, textOriginX, textOriginY, style.Foreground);
        DrawInlineImages(ds, sx, sy, viewport);
        DrawInlineVectorScenes(ds, sx, sy, viewport);

        DrawDecorations(ds, sx, sy);
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return false;
        }

        return HitTestNativeLayout(point, out position);
    }

    /// <summary>
    /// Hit tests native text after a clipping parent has established that the
    /// point is inside its horizontal viewport. Code blocks use this for
    /// horizontally scrolled no-wrap chunks whose logical bounds can sit
    /// outside the clipped viewport, while the visible native glyphs remain
    /// hit-testable. Ordinary inline containers must use <see cref="HitTest"/>
    /// and retain their own horizontal bounds check.
    /// </summary>
    internal bool HitTestWithinHorizontalViewport(Point point, out DocumentPosition position)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.Y < Bounds.Top || point.Y > Bounds.Bottom)
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return false;
        }

        return HitTestNativeLayout(point, out position);
    }

    private bool HitTestNativeLayout(Point point, out DocumentPosition position)
    {
        if (_layout is null)
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return true;
        }

        var style = GetContainerStyle();
        var (x, y) = GetSnappedOrigin(style);
        _layout.HitTest((float)point.X - x, (float)point.Y - y, out var hit, out bool trailingSide);
        int charIndex = (int)hit.CharacterIndex;
        // When the pointer is on the trailing (right for LTR) half of a glyph,
        // DirectWrite returns the glyph's character index + trailingSide=true.
        // For selection we want the CARET position, which is one past that index.
        // Wnthout this adjustment, draggnng past the last character of a run never
        // produces an offset that includes the fnnal character — the selection
        // stops one char short and the last char is never visually highlighted.
        if (trailingSide) charIndex++;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            // Use strnct '<' to match RunAt: character at index `cumulative+len`
            // belongs to the *next* run, not this one.  Prnor `<=` uulled boundary
            // hits backward into the wrong run, leaving HitTest and RunAt
            // disagreeing about which run owns the boundary character.
            if (charIndex < cumulative + len)
            {
                position = new DocumentPosition(BlockIndex, run.InlineIndex, charIndex - cumulative);
                return true;
            }
            cumulative += len;
        }
        // Past last char: position at end of last run (or block end if empty).
        if (_runs.Count > 0)
        {
            var last = _runs[_runs.Count - 1];
            position = new DocumentPosition(BlockIndex, last.InlineIndex, last.TextLength);
        }
        else
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
        }
        return true;
    }

    public IEnumerable<Rect> GetRangeRects(DocumentRange range)
        => EnumerateRangeRects(range, 0, 0, fullLineRects: false, logicalCoordinates: false);

    /// <summary>
    /// Resolves inline selection geometry after applying the active root
    /// placement to the snapped text origin, before adding DirectWrite's
    /// relative region offsets and crossing the WinRT Rect projection boundary.
    /// The ordinary range API remains root-local for painting and clipping.
    /// </summary>
    internal IEnumerable<Rect> GetLogicalRangeRects(DocumentRange range)
        => EnumerateRangeRects(range, 0, 0, fullLineRects: false, logicalCoordinates: true);

    /// <summary>
    /// Resolves the visual caret edge for a logical document position. Asking
    /// DirectWrite for the caret is essential for mixed bidi text: the visual
    /// leading/trailing edge cannot be inferred from the paragraph direction.
    /// </summary>
    internal bool TryGetCaretPoint(
        DocumentPosition position,
        bool rangeStart,
        out Point point)
        => TryGetCaretPoint(position, rangeStart, logicalCoordinates: false, out point);

    internal bool TryGetLogicalCaretPoint(
        DocumentPosition position,
        bool rangeStart,
        out Point point)
        => TryGetCaretPoint(position, rangeStart, logicalCoordinates: true, out point);

    private bool TryGetCaretPoint(
        DocumentPosition position,
        bool rangeStart,
        bool logicalCoordinates,
        out Point point)
    {
        point = default;
        if (_layout is null || position.BlockIndex != BlockIndex)
            return false;

        EnsureBuffer();
        if (_buffer.Length == 0)
            return false;

        int offset = Math.Clamp(ToBufferIndex(position), 0, _buffer.Length);
        TouchSelectionCaretQuery query = TouchSelectionCaretPolicy.Resolve(
            _buffer.Length,
            offset,
            rangeStart);
        var caret = _layout.GetCaretPosition(
            query.CharacterIndex,
            query.TrailingSideOfCharacter,
            out CanvasTextLayoutRegion region);
        var style = GetContainerStyle();
        var (rootLocalX, rootLocalY) = GetSnappedOrigin(
            style,
            out float logicalOriginX,
            out float logicalOriginY);
        float baseX = logicalCoordinates ? logicalOriginX : rootLocalX;
        float baseY = logicalCoordinates ? logicalOriginY : rootLocalY;
        // Win2D reports caret X in the paragraph's logical coordinate space.
        // Character regions and drawing are already mirrored for an RTL
        // paragraph, so mirror this one coordinate before translating it into
        // document space. DirectWrite still resolves the caret within each bidi
        // run; this transform only accounts for the paragraph base direction.
        double visualX = TouchSelectionCaretPolicy.ResolveVisualX(
            caret.X,
            _layout.RequestedSize.Width,
            Context.FlowDirection == FlowDirection.RightToLeft);
        double x = baseX + visualX;
        double y = baseY + region.LayoutBounds.Bottom;
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return false;

        point = new Point(x, y);
        return true;
    }

    internal IEnumerable<Rect> GetBufferRangeRects(int from, int length)
        => EnumerateRangeRects(documentRange: null, from, length, fullLineRects: false, logicalCoordinates: false);

    internal IEnumerable<Rect> GetBufferRangeLineRects(int from, int length)
        => EnumerateRangeRects(documentRange: null, from, length, fullLineRects: true, logicalCoordinates: false);

    private IEnumerable<Rect> EnumerateRangeRects(
        DocumentRange? documentRange,
        int from,
        int length,
        bool fullLineRects,
        bool logicalCoordinates)
    {
        if (_layout is null)
            yield break;

        EnsureBuffer();
        if (documentRange is { } range)
        {
            from = ToBufferIndex(range.Start);
            length = ToBufferIndex(range.End) - from;
            if (length <= 0)
                yield break;
        }
        else
        {
            if (_buffer.Length == 0)
                yield break;

            from = Math.Clamp(from, 0, _buffer.Length);
            length = Math.Clamp(length, 0, _buffer.Length - from);
            if (length <= 0)
                length = from < _buffer.Length ? 1 : 0;
            if (length <= 0)
                yield break;
        }

        var style = GetContainerStyle();
        var (rootLocalX, rootLocalY) = GetSnappedOrigin(
            style,
            out float logicalOriginX,
            out float logicalOriginY);
        float baseX = logicalCoordinates ? logicalOriginX : rootLocalX;
        float baseY = logicalCoordinates ? logicalOriginY : rootLocalY;
        var lineMetrics = fullLineRects ? TryGetLineMetrics() : null;
        if (!fullLineRects || lineMetrics is null)
        {
            var regions = _layout.GetCharacterRegions(from, length);
            if (regions is null)
                yield break;

            foreach (var region in regions)
            {
                var bounds = region.LayoutBounds;
                yield return new Rect(
                    baseX + bounds.X,
                    baseY + bounds.Y,
                    bounds.Width,
                    bounds.Height);
            }
            yield break;
        }

        int rangeEnd = from + length;
        int lineStart = 0;
        double y = 0;
        foreach (var metric in lineMetrics)
        {
            int charCount = Math.Max(0, metric.CharacterCount);
            int lineEnd = lineStart + charCount;
            bool intersects = lineEnd > from && lineStart < rangeEnd;
            if (intersects)
            {
                yield return new Rect(
                    baseX,
                    baseY + y,
                    Math.Max(1, _layout.LayoutBounds.Width),
                    Math.Max(1, metric.Height));
            }

            y += metric.Height;
            lineStart = lineEnd;
        }
    }

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        => GetRangeRects(range);

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport)
    {
        if (_layout is null) return;

        var normalized = range.Normalized();
        EnsureBuffer();
        int from = ToBufferIndex(normalized.Start);
        int to = ToBufferIndex(normalized.End);
        if (to <= from)
            return;

        var visibleSelectionRects = new List<Rect>();
        foreach (var rect in GetRangeRects(normalized))
        {
            if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
                rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
            {
                continue;
            }

            visibleSelectionRects.Add(rect);
        }

        if (visibleSelectionRects.Count == 0) return;

        var style = GetContainerStyle();
        var (sx, sy) = GetSnappedOrigin(style, out float textOriginX, out float textOriginY);
        var layout = EnsureSelectionLayout(color, style);
        // Draw a color-neutral copy of the full layout through the exact
        // selected character regions. Masking non-selected glyphs with a
        // transparent text color is unreliable on some Win2D/DirectWrite
        // paths: the later selected-color range can remain transparent and
        // leave an opaque highlight with no readable glyphs. Geometry clips
        // preserve the same native ordering and work for wrapped selections.
        foreach (var rect in visibleSelectionRects)
        {
            NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(ds, 1.0f, rect);
            try
            {
                DrawTextLayoutAtFinalOrigin(ds, layout, textOriginX, textOriginY, color);
            }
            catch (Exception primary)
            {
                layer.ClosePreservingPrimary(primary);
                throw;
            }
            layer.Close();
        }
        DrawSelectedDecorations(ds, sx, sy, color, from, to - from);
        DrawSelectedInlineImages(ds, sx, sy, viewport, normalized, color);
        DrawSelectedInlineVectorScenes(ds, sx, sy, viewport, normalized, color);
    }

    public void PaintLinkStateForeground(CanvasDrawingSession ds, LinkRun link, bool focused, Rect viewport)
    {
        if (_layout is null || link.TextLength == 0)
            return;

        var style = GetContainerStyle();
        var (sx, sy) = GetSnappedOrigin(style, out float textOriginX, out float textOriginY);
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (!ReferenceEquals(run, link))
            {
                cumulative += len;
                continue;
            }

            var runStyle = GetRunStyle(run);
            var color = focused
                ? runStyle.FocusForeground ?? runStyle.HoverForeground ?? runStyle.Foreground
                : runStyle.HoverForeground ?? runStyle.Foreground;
            var regions = _layout.GetCharacterRegions(cumulative, len);
            if (regions is null)
                return;

            var lineMetrics = TryGetLineMetrics();
            foreach (var r in regions)
            {
                var rect = new Rect(
                    sx + r.LayoutBounds.X,
                    sy + r.LayoutBounds.Y,
                    r.LayoutBounds.Width,
                    r.LayoutBounds.Height);
                if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
                    rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
                {
                    continue;
                }

                NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(ds, 1.0f, rect);
                try
                {
                    DrawTextLayoutAtFinalOrigin(ds, _layout, textOriginX, textOriginY, color);
                    DrawRunDecorations(ds, sx, sy, lineMetrics, r, run, runStyle, color);
                }
                catch (Exception primary)
                {
                    layer.ClosePreservingPrimary(primary);
                    throw;
                }
                layer.Close();
            }

            return;
        }
    }

    /// <summary>
    /// Returns the boundnng rectangle in document coordinates for the run at
    /// <paramref name="inlineIndex"/>. Used to position the keyboard-focus rnng.
    /// Returns an empty rect if the run is not found or the layout is not built.
    /// </summary>
    public Rect GetRunRect(int inlineIndex)
    {
        if (_layout is null) return default;
        var style = GetContainerStyle();
        var (baseX, baseY) = GetSnappedOrigin(style);
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (run.InlineIndex == inlineIndex && len > 0)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is not null && regions.Length > 0)
                {
                    // Unnon all regions for multn-line runs.
                    double x1 = double.MaxValue, y1 = double.MaxValue;
                    double x2 = double.MinValue, y2 = double.MinValue;
                    foreach (var r in regions)
                    {
                        var lb = r.LayoutBounds;
                        if (lb.X < x1) x1 = lb.X;
                        if (lb.Y < y1) y1 = lb.Y;
                        if (lb.X + lb.Width  > x2) x2 = lb.X + lb.Width;
                        if (lb.Y + lb.Height > y2) y2 = lb.Y + lb.Height;
                    }
                    return new Rect(baseX + x1, baseY + y1, x2 - x1, y2 - y1);
                }
                return default;
            }
            cumulative += len;
        }
        return default;
    }

    /// <summary>
    /// For each <see cref="InlineEmbedRun"/>, returns the rectangle in document
    /// coordinates where its hosted WinUI element should be placed on the
    /// overlay canvas.
    /// </summary>
    public IEnumerable<(InlineEmbedRun Run, Rect Rect)> EnumerateEmbedRects()
    {
        if (_layout is null || _inlineEmbedRuns is null) yield break;
        var style = GetContainerStyle();
        var (baseX, baseY) = GetSnappedOrigin(style);

        foreach (var (emb, offset) in _inlineEmbedRuns)
        {
            int len = emb.TextLength;
            if (len > 0)
            {
                var regions = _layout.GetCharacterRegions(offset, len);
                if (regions is null) continue;
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    double cellY = lb.Y + (lb.Height - emb.DesiredHeight) / 2.0;
                    if (cellY < lb.Y) cellY = lb.Y;
                    var rect = new Rect(baseX + lb.X, baseY + cellY,
                        Math.Min(emb.DesiredWidth, lb.Width), emb.DesiredHeight);
                    yield return (emb, rect);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// For each <see cref="InlineImageRun"/>, returns the image rectangle in
    /// document coordinates and syncs the backnng <see cref="ImageBox"/> bounds
    /// so lazy-loading and accessnbnlity see the same geometry as paint.
    /// </summary>
    public IEnumerable<(InlineImageRun Run, Rect Rect)> EnumerateInlineImageRects()
    {
        if (_layout is null || _inlineImageRuns is null) yield break;
        var style = GetContainerStyle();
        var (baseX, baseY) = GetSnappedOrigin(style);

        foreach (var (image, offset) in _inlineImageRuns)
        {
            int len = image.TextLength;
            if (len > 0)
            {
                var regions = _layout.GetCharacterRegions(offset, len);
                if (regions is null) continue;
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    double cellY = lb.Y + (lb.Height - image.DesiredHeight) / 2.0;
                    if (cellY < lb.Y) cellY = lb.Y;
                    var rect = new Rect(
                        baseX + lb.X,
                        baseY + cellY,
                        Math.Min(image.DesiredWidth, lb.Width),
                        image.DesiredHeight);
                    image.Image.SetInlineBounds(rect);
                    yield return (image, rect);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Returns the run hovered for the gnven document-coordnnate point, or
    /// null if no run is hovered. Used by the control to drnve link hover.
    /// </summary>
    public InlineRun? RunAt(Point point)
    {
        if (_layout is null) return null;
        if (!Bounds.Contains(point)) return null;
        var style = GetContainerStyle();
        var (x, y) = GetSnappedOrigin(style);
        _layout.HitTest((float)point.X - x, (float)point.Y - y, out var hitRegion, out bool trailingSide);
        int charIndex = (int)hitRegion.CharacterIndex;
        if (trailingSide) charIndex++;
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (charIndex < cumulative + len) return run;
            cumulative += len;
        }
        // charIndex >= total buffer length (trailing-edge of last run): return last run
        // so hover-cursor and highlight match the hit-test behavnour that maps this
        // position to the last run's DocumentPosition.
        if (_runs.Count > 0) return _runs[_runs.Count - 1];
        return null;
    }

    /// <summary>
    /// Returns the run whose laid-out character region contains
    /// <paramref name="point"/>. Unlike <see cref="RunAt"/>, this preserves
    /// glyph ownership on the trailing half of a character instead of turning
    /// the hit into the following caret position. Pointer commands target the
    /// painted object, not a text insertion point.
    /// </summary>
    internal InlineRun? RunAtVisualPoint(Point point)
    {
        if (_layout is null || !Bounds.Contains(point))
            return null;

        EnsureBuffer();

        var style = GetContainerStyle();
        var (baseX, baseY) = GetSnappedOrigin(style);
        _layout.HitTest(
            (float)point.X - baseX,
            (float)point.Y - baseY,
            out CanvasTextLayoutRegion hitRegion,
            out _);

        // CharacterIndex identifies the glyph under the pointer. Deliberately
        // ignore trailingSide here: applying it converts this geometric hit to
        // the next caret position and loses one-character atomic runs such as
        // inline images.
        int characterIndex = (int)hitRegion.CharacterIndex;
        int cumulative = 0;
        foreach (InlineRun run in _runs)
        {
            int length = run.TextLength;
            if (characterIndex < cumulative + length)
            {
                return IsPointInsideBufferRange(
                    cumulative,
                    length,
                    point,
                    baseX,
                    baseY)
                    ? run
                    : null;
            }

            cumulative += length;
        }

        return null;
    }

    internal bool TryGetRunBounds(InlineRun run, Point preferredPoint, out Rect bounds)
    {
        bounds = default;
        if (_layout is null)
            return false;

        EnsureBuffer();

        int cumulative = 0;
        foreach (var candidate in _runs)
        {
            int len = candidate.TextLength;
            if (!ReferenceEquals(candidate, run))
            {
                cumulative += len;
                continue;
            }

            if (len <= 0)
                return false;

            var regions = _layout.GetCharacterRegions(cumulative, len);
            if (regions is null || regions.Length == 0)
                return false;

            var style = GetContainerStyle();
            var (baseX, baseY) = GetSnappedOrigin(style);
            Rect? first = null;
            Rect? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var region in regions)
            {
                var lb = region.LayoutBounds;
                var rect = new Rect(
                    baseX + lb.X,
                    baseY + lb.Y,
                    lb.Width,
                    lb.Height);

                first ??= rect;
                if (rect.Contains(preferredPoint))
                {
                    bounds = rect;
                    return true;
                }

                double centerX = rect.X + rect.Width / 2.0;
                double centerY = rect.Y + rect.Height / 2.0;
                double dx = centerX - preferredPoint.X;
                double dy = centerY - preferredPoint.Y;
                double distance = dx * dx + dy * dy;
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = rect;
                }
            }

            bounds = closest ?? first.GetValueOrDefault();
            return bounds.Width > 0 && bounds.Height > 0;
        }

        return false;
    }

    internal bool TryGetCurrentSceneGeometry(
        out long generation,
        out InlineSceneGeometry[] geometry)
    {
        generation = SceneGeometryGeneration;
        geometry = Array.Empty<InlineSceneGeometry>();
        if (generation == 0)
            return false;
        if (_vectorSceneRunCount == 0)
            return true;
        if (_pendingSceneGeometryGeneration != generation)
            return false;

        geometry = _pendingSceneGeometry;
        return true;
    }

    /// <summary>
    /// Captures immutable scalar regions for a current layout when an active
    /// band needs to rebuild its optional index after eviction. Call only from
    /// the serialized background layout-realization path, never from Paint or
    /// a UI readiness probe.
    /// </summary>
    internal bool TryCaptureCurrentSceneGeometry(
        CancellationToken cancellationToken,
        out long generation,
        out InlineSceneGeometry[] geometry)
    {
        generation = SceneGeometryGeneration;
        geometry = Array.Empty<InlineSceneGeometry>();
        CanvasTextLayout? layout = _layout;
        if (generation == 0 || layout is null)
            return false;
        if (_vectorSceneRunCount == 0)
            return true;

        geometry = CaptureVectorSceneGeometry(layout, cancellationToken);
        return true;
    }

    internal void ReleasePendingSceneGeometry(long generation)
    {
        if (generation == _sceneGeometryGeneration)
        {
            _pendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
            _pendingSceneGeometryGeneration = 0;
        }
    }

    private void RecordSceneCharacterRegionQuery()
    {
        if (_vectorSceneState is { } vectorState)
            vectorState.SceneCharacterRegionQueryCount++;
    }

    internal void GetSceneGeometryOrigin(out double x, out double y)
    {
        var origin = GetSnappedOrigin(GetContainerStyle());
        x = origin.X;
        y = origin.Y;
    }

    internal bool TryGetRunCharacterRegionsForSceneReadiness(
        int start,
        int length,
        out CanvasTextLayoutRegion[]? regions,
        out double originY)
    {
        regions = null;
        originY = 0;
        if (start < 0 || length <= 0 || _layout is null || _bufferDirty ||
            start > _buffer.Length - length)
        {
            return false;
        }

        CanvasTextLayoutRegion[]? currentRegions = _layout.GetCharacterRegions(start, length);
        RecordSceneCharacterRegionQuery();
        if (currentRegions is not { Length: > 0 })
            return false;

        var (_, snappedOriginY) = GetSnappedOrigin(GetContainerStyle());
        if (!double.IsFinite(snappedOriginY))
            return false;

        regions = currentRegions;
        originY = snappedOriginY;
        return true;
    }

    internal bool IsPointInsideRunBounds(InlineRun run, Point point)
    {
        if (_layout is null || !Bounds.Contains(point))
            return false;

        EnsureBuffer();

        int cumulative = 0;
        foreach (var candidate in _runs)
        {
            int len = candidate.TextLength;
            if (!ReferenceEquals(candidate, run))
            {
                cumulative += len;
                continue;
            }

            if (len <= 0)
                return false;

            var style = GetContainerStyle();
            var (baseX, baseY) = GetSnappedOrigin(style);
            return IsPointInsideBufferRange(
                cumulative,
                len,
                point,
                baseX,
                baseY);
        }

        return false;
    }

    private bool IsPointInsideBufferRange(
        int start,
        int length,
        Point point,
        float baseX,
        float baseY)
    {
        if (_layout is null || length <= 0)
            return false;

        var regions = _layout.GetCharacterRegions(start, length);
        if (regions is null || regions.Length == 0)
            return false;

        foreach (CanvasTextLayoutRegion region in regions)
        {
            Rect layoutBounds = region.LayoutBounds;
            var bounds = new Rect(
                baseX + layoutBounds.X,
                baseY + layoutBounds.Y,
                layoutBounds.Width,
                layoutBounds.Height);

            if (bounds.Contains(point))
                return true;
        }

        return false;
    }

    private int ToBufferIndex(DocumentPosition pos)
    {
        if (pos.BlockIndex < BlockIndex) return 0;
        if (pos.BlockIndex > BlockIndex) return _buffer.Length;
        int cumulative = 0;
        foreach (var run in _runs)
        {
            if (run.InlineIndex == pos.InlineIndex)
                return cumulative + Math.Clamp(pos.CharacterOffset, 0, run.TextLength);
            cumulative += run.TextLength;
        }
        return _buffer.Length;
    }

    public override void Dispose()
    {
        if (!HasValidNativeLayoutCustody(this))
            throw new InvalidOperationException("Cannot detach inline text layouts without their reserved native-retirement owners.");

        var cleanup = new BlockBoxCleanupScope();
        CanvasTextLayout? layout = _layout;
        NativeResourceRetirementRecord? layoutRetirementRecord = _layoutRetirementRecord;
        CanvasTextLayout? selectionLayout = _selectionLayout;
        NativeResourceRetirementRecord? selectionLayoutRetirementRecord =
            _selectionLayoutRetirementRecord;

        // Detach all box/context-owned state before publishing any failed
        // native slots. The retirement registry must retain only the layouts,
        // never this box, its runs, or the document context.
        _layout = null;
        _layoutRetirementRecord = null;
        _selectionLayout = null;
        _selectionLayoutRetirementRecord = null;
        _selectionLayoutWidth = 0;
        _sceneGeometryGeneration = 0;
        _pendingSceneGeometryGeneration = 0;
        _pendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
        InvalidateLineMetrics();

        bool layoutsAlias = ReferenceEquals(layout, selectionLayout);
        Exception? layoutFailure = RetireNativeLayout(
            ref layout,
            layoutRetirementRecord,
            "disposing an inline text layout");
        Exception? selectionFailure = layoutsAlias
            ? ReferenceEquals(layoutRetirementRecord, selectionLayoutRetirementRecord)
                ? null
                : ReleaseEmptyNativeRecord(selectionLayoutRetirementRecord)
            : RetireNativeLayout(
                ref selectionLayout,
                selectionLayoutRetirementRecord,
                "disposing an inline selection layout");
        if (layoutFailure is not null)
            cleanup.RecordFailure(layoutFailure);
        if (selectionFailure is not null)
            cleanup.RecordFailure(selectionFailure);

        foreach (InlineRun run in _runs)
        {
            switch (run)
            {
                case InlineImageRun image:
                    cleanup.TryDispose(image.Image);
                    break;
                case InlineVectorSceneRun vector:
                    cleanup.TryDispose(vector);
                    break;
            }
        }

        cleanup.ThrowIfAny("disposing an inline container");
    }

    private static Exception? RetireNativeLayout(
        ref CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord,
        string operation)
    {
        if (layout is null)
            return ReleaseEmptyNativeRecord(retirementRecord);

        // Missing custody is a structural error, not permission to attempt a
        // potentially throwing Dispose and lose the last handle. Participating
        // creation paths reserve the record before the native factory.
        if (retirementRecord is null)
            return new InvalidOperationException(
                $"Cannot retire a native inline layout during {operation} without its pre-reserved owner.");

        CanvasTextLayout current = layout;
        Exception? cleanupFailure = null;
        try
        {
            retirementRecord.Disposer(current);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        // Custody handoff is deliberately allocation-free between the native
        // failure and Publish. On failure the existing slot keeps the layout;
        // on success it is cleared. The caller-visible ref is cleared only
        // after a failed resource is rooted in its record.
        retirementRecord.SetTemporary(0, cleanupFailure is null ? null : current);
        layout = null;
        retirementRecord.Publish();
        return cleanupFailure;
    }

    private static Exception? ReleaseEmptyNativeRecord(
        NativeResourceRetirementRecord? retirementRecord)
    {
        if (retirementRecord is null)
            return null;
        try
        {
            retirementRecord.SetTemporary(0, null);
            retirementRecord.Publish();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? CombineFailures(
        string operation,
        Exception? first,
        Exception? second)
    {
        if (first is null)
            return second;
        if (second is null)
            return first;
        return new AggregateException($"One or more errors occurred while {operation}.", first, second);
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        var cleanup = new BlockBoxCleanupScope();
        foreach (InlineRun run in _runs)
        {
            if (run is InlineImageRun image)
                cleanup.TryCollectRetirementTasks(image.Image, pendingTasks);
        }

        cleanup.ThrowIfAny("collecting inline-container retirement tasks");
    }

    private void BuildBuffer()
    {
        var sb = StringBuilderPool.Rent();
        foreach (var run in _runs)
        {
            Context.CancellationToken.ThrowIfCancellationRequested();
            run.AppendLayoutTextTo(sb);
        }
        _buffer = StringBuilderPool.ToStringAndReturn(sb);
        _bufferDirty = false;
    }

    private void ApplyRunStyles(CanvasTextLayout layout, bool applyColors, bool observeCancellation = true)
    {
        var containerStyle = GetContainerStyle();
        string containerFontFamily = SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(
            containerStyle.FontFamily);
        int cumulative = 0;
        foreach (var run in _runs)
        {
            if (observeCancellation)
                Context.CancellationToken.ThrowIfCancellationRequested();
            int len = run.TextLength;
            if (len == 0) continue;
            if (ShouldApplyRunTextStyle(run))
            {
                var rs = GetRunStyle(run);
                string runFontFamily = SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(rs.FontFamily);
                if (!string.Equals(runFontFamily, containerFontFamily, StringComparison.Ordinal))
                    layout.SetFontFamily(cumulative, len, runFontFamily);
                if (rs.FontWeight.Weight != containerStyle.FontWeight.Weight)
                    layout.SetFontWeight(cumulative, len, rs.FontWeight);
                if (rs.FontStyle != containerStyle.FontStyle)
                    layout.SetFontStyle(cumulative, len, rs.FontStyle);
                if (rs.FontSize != containerStyle.FontSize)
                    layout.SetFontSize(cumulative, len, rs.FontSize);
                if (applyColors && rs.Foreground != containerStyle.Foreground)
                    layout.SetColor(cumulative, len, rs.Foreground);
            }
            ApplyBaselineStyle(layout, cumulative, len, run);
            cumulative += len;
        }
    }

    private void ApplyForegroundSpans(CanvasTextLayout layout)
    {
        if (_foregroundSpans.Count == 0)
            return;

        EnsureBuffer();
        foreach (var span in _foregroundSpans)
        {
            int start = Math.Clamp(span.Start, 0, _buffer.Length);
            int length = Math.Clamp(span.Length, 0, _buffer.Length - start);
            if (length > 0)
                layout.SetColor(start, length, span.Foreground);
        }
    }

    private static void ApplyBaselineStyle(CanvasTextLayout layout, int start, int length, InlineRun run)
    {
        if (length <= 0)
            return;

        CanvasTypographyFeatureName? feature = run switch
        {
            SubscriptRun => CanvasTypographyFeatureName.Subscript,
            SuperscriptRun => CanvasTypographyFeatureName.Superscript,
            LinkRun { IsSuperscript: true } => CanvasTypographyFeatureName.Superscript,
            _ when run.HasStyleModifier(MarkdownElementKeys.Subscript) => CanvasTypographyFeatureName.Subscript,
            _ when run.HasStyleModifier(MarkdownElementKeys.Superscript) => CanvasTypographyFeatureName.Superscript,
            _ => null,
        };

        if (feature is null)
            return;

        try
        {
            var typography = new CanvasTypography();
            typography.AddFeature(new CanvasTypographyFeature
            {
                Name = feature.Value,
                Parameter = 1,
            });
            layout.SetTypography(start, length, typography);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[InlineContainerBox] baseline typography failed: {ex.Message}");
        }
    }

    private CanvasTextLayout EnsureSelectionLayout(Color color, ElementStyle style)
    {
        if (!HasValidNativeLayoutCustody(this))
            throw new InvalidOperationException("Cannot replace an inline selection layout without its reserved native-retirement owner.");

        EnsureBuffer();
        // The neutral clone covers the full native layout. The current range
        // only supplies paint clips and decorations, so endpoint movement must
        // not recreate a native layout or change its retirement owner.
        if (_selectionLayout is not null &&
            _selectionLayoutColor == color &&
            Math.Abs(_selectionLayoutWidth - _lastWidth) <= 0.5f)
        {
            return _selectionLayout;
        }

        using var formatLease = CreateTextFormatLease(style, WordWrapping, TextAlignment);
        var padding = GetEffectivePadding(style);
        float horizontalPadding = (float)(padding.Left + padding.Right);
        NativeResourceRetirementRecord candidateRetirementRecord = _nativeRetirement.Reserve(
            resourceGroupCount: 0,
            temporarySlotCount: 1,
            disposer: _nativeDisposer);
        CanvasTextLayout? candidateLayout = null;
        Exception? candidateFailure = null;
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            candidateLayout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                _buffer,
                formatLease.Format,
                Math.Max(1f, _lastWidth - horizontalPadding),
                float.MaxValue,
                Context.LayoutDiagnostics);
#else
            candidateLayout = new CanvasTextLayout(
                Context.ResourceCreator,
                _buffer,
                formatLease.Format,
                Math.Max(1f, _lastWidth - horizontalPadding),
                float.MaxValue);
#endif
            candidateRetirementRecord.SetTemporary(0, candidateLayout);
            candidateLayout.Options = CanvasDrawTextOptions.EnableColorFont;
            // Selection is painted synchronously by a Win2D Draw callback against
            // an already-committed snapshot. A newer layout may have cancelled the
            // snapshot's build token, but cancellation must never escape Draw.
            ApplyRunStyles(candidateLayout, applyColors: false, observeCancellation: false);
            float inlineMetricScale = ApplyContentAwareLineSpacing(candidateLayout, style);
            ApplyEmbedSpacing(
                candidateLayout,
                observeCancellation: false,
                inverseMetricScale: inlineMetricScale);
        }
        catch (Exception exception)
        {
            candidateFailure = exception;
        }

        if (candidateFailure is not null)
        {
            Exception? cleanupFailure = RetireNativeLayout(
                ref candidateLayout,
                candidateRetirementRecord,
                "disposing a failed inline selection-layout candidate");
            Exception combined = CombineFailures(
                "building an inline selection layout",
                candidateFailure,
                cleanupFailure)!;
            ExceptionDispatchInfo.Capture(combined).Throw();
        }

        CanvasTextLayout? previousSelectionLayout = _selectionLayout;
        NativeResourceRetirementRecord? previousSelectionRetirementRecord =
            _selectionLayoutRetirementRecord;
        if (previousSelectionLayout is not null)
        {
            try
            {
                if (previousSelectionRetirementRecord is null)
                    previousSelectionLayout.Dispose();
                else
                    previousSelectionRetirementRecord.Disposer(previousSelectionLayout);
            }
            catch (Exception previousCleanupFailure)
            {
                Exception? candidateCleanupFailure = RetireNativeLayout(
                    ref candidateLayout,
                    candidateRetirementRecord,
                    "disposing a superseded inline selection-layout candidate");
                Exception combined = CombineFailures(
                    "replacing an inline selection layout",
                    previousCleanupFailure,
                    candidateCleanupFailure)!;
                ExceptionDispatchInfo.Capture(combined).Throw();
            }
        }

        // The previous native owner was released successfully. Clear its box
        // reference before publishing the now-empty preallocated record, then
        // commit the fully styled candidate in one non-throwing field group.
        _selectionLayout = null;
        _selectionLayoutRetirementRecord = null;
        Exception? previousRecordFailure = ReleaseEmptyNativeRecord(previousSelectionRetirementRecord);
        if (previousRecordFailure is not null)
        {
            Exception? candidateCleanupFailure = RetireNativeLayout(
                ref candidateLayout,
                candidateRetirementRecord,
                "disposing an uncommitted inline selection-layout candidate");
            Exception combined = CombineFailures(
                "retiring the previous inline selection layout",
                previousRecordFailure,
                candidateCleanupFailure)!;
            ExceptionDispatchInfo.Capture(combined).Throw();
        }

        _selectionLayout = candidateLayout;
        _selectionLayoutRetirementRecord = candidateRetirementRecord;
        _selectionLayoutColor = color;
        _selectionLayoutWidth = _lastWidth;
        return candidateLayout!;
    }

    /// <summary>
    /// Restores the authored block line-height for text while retaining
    /// content-aware growth for atomic inline runs. DirectWrite's proportional
    /// mode scales inline-object metrics as well as text metrics, so the inline
    /// objects receive inverse-scaled dimensions below. Text-only layouts keep
    /// their existing absolute CSS line-spacing format.
    /// </summary>
    private float ApplyContentAwareLineSpacing(CanvasTextLayout layout, ElementStyle style)
    {
        if (!_hasAtomicInlineObject)
            return 1f;

        float requestedHeight = style.FontSize * style.LineHeightMultiplier;
        if (!float.IsFinite(requestedHeight) ||
            requestedHeight <= 0f ||
            requestedHeight > MaximumContentAwareLineHeight)
        {
            return 1f;
        }

        // A short atomic run must still occupy the authored text line box.
        // DirectWrite can collapse an image-only line to the image's height
        // when the content-aware format has no absolute line spacing. Use the
        // ordinary CSS line spacing while every inline object fits within it;
        // only taller objects need the proportional, content-aware path below.
        bool hasOversizedInline = false;
        foreach (InlineRun run in _runs)
        {
            float height = run switch
            {
                InlineEmbedRun embed => embed.DesiredHeight,
                InlineImageRun image => image.DesiredHeight,
                InlineVectorSceneRun vector => vector.DesiredHeight,
                _ => 0f,
            };
            if (height > requestedHeight)
            {
                hasOversizedInline = true;
                break;
            }
        }

        if (!hasOversizedInline)
        {
            layout.LineSpacing = requestedHeight;
            layout.LineSpacingBaseline = requestedHeight * 0.8f;
            return 1f;
        }

        CanvasLineMetrics[] naturalLines;
        try
        {
            naturalLines = layout.LineMetrics;
        }
        catch (NotSupportedException)
        {
            // Some WinRT projections cannot marshal DirectWrite line metrics.
            // Preserve the existing content-aware fallback instead of failing
            // the entire paragraph.
            return 1f;
        }

        if (naturalLines.Length == 0 ||
            !float.IsFinite(naturalLines[0].Height) ||
            naturalLines[0].Height <= 0f)
        {
            return 1f;
        }

        float scale = requestedHeight / naturalLines[0].Height;
        if (!float.IsFinite(scale) ||
            scale < MinimumContentAwareLineScale ||
            scale > MaximumContentAwareLineScale ||
            !CanInverseScaleInlineMetrics(scale))
        {
            return 1f;
        }

        layout.LineSpacingMode = CanvasLineSpacingMode.Proportional;
        layout.LineSpacing = scale;
        layout.LineSpacingBaseline = scale * 0.8f;
        return scale;
    }

    private bool CanInverseScaleInlineMetrics(float scale)
    {
        foreach (InlineRun run in _runs)
        {
            float height = run switch
            {
                InlineEmbedRun embed => embed.DesiredHeight,
                InlineImageRun image => image.DesiredHeight,
                InlineVectorSceneRun vector => vector.DesiredHeight,
                _ => 0f,
            };

            if (height > 0f)
            {
                float inverseHeight = height / scale;
                if (!float.IsFinite(inverseHeight) ||
                    inverseHeight < MinimumInverseInlineMetricHeight ||
                    inverseHeight > MaximumContentAwareLineHeight)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ApplyEmbedSpacing(
        CanvasTextLayout layout,
        bool observeCancellation = true,
        float inverseMetricScale = 1f)
    {
        int cumulative = 0;
        foreach (var run in _runs)
        {
            if (observeCancellation)
            {
                Context.CancellationToken.ThrowIfCancellationRequested();
            }

            int len = run.TextLength;
            if (run is InlineEmbedRun emb && len > 0)
            {
                try
                {
                    layout.SetInlineObject(
                        cumulative,
                        len,
                        new LayoutInlineObject(emb.DesiredWidth, emb.DesiredHeight, inverseMetricScale));
                }
                catch (Exception ex)
                {
                    MarkdownDiagnostics.WriteLine($"[InlineContainerBox] inline embed layout failed: {ex.Message}");
                }
            }
            else if (run is InlineImageRun image && len > 0)
            {
                try
                {
                    layout.SetInlineObject(
                        cumulative,
                        len,
                        new LayoutInlineObject(image.DesiredWidth, image.DesiredHeight, inverseMetricScale));
                }
                catch (Exception ex)
                {
                    MarkdownDiagnostics.WriteLine($"[InlineContainerBox] inline image layout failed: {ex.Message}");
                }
            }
            else if (run is InlineVectorSceneRun vector && len > 0)
            {
                try
                {
                    layout.SetInlineObject(
                        cumulative,
                        len,
                        new LayoutInlineObject(vector.DesiredWidth, vector.DesiredHeight, inverseMetricScale));
                }
                catch (Exception ex)
                {
                    MarkdownDiagnostics.WriteLine($"[InlineContainerBox] vector scene layout failed: {ex.Message}");
                }
            }
            cumulative += len;
        }
    }

    /// <summary>
    /// Supplies DirectWrite with the exact advance and line height of an
    /// atomic Markdown run. Painting remains owned by the renderer so the
    /// callback is intentionally empty. Using a real inline object avoids the
    /// font-dependent U+FFFC glyph advance that previously made tall images
    /// consume hundreds of extra horizontal DIPs and wrap unnecessarily.
    /// </summary>
    private sealed partial class LayoutInlineObject : ICanvasTextInlineObject
    {
        public LayoutInlineObject(float width, float height, float inverseMetricScale = 1f)
        {
            float safeWidth = Math.Max(1f, width);
            float scale = float.IsFinite(inverseMetricScale) && inverseMetricScale > 0f
                ? inverseMetricScale
                : 1f;
            float safeHeight = Math.Max(1f, height / scale);
            Size = new Size(safeWidth, safeHeight);
            DrawBounds = new Rect(0, 0, safeWidth, safeHeight);
            Baseline = safeHeight;
        }

        public Size Size { get; }
        public float Baseline { get; }
        public bool SupportsSideways => false;
        public Rect DrawBounds { get; }
        public CanvasLineBreakCondition BreakBefore => CanvasLineBreakCondition.Neutral;
        public CanvasLineBreakCondition BreakAfter => CanvasLineBreakCondition.Neutral;

        public void Draw(
            ICanvasTextRenderer textRenderer,
            Vector2 point,
            bool isSideways,
            bool isRightToLeft,
            object brush)
        {
        }
    }

    private bool MeasureAtomicInlineRuns(
        float maxWidth,
        float lineHeight,
        out List<InlineRollbackState>? rollbackStates)
    {
        bool changed = false;
        rollbackStates = null;
        // Keep the out list published before each mutation: a later run may
        // throw or cancel, and the caller must be able to restore the prefix.
        foreach (var run in _runs)
        {
            Context.CancellationToken.ThrowIfCancellationRequested();
            if (run is InlineImageRun image)
            {
                float oldW = image.DesiredWidth;
                float oldH = image.DesiredHeight;
                List<InlineRollbackState> imageStates =
                    rollbackStates ??= new List<InlineRollbackState>();
                imageStates.EnsureCapacity(imageStates.Count + 1);
                ImageBox.InlineMeasureCheckpoint checkpoint =
                    image.Image.BeginInlineCheckpoint();
                imageStates.Add(new InlineRollbackState(
                    image,
                    null,
                    oldW,
                    oldH,
                    0,
                    default,
                    checkpoint));
                image.Measure(maxWidth, lineHeight, checkpoint);
                if (!float.IsFinite(image.DesiredWidth) || !float.IsFinite(image.DesiredHeight))
                    throw new InvalidOperationException("Inline image measurement returned non-finite dimensions.");
                if (Math.Abs(oldW - image.DesiredWidth) > 0.5f ||
                    Math.Abs(oldH - image.DesiredHeight) > 0.5f)
                {
                    changed = true;
                }
            }
            else if (run is InlineVectorSceneRun vector)
            {
                float oldW = vector.DesiredWidth;
                float oldH = vector.DesiredHeight;
                float oldBaseline = vector.DesiredBaseline;
                Rect oldLocalBounds = vector.LocalBounds;
                List<InlineRollbackState> vectorStates =
                    rollbackStates ??= new List<InlineRollbackState>();
                vectorStates.EnsureCapacity(vectorStates.Count + 1);
                vectorStates.Add(new InlineRollbackState(
                    null,
                    vector,
                    oldW,
                    oldH,
                    oldBaseline,
                    oldLocalBounds));

                vector.Measure(lineHeight);
                if (!float.IsFinite(vector.DesiredWidth) ||
                    !float.IsFinite(vector.DesiredHeight) ||
                    !float.IsFinite(vector.DesiredBaseline))
                {
                    throw new InvalidOperationException("Inline vector measurement returned non-finite dimensions.");
                }
                if (Math.Abs(oldW - vector.DesiredWidth) > 0.5f ||
                    Math.Abs(oldH - vector.DesiredHeight) > 0.5f)
                {
                    changed = true;
                }
            }
        }

        return changed;
    }

    private InlineSceneGeometry[] UpdateVectorScenePlacements(CanvasTextLayout layout)
    {
        CanvasLineMetrics[]? lineMetrics = TryGetLineMetrics();
        List<InlineSceneGeometry>? sceneGeometry = null;
        int cumulative = 0;
        foreach (InlineRun run in _runs)
        {
            int length = run.TextLength;
            if (run is InlineVectorSceneRun vector && length > 0)
            {
                CanvasTextLayoutRegion[]? regions = layout.GetCharacterRegions(cumulative, length);
                RecordSceneCharacterRegionQuery();
                if (_usesProgressiveNativeSceneResidency)
                    (sceneGeometry ??= []).Add(new InlineSceneGeometry(vector, regions));

                if (regions is { Length: > 0 })
                {
                    CanvasTextLayoutRegion region = regions[0];
                    Rect bounds = region.LayoutBounds;
                    CanvasLineMetrics metric = FindLineMetrics(lineMetrics, region);
                    float baseline = metric.Baseline > 0
                        ? metric.Baseline
                        : (float)bounds.Height * 0.8f;
                    double y = bounds.Y + baseline - vector.DesiredBaseline;
                    vector.SetLocalBounds(new Rect(
                        bounds.X,
                        y,
                        Math.Min(vector.DesiredWidth, bounds.Width),
                        vector.DesiredHeight));
                }
            }
            cumulative += length;
        }

        return sceneGeometry?.ToArray() ?? Array.Empty<InlineSceneGeometry>();
    }

    private InlineSceneGeometry[] CaptureVectorSceneGeometry(
        CanvasTextLayout layout,
        CancellationToken cancellationToken)
    {
        List<InlineSceneGeometry>? sceneGeometry = null;
        int cumulative = 0;
        foreach (InlineRun run in _runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = run.TextLength;
            if (run is InlineVectorSceneRun vector && length > 0)
            {
                CanvasTextLayoutRegion[]? regions = layout.GetCharacterRegions(cumulative, length);
                RecordSceneCharacterRegionQuery();
                (sceneGeometry ??= []).Add(new InlineSceneGeometry(vector, regions));
            }

            if (length > int.MaxValue - cumulative)
                throw new InvalidOperationException("Inline text geometry exceeded the supported layout range.");
            cumulative += length;
        }

        return sceneGeometry?.ToArray() ?? Array.Empty<InlineSceneGeometry>();
    }

    private void DrawInlineImages(CanvasDrawingSession ds, float baseX, float baseY, Rect viewport)
    {
        if (_layout is null) return;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (run is InlineImageRun image && len > 0)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is null) { cumulative += len; continue; }
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    double cellY = lb.Y + (lb.Height - image.DesiredHeight) / 2.0;
                    if (cellY < lb.Y) cellY = lb.Y;
                    var rect = new Rect(
                        baseX + lb.X,
                        baseY + cellY,
                        Math.Min(image.DesiredWidth, lb.Width),
                        image.DesiredHeight);
                    image.Image.SetInlineBounds(rect);
                    image.Image.PaintInline(ds, rect, viewport);
                    break;
                }
            }

            cumulative += len;
        }
    }

    private void DrawSelectedInlineImages(
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        Rect viewport,
        DocumentRange range,
        Color selectionForeground)
    {
        if (_layout is null) return;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (run is InlineImageRun image && len > 0 && SelectionIntersectsRun(range, run, len))
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is null) { cumulative += len; continue; }
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    double cellY = lb.Y + (lb.Height - image.DesiredHeight) / 2.0;
                    if (cellY < lb.Y) cellY = lb.Y;
                    var rect = new Rect(
                        baseX + lb.X,
                        baseY + cellY,
                        Math.Min(image.DesiredWidth, lb.Width),
                        image.DesiredHeight);
                    image.Image.SetInlineBounds(rect);
                    image.Image.PaintInlineSelectionForeground(ds, rect, viewport, selectionForeground);
                    break;
                }
            }

            cumulative += len;
        }
    }

    private void DrawInlineVectorScenes(
        CanvasDrawingSession drawingSession,
        float baseX,
        float baseY,
        Rect viewport)
    {
        foreach (InlineRun run in _runs)
        {
            if (run is not InlineVectorSceneRun vector)
                continue;

            Rect local = vector.LocalBounds;
            var bounds = new Rect(
                baseX + local.X,
                baseY + local.Y,
                local.Width,
                local.Height);
            if (!Intersects(bounds, viewport))
                continue;
            vector.Paint(
                drawingSession,
                baseX,
                baseY,
                GetRunStyle(vector).Foreground,
                Context.ThemeSnapshot.IsHighContrast);
        }
    }

    private void DrawSelectedInlineVectorScenes(
        CanvasDrawingSession drawingSession,
        float baseX,
        float baseY,
        Rect viewport,
        DocumentRange range,
        Color selectionForeground)
    {
        foreach (InlineRun run in _runs)
        {
            if (run is not InlineVectorSceneRun vector ||
                !SelectionIntersectsRun(range, run, run.TextLength))
            {
                continue;
            }

            Rect local = vector.LocalBounds;
            var bounds = new Rect(
                baseX + local.X,
                baseY + local.Y,
                local.Width,
                local.Height);
            if (!Intersects(bounds, viewport))
                continue;
            vector.Paint(
                drawingSession,
                baseX,
                baseY,
                selectionForeground,
                highContrast: true,
                foregroundOverride: selectionForeground);
        }
    }

    private bool SelectionIntersectsRun(DocumentRange range, InlineRun run, int length)
    {
        var start = new DocumentPosition(BlockIndex, run.InlineIndex, 0);
        var end = new DocumentPosition(BlockIndex, run.InlineIndex, length);
        return end > range.Start && start < range.End;
    }

    private static bool Intersects(Rect left, Rect right) =>
        left.Right >= right.Left && left.Left <= right.Right &&
        left.Bottom >= right.Top && left.Top <= right.Bottom;

    private void DrawDecorations(CanvasDrawingSession ds, float baseX, float baseY)
        => DrawDecorations(ds, baseX, baseY, null);

    private void DrawDecorations(CanvasDrawingSession ds, float baseX, float baseY, Color? overrideColor)
    {
        if (_layout is null) return;

        int cumulative = 0;
        CanvasLineMetrics[]? lineMetrics = null;
        bool lineMetricsAttempted = false;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (len == 0) { continue; }
            if (run is InlineEmbedRun or InlineImageRun or InlineVectorSceneRun) { cumulative += len; continue; }
            // Superscript link runs (footnote citation markers ¹²³…) should not
            // have an underline drawn at the normal line baseline — the small
            // Unicode glyphs snt high in the line and the baseline underline ends
            // uu drawn ~10 ux below them, looknng comuletely detached.  Skip
            // decorations for these runs entirely; their link appearance is already
            // communicated by the accent foreground color.
            if (run is LinkRun { IsSuperscript: true }) { cumulative += len; continue; }

            var rs = GetRunStyle(run);
            var decoratnonColor = overrideColor ?? rs.Foreground;

            if (rs.Underline || rs.Strikethrough)
            {
                if (!lineMetricsAttempted)
                {
                    lineMetrics = TryGetLineMetrics();
                    lineMetricsAttempted = true;
                }
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is null) { cumulative += len; continue; } // Win2D can return null on DirectWrite errors
                foreach (var r in regions)
                {
                    DrawRunDecorations(ds, baseX, baseY, lineMetrics, r, run, rs, decoratnonColor);
                }
            }
            cumulative += len;
        }
    }

    private void DrawSelectedDecorations(CanvasDrawingSession ds, float baseX, float baseY, Color color, int selectedStart, int selectedLength)
    {
        if (_layout is null || selectedLength <= 0)
            return;

        int selectedEnd = selectedStart + selectedLength;
        int cumulative = 0;
        CanvasLineMetrics[]? lineMetrics = null;
        bool lineMetricsAttempted = false;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (len == 0)
                continue;

            int runStart = cumulative;
            int runEnd = cumulative + len;
            cumulative = runEnd;

            if (runEnd <= selectedStart || runStart >= selectedEnd)
                continue;
            if (run is InlineEmbedRun or InlineImageRun or InlineVectorSceneRun)
                continue;
            if (run is LinkRun { IsSuperscript: true })
                continue;

            var rs = GetRunStyle(run);
            if (!rs.Underline && !rs.Strikethrough)
                continue;

            if (!lineMetricsAttempted)
            {
                lineMetrics = TryGetLineMetrics();
                lineMetricsAttempted = true;
            }

            int segmentStart = Math.Max(runStart, selectedStart);
            int segmentLength = Math.Min(runEnd, selectedEnd) - segmentStart;
            var regions = _layout.GetCharacterRegions(segmentStart, segmentLength);
            if (regions is null)
                continue;

            foreach (var r in regions)
                DrawRunDecorations(ds, baseX, baseY, lineMetrics, r, run, rs, color);
        }
    }

    private static void DrawRunDecorations(
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        CanvasLineMetrics[]? lineMetrics,
        CanvasTextLayoutRegion region,
        InlineRun run,
        ElementStyle style,
        Color color)
    {
        if (!style.Underline && !style.Strikethrough)
            return;
        if (run is LinkRun { IsSuperscript: true })
            return;

        var lb = region.LayoutBounds;
        var lm = FindLineMetrics(lineMetrics, region);
        float baselineFromTop = lm.Baseline > 0 ? lm.Baseline : (float)lb.Height * 0.80f;
        float xHeight = baselineFromTop * 0.50f;
        float strnkeY = (float)(baseY + lb.Y + (baselineFromTop - xHeight * 0.5f));
        float underlineY = (float)(baseY + lb.Y + baselineFromTop + 1.5f);

        if (style.Underline)
        {
            if (run is AbbreviationRun)
                DrawDottedUnderline(ds, (float)(baseX + lb.X), (float)(baseX + lb.X + lb.Width), underlineY, style.AccentBar ?? color);
            else
                ds.DrawLine((float)(baseX + lb.X), underlineY, (float)(baseX + lb.X + lb.Width),
                    underlineY, color, 1.0f);
        }

        if (style.Strikethrough)
        {
            ds.DrawLine((float)(baseX + lb.X), strnkeY, (float)(baseX + lb.X + lb.Width),
                strnkeY, color, 1.0f);
        }
    }

    private static void DrawDottedUnderline(CanvasDrawingSession ds, float startX, float endX, float y, Color color)
    {
        const float dot = 1.25f;
        const float gap = 2.25f;
        for (float x = startX; x < endX; x += dot + gap)
        {
            float x2 = MathF.Min(x + dot, endX);
            ds.DrawLine(x, y, x2, y, color, 1.0f);
        }
    }

    private void DrawRunBackgrounds(CanvasDrawingSession ds, float baseX, float baseY)
    {
        if (_layout is null) return;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.TextLength;
            if (len == 0) { continue; }
            if (run is InlineEmbedRun or InlineImageRun or InlineVectorSceneRun) { cumulative += len; continue; }

            var rs = GetRunStyle(run);
            bool hasRunSpecificStyle = HasRunSpecificStyle(run);
            bool drawRunBg = hasRunSpecificStyle && rs.Background is { };
            if (!drawRunBg) { cumulative += len; continue; }

            var regions = _layout.GetCharacterRegions(cumulative, len);
            if (regions is null) { cumulative += len; continue; }

            foreach (var r in regions)
            {
                var lb = r.LayoutBounds;
                double bgTop = lb.Y + lb.Height * 0.05;
                double bgH = lb.Height * 0.90;
                ds.FillRoundedRectangle(
                    new Rect(baseX + lb.X - 2, baseY + bgTop, lb.Width + 4, bgH),
                    rs.CornerRadius,
                    rs.CornerRadius,
                    rs.Background!.Value);
            }

            cumulative += len;
        }
    }

    internal CanvasLineMetrics[]? GetLineMetricsSnapshot()
        => TryGetLineMetrics();

    internal double GetTextOriginY()
    {
        ElementStyle style = GetContainerStyle();
        return GetSnappedOrigin(style).Y;
    }

    private CanvasLineMetrics[]? TryGetLineMetrics()
    {
        if (_lineMetricsRead)
            return _cachedLineMetrics;

        _lineMetricsRead = true;
        try
        {
            _cachedLineMetrics = _layout?.LineMetrics;
            return _cachedLineMetrics;
        }
        catch (NotSupportedException)
        {
            // Some WinRT/Release projections cannot marshal CanvasLineMetrics.
            // Text still renders; decoration placement can use region bounds.
            return null;
        }
    }

    private void InvalidateLineMetrics()
    {
        _cachedLineMetrics = null;
        _lineMetricsRead = false;
    }

    private static CanvasLineMetrics FindLineMetrics(CanvasLineMetrics[]? metrics, CanvasTextLayoutRegion region)
    {
        if (metrics is null || metrics.Length == 0)
            return default;

        int idx = (int)region.CharacterIndex;
        int run = 0;
        foreach (var m in metrics)
        {
            int next = run + m.CharacterCount;
            if (idx < next) return m;
            run = next;
        }
        return metrics.Length > 0 ? metrics[metrics.Length - 1] : default;
    }

    private bool ShouldApplyRunTextStyle(InlineRun run)
    {
        if (run.ElementKey == MarkdownElementKeys.CodeInline && IsHeadingElement(ElementKey))
            return false;

        return HasRunSpecificStyle(run);
    }

    private bool HasRunSpecificStyle(InlineRun run)
    {
        return (!string.IsNullOrEmpty(run.ElementKey) && run.ElementKey != ElementKey) ||
            run.StyleAliases.Count > 0 ||
            run.StyleModifierKeys.Count > 0;
    }

    private static bool IsHeadingElement(string elementKey)
        => elementKey is MarkdownElementKeys.Heading1
            or MarkdownElementKeys.Heading2
            or MarkdownElementKeys.Heading3
            or MarkdownElementKeys.Heading4
            or MarkdownElementKeys.Heading5
            or MarkdownElementKeys.Heading6;
}
