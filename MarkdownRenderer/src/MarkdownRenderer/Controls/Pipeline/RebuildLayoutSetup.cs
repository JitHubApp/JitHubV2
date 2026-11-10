using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using MarkdownRenderer.Theming.Internal.ResourceDictionaries;
using Microsoft.Graphics.Canvas;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The setup stage of a rebuild: composes the presentation registry, prepares
/// the image resolver scope or prefetch, snapshots the theme, and creates the
/// layout context and builder the layout stage runs.
/// </summary>
internal sealed class RebuildLayoutSetup
{
    private readonly IRebuildLayoutSetupHost _host;
    private readonly MarkdownRendererDiagnosticOptions _diagnostics;
    private readonly SnapshotRetirementTracker _retirements;
    private readonly ImageSourcePrefetch _imagePrefetch;
    private readonly CompletedImageCarryLedger _completedImageCarry;
    private readonly ImagePlanSet _imagePlans;
    private readonly EmbedRealization _embeds;
    private readonly FocusableItemsPublication _focusableItems;
    private readonly object _imagePreparationOwner;
    private readonly MarkdownExtensionRegistry _fallbackRegistry;
    // The control's resource-key discovery, reused across its theme snapshots.
    private readonly RelevantResourceKeyCache<Microsoft.UI.Xaml.ResourceDictionary> _themeResourceKeys =
        ThemeResourceLookup.CreateRelevantResourceKeyCache();

    internal RebuildLayoutSetup(
        IRebuildLayoutSetupHost host,
        MarkdownRendererDiagnosticOptions diagnostics,
        SnapshotRetirementTracker retirements,
        ImageSourcePrefetch imagePrefetch,
        CompletedImageCarryLedger completedImageCarry,
        ImagePlanSet imagePlans,
        EmbedRealization embeds,
        FocusableItemsPublication focusableItems,
        object imagePreparationOwner,
        MarkdownExtensionRegistry fallbackRegistry)
    {
        _fallbackRegistry = fallbackRegistry;
        _host = host;
        _diagnostics = diagnostics;
        _retirements = retirements;
        _imagePrefetch = imagePrefetch;
        _completedImageCarry = completedImageCarry;
        _imagePlans = imagePlans;
        _embeds = embeds;
        _focusableItems = focusableItems;
        _imagePreparationOwner = imagePreparationOwner;
    }

    /// <summary>
    /// Forgets this control's cached resource-key discovery so the next theme
    /// snapshot rediscovers same-count key substitutions.
    /// </summary>
    internal void InvalidateThemeResourceKeys() => _themeResourceKeys.Invalidate();

    /// <summary>
    /// Creates the plan for laying out <paramref name="parse"/> at
    /// <paramref name="width"/>. The plan's context owns every retirement the
    /// build registers from here on.
    /// </summary>
    internal RebuildLayoutPlan CreatePlan(
        RebuildSourceInputs source,
        RebuildLayoutInputs inputs,
        RebuildParseResult parse,
        RebuildReason reason,
        float width,
        CancellationToken ct)
    {
        ParsedMarkdown parsed = parse.Parsed;
        MarkdownRenderer.Document.MarkdownDocument semanticDocument = parse.SemanticDocument;
        // Presentation registrations travel with reusable documents/engines.
        // Keep the legacy control registry only as the fallback parser/layout
        // source so an engine-produced document cannot silently lose its
        // custom renderers when it reaches the WinUI adapter.
        MarkdownExtensionRegistry baseRegistry =
            semanticDocument.PresentationConfiguration as MarkdownExtensionRegistry ??
            source.Engine?.PresentationConfiguration as MarkdownExtensionRegistry ??
            _fallbackRegistry;
        MarkdownExtensionRegistry layoutRegistry =
            MarkdownExtensionRegistry.ComposePresentation(baseRegistry, source.ExtensionRegistry);

        IMarkdownPerformanceSessionInternal? performanceSession = source.PerformanceSession;
        IMarkdownImageResolver? imageResolver = PrepareImageResolver(
            parsed, layoutRegistry, inputs, ref performanceSession);
        (ThemeSnapshot theme, double themeMilliseconds) = CreateThemeSnapshot(
            inputs, semanticDocument, layoutRegistry, performanceSession, ct);
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing the canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        MarkdownLayoutDiagnostics? layoutDiagnostics =
            performanceSession?.IsLayoutDiagnosticCaptureEnabled == true ||
            _diagnostics.CaptureLayoutStages
                ? new MarkdownLayoutDiagnostics()
                : null;
        MarkdownLayoutContext context = CreateContext(
            parsed,
            layoutRegistry,
            inputs,
            imageResolver,
            performanceSession,
            theme,
            device,
            reason,
            layoutDiagnostics,
            // Views that are not given an engine render within the default
            // engine's budgets, as they parse with it.
            (source.Engine ?? MarkdownEngine.Default).RenderResources,
            ct);
        return new RebuildLayoutPlan
        {
            Source = source,
            Inputs = inputs,
            Parse = parse,
            Width = width,
            BaseRegistry = baseRegistry,
            PresentationRegistryRevision = baseRegistry.Revision,
            ExtensionRegistryRevision = source.ExtensionRegistry?.Revision ?? 0,
            LayoutRegistry = layoutRegistry,
            ImageResolver = imageResolver,
            PerformanceSession = performanceSession,
            Theme = theme,
            ThemeSnapshotMilliseconds = themeMilliseconds,
            Device = device,
            Context = context,
            LayoutDiagnostics = layoutDiagnostics,
        };
    }

