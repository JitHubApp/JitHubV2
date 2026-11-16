using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

internal static class WorkerFontCatalogProtocolPolicy
{
    public static void ValidateProbeResponse(long requestedGeneration, WorkerResponse response)
    {
        if (requestedGeneration is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration ||
            response.ReadyFontGeneration is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration)
        {
            throw new WorkerProtocolException("The SVG worker returned an out-of-range font generation.");
        }

        if (response.Status == WorkerStatus.Ok && response.ReadyFontGeneration < requestedGeneration)
        {
            throw new WorkerProtocolException("The SVG worker reported fonts ready before the requested generation.");
        }

        if (response.Status == WorkerStatus.FontCatalogPending &&
            (requestedGeneration == 0
                ? response.ReadyFontGeneration != 0
                : response.ReadyFontGeneration >= requestedGeneration))
        {
            throw new WorkerProtocolException("The SVG worker returned an inconsistent pending font generation.");
        }

        if (response.Status == WorkerStatus.FontCatalogFailure &&
            (requestedGeneration == 0
                ? response.ReadyFontGeneration != 0
                : response.ReadyFontGeneration >= requestedGeneration))
        {
            throw new WorkerProtocolException("The SVG worker returned an inconsistent failed font generation.");
        }
    }
}
