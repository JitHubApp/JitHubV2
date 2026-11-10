using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Performance;
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// Everything one <see cref="SvgSourceOpener"/> open holds while it runs: the
/// request identity, the current source array and its preparation/open
/// aliases, and every owned document, use, slot, admission and lease. The
/// opener clears the source-bearing aliases and releases the unconsumed
/// resources in one place before returning to its caller. It is a class
/// because the asynchronous stages that fill it cannot take a
/// <see langword="ref"/>.
/// </summary>
internal sealed class SvgOpenState
{
    internal SvgOpenState(
        ImageWorkLease renderWork,
        byte[] rawBytes,
        Size intrinsicHint,
        string? builtInSourceAliasKey,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration,
        int renderGeneration,
        string resolvedCacheKey,
        string effectiveSecurityPartitionKey,
        IMarkdownSvgRenderer renderer,
        long svgWorkId)
    {
        RenderWork = renderWork;
        RawBytes = rawBytes;
        IntrinsicHint = intrinsicHint;
        BuiltInSourceAliasKey = builtInSourceAliasKey;
        Demand = demand;
        LoadDemandGeneration = loadDemandGeneration;
        RenderGeneration = renderGeneration;
        ResolvedCacheKey = resolvedCacheKey;
        EffectiveSecurityPartitionKey = effectiveSecurityPartitionKey;
        Renderer = renderer;
        HostAdmission = renderer as IMarkdownSvgAdmission;
        SvgWorkId = svgWorkId;
        SvgSourceBytes = rawBytes.LongLength;
    }

    // Request identity.
    internal readonly ImageWorkLease RenderWork;
    internal readonly Size IntrinsicHint;
    internal readonly string? BuiltInSourceAliasKey;
    internal readonly MarkdownSvgRenderDemand Demand;
    internal readonly int LoadDemandGeneration;
    internal readonly int RenderGeneration;
    internal readonly string ResolvedCacheKey;
    internal readonly string EffectiveSecurityPartitionKey;
    internal readonly IMarkdownSvgRenderer Renderer;
    internal readonly IMarkdownSvgAdmission? HostAdmission;
    internal readonly long SvgWorkId;
    internal readonly long SvgSourceBytes;
    internal IMarkdownPerformanceSessionInternal? PreparationSession;
    internal CancellationToken SessionLifetimeToken;
    internal CancellationToken CancellationToken;
    internal BitmapOutputWorkContext BitmapOutputWork;
    internal string CurrentSecurityPartitionHash = string.Empty;
    internal bool MetadataWasKnown;
    internal long RendererIdentity;
    internal long ProviderCacheGeneration;
    internal string SvgFailureCacheKey = string.Empty;

    // Content identity and the accepted document, filled as the open advances.
    // None of them refers to the source array.
    internal SvgPreflight Preflight;
    internal MarkdownSvgColor SemanticColor;
    internal MarkdownSvgColorScheme ColorScheme;
    internal string ContentCacheKey = string.Empty;
    internal MarkdownSvgDocumentInfo? Info;
    internal Size Intrinsic;
    internal string? Description;

    // Source-bearing aliases, cleared before the opener returns.
    internal byte[] RawBytes;
    internal MarkdownSvgSourcePreparation? Preparation;
    internal MarkdownSvgOpenRequest? OpenRequest;
    internal SvgCacheEntry? SourceEntry;

    // Owned until handed to SvgOpenedRenderWork or released.
    internal CancellationTokenSource? PreparationCancellation;
    internal IMarkdownSvgDocument? OpenedDocument;
    internal SvgDocumentUseLease? OpenedDocumentUse;
    internal SvgHostOwnerSlot? OwnerSlot;
    internal SvgHostCapacityDeferral? HostCapacityDeferral;
    internal IMarkdownSvgPreparationLease? HostPreparationLease;
    internal long HostPreparationScratchBytes;
    internal long HostPreparedSourceBytes;
    internal long SourceReservationBytes;
    internal object? AdmissionDomain;
    internal BoundedAsyncKeyedGate.Entry? OpenAdmissionEntry;
    internal bool ProviderAdmissionHeld;
    internal bool RenderOwnershipTransferred;
}
