using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Per-build context passed through layout. Provides the canvas device for text
/// metrics, the theme snapshot (pre-resolved, thread-safe), the source map writer,
/// and a block-index counter.
/// </summary>
internal sealed class MarkdownLayoutContext
{
    private CancellationToken _cancellationToken;
    private SafeHtmlAlignment _safeHtmlBlockAlignment;
    private SafeHtmlBlockScopeTracker? _safeHtmlNestedScopes;
    private int? _safeHtmlInlineFlowScopeId;
    private bool _safeHtmlInlineFlowContinuesPrevious;
    private readonly LayoutContextRetirementLedger _retirements = new();
    private readonly LayoutNavigationTargets _navigation = new();
    private MarkdownLayoutConstructionScope? _constructionScope;
    private Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int>? _childInlineBuilder;
    private Func<Block, BlockBox?>? _childBlockBuilder;
    private MarkdownSourceProjection? _sourceProjection;
    private MarkdownSourceProjection? _safeHtmlSourceProjection;

    // Reuse the owning builder for structural children of extension-owned
    // containers. Like the inline callback, this must not survive construction
    // and retain the builder or semantic document in a published layout context.
    internal LayoutChildBlockBuildScope PushChildBlockBuilder(Func<Block, BlockBox?> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var previous = _childBlockBuilder;
        _childBlockBuilder = builder;
        return new LayoutChildBlockBuildScope(this, previous);
    }

    internal void RestoreChildBlockBuilder(Func<Block, BlockBox?>? previous) => _childBlockBuilder = previous;

    internal bool TryBuildChildBlock(Block block, out BlockBox? result)
    {
        result = null;
        if (_childBlockBuilder is not { } builder)
            return false;
        ArgumentNullException.ThrowIfNull(block);
        result = builder(block);
        return true;
    }

    // Optional built-in container renderers must use the owning layout build's
    // semantic extension fragments, not a second reduced inline implementation.
    // Scope this callback to construction so contexts do not retain a builder
    // or its semantic document after publication, failure or cancellation.
    internal LayoutChildInlineBuildScope PushChildInlineBuilder(
        Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int> builder)
    {
        var previous = _childInlineBuilder;
        _childInlineBuilder = builder;
        return new LayoutChildInlineBuildScope(this, previous);
    }

    internal void RestoreChildInlineBuilder(
        Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int>? previous) =>
        _childInlineBuilder = previous;

    internal bool TryAppendChildInlines(
        InlineContainerBox box, ContainerInline inlines,
        Func<Inline, bool>? skipFirstIf, int inheritedAliasStart)
    {
        if (_childInlineBuilder is not { } builder)
            return false;
        if (!ReferenceEquals(box.Context, this))
            throw new InvalidOperationException("Child inline construction must use its owning layout context.");
        builder(box, inlines, skipFirstIf, inheritedAliasStart);
        return true;
    }

