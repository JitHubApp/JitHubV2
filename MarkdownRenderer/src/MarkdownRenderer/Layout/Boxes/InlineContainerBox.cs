using System;
using System.Collections.Generic;
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
    internal int PrivateRasterPlacementTextLength => _sequence.TextLength;
    internal int PrivateRasterPlacementForegroundSpanCount => _foregroundSpans.Count;

    // Owned by value and mutated in place; never copy it out of this field.
    private InlineRunSequence _sequence = new();
    internal SingletonMutableList<InlineRun> RunList => _sequence.Runs;
    internal List<(InlineImageRun Run, int Offset)>? InlineImageEntries => _sequence.ImageEntries;
    internal List<(InlineEmbedRun Run, int Offset)>? InlineEmbedEntries => _sequence.EmbedEntries;
    internal bool HasAtomicInlineObject => _sequence.HasAtomicInlineObject;
    internal int RunTextLength => _sequence.TextLength;
    // Owned by value and mutated in place; never copy it out of this field.
    private InlineRunStyleCache _runStyleCache;
    internal List<string[]?>? EffectiveRunAliasOverrides => _runStyleCache.AliasOverrides;
    internal List<ElementStyle?>? ResolvedRunStyles => _runStyleCache.RunStyles;
    internal List<ElementStyle[]?>? ResolvedLinkInlineSpanStyles => _runStyleCache.LinkSpanStyles;
    internal int ResolvedRunStyleCount => _runStyleCache.GetRunStyleCount(RunList.Count);
    internal ElementStyle? GetCachedRunStyle(int index) => _runStyleCache.GetCachedRunStyle(index, RunList.Count);
    public readonly string ElementKey;
    // Owned by value and mutated in place; never copy it out of this field.
    private InlineTextLayoutState _textLayout = new();
    internal CanvasTextLayout? TextLayout => _textLayout.Layout;
    internal NativeResourceRetirementRecord? TextLayoutRetirementRecord => _textLayout.RetirementRecord;
    internal ScriptBaselineGeometry ScriptBaselineGeometry => _textLayout.ScriptBaselineGeometry;
    internal CanvasLineMetrics[]? CachedLineMetrics => _textLayout.CachedLineMetrics;
    internal bool HasReadLineMetrics => _textLayout.HasReadLineMetrics;
    // Owned by value and mutated in place; never copy it out of this field.
    private InlineSelectionLayoutState _selection;
    internal CanvasTextLayout? SelectionLayout => _selection.Layout;
    internal NativeResourceRetirementRecord? SelectionLayoutRetirementRecord => _selection.RetirementRecord;
    internal Color SelectionLayoutColor => _selection.Color;
    internal float SelectionLayoutWidth => _selection.Width;
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
    internal string LayoutBuffer => _sequence.Buffer;
    internal bool IsBufferDirty => _sequence.IsBufferDirty;
    internal float LastMeasuredWidth => _textLayout.LastWidth;

    // Text-only inline boxes are the common case. Keep their vector-specific
    // counters and geometry in no per-box holder until a vector run is added.
    // This state is mutable and strictly box-owned; private placement clones
    // create their own holder rather than sharing it with the source box.
    private InlineVectorSceneState? _vectorSceneState;
    internal InlineVectorSceneState? VectorSceneStateHolder => _vectorSceneState;
    internal InlineSceneGeometry[] PendingSceneGeometry =>
        _vectorSceneState?.PendingSceneGeometry ?? Array.Empty<InlineSceneGeometry>();
    internal long CurrentSceneGeometryGeneration => _vectorSceneState?.SceneGeometryGeneration ?? 0;
    internal long PendingSceneGeometryGeneration => _vectorSceneState?.PendingSceneGeometryGeneration ?? 0;
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

    public IReadOnlyList<InlineRun> Runs => _sequence.Runs;
    internal bool HasInlineImages => _sequence.ImageEntries is not null;
    internal bool HasInlineEmbeds => _sequence.EmbedEntries is not null;
    internal IReadOnlyList<(InlineImageRun Run, int Offset)> InlineImageRuns =>
        _sequence.ImageEntries is { } runs
            ? runs
            : Array.Empty<(InlineImageRun Run, int Offset)>();
    public string? CodeLanguage { get; init; }
    internal string? StyleState { get; init; }
    internal int CodeBlockTextOffset { get; init; }
    internal int CodeBlockTextLength { get; init; }
    internal CanvasWordWrapping WordWrapping { get; init; } = CanvasWordWrapping.Wrap;
    public CanvasHorizontalAlignment TextAlignment { get; set; } = CanvasHorizontalAlignment.Left;
    internal bool HasMeasuredLayout => _textLayout.Layout is not null;
    internal bool HasDeferredSceneMarker => _vectorSceneState?.HasDeferredSceneMarker ?? false;
    internal int VectorSceneRunCount => _vectorSceneState?.VectorSceneRunCount ?? 0;
    internal long SceneGeometryGeneration =>
        _textLayout.Layout is not null && !_sequence.IsBufferDirty ? CurrentSceneGeometryGeneration : 0;
    internal int SceneCharacterRegionQueryCount => _vectorSceneState?.SceneCharacterRegionQueryCount ?? 0;
    internal float ContentWidth => _textLayout.Layout is null ? (float)Bounds.Width : (float)Math.Max(_textLayout.Layout.LayoutBounds.Width, _textLayout.Layout.DrawBounds.Width);
    internal bool DrawContainerChrome { get; set; } = true;
    internal bool UseContainerPadding { get; set; } = true;
    internal bool UseContainerMargin { get; set; } = true;

    internal bool TrySetMeasuredCodeBlockChunkEffectiveHeight(double effectiveHeight)
    {
        if (ElementKey != MarkdownElementKeys.CodeBlock ||
            _textLayout.Layout is null ||
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
        _nativeRetirement = nativeRetirement ?? context.RenderResources.NativeRetirement;
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
        _styleContextKeys = InlineRunStyleCache.CopyKeys(source._styleContextKeys);
        _styleAliasKeys = InlineRunStyleCache.CopyKeys(source._styleAliasKeys);
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
        // The string is immutable and safe to share, but an unmeasured source
        // may have a stale buffer. Preserve that fact so the candidate builds
        // its own buffer later, outside the source snapshot gate.
        _sequence.CopyBufferFrom(in source._sequence);
        _textLayout.CopyLastWidthFrom(in source._textLayout);
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
        _sequence.CopyAtomicObjectFlagFrom(in source._sequence);
        BlockIndex = source.BlockIndex;
        SafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
        CopyPlacementMarginsFrom(source);
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
        _sequence.Runs.Add(copiedRun);
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
            sourceAliasOverride is null ? null : InlineRunStyleCache.CopyKeys(sourceAliasOverride));
        if (source._runStyleCache.RunStyles is { } sourceRunStyles)
            _runStyleCache.AppendCopiedRunStyle(sourceRunStyles[index], runCount);
        if (copiedRun is InlineImageRun imageRun)
        {
            imageRun.Image.BlockIndex = source.BlockIndex;
            _sequence.AddImageEntry(imageRun, offset);
        }
        else if (copiedRun is InlineVectorSceneRun)
        {
            InlineVectorSceneState vectorState = _vectorSceneState ??= new InlineVectorSceneState();
            vectorState.VectorSceneRunCount++;
        }
    }

    internal void SetPrivatePlacementRunTextLength(int textLength)
        => _sequence.SetTextLength(textLength);

    internal static bool ArePrivateRasterPlacementContextsCompatible(
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext)
        => InlinePrivatePlacementPreflight.ArePrivateRasterPlacementContextsCompatible(sourceContext, targetContext);

    public void Add(InlineRun run) => Add(run, sourceProjection: null);

    internal void Add(InlineRun run, MarkdownSourceMap.SourceProjection? sourceProjection)
    {
        System.Diagnostics.Debug.Assert(BlockIndex != 0,
            "BlockIndex must be assigned before calling Add(); source-map entries will be registered under block 0 otherwise.");
        InlineVectorSceneState? vectorStateForAddedRun = run is InlineVectorSceneRun
            ? _vectorSceneState ?? new InlineVectorSceneState()
            : null;
        _sequence.Append(run, BlockIndex);
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
            run.StyleAliases.Count == 0 ? null : InlineRunStyleCache.CombineAliases(_styleAliasKeys, run.StyleAliases));
        _runStyleCache.AppendUnresolvedRunSlots();
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
        _sequence.MarkBufferDirty();
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
        if (_textLayout.Layout is not null || _selection.Layout is not null)
        {
            throw new InvalidOperationException(
                "Inline image runs can only be transferred before their container is measured.");
        }

        InlineImageRun[] detached = _sequence.RemoveImageRuns();
        if (detached.Length != 0)
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
        if (_textLayout.Layout is not null || _selection.Layout is not null)
        {
            throw new InvalidOperationException(
                "Inline image and spacer runs can only be transferred before their container is measured.");
        }

        InlineRun[] detached = _sequence.RemoveImageAndSpacerRuns();
        if (detached.Length != 0)
            RebuildAfterInlineRunTransfer();
        return detached;
    }

    private void RebuildAfterInlineRunTransfer()
    {
        _inlineImageXHeightByRunIndex = null;
        _inlineImageLineBaselineByRunIndex = null;
        _vectorSceneState?.ResetRunCounters();
        _sequence.RebuildAfterTransfer(ref _runStyleCache, ref _vectorSceneState, _styleAliasKeys);
        _vectorSceneState?.ResetGeometry();
    }

    internal void SetForegroundSpans(IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        _foregroundSpans = spans ?? Array.Empty<CodeBlockHighlightSpan>();
        if (_textLayout.Layout is not null)
            InlineRunStyleApplier.ApplyForegroundSpans(this, _textLayout.Layout);
    }

    /// <summary>
    /// Converts a <see cref="DocumentPosition"/> (which must target this box) to an
    /// absolute character index within the concatenated inline buffer.
    /// </summary>
    public int GetBufferCharOffset(DocumentPosition pos)
        => _sequence.GetBufferCharOffset(pos);

    /// <summary>
    /// Converts an absolute character index within the buffer back to a
    /// <see cref="DocumentPosition"/> targeting this box.
    /// </summary>
    public DocumentPosition GetPositionFromBufferOffset(int bufOffset)
        => _sequence.GetPositionFromBufferOffset(BlockIndex, bufOffset);

    internal void EnsureBuffer() => _sequence.EnsureBuilt(Context);

    /// <summary>
    /// Returns the start and end positions that span the word containing
    /// <paramref name="pos"/>. Unicode word characters span formatted runs,
    /// while symbols and emoji are selected as complete graphemes. Whitespace
    /// and punctuation keep the established nearest-word behavior.
    /// </summary>
    public (DocumentPosition Start, DocumentPosition End) GetWordBoundaries(DocumentPosition pos)
        => _sequence.GetWordBoundaries(Context, BlockIndex, pos);

    /// <summary>Returns positions for the very start and end of this inline container.</summary>
    public (DocumentPosition Start, DocumentPosition End) GetBlockBoundaries()
        => _sequence.GetBlockBoundaries(BlockIndex);

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

    public override float Measure(float availableWidth)
        => _textLayout.Measure(this, availableWidth);

    internal void SetMeasuredBounds(Rect bounds) => Bounds = bounds;

    internal void BuildLayoutBufferIfDirty() => _sequence.BuildIfDirty(Context);

    internal void RestoreLayoutBuffer(string buffer, bool bufferDirty)
        => _sequence.RestoreBuffer(buffer, bufferDirty);

    internal void PublishInlineImageLineBaselines(float[]? baselines)
        => _inlineImageLineBaselineByRunIndex = baselines;

    internal void RestoreSelectionLayout(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord,
        Color color,
        float width)
        => _selection.Restore(layout, retirementRecord, color, width);

    internal void DetachSelectionLayout() => _selection.Detach();

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

        (source._textLayout, prepared._textLayout) = (prepared._textLayout, source._textLayout);
        (source._selection, prepared._selection) = (prepared._selection, source._selection);
        (source.Bounds, prepared.Bounds) = (prepared.Bounds, source.Bounds);

        // Scene generation belongs to the stable source box, not the
        // temporary clone. Candidate progressive geometry is remapped to
        // source-run identities before the final gate; adopt that bounded
        // array only for the stable source and keep the candidate's prior
        // pending array from becoming another source owner.
        if (prepared._vectorSceneState is { } preparedState)
        {
            preparedState.SceneGeometryGeneration = sourceSceneGeneration;
            preparedState.PendingSceneGeometryGeneration = sourcePendingSceneGeneration;
            preparedState.PendingSceneGeometry = sourcePendingSceneGeometry;
        }
        if (source._vectorSceneState is { } sourceState)
        {
            long nextSceneGeneration = unchecked(sourceSceneGeneration + 1);
            sourceState.SceneGeometryGeneration = nextSceneGeneration == 0 ? 1 : nextSceneGeneration;
            sourceState.PendingSceneGeometryGeneration = source._usesProgressiveNativeSceneResidency
                ? sourceState.SceneGeometryGeneration
                : 0;
            sourceState.PendingSceneGeometry = adoptPreparedGeometry &&
                source._usesProgressiveNativeSceneResidency
                ? preparedGeometryForSource!
                : Array.Empty<InlineSceneGeometry>();
        }
    }

    internal SharedCanvasTextFormatCache.Lease CreateTextFormatLease(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment)
        => Context.RenderResources.TextFormats.Acquire(
            style,
            wordWrapping,
            horizontalAlignment,
            Context.FlowDirection,
            Context.Language,
            useContentAwareLineSpacing: _sequence.HasAtomicInlineObject);

    internal ElementStyle GetContainerStyle()
        => _resolvedContainerStyle ??= Context.ThemeSnapshot.GetStyle(
                ElementKey,
                _styleContextKeys,
                _styleAliasKeys,
                CodeLanguage,
                StyleState);

    internal ElementStyle GetRunStyle(InlineRun run) => _runStyleCache.GetRunStyle(this, run);

    internal ElementStyle[] GetLinkInlineSpanStyles(LinkRun link)
        => _runStyleCache.GetLinkInlineSpanStyles(this, link);

    internal Thickness GetEffectivePadding(ElementStyle style)
        => UseContainerPadding ? style.Padding : default;

    internal override void ThrowIfCancellationRequested()
        => Context.CancellationToken.ThrowIfCancellationRequested();

    internal bool HasWellFormedEffectiveRunAliases =>
        _runStyleCache.HasWellFormedAliasOverrides(RunList.Count);

    internal bool HasWellFormedResolvedLinkInlineStyleCache =>
        _runStyleCache.HasWellFormedLinkSpanStyles(RunList);

    internal string[] GetEffectiveRunAliases(int index)
        => _runStyleCache.GetEffectiveAliases(index, RunList.Count, _styleAliasKeys);

    internal string[]? GetEffectiveRunAliasOverride(int index)
        => _runStyleCache.GetAliasOverride(index, RunList.Count);

    internal void AppendEffectiveRunAliasOverride(int index, string[]? aliases)
        => _runStyleCache.AppendAliasOverride(index, RunList.Count, aliases);

    /// <summary>
    /// Returns the device-pixel-snapped text origin that paint, hit testing and
    /// every geometry query share; see <see cref="InlineTextOrigin"/>.
    /// </summary>
    internal (float X, float Y) GetSnappedOrigin(Theming.ElementStyle style)
        => InlineTextOrigin.GetSnappedOrigin(this, style, out _, out _);

    internal (float X, float Y) GetSnappedOrigin(
        Theming.ElementStyle style,
        out float finalOriginX,
        out float finalOriginY)
        => InlineTextOrigin.GetSnappedOrigin(this, style, out finalOriginX, out finalOriginY);

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
        if (metrics is null || metrics.Length != RunList.Count)
        {
            metrics = new float[RunList.Count];
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
        => InlineVectorScenePlacement.TryGetCurrentGeometry(
            _vectorSceneState,
            SceneGeometryGeneration,
            out generation,
            out geometry);

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
        => InlineVectorScenePlacement.TryCaptureCurrentGeometry(
            RunList,
            _textLayout.Layout,
            _vectorSceneState,
            SceneGeometryGeneration,
            cancellationToken,
            out generation,
            out geometry);

    internal void ReleasePendingSceneGeometry(long generation)
        => _vectorSceneState?.ReleasePendingGeometry(generation);

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
        => InlineVectorScenePlacement.TryGetRunCharacterRegions(this, start, length, out regions, out originY);

    internal bool IsPointInsideRunBounds(InlineRun run, Point point)
        => InlineTextHitTesting.IsPointInsideRunBounds(this, run, point);

    public override void Dispose()
    {
        if (!InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(this))
            throw new InvalidOperationException("Cannot detach inline text layouts without their reserved native-retirement owners.");

        var cleanup = new BlockBoxCleanupScope();
        CanvasTextLayout? layout = _textLayout.Layout;
        NativeResourceRetirementRecord? layoutRetirementRecord = _textLayout.RetirementRecord;
        CanvasTextLayout? selectionLayout = _selection.Layout;
        NativeResourceRetirementRecord? selectionLayoutRetirementRecord =
            _selection.RetirementRecord;

        // Detach all box/context-owned state before publishing any failed
        // native slots. The retirement registry must retain only the layouts,
        // never this box, its runs, or the document context.
        _textLayout.DetachLayout();
        _selection.Detach();
        _vectorSceneState?.ResetGeometry();
        _textLayout.InvalidateLineMetrics();

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

        foreach (InlineRun run in RunList)
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
        foreach (InlineRun run in RunList)
        {
            if (run is InlineImageRun image)
                cleanup.TryCollectRetirementTasks(image.Image, pendingTasks);
        }

        cleanup.ThrowIfAny("collecting inline-container retirement tasks");
    }

    internal CanvasTextLayout EnsureSelectionLayout(Color color, ElementStyle style)
        => _selection.Ensure(this, color, style);

    internal CanvasLineMetrics[]? GetLineMetricsSnapshot()
        => TryGetLineMetrics();

    internal double GetTextOriginY()
    {
        ElementStyle style = GetContainerStyle();
        return GetSnappedOrigin(style).Y;
    }

    internal CanvasLineMetrics[]? TryGetLineMetrics() => _textLayout.TryGetLineMetrics();

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
