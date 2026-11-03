using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

/// <summary>
/// Owns the sanitized source snapshot needed for worker attachment and render
/// fallback. Its owner releases it only after all admitted renders have drained.
/// </summary>
internal sealed class SvgDocumentSourceSnapshot
{
    private SourceState? _state;

    public SvgDocumentSourceSnapshot(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(openRequest);

        // Preserve the authoritative preflight snapshot, not the caller's
        // possibly sliced/reusable input memory. The request copy carries only
        // immutable rendering metadata; source bytes live solely in preflight
        // and are supplied to worker mappings from that owner.
        _state = new SourceState(
            preflight,
            openRequest with { SanitizedSvgBytes = ReadOnlyMemory<byte>.Empty });
    }

    public SvgDocumentRenderSnapshot GetForRender()
    {
        SourceState? state = Volatile.Read(ref _state);
        if (state is null)
            throw new ObjectDisposedException(nameof(SvgDocumentSourceSnapshot));

        return state.GetForRender();
    }

    public void Release()
    {
        SourceState? state = Interlocked.Exchange(ref _state, null);
        state?.Release();
    }

    private sealed class SourceState(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest)
    {
        private SvgPreflightResult? _preflight = preflight;
        private MarkdownSvgOpenRequest? _openRequest = openRequest;

        public SvgDocumentRenderSnapshot GetForRender()
        {
            SvgPreflightResult? currentPreflight = Volatile.Read(ref _preflight);
            MarkdownSvgOpenRequest? currentOpenRequest = Volatile.Read(ref _openRequest);
            if (currentPreflight is null || currentOpenRequest is null)
                throw new ObjectDisposedException(nameof(SvgDocumentSourceSnapshot));

            return new SvgDocumentRenderSnapshot(currentPreflight, currentOpenRequest);
        }

        public void Release()
        {
            // The request holds rendering metadata only. Clear it and release
            // the sole source owner, including any retained task that still
            // holds the preflight result object itself.
            Interlocked.Exchange(ref _openRequest, null);
            Interlocked.Exchange(ref _preflight, null)?.ReleaseSource();
        }
    }
}