    /// <summary>
    /// Initializes a layout context. Advanced renderer authors receive this object
    /// on a background layout thread and must not use it to touch WinUI objects.
    /// </summary>
    public MarkdownLayoutContext(
        ICanvasResourceCreator resourceCreator,
        ThemeSnapshot themeSnapshot,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry,
        FlowDirection flowDirection,
        DispatcherQueue? dispatcher = null,
        string? language = null)
    {
        ResourceCreator = resourceCreator;
        ThemeSnapshot = themeSnapshot;
        SourceMap = sourceMap;
        Registry = registry;
        FlowDirection = flowDirection;
        Dispatcher = dispatcher;
        Language = string.IsNullOrWhiteSpace(language)
            ? System.Globalization.CultureInfo.CurrentUICulture.Name
            : language.Trim();
        try
        {
            Culture = string.IsNullOrWhiteSpace(Language)
                ? System.Globalization.CultureInfo.InvariantCulture
                : System.Globalization.CultureInfo.GetCultureInfo(Language);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            Culture = System.Globalization.CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Canvas resource creator used for text and graphics resources.</summary>
    public readonly ICanvasResourceCreator ResourceCreator;

    /// <summary>Resolved theme snapshot for the current layout pass.</summary>
    public readonly ThemeSnapshot ThemeSnapshot;

    /// <summary>Source map being populated by the current layout pass.</summary>
    public readonly MarkdownSourceMap SourceMap;

    /// <summary>Extension registry used by the current layout pass.</summary>
    public readonly MarkdownExtensionRegistry Registry;

    internal SafeHtmlAlignment SafeHtmlBlockAlignment => _safeHtmlBlockAlignment;

    internal MarkdownSourceProjection? SourceProjection => _sourceProjection;

    internal MarkdownSourceProjection? SafeHtmlSourceProjection => _safeHtmlSourceProjection;

    internal SafeHtmlBlockScopeTracker? SafeHtmlNestedScopes => _safeHtmlNestedScopes;

    internal int? SafeHtmlInlineFlowScopeId => _safeHtmlInlineFlowScopeId;

    internal bool SafeHtmlInlineFlowContinuesPrevious => _safeHtmlInlineFlowContinuesPrevious;

    /// <summary>Flow direction used for text layout.</summary>
    public readonly FlowDirection FlowDirection;

    /// <summary>BCP-47 language used by DirectWrite text shaping for this pass.</summary>
    public readonly string Language;

    /// <summary>Culture resolved once from <see cref="Language"/> for this layout pass.</summary>
    internal readonly System.Globalization.CultureInfo Culture;
    /// <summary>UI-thread dispatcher used to marshal async load completions back
    /// to the thread that owns the canvas. May be null in unit tests.</summary>
    public readonly DispatcherQueue? Dispatcher;

    /// <summary>
    /// Receives asynchronous retirement barriers from boxes discarded before
    /// snapshot publication. The host uses this to include failed-build work
    /// in its normal shutdown drain without blocking the layout worker.
    /// </summary>
    internal Action<Task>? RetirementSink { get; init; }

    /// <summary>Registers failed-build native retirement without waiting on the layout worker.</summary>
    internal void RegisterRetirement(Task retirementTask)
    {
        ArgumentNullException.ThrowIfNull(retirementTask);
        _retirements.Register(retirementTask, RetirementSink);
    }

    /// <summary>
    /// Awaits all pending retirement registered by this construction context.
    /// The owner calls this outside snapshot/layout locks before completing the
    /// corresponding pipeline task, closing teardown epoch races.
    /// </summary>
    internal Task DrainOwnedRetirementsAsync(Exception? primaryFailure = null)
        => _retirements.DrainAsync(primaryFailure);

    /// <summary>Optional diagnostic-only timing sink for the current immutable layout build.</summary>
    internal MarkdownLayoutDiagnostics? LayoutDiagnostics { get; init; }

    /// <summary>
    /// Effective rasterization scale of the host control (matches
    /// <c>XamlRoot.RasterizationScale</c>: 1.0 at 100%, 1.5 at 150%, 2.0
    /// at 200%, etc.). Used by raster-fallback image paths (e.g.
    /// <c>SvgSkiaRasterizer</c>) to render at device-pixel resolution so
    /// the result is crisp on high-DPI displays. Defaults to 1.0 — paint
    /// is unchanged at the standard scale and in unit tests.
    /// </summary>
    public double RasterizationScale { get; init; } = 1.0;

    /// <summary>Cancellation token for the current background layout pass.</summary>
    public CancellationToken CancellationToken
    {
        get
        {
            // The lazy-pass token is linked to the owning pipeline in normal
            // control operation, but retaining this check also protects direct
            // relayout callers and test hosts that supply an independent token.
            _cancellationToken.ThrowIfCancellationRequested();
            return PassCancellation.GetEffectiveToken(_cancellationToken);
        }
        init => _cancellationToken = value;
    }

    /// <summary>
    /// The token of the measure pass that currently uses this context. A pass
    /// pushes its token here so that boxes built in this context observe it.
    /// </summary>
    internal LayoutPassCancellation PassCancellation { get; } = new();

    /// <summary>
    /// The root-placement translation of the query, paint or measure that is
    /// reading boxes built in this context.
    /// </summary>
    internal LayoutPlacementTransformContext PlacementTransform { get; } = new();

    internal void SetCancellationToken(CancellationToken cancellationToken) =>
        _cancellationToken = cancellationToken;

    // Placement candidates borrow the immutable layout environment, never the
    // previous pass's mutable registries, cancellation or retirement ledger.
    // Future image reactivation still uses the captured resolver and policy.
    internal MarkdownLayoutContext CreateRasterPlacementContext(
        CancellationToken imageCancellationToken,
        object imagePreparationOwner,
        Action<Task>? retirementSink,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable)
    {
        ArgumentNullException.ThrowIfNull(imagePreparationOwner);
        return new(ResourceCreator, ThemeSnapshot, SourceMap, Registry, FlowDirection, Dispatcher, Language)
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = imageCancellationToken,
            RetirementSink = retirementSink,
            ImageUnavailable = imageUnavailable,
            RasterizationScale = RasterizationScale,
            IsCodeBlockCopyEnabled = IsCodeBlockCopyEnabled,
            CodeBlockLineNumberMode = CodeBlockLineNumberMode,
            CodeBlockWrappingMode = CodeBlockWrappingMode,
            CodeBlockChromeMode = CodeBlockChromeMode,
            IsTaskListEditingEnabled = IsTaskListEditingEnabled,
            CommandProvider = CommandProvider,
            StringProvider = StringProvider,
            ImageResolver = ImageResolver,
            PerformanceSession = PerformanceSession,
            PerformanceDocumentOwner = null,
            ImagePreparationOwner = imagePreparationOwner,
            SvgRenderer = SvgRenderer,
            RasterDecoder = RasterDecoder,
            ImageBaseUri = ImageBaseUri,
            ImageDocumentPath = ImageDocumentPath,
            ImageDocumentSource = ImageDocumentSource,
            AllowThirdPartyRemoteImages = AllowThirdPartyRemoteImages,
            DisclosureStates = DisclosureStates,
            ImageCarryIdentity = ImageCarryIdentity,
            CompletedImageCarry = CompletedImageCarry,
        };
    }

    /// <summary>
    /// Begins synchronous construction ownership for one layout pass. Builders
    /// register newly-created block roots here and remove them only after the
    /// parent successfully accepts ownership. The context is single-build at a
    /// time, matching its other mutable per-pass registries.
    /// </summary>
    internal MarkdownLayoutConstructionScope BeginConstructionScope()
    {
        if (_constructionScope is not null)
            throw new InvalidOperationException("A layout construction scope is already active.");

        return _constructionScope = new MarkdownLayoutConstructionScope(this);
    }

    internal void RegisterConstructionBlock(BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _constructionScope?.Register(block);
    }

    internal void RegisterConstructionResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _constructionScope?.Register(resource);
    }

