using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Block hosting a sequence of inline runs (paragraph or heading body).
/// Owns a <see cref="CanvasTextLayout"/> built from a single concatenated buffer
/// with per-run style spans.
/// </summary>
internal sealed class InlineContainerBox : BlockBox
{
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
    internal SingletonMutableList<InlineRun> RunList => _runs;
    private List<(InlineImageRun Run, int Offset)>? _inlineImageRuns;
    internal List<(InlineImageRun Run, int Offset)>? InlineImageEntries => _inlineImageRuns;
    private List<(InlineEmbedRun Run, int Offset)>? _inlineEmbedRuns;
    internal List<(InlineEmbedRun Run, int Offset)>? InlineEmbedEntries => _inlineEmbedRuns;
    private bool _hasAtomicInlineObject;
    internal bool HasAtomicInlineObject => _hasAtomicInlineObject;
    private int _runTextLength;
    internal int RunTextLength => _runTextLength;
    // Most runs inherit the container aliases. Keep that common case implicit;
    // once an override exists, null slots preserve the exact run ordinals.
    private List<string[]?>? _effectiveRunAliasOverrides;
    internal List<string[]?>? EffectiveRunAliasOverrides => _effectiveRunAliasOverrides;
    // Offscreen lazy boxes have not resolved any run style yet. Preserve the
    // ordinal cache contract without allocating a List and null slots for them.
    private List<ElementStyle?>? _resolvedRunStyles;
    internal List<ElementStyle?>? ResolvedRunStyles => _resolvedRunStyles;
    // Nested link styles are resolved once per measured box. Plain links never
    // allocate this cache, and immutable maps remain safely shareable by private
    // placement clones while each clone resolves against its own context.
    private List<ElementStyle[]?>? _resolvedLinkInlineSpanStyles;
    internal List<ElementStyle[]?>? ResolvedLinkInlineSpanStyles => _resolvedLinkInlineSpanStyles;
    internal int ResolvedRunStyleCount => _resolvedRunStyles?.Count ?? _runs.Count;
    internal ElementStyle? GetCachedRunStyle(int index)
    {
        if ((uint)index >= (uint)_runs.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _resolvedRunStyles?[index];
    }
    public readonly string ElementKey;
    private CanvasTextLayout? _layout;
    internal CanvasTextLayout? TextLayout => _layout;
    private NativeResourceRetirementRecord? _layoutRetirementRecord;
    internal NativeResourceRetirementRecord? TextLayoutRetirementRecord => _layoutRetirementRecord;
    private ScriptBaselineGeometry _scriptBaselineGeometry = ScriptBaselineGeometry.Empty;
    internal ScriptBaselineGeometry ScriptBaselineGeometry => _scriptBaselineGeometry;
    private CanvasLineMetrics[]? _cachedLineMetrics;
    internal CanvasLineMetrics[]? CachedLineMetrics => _cachedLineMetrics;
    private bool _lineMetricsRead;
    internal bool HasReadLineMetrics => _lineMetricsRead;
    private CanvasTextLayout? _selectionLayout;
    internal CanvasTextLayout? SelectionLayout => _selectionLayout;
    private NativeResourceRetirementRecord? _selectionLayoutRetirementRecord;
    internal NativeResourceRetirementRecord? SelectionLayoutRetirementRecord => _selectionLayoutRetirementRecord;
    private Color _selectionLayoutColor;
    internal Color SelectionLayoutColor => _selectionLayoutColor;
    private float _selectionLayoutWidth;
    internal float SelectionLayoutWidth => _selectionLayoutWidth;
    // Per-run scalar snapshot populated only while applying the measured
    // layout. Paint, selection and geometry consumers only read this array;
    // they never create DirectWrite analyzers or inspect cancellation state.
    private float[]? _inlineImageXHeightByRunIndex;
    internal float[]? InlineImageXHeightByRunIndex => _inlineImageXHeightByRunIndex;
    // The corresponding final-layout alphabetic baseline for each image run.
    // Geometry reads this directly instead of scanning every line metric for
    // every image (or materializing metrics on the paint path).
    private float[]? _inlineImageLineBaselineByRunIndex;
    internal float[]? InlineImageLineBaselineByRunIndex => _inlineImageLineBaselineByRunIndex;
    private string _buffer = string.Empty;
    internal string LayoutBuffer => _buffer;
    private bool _bufferDirty;
    internal bool IsBufferDirty => _bufferDirty;
    private float _lastWidth;
    internal float LastMeasuredWidth => _lastWidth;

    // Text-only inline boxes are the common case. Keep their vector-specific
    // counters and geometry in no per-box holder until a vector run is added.
    // This state is mutable and strictly box-owned; private placement clones
    // create their own holder rather than sharing it with the source box.
    private InlineVectorSceneState? _vectorSceneState;
    internal InlineVectorSceneState? VectorSceneStateHolder => _vectorSceneState;
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
    internal InlineSceneGeometry[] PendingSceneGeometry => _pendingSceneGeometry;
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
    internal long CurrentSceneGeometryGeneration => _sceneGeometryGeneration;
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
    internal long PendingSceneGeometryGeneration => _pendingSceneGeometryGeneration;
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
    internal bool UsesProgressiveNativeSceneResidency => _usesProgressiveNativeSceneResidency;
    private readonly IReadOnlyList<string> _styleContextKeys;
    internal IReadOnlyList<string> StyleContextKeys => _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    internal IReadOnlyList<string> StyleAliasKeys => _styleAliasKeys;
    private ElementStyle? _resolvedContainerStyle;
    internal ElementStyle? ResolvedContainerStyle => _resolvedContainerStyle;
    private IReadOnlyList<CodeBlockHighlightSpan> _foregroundSpans = Array.Empty<CodeBlockHighlightSpan>();
    internal IReadOnlyList<CodeBlockHighlightSpan> ForegroundSpans => _foregroundSpans;
    private readonly NativeResourceRetirement _nativeRetirement;
    internal NativeResourceRetirement NativeRetirement => _nativeRetirement;
    private readonly Action<IDisposable>? _nativeDisposer;
    internal Action<IDisposable>? NativeDisposer => _nativeDisposer;

    /// <summary>
    /// Run currently being hovered by the pointer.  Retained for hit-test /
    /// click routing diagnostics only; we deliberately do NOT mutate any
    /// CanvasTextLayout state in response to hover changes because calling
    /// <c>CanvasTextLayout.SetColor</c> on every hover transition invalidates
    /// DirectWrite's cached glyph-run metrics, which causes visible glyph shake
    /// when the cursor moves over text or links.  Link hover affordance is
    /// communicated by the cursor shape (Hand) only — matching Win11 Settings,
    /// Notepad, Word, etc.  A future enhancement may render an underline
    /// accent on a XAML overlay (no canvas invalidation) for stronger
    /// affordance without re-introducing shake.
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

    internal bool TrySetMeasuredCodeBlockChunkEffectiveHeight(double effectiveHeight)
    {
        if (ElementKey != MarkdownElementKeys.CodeBlock ||
            _layout is null ||
            CodeBlockTextOffset < 0 ||
            CodeBlockTextLength <= 0 ||
            !double.IsFinite(Bounds.Height) ||
            Bounds.Height < 0 ||
            !double.IsFinite(effectiveHeight) ||
            effectiveHeight < 0 ||
            effectiveHeight > Bounds.Height)
        {
            return false;
        }

        Bounds = new Rect(Bounds.X, Bounds.Y, Bounds.Width, effectiveHeight);
        return true;
    }

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
        => InlinePrivatePlacementCloner.TryCreatePrivateRasterImageCandidates(
            this,
            targetContext,
            availableWidth,
            out candidate,
            out expectedSources);

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
        => InlinePrivatePlacementPreflight.TryGetPrivateRasterPlacementCounts(
            this,
            targetContext,
            availableWidth,
            out runCount,
            out imageCount,
            allowUnmeasuredTextOnly,
            allowIdleImagePlacements);

    internal bool TryGetPrivateRasterPlacementCounts(
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false)
        => InlinePrivatePlacementPreflight.TryGetPrivateRasterPlacementCounts(
            this,
            targetContext,
            availableWidth,
            out runCount,
            out imageCount,
            out styleKeyCopyEntries,
            allowUnmeasuredTextOnly,
            allowIdleImagePlacements,
            allowCodeForegroundSpans);

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
        => InlinePrivatePlacementPreflight.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
            this,
            targetContext,
            availableWidth,
            out runCount,
            out imageCount,
            out styleKeyCopyEntries);