    /// <summary>
    /// Creates the builder for <paramref name="plan"/>, keyed for the shared
    /// layout metrics cache when an engine parsed the document.
    /// </summary>
    internal LayoutBuilder CreateBuilder(RebuildLayoutPlan plan)
    {
        RebuildLayoutInputs inputs = plan.Inputs;
        MarkdownEngine? engine = plan.Source.Engine;
        SharedLayoutMetricsKey? sharedLayoutMetricsKey = engine is null
            ? null
            : new SharedLayoutMetricsKey(
                plan.Parse.Parsed.SourceText,
                plan.Parse.SemanticDocument,
                widthInSixtyFourthDips: (int)Math.Clamp(
                    Math.Round(plan.Width * 64d),
                    1d,
                    int.MaxValue),
                typographyFingerprint: plan.Theme.LayoutFingerprint,
                flowDirection: (int)inputs.FlowDirection,
                codeWrappingMode: (int)inputs.WrappingMode,
                lineNumberMode: (int)inputs.LineNumberMode,
                codeCopyEnabled: inputs.IsCodeBlockCopyEnabled,
                taskEditingEnabled: inputs.IsTaskListEditingEnabled,
                rasterScaleInThousandths: (int)Math.Clamp(
                    Math.Round(inputs.RasterScale * 1_000d),
                    1d,
                    int.MaxValue),
                language: inputs.Language,
                registryRevision: plan.LayoutRegistry.Revision,
                disclosureFingerprint: DisclosureFingerprint.Compute(inputs.DisclosureStates),
                imageContext: new SharedLayoutImageContext(
                    inputs.ImageBaseUri?.OriginalString,
                    inputs.ImageDocumentPath,
                    inputs.AllowThirdPartyRemoteImages,
                    inputs.ImageDocumentSource),
                inputs.StyleSheet,
                plan.LayoutRegistry,
                inputs.EmbedFactory,
                inputs.HostedElementFactory,
                plan.ImageResolver,
                inputs.SvgRenderer,
                inputs.RasterDecoder,
                inputs.CommandProvider,
                inputs.StringProvider);
        return new LayoutBuilder(
            plan.Context,
            inputs.EmbedFactory,
            enableDeclarativeHostedElements: inputs.HostedElementFactory is not null,
            plan.Parse.SemanticDocument,
            _embeds.CopyHostedElementFallbacks(),
            engine?.LayoutMetrics,
            sharedLayoutMetricsKey);
    }

    /// <summary>
    /// Scopes the host's resolver to this document through the performance
    /// session, or queues the document's images for prefetch when there is no
    /// scope. Clears <paramref name="performanceSession"/> when it declines.
    /// </summary>
    private IMarkdownImageResolver? PrepareImageResolver(
        ParsedMarkdown parsed,
        MarkdownExtensionRegistry layoutRegistry,
        RebuildLayoutInputs inputs,
        ref IMarkdownPerformanceSessionInternal? performanceSession)
    {
        IMarkdownImageResolver? imageResolver = inputs.ImageResolver;
        var imageResolveContext = new MarkdownImageResolveContext(
            inputs.ImageBaseUri,
            inputs.ImageDocumentPath,
            inputs.AllowThirdPartyRemoteImages,
            inputs.ImageDocumentSource);
        if (performanceSession is not null && imageResolver is not null)
        {
            IMarkdownPerformanceDocumentScope? scope = performanceSession.PrepareDocumentScope(
                _host.DocumentOwner,
                parsed.SourceText,
                layoutRegistry.Revision,
                parsed.Document,
                layoutRegistry.SafeHtmlPolicy,
                imageResolver,
                imageResolveContext);
            if (scope is null)
            {
                performanceSession = null;
                _imagePrefetch.Queue(
                    parsed.Document,
                    parsed.SourceText,
                    layoutRegistry,
                    imageResolver,
                    imageResolveContext,
                    _host.ImageLifetimeCts);
            }
            else
                imageResolver = scope;
        }
        else
        {
            performanceSession?.ReleaseDocumentScope(_host.DocumentOwner);
            _imagePrefetch.Queue(
                parsed.Document,
                parsed.SourceText,
                layoutRegistry,
                imageResolver,
                imageResolveContext,
                _host.ImageLifetimeCts);
        }

        return imageResolver;
    }

