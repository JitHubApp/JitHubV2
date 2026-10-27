using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal readonly record struct RasterDecodeGeometry(int SourceWidth, int SourceHeight,
    int Orientation, int IntrinsicWidth, int IntrinsicHeight, int OutputLength, long WorkingBytes);

internal static class RasterDecodePolicy
{
    internal const int MaximumSourceBytes = 32 * 1024 * 1024;
    internal const int MaximumDimension = 16_384;
    internal const long MaximumSourcePixels = 40_000_000;
    internal const long MaximumOutputBytes = 64L * 1024 * 1024;
    internal static long MaximumWorkingBytes => Environment.Is64BitProcess
        ? 64L * 1024 * 1024 : 32L * 1024 * 1024;

    internal static RasterDecodeGeometry Inspect(MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Format) || !Enum.IsDefined(request.PixelFormat) ||
            !Enum.IsDefined(request.Priority) || request.TargetWidthPixels <= 0 ||
            request.TargetHeightPixels <= 0 || request.MaximumWorkingBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Format != MarkdownRasterFormat.WebP)
            throw Failure(MarkdownRasterFailureReason.UnsupportedContent);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.EncodedBytes.IsEmpty)
            throw Failure(MarkdownRasterFailureReason.UnsupportedContent);
        if (request.EncodedBytes.Length > MaximumSourceBytes ||
            request.TargetWidthPixels > MaximumDimension || request.TargetHeightPixels > MaximumDimension)
            throw Failure(MarkdownRasterFailureReason.ResourceLimitExceeded);
        long outputLength = checked((long)request.TargetWidthPixels * request.TargetHeightPixels * 4);
        long workingBytes = Math.Min(request.MaximumWorkingBytes, MaximumWorkingBytes);
        if (outputLength > MaximumOutputBytes || MappingBytes(request.EncodedBytes.Length, outputLength) > workingBytes)
            throw Failure(MarkdownRasterFailureReason.ResourceLimitExceeded);

        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(request.EncodedBytes.Span);
        if ((!budget.Accepted && !budget.CanRenderStaticPreview) || budget.Format != "WEBP" ||
            budget.FrameCount != 1 || budget.FirstFrame is not null ||
            !RasterIntrinsicMetadata.TryReadValidated(request.EncodedBytes.Span, budget,
                out RasterIntrinsicMetadata intrinsic, out int orientation, cancellationToken))
            throw Failure(MarkdownRasterFailureReason.UnsupportedContent);
        if (budget.Width <= 0 || budget.Height <= 0 ||
            budget.Width > MaximumDimension || budget.Height > MaximumDimension ||
            (long)budget.Width * budget.Height > MaximumSourcePixels)
            throw Failure(MarkdownRasterFailureReason.ResourceLimitExceeded);
        if (request.TargetWidthPixels > intrinsic.Width || request.TargetHeightPixels > intrinsic.Height)
            throw Failure(MarkdownRasterFailureReason.UnsupportedContent);
        cancellationToken.ThrowIfCancellationRequested();
        return new(budget.Width, budget.Height, orientation, intrinsic.Width, intrinsic.Height,
            checked((int)outputLength), workingBytes);
    }

    internal static long MappingBytes(long sourceLength, long outputLength) =>
        checked(((sourceLength + 63) & ~63L) + outputLength + SharedMemoryLease.ProgressAreaBytes);

    internal static void ValidateWireRequest(in WorkerRequest request)
    {
        bool swapsAxes = request.RasterOrientation >= 5;
        int intrinsicWidth = swapsAxes ? request.RasterSourceHeight : request.RasterSourceWidth;
        int intrinsicHeight = swapsAxes ? request.RasterSourceWidth : request.RasterSourceHeight;
        if (request.SourceLength <= 0 || request.SourceLength > MaximumSourceBytes ||
            request.RasterSourceWidth <= 0 || request.RasterSourceWidth > MaximumDimension ||
            request.RasterSourceHeight <= 0 || request.RasterSourceHeight > MaximumDimension ||
            (long)request.RasterSourceWidth * request.RasterSourceHeight > MaximumSourcePixels ||
            request.RasterOrientation is < 1 or > 8 || request.TargetWidth <= 0 || request.TargetHeight <= 0 ||
            request.TargetWidth > intrinsicWidth || request.TargetHeight > intrinsicHeight ||
            request.OutputLength != (long)request.TargetWidth * request.TargetHeight * 4 ||
            request.OutputLength > MaximumOutputBytes || request.RasterWorkingBytes <= 0 ||
            request.RasterWorkingBytes > MaximumWorkingBytes ||
            MappingBytes(request.SourceLength, request.OutputLength) > request.RasterWorkingBytes ||
            !Enum.IsDefined(request.PixelFormat) || request.Tile is not null || request.SemanticColor is not null ||
            request.PhysicalViewport is not null || request.Locale.Length != 0 || request.FontGeneration != 0 ||
            request.ParsedResourceCacheBytes != 0 || request.DocumentId != 0)
            throw new WorkerProtocolException("The raster decode request has invalid fields.");
    }

    internal static void ValidateResponse(in WorkerResponse response, MarkdownRasterDecodeRequest request,
        in RasterDecodeGeometry geometry)
    {
        if (response.HasText || response.UsesCurrentColor || response.UsesColorScheme || response.ReadyFontGeneration != 0 ||
            response.Status is not (WorkerStatus.Ok or WorkerStatus.Unsupported or WorkerStatus.ResourceLimit or WorkerStatus.WorkerFailure))
            throw new WorkerProtocolException("The raster decoder returned invalid status or metadata.");
        if (response.Status != WorkerStatus.Ok)
        {
            if (response.OutputLength != 0 || response.Stride != 0 || response.Width != 0 || response.Height != 0 ||
                response.IntrinsicWidth != 0 || response.IntrinsicHeight != 0 || response.AspectRatio != 0)
                throw new WorkerProtocolException("A failed raster decode returned output metadata.");
            return;
        }
        if (response.Width != request.TargetWidthPixels || response.Height != request.TargetHeightPixels ||
            response.Stride != checked(request.TargetWidthPixels * 4) || response.OutputLength != geometry.OutputLength ||
            response.PixelFormat != ToWorkerFormat(request.PixelFormat) ||
            response.IntrinsicWidth != geometry.IntrinsicWidth || response.IntrinsicHeight != geometry.IntrinsicHeight ||
            response.AspectRatio != (double)geometry.IntrinsicWidth / geometry.IntrinsicHeight)
            throw new WorkerProtocolException("The raster decoder returned mismatched output geometry.");
    }

    internal static MarkdownSvgPixelFormat ToWorkerFormat(MarkdownRasterPixelFormat format) => format switch
    {
        MarkdownRasterPixelFormat.Rgba8Premultiplied => MarkdownSvgPixelFormat.Rgba8Premultiplied,
        MarkdownRasterPixelFormat.Bgra8Premultiplied => MarkdownSvgPixelFormat.Bgra8Premultiplied,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static MarkdownRasterException Failure(MarkdownRasterFailureReason reason) => new(reason);
}
