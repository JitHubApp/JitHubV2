using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Fonts;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

/// <summary>
/// Checks worker responses against the request that produced them and
/// classifies transport failures. A malformed or out-of-contract response is a
/// protocol failure; the content-quarantine helper runs before the worker that
/// produced the failure is released.
/// </summary>
internal static class WorkerResponseValidation
{
    internal static void ValidateRasterResponse(
        WorkerResponse response,
        MarkdownSvgRenderRequest renderRequest,
        int expectedOutputLength)
    {
        ArgumentNullException.ThrowIfNull(renderRequest);
        int width = renderRequest.TileRegion?.Width ?? renderRequest.TargetWidthPixels;
        int height = renderRequest.TileRegion?.Height ?? renderRequest.TargetHeightPixels;
        long expectedLength = (long)width * height * 4;
        long expectedStride = (long)width * 4;
        if (response.Status != WorkerStatus.Ok ||
            expectedLength != expectedOutputLength ||
            response.OutputLength != expectedOutputLength ||
            response.Width != width ||
            response.Height != height ||
            response.Stride != expectedStride ||
            response.PixelFormat != renderRequest.PixelFormat)
        {
            throw new WorkerProtocolException("The resvg worker returned malformed raster metadata.");
        }
    }

    internal static void ValidateOpenResponse(WorkerResponse response)
    {
        if (response.Status != WorkerStatus.Ok ||
            !double.IsFinite(response.IntrinsicWidth) || response.IntrinsicWidth <= 0 ||
            !double.IsFinite(response.IntrinsicHeight) || response.IntrinsicHeight <= 0 ||
            response.OutputLength != 0 ||
            response.Width != 0 ||
            response.Height != 0)
        {
            throw new WorkerProtocolException("The resvg worker returned malformed document metadata.");
        }
    }

    internal static void ValidateOpenStatus(
        WorkerResponse response,
        bool requiresFontCatalog,
        long requestGeneration,
        string operation)
    {
        if (requiresFontCatalog)
            ValidateTextResponseMetadata(response, requestGeneration, operation);

        bool statusAllowed = response.Status is
            WorkerStatus.Ok or
            WorkerStatus.Unsupported or
            WorkerStatus.ResourceLimit or
            WorkerStatus.WorkerFailure;
        statusAllowed |= requiresFontCatalog &&
            (response.Status is WorkerStatus.FontCatalogPending or WorkerStatus.FontCatalogFailure);
        if (!statusAllowed)
        {
            throw new WorkerProtocolException($"The worker returned an invalid status for {operation}.");
        }
    }

    internal static void ValidateRenderStatus(
        WorkerResponse response,
        bool requiresFontCatalog,
        long requestGeneration,
        bool documentMissingAllowed,
        string operation)
    {
        if (requiresFontCatalog)
            ValidateTextResponseMetadata(response, requestGeneration, operation);

        bool statusAllowed = response.Status is WorkerStatus.Ok or
            WorkerStatus.Unsupported or
            WorkerStatus.ResourceLimit or
            WorkerStatus.WorkerFailure;
        statusAllowed |= requiresFontCatalog &&
            (response.Status is WorkerStatus.FontCatalogPending or WorkerStatus.FontCatalogFailure);
        statusAllowed |= documentMissingAllowed && response.Status == WorkerStatus.DocumentMissing;
        if (statusAllowed)
        {
            return;
        }

        throw new WorkerProtocolException($"The worker returned an invalid status for {operation}.");
    }

    internal static bool ShouldQuarantineContentFailure(
        bool callerCanceled,
        bool requiresFontCatalog,
        long requestGeneration,
        long currentGeneration) =>
        !callerCanceled && (!requiresFontCatalog || requestGeneration == currentGeneration);

    internal static void QuarantineContentBeforeWorkerRelease(
        WorkerDocumentHandle? document,
        Action? contentFailureCallback,
        bool callerCanceled,
        bool requiresFontCatalog,
        long requestGeneration,
        long currentGeneration,
        Action invalidateWorker)
    {
        ArgumentNullException.ThrowIfNull(invalidateWorker);
        try
        {
            if (ShouldQuarantineContentFailure(
                    callerCanceled,
                    requiresFontCatalog,
                    requestGeneration,
                    currentGeneration))
            {
                if (document is not null)
                    document.QuarantineContent();
                else
                    contentFailureCallback?.Invoke();
            }
        }
        finally
        {
            invalidateWorker();
        }
    }

