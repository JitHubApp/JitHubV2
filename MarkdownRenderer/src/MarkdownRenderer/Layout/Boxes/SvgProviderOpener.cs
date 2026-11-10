using System;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The provider-open stage of a <see cref="SvgSourceOpener"/> open: it opens
/// the prepared source with the provider, or through the provider's host
/// admission, which may instead deny capacity. It records the opened document
/// and its admission ownership in the open's <see cref="SvgOpenState"/>.
/// </summary>
internal sealed class SvgProviderOpener
{
    private readonly ISvgLoadHost _host;
    private readonly MarkdownLayoutContext _context;

    internal SvgProviderOpener(ISvgLoadHost host, MarkdownLayoutContext context)
    {
        _host = host;
        _context = context;
    }

    // The provider owns the hard deadline for each active worker
    // transaction. Do not start a second wall-clock deadline around
    // provider queueing plus Open + Render: a visible badge burst can
    // legitimately wait behind earlier visible work even though every
    // individual worker request stays inside its immutable ceiling.
    /// <returns><see langword="false"/> when the host admission denied capacity.</returns>
    internal async Task<bool> TryOpenAsync(SvgOpenState state)
    {
        state.OpenRequest = new MarkdownSvgOpenRequest(
            state.RawBytes,
            _context.Language,
            state.ColorScheme,
            state.SemanticColor,
            state.Demand.Priority);

        using var stageTrace = MarkdownSvgStageTrace.Measure(
            state.SvgWorkId,
            MarkdownSvgWorkStage.ProviderOpen,
            state.SvgSourceBytes);
        // Open may still read the caller's bytes until it really
        // completes. Keep the source reservation and image-work lease
        // across that completion, even if the provider ignores cancel.
        // The provider owns its bounded transaction deadline; do not
        // release caller memory on cancellation of just our await.
        if (state.HostAdmission is { } hostAdmission)
            return await TryOpenWithHostAdmissionAsync(state, hostAdmission).ConfigureAwait(false);

        state.OpenedDocument = await state.Renderer.OpenAsync(state.OpenRequest, state.CancellationToken)
            .ConfigureAwait(false);
        state.OpenRequest = null;
        return true;
    }

    private async Task<bool> TryOpenWithHostAdmissionAsync(SvgOpenState state, IMarkdownSvgAdmission hostAdmission)
    {
        MarkdownSvgRenderDemand demand = state.Demand;
        long demandVersionAtOpen = demand.Version;
        MarkdownSvgAdmissionResult hostResult = await hostAdmission.TryOpenAsync(
                state.OpenRequest!,
                demand,
                state.CancellationToken)
            .ConfigureAwait(false);
        if (hostResult.Status == MarkdownSvgAdmissionStatus.CapacityUnavailable)
        {
            if (hostResult.Document is not null ||
                hostResult.SourceReservationBytes != 0 ||
                hostResult.AdmissionDomain is not null ||
                hostResult.RequiredSourceReservationBytes <= 0)
            {
                state.OpenedDocument = hostResult.Document;
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG provider returned an invalid capacity denial.");
            }

            state.HostCapacityDeferral = new SvgHostCapacityDeferral(
                hostAdmission,
                demand,
                state.LoadDemandGeneration,
                hostResult.CapacityVersion,
                Math.Max(state.HostPreparationScratchBytes,
                    checked(state.HostPreparedSourceBytes + hostResult.RequiredSourceReservationBytes)),
                RequiresOwnerSlot: false,
                OwnerVersionAtDenial: 0,
                DemandVersionAtDenial: demandVersionAtOpen,
                VisualDemandEpochAtDenial: _host.SvgHostVisualDemandEpoch);
            state.OpenRequest = null;
            state.Preparation = null;
            state.RawBytes = Array.Empty<byte>();
            return false;
        }

        // Keep a returned candidate in the outer cleanup owner
        // before validating its admission metadata. Malformed or
        // foreign-domain handles are still caller-owned and must
        // be retired in finally.
        state.OpenedDocument = hostResult.Document;
        if (hostResult.Status != MarkdownSvgAdmissionStatus.Opened ||
            state.OpenedDocument is null)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG provider returned no opened document.");
        }
        state.AdmissionDomain = hostResult.AdmissionDomain;
        state.SourceReservationBytes = hostResult.SourceReservationBytes;
        if (state.AdmissionDomain is null || state.SourceReservationBytes <= 0 ||
            !ReferenceEquals(state.AdmissionDomain, hostAdmission.AdmissionDomain))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG provider returned inconsistent source-admission ownership.");
        }
        state.OpenRequest = null;
        return true;
    }
}