    /// <summary>
    /// Allocation-free count of every key reference array copied by a private
    /// placement clone. The caller applies the same aggregate bound across
    /// document roots; this per-inline check also protects clone entry points.
    /// </summary>
    internal bool TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long entries)
        => InlinePrivatePlacementPreflight.TryGetPrivateRasterPlacementStyleKeyCopyEntries(this, out entries);

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
        => InlinePrivatePlacementCloner.TryCreatePrivateInlinePlacementClone(
            this,
            targetContext,
            availableWidth,
            out candidate,
            out expectedSources,
            allowUnmeasuredTextOnly,
            allowIdleImagePlacements,
            allowCodeForegroundSpans);

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
        => InlinePrivatePlacementCloner.TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(
            this,
            targetContext,
            availableWidth,
            out candidate,
            out expectedSources);

    /// <summary>
    /// Creates the unmeasured private placement candidate that copies this
    /// box's container state. The caller adds the copied runs.
    /// </summary>
    internal InlineContainerBox CreatePrivatePlacementShell(
        MarkdownLayoutContext targetContext,
        bool copyCodeForegroundSpans)
        => new(targetContext, this, copyCodeForegroundSpans);

    /// <summary>
    /// Appends a copied run to this private placement candidate. Once added,
    /// the candidate owns the run's cloned image.
    /// </summary>
    internal void AddPrivatePlacementRun(InlineRun copiedRun)
    {
        // Preflight required the source ordinals to be exact; preserve
        // rather than repair them so malformed identity never passes.
        _runs.Add(copiedRun);
    }

    /// <summary>
    /// Records the per-run state of a run just added to this private placement
    /// candidate: the source ordinal's alias override and resolved style, and
    /// the image or vector-scene index entry.
    /// </summary>
    internal void RecordPrivatePlacementRunState(
        InlineContainerBox source,
        int index,
        InlineRun copiedRun,
        int offset,
        int runCount)
    {
        string[]? sourceAliasOverride = source.GetEffectiveRunAliasOverride(index);
        AppendEffectiveRunAliasOverride(
            index,
            sourceAliasOverride is null ? null : CopyKeys(sourceAliasOverride));
        if (source._resolvedRunStyles is not null)
            (_resolvedRunStyles ??= new(runCount)).Add(source._resolvedRunStyles[index]);
        if (copiedRun is InlineImageRun imageRun)
        {
            imageRun.Image.BlockIndex = source.BlockIndex;
            (_inlineImageRuns ??= new()).Add((imageRun, offset));
        }
        else if (copiedRun is InlineVectorSceneRun)
        {
            InlineVectorSceneState vectorState = _vectorSceneState ??= new InlineVectorSceneState();
            vectorState.VectorSceneRunCount++;
        }
    }

    internal void SetPrivatePlacementRunTextLength(int textLength)
        => _runTextLength = textLength;

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

    private static string[] CopyKeys(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    public void Add(InlineRun run) => Add(run, sourceProjection: null);

    internal void Add(InlineRun run, MarkdownSourceMap.SourceProjection? sourceProjection)
    {
        System.Diagnostics.Debug.Assert(BlockIndex != 0,
            "BlockIndex must be assigned before calling Add(); source-map entries will be registered under block 0 otherwise.");
        InlineVectorSceneState? vectorStateForAddedRun = run is InlineVectorSceneRun
            ? _vectorSceneState ?? new InlineVectorSceneState()
            : null;
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
            imageRun.Image.BlockIndex = BlockIndex;
        }
        else if (run is InlineEmbedRun embedRun)
        {
            (_inlineEmbedRuns ??= new()).Add((embedRun, runOffset));
        }
        _runs.Add(run);
        _inlineImageXHeightByRunIndex = null;
        _inlineImageLineBaselineByRunIndex = null;
        if (run is InlineVectorSceneRun vectorScene)
        {
            InlineVectorSceneState vectorState = vectorStateForAddedRun!;
            _vectorSceneState = vectorState;
            vectorState.VectorSceneRunCount++;
            vectorState.HasDeferredSceneMarker |= vectorScene.DeferredSceneMarker is not null;
        }
        AppendEffectiveRunAliasOverride(
            run.InlineIndex,
            run.StyleAliases.Count == 0 ? null : CombineAliases(_styleAliasKeys, run.StyleAliases));
        _resolvedRunStyles?.Add(null);
        _resolvedLinkInlineSpanStyles?.Add(null);
        if (sourceProjection is not null)
        {
            Context.SourceMap.Add(BlockIndex, run.InlineIndex, run.RenderedLength, run.SourceSpan, sourceProjection);
        }
        else if (run is SourceSliceTextRun sourceSlice &&
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
        _bufferDirty = true; // buffer is stale until next BuildBuffer()
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

        RebuildAfterInlineRunTransfer();
        return detached;
    }

    /// <summary>
    /// Detaches resource-backed image runs and resource-free decorative
    /// spacers from an unmeasured staging container, preserving their visual
    /// order. Only images require construction-block ownership transfer.
    /// </summary>
    internal InlineRun[] DetachInlineImagesAndSpacersForTransfer()
    {
        if (_layout is not null || _selectionLayout is not null)
        {
            throw new InvalidOperationException(
                "Inline image and spacer runs can only be transferred before their container is measured.");
        }

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

        RebuildAfterInlineRunTransfer();
        return detached;
    }

    private void RebuildAfterInlineRunTransfer()
    {
        _inlineImageRuns = null;
        _inlineEmbedRuns = null;
        _inlineImageXHeightByRunIndex = null;
        _inlineImageLineBaselineByRunIndex = null;
        _hasAtomicInlineObject = false;
        _runTextLength = 0;
        _vectorSceneRunCount = 0;
        _hasDeferredSceneMarker = false;
        _effectiveRunAliasOverrides = null;
        _resolvedRunStyles = null;
        _resolvedLinkInlineSpanStyles = null;

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
                _vectorSceneState ??= new InlineVectorSceneState();
                _vectorSceneRunCount++;
                _hasDeferredSceneMarker |= vectorScene.DeferredSceneMarker is not null;
            }
            else if (run is InlineSpacerRun)
            {
                _hasAtomicInlineObject = true;
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
    }

    internal void SetForegroundSpans(IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        _foregroundSpans = spans ?? Array.Empty<CodeBlockHighlightSpan>();
        if (_layout is not null)
            InlineRunStyleApplier.ApplyForegroundSpans(this, _layout);
    }

    /// <summary>
    /// Converts a <see cref="DocumentPosition"/> (which must target this box) to an
    /// absolute character index within the concatenated inline buffer.
    /// </summary>
    public int GetBufferCharOffset(DocumentPosition pos)
    {
        int offset = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            if (i == pos.InlineIndex) return offset + Math.Clamp(pos.CharacterOffset, 0, _runs[i].TextLength);
            offset += _runs[i].TextLength;
        }
        return offset;
    }

    /// <summary>
    /// Converts an absolute character index within the buffer back to a
    /// <see cref="DocumentPosition"/> targeting this box.
    /// </summary>
    public DocumentPosition GetPositionFromBufferOffset(int bufOffset)
    {
        bufOffset = Math.Max(0, bufOffset);
        int offset = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            int len = _runs[i].TextLength;
            if (bufOffset < offset + len)
                return new DocumentPosition(BlockIndex, _runs[i].InlineIndex, bufOffset - offset);
            // At exact run boundary: prefer start of next run (continue loop).
            offset += len;
        }
        // bufOffset >= total length: clamp to end of last run.
        if (_runs.Count == 0) return new DocumentPosition(BlockIndex, 0, 0);
        int last = _runs.Count - 1;
        return new DocumentPosition(BlockIndex, last, _runs[last].TextLength);
    }

    internal void EnsureBuffer()
    {
        if (_runs.Count == 0) { _buffer = string.Empty; _bufferDirty = false; return; }
        // Rely solely on _bufferDirty (set by Add()) — the _buffer.Length == 0 fallback
        // would cause repeated BuildBuffer() calls for boxes whose runs all produce empty
        // text (e.g. embed-placeholder runs), burning CPU without changing anything.
        if (_bufferDirty) BuildBuffer();
    }

    /// <summary>
    /// Returns the start and end positions that span the word containing
    /// <paramref name="pos"/>. Unicode word characters span formatted runs,
    /// while symbols and emoji are selected as complete graphemes. Whitespace
    /// and punctuation keep the established nearest-word behavior.
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
        => InlineMeasuredPlacementCapture.TryCaptureMeasuredPlacementTransfer(
            this,
            prepared,
            expectedAvailableWidth,
            out transfer);

    /// <summary>
    /// Root-local-only variant for the explicit measurement-only vector-run
    /// recipe. Do not call from whole-tree COW; the candidate has no painter.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => InlineMeasuredPlacementCapture.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
            this,
            prepared,
            expectedAvailableWidth,
            out transfer);

    /// <summary>
    /// Root-local image-completion variant for a table cell. The candidate is
    /// measurement-only and its vector geometry is remapped to stable source
    /// runs before the placement transaction commits.
    /// </summary>
    internal bool TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => InlineMeasuredPlacementCapture.TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
            this,
            prepared,
            expectedSourceWidth,
            out transfer);

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
        => InlineMeasuredPlacementCapture.TryCaptureTableCellMeasuredPlacementTransfer(
            this,
            prepared,
            expectedSourceWidth,
            out transfer);

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
        => InlineMeasuredPlacementCapture.TryCaptureContainerLeafMeasuredPlacementTransfer(
            this,
            prepared,
            expectedAvailableWidth,
            out transfer);

    /// <summary>
    /// Root-local image-completion variant for a vector-bearing leaf in an
    /// ordinary stack/list placement tree. It never publishes its measured-only
    /// candidate runs.
    /// </summary>
    internal bool TryCaptureContainerLeafMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => InlineMeasuredPlacementCapture.TryCaptureContainerLeafMeasuredPlacementTransferWithMeasuredVectorPlacement(
            this,
            prepared,
            expectedAvailableWidth,
            out transfer);

    public override float Measure(float availableWidth)
    {
        ThrowIfCancellationRequested();
        if (!InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(this))
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
        List<InlineMeasureRollbackState>? changedInlineMeasures = null;
        InlineScenePlacementRollbackState[]? previousScenePlacements = null;
        CanvasTextLayout? replacementLayout = null;
        NativeResourceRetirementRecord? replacementLayoutRetirementRecord = null;
        ScriptBaselineGeometry? replacementScriptBaselineGeometry = null;
        InlineSceneGeometry[]? replacementSceneGeometry = null;
        float[]? replacementInlineImageLineBaselines = null;
        bool publishInlineImageLineBaselines = false;
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
            bool atomicMetricsChanged = InlineAtomicObjectLayout.MeasureAtomicInlineRuns(
                this,
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
                CanvasTextLayout candidateLayout = CanvasTextLayoutFactory.Create(
                    Context.ResourceCreator,
                    _buffer,
                    formatLease.Format,
                    layoutWidth,
                    float.MaxValue,
                    Context.LayoutDiagnostics);
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
                InlineRunStyleApplier.ApplyRunStyles(this, candidateLayout, applyColors: true);
                InlineRunStyleApplier.ApplyForegroundSpans(this, candidateLayout);
                float inlineMetricScale = InlineAtomicObjectLayout.ApplyContentAwareLineSpacing(this, candidateLayout, style, layoutWidth);
                InlineAtomicObjectLayout.ApplyEmbedSpacing(
                    this,
                    candidateLayout,
                    inverseMetricScale: inlineMetricScale,
                    maxInlineWidth: layoutWidth);
                replacementScriptBaselineGeometry = InlineScriptBaselineLayout.BuildScriptBaselineGeometry(this, style.FontSize);
                if (Context.ThemeSnapshot.MiddleAlignInlineImages &&
                    _inlineImageRuns is { Count: > 0 })
                {
                    replacementInlineImageLineBaselines =
                        InlineObjectGeometry.BuildInlineImageLineBaselines(this, TryGetLineMetrics());
                    publishInlineImageLineBaselines = true;
                }
                replacementSceneGeometry = UpdateVectorScenePlacements(candidateLayout);
                _lastWidth = availableWidth;
            }

            // Keep defensive recovery for an absent/stale baseline snapshot
            // on the measurement path, never on paint.
            else if (Context.ThemeSnapshot.MiddleAlignInlineImages &&
                _inlineImageRuns is { Count: > 0 } &&
                (_inlineImageLineBaselineByRunIndex is null ||
                    _inlineImageLineBaselineByRunIndex.Length != _runs.Count))
            {
                replacementInlineImageLineBaselines =
                    InlineObjectGeometry.BuildInlineImageLineBaselines(this, TryGetLineMetrics());
                publishInlineImageLineBaselines = true;
            }

            var bounds = _layout!.LayoutBounds;
            float top = (float)(margin.Top + padding.Top);
            float bottom = (float)(margin.Bottom + padding.Bottom);
            ScriptBaselineGeometry scriptGeometry =
                replacementScriptBaselineGeometry ?? _scriptBaselineGeometry;
            measuredHeight = (float)bounds.Height + top + bottom +
                scriptGeometry.TopInset + scriptGeometry.BottomInset;
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
                    foreach (InlineMeasureRollbackState state in changedInlineMeasures)
                    {
                        try
                        {
                            state.Restore();
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
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
                            cleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
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
                    Exception? layoutCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                        ref replacementLayout,
                        replacementLayoutRetirementRecord,
                        "disposing a failed inline-container measure candidate");
                    cleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
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
            foreach (InlineMeasureRollbackState state in changedInlineMeasures)
                state.Commit();
        }

        if (publishInlineImageLineBaselines)
            _inlineImageLineBaselineByRunIndex = replacementInlineImageLineBaselines;

        if (replacementScriptBaselineGeometry is not null)
            _scriptBaselineGeometry = replacementScriptBaselineGeometry;

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
            Exception? layoutCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                ref previousLayout,
                previousLayoutRetirementRecord,
                "retiring a superseded inline layout");
            Exception? selectionCleanupFailure = previousLayoutsAlias
                ? ReferenceEquals(previousLayoutRetirementRecord, previousSelectionLayoutRetirementRecord)
                    ? null
                    : InlineNativeLayoutRetirement.ReleaseEmptyNativeRecord(previousSelectionLayoutRetirementRecord)
                : InlineNativeLayoutRetirement.RetireNativeLayout(
                    ref previousSelectionLayout,
                    previousSelectionLayoutRetirementRecord,
                    "retiring a superseded inline selection layout");
            Exception? supersededCleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
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
        => InlineIntrinsicWidthMeasurer.MeasureIntrinsicWidths(this, maximumPreferredWidth);

    /// <summary>
    /// No-throw exchange of this source box's measured native-layout state
    /// with its private measured candidate. Scene generations stay with this
    /// stable source box. Called by
    /// <see cref="InlineMeasuredPlacementTransfer.SwapPlacementNoThrow"/> only after
    /// every transfer fence has succeeded under the snapshot gate.
    /// </summary>
    /// <param name="prepared">The private measured candidate.</param>
    /// <param name="sourceSceneGeneration">This box's captured scene generation.</param>
    /// <param name="sourcePendingSceneGeneration">This box's captured pending scene generation.</param>
    /// <param name="sourcePendingSceneGeometry">This box's captured pending scene geometry.</param>
    /// <param name="adoptPreparedGeometry">Whether the transfer carries remapped vector geometry.</param>
    /// <param name="preparedGeometryForSource">The candidate geometry remapped to this box's runs.</param>
    internal void SwapMeasuredPlacementStateNoThrow(
        InlineContainerBox prepared,
        long sourceSceneGeneration,
        long sourcePendingSceneGeneration,
        InlineSceneGeometry[] sourcePendingSceneGeometry,
        bool adoptPreparedGeometry,
        InlineSceneGeometry[]? preparedGeometryForSource)
    {
        InlineContainerBox source = this;

        (source._layout, prepared._layout) = (prepared._layout, source._layout);
        (source._layoutRetirementRecord, prepared._layoutRetirementRecord) =
            (prepared._layoutRetirementRecord, source._layoutRetirementRecord);
        (source._scriptBaselineGeometry, prepared._scriptBaselineGeometry) =
            (prepared._scriptBaselineGeometry, source._scriptBaselineGeometry);
        (source._cachedLineMetrics, prepared._cachedLineMetrics) =
            (prepared._cachedLineMetrics, source._cachedLineMetrics);
        (source._lineMetricsRead, prepared._lineMetricsRead) =
            (prepared._lineMetricsRead, source._lineMetricsRead);
        (source._selectionLayout, prepared._selectionLayout) =
            (prepared._selectionLayout, source._selectionLayout);
        (source._selectionLayoutRetirementRecord, prepared._selectionLayoutRetirementRecord) =
            (prepared._selectionLayoutRetirementRecord, source._selectionLayoutRetirementRecord);
        (source._selectionLayoutColor, prepared._selectionLayoutColor) =
            (prepared._selectionLayoutColor, source._selectionLayoutColor);
        (source._selectionLayoutWidth, prepared._selectionLayoutWidth) =
            (prepared._selectionLayoutWidth, source._selectionLayoutWidth);
        (source._lastWidth, prepared._lastWidth) = (prepared._lastWidth, source._lastWidth);
        (source.Bounds, prepared.Bounds) = (prepared.Bounds, source.Bounds);

        // Scene generation belongs to the stable source box, not the
        // temporary clone. Candidate progressive geometry is remapped to
        // source-run identities before the final gate; adopt that bounded
        // array only for the stable source and keep the candidate's prior
        // pending array from becoming another source owner.
        if (prepared._vectorSceneState is not null)
        {
            prepared._sceneGeometryGeneration = sourceSceneGeneration;
            prepared._pendingSceneGeometryGeneration = sourcePendingSceneGeneration;
            prepared._pendingSceneGeometry = sourcePendingSceneGeometry;
        }
        if (source._vectorSceneState is not null)
        {
            long nextSceneGeneration = unchecked(sourceSceneGeneration + 1);
            source._sceneGeometryGeneration = nextSceneGeneration == 0 ? 1 : nextSceneGeneration;
            source._pendingSceneGeometryGeneration = source._usesProgressiveNativeSceneResidency
                ? source._sceneGeometryGeneration
                : 0;
            source._pendingSceneGeometry = adoptPreparedGeometry &&
                source._usesProgressiveNativeSceneResidency
                ? preparedGeometryForSource!
                : Array.Empty<InlineSceneGeometry>();
        }
    }

    internal SharedCanvasTextFormatCache.Lease CreateTextFormatLease(
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

    internal ElementStyle GetContainerStyle()
        => _resolvedContainerStyle ??= Context.ThemeSnapshot.GetStyle(
                ElementKey,
                _styleContextKeys,
                _styleAliasKeys,
                CodeLanguage,
                StyleState);

    internal ElementStyle GetRunStyle(InlineRun run)
    {
        // A decorative spacer has no text styling, baseline, or foreground.
        // Its authored dimensions are applied independently as an inline
        // object and must not inherit a script/font-size modifier.
        if (run is InlineSpacerRun)
            return GetContainerStyle();

        ElementStyle? resolved = GetCachedRunStyle(run.InlineIndex);
        if (resolved is not null)
            return resolved;

        resolved = InlineRunStyleResolver.ResolveRunStyle(this, run);
        CacheRunStyle(run.InlineIndex, resolved);
        return resolved;
    }

    private void CacheRunStyle(int index, ElementStyle resolved)
    {
        if (_resolvedRunStyles is null)
        {
            var styles = new List<ElementStyle?>(_runs.Count);
            for (int slot = 0; slot < _runs.Count; slot++)
                styles.Add(null);
            _resolvedRunStyles = styles;
        }
        _resolvedRunStyles[index] = resolved;
    }

    internal ElementStyle[] GetLinkInlineSpanStyles(LinkRun link)
    {
        LinkInlineStyleMap map = link.InlineStyleMap
            ?? throw new ArgumentException("The link has no nested inline style map.", nameof(link));

        List<ElementStyle[]?>? cache = _resolvedLinkInlineSpanStyles;
        if (cache is null)
        {
            cache = new List<ElementStyle[]?>(_runs.Count);
            for (int index = 0; index < _runs.Count; index++)
                cache.Add(null);
            _resolvedLinkInlineSpanStyles = cache;
        }
        if ((uint)link.InlineIndex >= (uint)cache.Count ||
            !ReferenceEquals(_runs[link.InlineIndex], link))
        {
            throw new InvalidOperationException("A styled link's inline ordinal is inconsistent.");
        }

        if (cache[link.InlineIndex] is { } resolved)
            return resolved;

        // The map and link text are immutable after publication. Validate on
        // first resolution, then let paint/hover/selection reuse the cached
        // styles without walking every span and nested path on each draw.
        if (!map.IsValidForTextLength(link.TextLength))
            throw new InvalidOperationException("A link's nested inline style map is malformed.");

        ElementStyle[] spanStyles = InlineRunStyleResolver.ResolveLinkInlineSpanStyles(this, link, map);
        cache[link.InlineIndex] = spanStyles;
        return spanStyles;
    }

    internal Thickness GetEffectivePadding(ElementStyle style)
        => UseContainerPadding ? style.Padding : default;

    internal override void ThrowIfCancellationRequested()
        => Context.CancellationToken.ThrowIfCancellationRequested();

    internal bool HasWellFormedEffectiveRunAliases =>
        _effectiveRunAliasOverrides is null || _effectiveRunAliasOverrides.Count == _runs.Count;

    internal bool HasWellFormedResolvedLinkInlineStyleCache
    {
        get
        {
            List<ElementStyle[]?>? cache = _resolvedLinkInlineSpanStyles;
            if (cache is null)
                return true;
            if (cache.Count != _runs.Count)
                return false;

            for (int index = 0; index < cache.Count; index++)
            {
                if (cache[index] is not { } styles)
                    continue;
                if (_runs[index] is not LinkRun link ||
                    link.InlineStyleMap is not { } map ||
                    !map.IsValidForTextLength(link.TextLength) || styles.Length != map.Count)
                    return false;
            }
            return true;
        }
    }

    internal string[] GetEffectiveRunAliases(int index)
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

    internal string[]? GetEffectiveRunAliasOverride(int index)
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

    internal void AppendEffectiveRunAliasOverride(int index, string[]? aliases)
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
    /// document coordinates.  Snapping eliminates fractional sub-pixel
    /// positioning that otherwise causes DirectWrite glyphs straddling
    /// CanvasVirtualControl tile / dirty-rect boundaries to be re-rasterised
    /// at slightly different sub-pixel offsets when the dirty rect changes
    /// shape (e.g. as a selection-drag extends a highlight rect across the
    /// text), which reads as visible "shake" of the rendered glyphs.
    /// All paint / hit-test / selection-rect / embed-rect computations use
    /// this single snapped origin so they stay in agreement.
    /// </summary>
    internal (float X, float Y) GetSnappedOrigin(Theming.ElementStyle style)
        => GetSnappedOrigin(style, out _, out _);

    internal (float X, float Y) GetSnappedOrigin(
        Theming.ElementStyle style,
        out float finalOriginX,
        out float finalOriginY)
    {
        // Snap to *device pixels*, not DIPs. At a non-1x rasterization scale
        // (e.g., 1.25x / 1.5x / 2x), snapping only to integer DIPs still leaves
        // glyph origins at fractional device-pixel positions. When the canvas
        // dirty-rect shape changes between frames (as it does whenever the
        // selection drag grows / shrinks adjacent tiles), DirectWrite's pixel
        // snapping can resolve to a slightly different device-pixel column,
        // which manifests as the "selection shake" the user reports —
        // visually amplified on large heading glyphs.
        float scale = (float)Context.RasterizationScale;
        if (scale <= 0f) scale = 1f;
        var margin = Margin;
        var padding = GetEffectivePadding(style);
        float xDip = (float)(Bounds.X + margin.Left + padding.Left);
        float yDip = (float)(Bounds.Y + margin.Top + padding.Top + _scriptBaselineGeometry.TopInset);
        LayoutPlacementTransformContext.SnapOriginToDevicePixels(
            xDip,
            yDip,
            scale,
            out float x,
            out float y);
        LayoutPlacementTransformContext.SnapFinalOriginToDevicePixels(
            xDip,
            yDip,
            scale,
            out finalOriginX,
            out finalOriginY);
        return (x, y);
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
        => InlineTextPainter.Paint(this, ds, viewport);

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return false;
        }

        return InlineTextHitTesting.HitTestNativeLayout(this, point, out position);
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

        return InlineTextHitTesting.HitTestNativeLayout(this, point, out position);
    }

    public IEnumerable<Rect> GetRangeRects(DocumentRange range)
        => InlineRangeGeometry.GetRangeRects(this, range);

    /// <summary>
    /// Resolves inline selection geometry after applying the active root
    /// placement to the snapped text origin, before adding DirectWrite's
    /// relative region offsets and crossing the WinRT Rect projection boundary.
    /// The ordinary range API remains root-local for painting and clipping.
    /// </summary>
    internal IEnumerable<Rect> GetLogicalRangeRects(DocumentRange range)
        => InlineRangeGeometry.GetLogicalRangeRects(this, range);

    /// <summary>
    /// Resolves the visual caret edge for a logical document position. Asking
    /// DirectWrite for the caret is essential for mixed bidi text: the visual
    /// leading/trailing edge cannot be inferred from the paragraph direction.
    /// </summary>
    internal bool TryGetCaretPoint(
        DocumentPosition position,
        bool rangeStart,
        out Point point)
        => InlineRangeGeometry.TryGetCaretPoint(this, position, rangeStart, out point);

    internal bool TryGetLogicalCaretPoint(
        DocumentPosition position,
        bool rangeStart,
        out Point point)
        => InlineRangeGeometry.TryGetLogicalCaretPoint(this, position, rangeStart, out point);

    internal IEnumerable<Rect> GetBufferRangeRects(int from, int length)
        => InlineRangeGeometry.GetBufferRangeRects(this, from, length);

    internal IEnumerable<Rect> GetBufferRangeLineRects(int from, int length)
        => InlineRangeGeometry.GetBufferRangeLineRects(this, from, length);

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        => GetRangeRects(range);

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport)
        => InlineTextPainter.PaintSelectionForeground(this, ds, range, color, viewport);

    public void PaintLinkStateForeground(CanvasDrawingSession ds, LinkRun link, bool focused, Rect viewport)
        => InlineTextPainter.PaintLinkStateForeground(this, ds, link, focused, viewport);

    /// <summary>
    /// Returns the bounding rectangle in document coordinates for the run at
    /// <paramref name="inlineIndex"/>. Used to position the keyboard-focus ring.
    /// Returns an empty rect if the run is not found or the layout is not built.
    /// </summary>
    public Rect GetRunRect(int inlineIndex)
        => InlineRangeGeometry.GetRunRect(this, inlineIndex);

    /// <summary>
    /// For each <see cref="InlineEmbedRun"/>, returns the rectangle in document
    /// coordinates where its hosted WinUI element should be placed on the
    /// overlay canvas.
    /// </summary>
    public IEnumerable<(InlineEmbedRun Run, Rect Rect)> EnumerateEmbedRects()
        => InlineObjectGeometry.EnumerateEmbedRects(this);

    /// <summary>
    /// Returns the per-run inline-image x-height cache for the current run
    /// sequence, replacing a missing or stale array. Zero entries are
    /// unresolved; negative entries record a stable fallback.
    /// </summary>
    internal float[] GetOrCreateInlineImageXHeightCache()
    {
        float[]? metrics = _inlineImageXHeightByRunIndex;
        if (metrics is null || metrics.Length != _runs.Count)
        {
            metrics = new float[_runs.Count];
            _inlineImageXHeightByRunIndex = metrics;
        }

        return metrics;
    }

    /// <summary>
    /// For each <see cref="InlineImageRun"/>, returns the image rectangle in
    /// document coordinates and syncs the backnng <see cref="ImageBox"/> bounds
    /// so lazy-loading and accessnbnlity see the same geometry as paint.
    /// </summary>
    public IEnumerable<(InlineImageRun Run, Rect Rect)> EnumerateInlineImageRects()
        => InlineObjectGeometry.EnumerateInlineImageRects(this);

    /// <summary>
    /// Returns the run hovered for the given document-coordinate point, or
    /// null if no run is hovered. Used by the control to drive link hover.
    /// </summary>
    public InlineRun? RunAt(Point point)
        => InlineTextHitTesting.RunAt(this, point);

    /// <summary>
    /// Returns the run whose laid-out character region contains
    /// <paramref name="point"/>. Unlike <see cref="RunAt"/>, this preserves
    /// glyph ownership on the trailing half of a character instead of turning
    /// the hit into the following caret position. Pointer commands target the
    /// painted object, not a text insertion point.
    /// </summary>
    internal InlineRun? RunAtVisualPoint(Point point)
        => InlineTextHitTesting.RunAtVisualPoint(this, point);

    internal bool TryGetRunBounds(InlineRun run, Point preferredPoint, out Rect bounds)
        => InlineTextHitTesting.TryGetRunBounds(this, run, preferredPoint, out bounds);

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
        => InlineTextHitTesting.IsPointInsideRunBounds(this, run, point);

    public override void Dispose()
    {
        if (!InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(this))
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
        Exception? layoutFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
            ref layout,
            layoutRetirementRecord,
            "disposing an inline text layout");
        Exception? selectionFailure = layoutsAlias
            ? ReferenceEquals(layoutRetirementRecord, selectionLayoutRetirementRecord)
                ? null
                : InlineNativeLayoutRetirement.ReleaseEmptyNativeRecord(selectionLayoutRetirementRecord)
            : InlineNativeLayoutRetirement.RetireNativeLayout(
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

    internal CanvasTextLayout EnsureSelectionLayout(Color color, ElementStyle style)
    {
        if (!InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(this))
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
            candidateLayout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                _buffer,
                formatLease.Format,
                Math.Max(1f, _lastWidth - horizontalPadding),
                float.MaxValue,
                Context.LayoutDiagnostics);
            candidateRetirementRecord.SetTemporary(0, candidateLayout);
            candidateLayout.Options = CanvasDrawTextOptions.EnableColorFont;
            // Selection is painted synchronously by a Win2D Draw callback against
            // an already-committed snapshot. A newer layout may have cancelled the
            // snapshot's build token, but cancellation must never escape Draw.
            InlineRunStyleApplier.ApplyRunStyles(this, candidateLayout, applyColors: false, observeCancellation: false);
            float selectionLayoutWidth = Math.Max(1f, _lastWidth - horizontalPadding);
            float inlineMetricScale = InlineAtomicObjectLayout.ApplyContentAwareLineSpacing(
                this,
                candidateLayout,
                style,
                selectionLayoutWidth);
            InlineAtomicObjectLayout.ApplyEmbedSpacing(
                this,
                candidateLayout,
                observeCancellation: false,
                inverseMetricScale: inlineMetricScale,
                maxInlineWidth: selectionLayoutWidth,
                computeInlineImageMetrics: false);
        }
        catch (Exception exception)
        {
            candidateFailure = exception;
        }

        if (candidateFailure is not null)
        {
            Exception? cleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                ref candidateLayout,
                candidateRetirementRecord,
                "disposing a failed inline selection-layout candidate");
            Exception combined = InlineNativeLayoutRetirement.CombineFailures(
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
                Exception? candidateCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                    ref candidateLayout,
                    candidateRetirementRecord,
                    "disposing a superseded inline selection-layout candidate");
                Exception combined = InlineNativeLayoutRetirement.CombineFailures(
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
        Exception? previousRecordFailure = InlineNativeLayoutRetirement.ReleaseEmptyNativeRecord(previousSelectionRetirementRecord);
        if (previousRecordFailure is not null)
        {
            Exception? candidateCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                ref candidateLayout,
                candidateRetirementRecord,
                "disposing an uncommitted inline selection-layout candidate");
            Exception combined = InlineNativeLayoutRetirement.CombineFailures(
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

    internal CanvasLineMetrics[]? GetLineMetricsSnapshot()
        => TryGetLineMetrics();

    internal double GetTextOriginY()
    {
        ElementStyle style = GetContainerStyle();
        return GetSnappedOrigin(style).Y;
    }

    internal CanvasLineMetrics[]? TryGetLineMetrics()
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

    internal static CanvasLineMetrics FindLineMetrics(CanvasLineMetrics[]? metrics, CanvasTextLayoutRegion region)
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

    internal bool ShouldApplyRunTextStyle(InlineRun run)
        => HasRunSpecificStyle(run);

    internal bool HasRunSpecificStyle(InlineRun run)
    {
        return (!string.IsNullOrEmpty(run.ElementKey) && run.ElementKey != ElementKey) ||
            run.StyleAliases.Count > 0 ||
            run.StyleModifierKeys.Count > 0 ||
            Context.ThemeSnapshot.HasRelativeScriptFontSizeRules &&
                (run.ScriptStyleAncestryKeys.Count > 0 || run is LinkRun { IsSuperscript: true });
    }
}