    internal static async Task CloseDocumentOrInvalidateAsync(
        Func<Task<WorkerResponse>> close,
        Action invalidate)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(close);
            ArgumentNullException.ThrowIfNull(invalidate);
            WorkerResponse response = await close().ConfigureAwait(false);
            if (response.Status != WorkerStatus.Ok || response.OutputLength != 0)
                throw new WorkerProtocolException("The resvg worker rejected a document close request.");
        }
        catch (MarkdownSvgException exception) when (
            exception.Reason == MarkdownSvgFailureReason.ResourceLimitExceeded)
        {
            // ExchangeAsync rejects a request before touching the worker when
            // its bounded transaction queue is full. A failed CLOSE at this
            // point is only a best-effort retirement miss: restarting a healthy
            // worker would abort unrelated OPEN/RENDER requests. The native
            // worker bounds document bindings and evicts uncached documents.
        }
        catch (Exception exception) when (
            IsWorkerTransportFailure(exception) ||
            exception is MarkdownSvgException or OperationCanceledException)
        {
            invalidate();
        }
        finally
        {
            close = null!;
            invalidate = null!;
        }
    }

    internal static WorkerResponse Stamp(WorkerResponse response, WindowsWorkerProcess worker) =>
        response with { WorkerInstanceId = worker.InstanceId };

    internal static void ValidateFontCatalogProbeResponse(
        long requestGeneration,
        WorkerResponse response)
    {
        if (response.OutputLength != 0)
            throw new WorkerProtocolException("The resvg worker returned output for font-catalog initialization.");
        if (response.Status is not (
                WorkerStatus.Ok or
                WorkerStatus.FontCatalogPending or
                WorkerStatus.FontCatalogFailure))
        {
            throw new WorkerProtocolException("The resvg worker returned an invalid font-catalog probe status.");
        }

        WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(requestGeneration, response);
        if (response.Status == WorkerStatus.Ok && response.ReadyFontGeneration != requestGeneration)
        {
            throw new WorkerProtocolException(
                "The resvg worker confirmed a font generation different from the host target.");
        }
    }

    internal static WorkerInitializationException InvalidateMalformedFontCatalogProbe(
        WorkerProtocolException malformedResponse,
        Action ensureRequestCurrent,
        Action invalidateWorker)
    {
        ArgumentNullException.ThrowIfNull(malformedResponse);
        ArgumentNullException.ThrowIfNull(ensureRequestCurrent);
        ArgumentNullException.ThrowIfNull(invalidateWorker);

        // Cancellation, deadline expiry, and generation rotation outrank the
        // diagnosis of a completed response. In particular, a stale but
        // otherwise healthy probe must not evict a reusable worker.
        ensureRequestCurrent();
        invalidateWorker();
        return new WorkerInitializationException(
            "The resvg worker returned an invalid font-catalog probe response.",
            malformedResponse);
    }

    internal static void ValidateTextResponse(
        WorkerResponse response,
        long requestGeneration,
        long currentGeneration,
        string operation)
    {
        if (requestGeneration != currentGeneration)
            throw new OperationCanceledException($"The SVG font environment changed during {operation}.");

        ValidateTextResponseMetadata(response, requestGeneration, operation);

        if (response.Status == WorkerStatus.FontCatalogPending)
        {
            throw new OperationCanceledException(
                response.Detail.Length == 0
                    ? $"The resvg worker's font catalog became pending during {operation}."
                    : $"The resvg worker's font catalog changed during {operation}: {response.Detail}");
        }

        if (response.Status == WorkerStatus.FontCatalogFailure)
        {
            throw new WorkerInitializationException(
                $"The resvg worker's font catalog failed during {operation}.",
                new WorkerProtocolException(response.Detail.Length == 0
                    ? "The font catalog refresh failed."
                    : response.Detail));
        }

    }

    internal static void ValidateTextResponseMetadata(
        WorkerResponse response,
        long requestGeneration,
        string operation)
    {
        if (response.Status == WorkerStatus.Ok && response.ReadyFontGeneration != requestGeneration)
        {
            throw new WorkerProtocolException($"The resvg worker used an unexpected font generation during {operation}.");
        }

        if ((response.Status is WorkerStatus.FontCatalogPending or WorkerStatus.FontCatalogFailure) &&
            response.ReadyFontGeneration > requestGeneration)
        {
            throw new WorkerProtocolException($"The resvg worker published a future font generation during {operation}.");
        }
    }

    internal static bool IsWorkerTransportFailure(Exception exception) =>
        exception is WorkerProtocolException or WorkerDeadlineException or IOException or InvalidOperationException;
}
