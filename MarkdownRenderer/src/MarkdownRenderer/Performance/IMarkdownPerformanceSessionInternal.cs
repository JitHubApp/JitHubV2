using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Images.Internal;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

internal interface IMarkdownPerformanceSessionInternal : IMarkdownPerformanceSession
{
    CancellationToken LifetimeToken { get; }
    IDisposable? TryBeginRasterWork();
    bool IsDisposed { get; }
    bool IsLayoutDiagnosticCaptureEnabled { get; }

    /// <summary>
    /// The native vector capacity that this session's views admit scenes
    /// against when the session defers offscreen scenes.
    /// </summary>
    VectorNativeResourceAdmission VectorNativeAdmission { get; }
    event EventHandler? Disposed;
    bool TryGetStyleRoleDemandMask(
        MarkdownDocument document,
        CancellationToken cancellationToken,
        out ulong roleDemandMask);
    ValueTask<MarkdownDocument?> ParseAndPrepareDocumentAsync(
        MarkdownEngine? engine,
        MarkdownDocument? document,
        string? source,
        MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken);
    Task<T> ScheduleLayoutWorkAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken);
    bool TryScheduleImageActivation(
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken);
    bool TryGetDeferredSceneResult(
        object documentOwner,
        string marker,
        out MarkdownContentFragment? result);
    bool TryGetDeferredSceneResult(
        object documentOwner, string marker,
        out MarkdownContentFragment? result, out object? projectionIdentity);
    bool HasRegisteredDeferredSceneWork(object documentOwner);
    bool IsRegisteredDeferredSceneMarker(object documentOwner, string marker);
    MarkdownVisibleSceneReadiness GetVisibleDeferredSceneReadiness(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport);
    IMarkdownVectorSceneResidencyPolicy? CreateVectorSceneResidencyPolicy(
        IReadOnlyList<BlockBox> blocks,
        object? documentOwner = null,
        LayoutPlacementTransformContext? placementTransform = null);
    Task<bool> PrepareDeferredSceneBandAsync(
        object documentOwner,
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        CancellationToken cancellationToken);
    IMarkdownImageResidencyPolicy? CreateImageResidencyPolicy(Action requestReconcile);
    Task AdvanceDeferredSceneGeneration(object documentOwner);
    Task ReleaseDeferredSceneDocument(object documentOwner);
    bool ScheduleDeferredScenes(object documentOwner);
    long BeginRasterPreparation(int sourceBytes, int sourceWidth, int sourceHeight);
    void RecordRasterPreparationStage(
        long preparationId,
        MarkdownRasterPreparationStage stage,
        long elapsedStopwatchTicks);
    IMarkdownPerformanceDocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    IMarkdownPerformanceDocumentScope? PrepareDocumentScope(
        object documentOwner,
        string source,
        int registryRevision,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.Internal.SafeHtml.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    void ReleaseDocumentScope(object documentOwner);
    ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken);
    ValueTask<IDisposable> ReserveImageSourceBytesAsync(
        object documentOwner,
        long bytes,
        CancellationToken cancellationToken);
    ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken);
}
