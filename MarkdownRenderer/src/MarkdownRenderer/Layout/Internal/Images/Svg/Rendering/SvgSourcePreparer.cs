using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// The preparation stage of a <see cref="SvgSourceOpener"/> open: CPU
/// preparation admission, the provider's preparation scratch reservation, the
/// provider's source preparation and its normalization, and the host
/// preflight. It records the preparation and preflight in the open's
/// <see cref="SvgOpenState"/>.
/// </summary>
internal sealed class SvgSourcePreparer
{
    private readonly ISvgLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;

    internal SvgSourcePreparer(ISvgLoadHost host, MarkdownLayoutContext context, ImageResourceScope resources)
    {
        _host = host;
        _context = context;
        _resources = resources;
    }

    // The optional provider prepares a bounded inert static image on
    // this background path. The host inspects its metadata and the
    // provider repeats authoritative checks before worker admission.
    // Sanitization, hashing and metadata inspection are preparation
    // work too. Borrow the optional session slot and process-wide
    // preparation gate only for that synchronous region, never across
    // a keyed/provider/worker wait. The source-scaled weight schedules
    // transient work; it does not reserve retained source bytes.
    /// <returns><see langword="false"/> when the provider denied preparation scratch.</returns>
    internal async Task<bool> TryPrepareAsync(SvgOpenState state)
    {
        CancellationToken preparationToken = state.CancellationToken;
        try
        {
            preparationToken.ThrowIfCancellationRequested();
            using IDisposable? preparationSlot = state.PreparationSession is null
                ? null
                : await state.PreparationSession.EnterCpuPreparationAsync(
                        _context.EffectiveImagePreparationOwner ?? _context,
                        exclusive: false,
                        preparationToken)
                    .ConfigureAwait(false);
            using IDisposable processPreparationSlot =
                await _resources.Preparation.EnterAsync(
                        _context.EffectiveImagePreparationOwner ?? _context,
                        state.RawBytes.LongLength * 3,
                        exclusive: false,
                        preparationToken)
                    .ConfigureAwait(false);
            if (!TryReservePreparationScratch(state))
                return false;
            {
                using var stageTrace = MarkdownSvgStageTrace.Measure(
                    state.SvgWorkId,
                    MarkdownSvgWorkStage.SourcePreparation,
                    state.SvgSourceBytes);
                preparationToken.ThrowIfCancellationRequested();
                state.Preparation = state.Renderer.PrepareSource(state.RawBytes, preparationToken);
                preparationToken.ThrowIfCancellationRequested();
            }
            MarkdownSvgSourcePreparation preparation = AdoptPreparation(state);
            state.ProviderCacheGeneration = state.Renderer.CacheGeneration;
            SvgPreflight preflight;
            {
                using var stageTrace = MarkdownSvgStageTrace.Measure(
                    state.SvgWorkId,
                    MarkdownSvgWorkStage.HostPreflight,
                    state.SvgSourceBytes);
                if (preparation.SanitizedBytes is not null &&
                    _host.TryGetKnownSvgPreflight(
                        state.RawBytes, state.CurrentSecurityPartitionHash, state.IntrinsicHint,
                        out SvgPreflight knownPreflight))
                {
                    preflight = knownPreflight;
                }
                else
                {
                    preflight = SvgSourcePreflight.Preflight(
                        state.RawBytes,
                        preparation,
                        state.IntrinsicHint,
                        state.EffectiveSecurityPartitionKey,
                        preparationToken);
                }
            }
            preparationToken.ThrowIfCancellationRequested();
            state.Preflight = preflight;
            return true;
        }
        catch (ObjectDisposedException) when (
            preparationToken.IsCancellationRequested || state.SessionLifetimeToken.IsCancellationRequested)
        {
            // A provider/preparation disposal race coinciding with
            // linked session/image cancellation is not invalid SVG
            // content and must not publish an unavailable image.
            throw new OperationCanceledException(preparationToken);
        }
    }

    // CPU admission is complete before taking byte ownership.
    // Only the opt-in session path and a participating provider
    // use this reservation; legacy/custom providers are unchanged.
    /// <returns><see langword="false"/> when the provider denied preparation scratch.</returns>
    private bool TryReservePreparationScratch(SvgOpenState state)
    {
        if (state.PreparationSession is null ||
            state.Renderer is not IMarkdownSvgPreparationAdmission scratchAdmission)
        {
            return true;
        }

        MarkdownSvgRenderDemand demand = state.Demand;
        long demandVersionAtPreparation = demand.Version;
        bool admitted = scratchAdmission.TryReservePreparationScratch(
            demand,
            out state.HostPreparationLease,
            out long capacityVersionAtPreparation,
            out state.HostPreparationScratchBytes);
        if (state.HostPreparationScratchBytes <= 0 ||
            capacityVersionAtPreparation < 0 ||
            admitted != (state.HostPreparationLease is not null))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG provider returned invalid preparation admission.");
        }
        if (admitted)
            return true;

        state.HostCapacityDeferral = new SvgHostCapacityDeferral(
            scratchAdmission,
            demand,
            state.LoadDemandGeneration,
            capacityVersionAtPreparation,
            state.HostPreparationScratchBytes,
            RequiresOwnerSlot: false,
            OwnerVersionAtDenial: 0,
            DemandVersionAtDenial: demandVersionAtPreparation,
            VisualDemandEpochAtDenial: _host.SvgHostVisualDemandEpoch);
        // Do not call PrepareSource or park while owning bytes.
        // The caller drains the original resolver lease before
        // registering the scalar-only capacity waiter.
        state.RawBytes = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// Normalizes the provider's preparation, sizes or returns the host
    /// preparation lease, and makes the sanitized bytes the current source.
    /// </summary>
    private static MarkdownSvgSourcePreparation AdoptPreparation(SvgOpenState state)
    {
        MarkdownSvgSourcePreparation? preparation = state.Preparation;
        if (preparation is null)
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG provider returned no source preparation result.");
        if (preparation.SanitizedBytes is { Length: 0 } or { Length: > SvgSourceOpener.MaxSvgBytes })
            preparation = MarkdownSvgSourcePreparation.Reject(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The SVG provider returned an empty or over-budget source.");
        if (preparation.SanitizedBytes is null && preparation.FailureReason is null)
            preparation = MarkdownSvgSourcePreparation.Reject(
                MarkdownSvgFailureReason.UnsupportedContent,
                "The SVG provider did not admit this source.");
        state.Preparation = preparation;
        if (state.HostPreparationLease is not null)
        {
            if (preparation.SanitizedBytes is { } preparedSource &&
                !ReferenceEquals(preparedSource, state.RawBytes))
            {
                state.HostPreparedSourceBytes = preparedSource.LongLength;
                state.HostPreparationLease.SetLength(state.HostPreparedSourceBytes);
            }
            else
            {
                // Rejection/identity preparation owns no distinct
                // host output. The resolver still owns the input.
                state.HostPreparationLease.Dispose();
                state.HostPreparationLease = null;
            }
        }
        state.RawBytes = preparation.SanitizedBytes ?? state.RawBytes;
        return preparation;
    }
}