    private (ThemeSnapshot Theme, double Milliseconds) CreateThemeSnapshot(
        RebuildLayoutInputs inputs,
        MarkdownRenderer.Document.MarkdownDocument semanticDocument,
        MarkdownExtensionRegistry layoutRegistry,
        IMarkdownPerformanceSessionInternal? performanceSession,
        CancellationToken ct)
    {
        ulong? styleRoleDemandMask = null;
        if (performanceSession is not null &&
            !layoutRegistry.HasUnclassifiedStyleRoleRenderers &&
            performanceSession.TryGetStyleRoleDemandMask(
                semanticDocument,
                ct,
                out ulong completeStyleRoleDemandMask))
        {
            styleRoleDemandMask = completeStyleRoleDemandMask;
        }

        long themeSnapshotStarted = Stopwatch.GetTimestamp();
        var themeResolver = new ThemeResolver(_host.ThemeHost, inputs.Theme, _themeResourceKeys);
        var themeSnapshot = themeResolver.CreateSnapshot(
            inputs.StyleSheet,
            inputs.TextScaleFactor,
            semanticDocument.GetExtensionStyleRoleNames(),
            styleRoleDemandMask);
        return (themeSnapshot, Stopwatch.GetElapsedTime(themeSnapshotStarted).TotalMilliseconds);
    }

    private MarkdownLayoutContext CreateContext(
        ParsedMarkdown parsed,
        MarkdownExtensionRegistry layoutRegistry,
        RebuildLayoutInputs inputs,
        IMarkdownImageResolver? imageResolver,
        IMarkdownPerformanceSessionInternal? performanceSession,
        ThemeSnapshot themeSnapshot,
        CanvasDevice device,
        RebuildReason reason,
        MarkdownLayoutDiagnostics? layoutDiagnostics,
        MarkdownRenderResources renderResources,
        CancellationToken ct)
    {
        var imageCarryIdentity = new Layout.Boxes.MarkdownImageCarryIdentity(
            parsed.SourceText,
            inputs.ImageBaseUri,
            inputs.ImageDocumentPath,
            inputs.ImageDocumentSource,
            // The resolver may be wrapped in a per-document scope; the carry
            // identity uses the host's own resolver.
            inputs.ImageResolver,
            inputs.SvgRenderer,
            inputs.RasterDecoder,
            inputs.AllowThirdPartyRemoteImages);
        IReadOnlyDictionary<string, Layout.Boxes.CompletedImageCarry>? completedImageCarrySnapshot =
            _completedImageCarry.Capture(
                imageCarryIdentity,
                // A permanent failure is carried only across an automatic
                // scene rebuild; any other rebuild (for example the host's Retry)
                // still retries it.
                carryFailures: reason == RebuildReason.SceneMaterialized,
                _imagePlans.Items);
        return new MarkdownLayoutContext(
            device,
            themeSnapshot,
            new MarkdownSourceMap(parsed.SourceText),
            layoutRegistry,
            inputs.FlowDirection,
            _host.DispatcherQueue,
            inputs.Language)
        {
            RasterizationScale = inputs.RasterScale,
            // Propagate the rebuild lifetime through layout and host image
            // construction. Image resolution has a separate loaded-lifetime
            // token so benign width/theme relayouts do not cancel committed
            // image boxes before they can publish their result.
            CancellationToken = ct,
            RenderResources = renderResources,
            ImageCancellationToken = _host.ImageLifetimeCts?.Token ?? CancellationToken.None,
            // Failed layout construction can retire images after the control's
            // current drain epoch has started. Keep a context-local barrier and
            // also publish those retirements to the control's observer.
            RetirementSink = _retirements.Track,
            IsCodeBlockCopyEnabled = inputs.IsCodeBlockCopyEnabled,
            IsTaskListEditingEnabled = inputs.IsTaskListEditingEnabled,
            CommandProvider = inputs.CommandProvider,
            TaskCommandAvailabilityChanged = _focusableItems.OnTaskCommandAvailabilityChanged,
            StringProvider = inputs.StringProvider,
            CodeBlockLineNumberMode = inputs.LineNumberMode,
            CodeBlockWrappingMode = inputs.WrappingMode,
            CodeBlockChromeMode = inputs.ChromeMode,
            LayoutDiagnostics = layoutDiagnostics,
            ImageResolver = imageResolver,
            PerformanceSession = performanceSession,
            PerformanceDocumentOwner = _host.DocumentOwner,
            ImagePreparationOwner = _imagePreparationOwner,
            SvgRenderer = inputs.SvgRenderer,
            RasterDecoder = inputs.RasterDecoder,
            ImageBaseUri = inputs.ImageBaseUri,
            ImageDocumentPath = inputs.ImageDocumentPath,
            ImageDocumentSource = inputs.ImageDocumentSource,
            AllowThirdPartyRemoteImages = inputs.AllowThirdPartyRemoteImages,
            ImageUnavailable = _host.RaiseImageUnavailable,
            DisclosureStates = inputs.DisclosureStates,
            ImageCarryIdentity = imageCarryIdentity,
            CompletedImageCarry = completedImageCarrySnapshot,
        };
    }
}