    internal void AdoptConstructionBlock(BlockBox child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _constructionScope?.Adopt(child);
    }

    internal void AdoptConstructionResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _constructionScope?.Adopt(resource);
    }

    internal void ForgetConstructionBlock(BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _constructionScope?.Forget(block);
    }

    /// <summary>Disposes a deliberately omitted build object and observes its async retirement.</summary>
    internal void DiscardConstructionBlock(BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ForgetConstructionBlock(block);
        var retirementTasks = new List<Task>();
        try
        {
            block.CollectRetirementTasks(retirementTasks);
        }
        catch (Exception exception)
        {
            retirementTasks.Add(Task.FromException(exception));
        }

        try
        {
            block.Dispose();
        }
        catch (Exception exception)
        {
            retirementTasks.Add(Task.FromException(exception));
        }

        foreach (Task task in retirementTasks)
        {
            try
            {
                RegisterRetirement(task);
            }
            catch
            {
                // Discard is best-effort and must not replace a primary build failure.
            }
        }
    }

    internal void EndConstructionScope(MarkdownLayoutConstructionScope scope)
    {
        if (ReferenceEquals(_constructionScope, scope))
            _constructionScope = null;
    }

    /// <summary>
    /// Cancellation token for asynchronous image resolution owned by the
    /// renderer's loaded lifetime. Image work must survive routine layout
    /// rebuild cancellation so a resize cannot strand a committed image box
    /// in a permanently-started state.
    /// </summary>
    public CancellationToken ImageCancellationToken { get; init; }

    /// <summary>True when code block copy buttons should be included in layout metadata.</summary>
    public bool IsCodeBlockCopyEnabled { get; init; } = true;

    /// <summary>Line-number policy for code blocks.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode { get; init; } = CodeBlockLineNumberMode.AutoMultiline;

    /// <summary>Wrapping policy for code blocks.</summary>
    public CodeBlockWrappingMode CodeBlockWrappingMode { get; init; } = CodeBlockWrappingMode.NoWrap;

    /// <summary>Chrome policy for code blocks.</summary>
    public CodeBlockChromeMode CodeBlockChromeMode { get; init; } = CodeBlockChromeMode.Enhanced;

    /// <summary>True when task-list markers may invoke an explicit host command.</summary>
    internal bool IsTaskListEditingEnabled { get; init; }

    /// <summary>Host command provider captured for UI-thread hosted-element realization.</summary>
    internal IMarkdownCommandProvider? CommandProvider { get; init; }

    /// <summary>Refreshes keyboard and automation projections after task command availability changes.</summary>
    internal Action? TaskCommandAvailabilityChanged { get; init; }

    /// <summary>Host string provider captured for UI-thread hosted-element realization.</summary>
    internal IMarkdownStringProvider? StringProvider { get; init; }

    /// <summary>Optional host-specific image resolver used before public URI loading.</summary>
    public IMarkdownImageResolver? ImageResolver { get; init; }

    /// <summary>Optional host-owned preparation policy captured for this layout generation.</summary>
    internal IMarkdownPerformanceSessionInternal? PerformanceSession { get; init; }

    /// <summary>
    /// True only while a builder is constructing the actual lazy snapshot
    /// whose performance session owns vector-scene residency. A progressive
    /// session alone does not defer native resources in eager/non-lazy builds.
    /// </summary>
    internal bool UseProgressiveNativeSceneResidency { get; set; }

    /// <summary>Stable identity of the document viewport for fair CPU preparation.</summary>
    internal object? PerformanceDocumentOwner { get; init; }
    // Scene/deferred-document ownership remains tied to the document host.
    // Image admission instead uses an opaque per-control identity, so private
    // no-scene raster candidates need not retain the control as their owner.
    internal object? ImagePreparationOwner { get; init; }
    internal object? EffectiveImagePreparationOwner =>
        ImagePreparationOwner ?? PerformanceDocumentOwner;

    /// <summary>
    /// The image caches, budgets and admission gates that this layout's image
    /// boxes share. Builders take the scope from here rather than naming one.
    /// It defaults to the process scope until an engine or device scope owns it.
    /// </summary>
    internal ImageResourceScope ImageResources { get; init; } = ImageResourceScope.Shared;

    /// <summary>
    /// Shared static-SVG provider borrowed from the host control. Image boxes
    /// may own documents opened from it, but never the renderer itself.
    /// </summary>
    public IMarkdownSvgRenderer? SvgRenderer { get; init; }

    /// <summary>
    /// Shared static-raster decoder borrowed from the host control. Image boxes
    /// use it only for an explicitly supported, admitted format and never own
    /// or dispose the provider.
    /// </summary>
    public IMarkdownRasterDecoder? RasterDecoder { get; init; }

    /// <summary>Optional base URI used to resolve relative image sources.</summary>
    public Uri? ImageBaseUri { get; init; }

    /// <summary>Optional source document path used by host image resolvers.</summary>
    public string? ImageDocumentPath { get; init; }

    /// <summary>Canonical identity and repository context for the rendered document.</summary>
    public MarkdownDocumentSource? ImageDocumentSource { get; init; }

    /// <summary>True when the host explicitly allowed third-party images for this document.</summary>
    public bool AllowThirdPartyRemoteImages { get; init; }

    /// <summary>Reports blocked or unavailable image sources to the host control.</summary>
    public System.Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? ImageUnavailable { get; init; }

    /// <summary>Snapshot of user-expanded HTML disclosure state for this layout pass.</summary>
    public IReadOnlyDictionary<string, bool> DisclosureStates { get; init; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>
    /// Identity of the document and image configuration this pass lays out.
    /// Image boxes keep it so a later rebuild can tell whether their completed
    /// result still applies.
    /// </summary>
    internal Boxes.MarkdownImageCarryIdentity? ImageCarryIdentity { get; init; }

    /// <summary>
    /// Completed image results from earlier layouts of the same document, by
    /// source URL. Already filtered to <see cref="ImageCarryIdentity"/>.
    /// </summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, Boxes.CompletedImageCarry>? CompletedImageCarry { get; init; }

    /// <summary>Returns the effective state of a safe HTML disclosure.</summary>
    public bool IsDisclosureExpanded(string id, bool defaultExpanded) =>
        DisclosureStates.TryGetValue(id, out bool expanded) ? expanded : defaultExpanded;

    internal string ResolveString(string key, string fallback)
    {
        string localizedFallback = MarkdownLocalizedStrings.Resolve(key, fallback, Culture);
        try
        {
            string? value = StringProvider?.GetString(
                key,
                Culture);
            return string.IsNullOrWhiteSpace(value) ? localizedFallback : value;
        }
        catch
        {
            return localizedFallback;
        }
    }

    internal string ResolveFormattedString(string key, string fallbackFormat, params object?[] arguments)
    {
        string format = ResolveString(key, fallbackFormat);
        try
        {
            return string.Format(
                Culture,
                format,
                arguments);
        }
        catch (FormatException)
        {
            return string.Format(
                Culture,
                fallbackFormat,
                arguments);
        }
    }

    /// <summary>Returns the next one-based block index for a custom block.</summary>
    public int NextBlockIndex() => ++_blockIndex;
    private int _blockIndex;

    /// <summary>
    /// Captures the upper bound of IDs issued by this layout context. IDs may
    /// have holes when optional or custom construction paths do not publish a
    /// box, so consumers must not infer a count from this value.
    /// </summary>
    internal int GeneratedBlockIndexUpperBound => _blockIndex;

    private readonly List<string> _styleContextKeys = new();
    private readonly List<string> _styleAliasKeys = new();
    private int _listDepth;

    internal int ListDepth => _listDepth;
    internal int StyleAliasCount => _styleAliasKeys.Count;

    internal IReadOnlyList<string> CreateStyleContextSnapshot()
        => _styleContextKeys.Count == 0 ? Array.Empty<string>() : _styleContextKeys.ToArray();

    internal IReadOnlyList<string> CreateStyleAliasSnapshot()
        => _styleAliasKeys.Count == 0 ? Array.Empty<string>() : _styleAliasKeys.ToArray();

    internal IReadOnlyList<string> CreateStyleAliasSnapshotFrom(int startIndex)
    {
        startIndex = Math.Clamp(startIndex, 0, _styleAliasKeys.Count);
        int count = _styleAliasKeys.Count - startIndex;
        if (count == 0)
            return Array.Empty<string>();

        var result = new string[count];
        _styleAliasKeys.CopyTo(startIndex, result, 0, count);
        return result;
    }

    internal LayoutStyleScope PushStyleContext(string key)
    {
        LayoutStyleScope scope = CaptureStyleScope();
        if (!string.IsNullOrWhiteSpace(key))
            _styleContextKeys.Add(key);
        return scope;
    }

    internal LayoutStyleScope PushListDepth()
    {
        LayoutStyleScope scope = CaptureStyleScope();
        _listDepth++;
        _styleContextKeys.Add(MarkdownElementKeys.ListDepth(_listDepth));
        return scope;
    }

    internal LayoutStyleScope PushMarkdownAttributes(IMarkdownObject markdownObject)
    {
        LayoutStyleScope scope = CaptureStyleScope();
        AddMarkdownAttributeAliases(markdownObject, _styleAliasKeys);
        return scope;
    }

    internal LayoutStyleScope PushStyleAliases(IReadOnlyList<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        LayoutStyleScope scope = CaptureStyleScope();
        for (int index = 0; index < aliases.Count; index++)
        {
            string alias = aliases[index];
            if (!string.IsNullOrWhiteSpace(alias))
                _styleAliasKeys.Add(alias);
        }

        return scope;
    }

    internal SafeHtmlBlockRenderScope PushSafeHtmlBlockContext(
        SafeHtmlAlignment alignment,
        SafeHtmlBlockScopeTracker? nestedScopes,
        int? inlineFlowScopeId,
        bool inlineFlowContinuesPrevious = false)
    {
        SafeHtmlAlignment previousAlignment = _safeHtmlBlockAlignment;
        SafeHtmlBlockScopeTracker? previousNestedScopes = _safeHtmlNestedScopes;
        int? previousInlineFlowScopeId = _safeHtmlInlineFlowScopeId;
        bool previousInlineFlowContinuesPrevious = _safeHtmlInlineFlowContinuesPrevious;
        _safeHtmlBlockAlignment = alignment;
        _safeHtmlNestedScopes = nestedScopes;
        _safeHtmlInlineFlowScopeId = inlineFlowScopeId;
        _safeHtmlInlineFlowContinuesPrevious = inlineFlowContinuesPrevious;
        return new SafeHtmlBlockRenderScope(
            this,
            previousAlignment,
            previousNestedScopes,
            previousInlineFlowScopeId,
            previousInlineFlowContinuesPrevious);
    }

    /// <summary>
    /// Scopes a transformed Markdown fragment's source mapping to the synchronous
    /// construction pass that consumes its already-parsed nodes.
    /// </summary>
    internal MarkdownSourceProjectionScope PushSourceProjection(MarkdownSourceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        MarkdownSourceProjection? previous = _sourceProjection;
        _sourceProjection = projection;
        return new MarkdownSourceProjectionScope(this, previous, safeHtml: false);
    }

    /// <summary>
    /// Scopes a composed raw-HTML line projection. Its coordinates are local to
    /// the exact string handed to SafeHtmlParser, not to the Markdig AST.
    /// </summary>
    internal MarkdownSourceProjectionScope PushSafeHtmlSourceProjection(MarkdownSourceProjection? projection)
    {
        MarkdownSourceProjection? previous = _safeHtmlSourceProjection;
        _safeHtmlSourceProjection = projection;
        return new MarkdownSourceProjectionScope(this, previous, safeHtml: true);
    }

    internal SourceSpan MapSafeHtmlSourceRange(int localStart, int localLength)
        => _safeHtmlSourceProjection?.MapRangeOrEmpty(localStart, localLength)
            ?? new SourceSpan(localStart, localLength);

    internal void RestoreSourceProjection(MarkdownSourceProjection? projection, bool safeHtml)
    {
        if (safeHtml)
            _safeHtmlSourceProjection = projection;
        else
            _sourceProjection = projection;
    }

    internal void RestoreSafeHtmlBlockContext(
        SafeHtmlAlignment alignment,
        SafeHtmlBlockScopeTracker? nestedScopes,
        int? inlineFlowScopeId,
        bool inlineFlowContinuesPrevious)
    {
        _safeHtmlBlockAlignment = alignment;
        _safeHtmlNestedScopes = nestedScopes;
        _safeHtmlInlineFlowScopeId = inlineFlowScopeId;
        _safeHtmlInlineFlowContinuesPrevious = inlineFlowContinuesPrevious;
    }

    private LayoutStyleScope CaptureStyleScope() =>
        new(this, _styleContextKeys.Count, _styleAliasKeys.Count, _listDepth);

    internal void RegisterMarkdownAttributes(IMarkdownObject markdownObject, int blockIndex)
    {
        var attrs = HtmlAttributesExtensions.TryGetAttributes(markdownObject);
        var id = attrs?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            RegisterFragmentTarget(id, blockIndex);
    }

    private static void AddMarkdownAttributeAliases(IMarkdownObject markdownObject, List<string> aliases)
    {
        var attrs = HtmlAttributesExtensions.TryGetAttributes(markdownObject);
        if (attrs is null)
            return;

        if (attrs.Classes is not null)
        {
            foreach (var @class in attrs.Classes)
            {
                if (!string.IsNullOrWhiteSpace(@class))
                    aliases.Add(MarkdownElementKeys.Class(@class));
            }
        }

        if (!string.IsNullOrWhiteSpace(attrs.Id))
            aliases.Add(MarkdownElementKeys.Id(attrs.Id));
    }

    internal void RestoreStyleState(int contextCount, int aliasCount, int listDepth)
    {
        if (_styleContextKeys.Count > contextCount)
            _styleContextKeys.RemoveRange(contextCount, _styleContextKeys.Count - contextCount);
        if (_styleAliasKeys.Count > aliasCount)
            _styleAliasKeys.RemoveRange(aliasCount, _styleAliasKeys.Count - aliasCount);
        _listDepth = listDepth;
    }

    /// <summary>
    /// Throws when a background-layout-only embed callback is invoked from the
    /// UI dispatcher thread. This catches factories that would otherwise touch
    /// WinUI APIs during layout and risk deadlocks or frame stalls.
    /// </summary>
    public void ThrowIfEmbedLayoutCallbackIsOnUiThread(string methodName)
    {
        if (Dispatcher?.HasThreadAccess != true)
            return;

        throw new InvalidOperationException(
            $"IMarkdownEmbedFactory.{methodName} must run on the background layout thread and must not touch WinUI APIs. " +
            "Move WinUI work to CreateBlock or RecycleBlock.");
    }

    // ---- Footnote and fragment registry ----
    // Written by FootnoteRenderer (def) and LayoutBuilder (ref) during build;
    // read by MarkdownRendererControl via LayoutSnapshot after build completes.

    /// <summary>Records the block index of the definition for footnote <paramref name="order"/>.</summary>
    public void RegisterFootnoteDef(int order, int blockIndex) => _navigation.RegisterFootnoteDef(order, blockIndex);

    /// <summary>Records the block index of the paragraph containing the inline citation for footnote <paramref name="order"/>.</summary>
    public void RegisterFootnoteRef(int order, int blockIndex) => _navigation.RegisterFootnoteRef(order, blockIndex);

    /// <summary>Returns block index of the footnote definition, or null.</summary>
    public int? GetFootnoteDefBlock(int order) => _navigation.GetFootnoteDefBlock(order);

    /// <summary>Returns block index of the inline citation paragraph, or null.</summary>
    public int? GetFootnoteRefBlock(int order) => _navigation.GetFootnoteRefBlock(order);

    /// <summary>Snapshots the registry into plain dictionaries (safe to read off the build thread).</summary>
    public (IReadOnlyDictionary<int,int> Defs, IReadOnlyDictionary<int,int> Refs) SnapshotFootnoteRegistry()
        => _navigation.SnapshotFootnoteRegistry();

    /// <inheritdoc cref="LayoutNavigationTargets.GetOrCreateFootnoteOrder"/>
    public int GetOrCreateFootnoteOrder(Footnote footnote, int fallbackHint = 0)
        => _navigation.GetOrCreateFootnoteOrder(footnote, fallbackHint);

    internal void RegisterFragmentTarget(string id, int blockIndex) => _navigation.RegisterFragmentTarget(id, blockIndex);

    internal IReadOnlyDictionary<string, int> SnapshotFragmentTargets() => _navigation.SnapshotFragmentTargets();
}
