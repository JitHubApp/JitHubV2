using System.Buffers;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Scripted <see cref="IMarkdownRasterDecoder"/> for <see cref="RasterDecoderImageBoxTests"/>: it records
/// each request and owner, and can fail, return invalid output or hold a pending result.
/// </summary>
internal sealed class FakeRasterDecoder(params MarkdownRasterFormat[] formats) : IMarkdownRasterDecoder, IDisposable
{
    private readonly MarkdownRasterFormat[] _formats = formats;
    private int _calls;
    private int _disposeCount;

    internal int Calls => Volatile.Read(ref _calls);
    internal int DisposeCount => Volatile.Read(ref _disposeCount);
    internal MarkdownRasterDecodeRequest? LastRequest { get; private set; }
    internal TrackingMemoryOwner? LastOwner { get; private set; }
    internal RasterDecoderImageBoxTests.InvalidProviderOutput InvalidOutput { get; init; }
    internal int FailAboveTargetWidth { get; init; } = int.MaxValue;
    internal Exception? Failure { get; init; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<MarkdownRaster>? PendingResult { get; init; }

    public void Dispose() => Interlocked.Increment(ref _disposeCount);

    public bool SupportsFormat(MarkdownRasterFormat format) => Array.IndexOf(_formats, format) >= 0;

    public ValueTask<MarkdownRaster> DecodeAsync(
        MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        LastRequest = request;
        Started.TrySetResult();
        if (Failure is not null)
            return ValueTask.FromException<MarkdownRaster>(Failure);
        if (request.TargetWidthPixels > FailAboveTargetWidth)
        {
            return ValueTask.FromException<MarkdownRaster>(
                new MarkdownRasterException(MarkdownRasterFailureReason.ResourceLimitExceeded));
        }
        if (PendingResult is not null)
            return new ValueTask<MarkdownRaster>(PendingResult.Task);
        if (InvalidOutput == RasterDecoderImageBoxTests.InvalidProviderOutput.Null)
            return ValueTask.FromResult<MarkdownRaster>(null!);
        return ValueTask.FromResult(CreateRaster(request));
    }

    internal MarkdownRaster CreateRaster(MarkdownRasterDecodeRequest request)
    {
        int width = InvalidOutput == RasterDecoderImageBoxTests.InvalidProviderOutput.WrongGeometry
            ? checked(request.TargetWidthPixels + 1)
            : request.TargetWidthPixels;
        int height = request.TargetHeightPixels;
        int length = checked(width * height * 4);
        var owner = new TrackingMemoryOwner(length);
        LastOwner = owner;
        MarkdownRasterPixelFormat format = InvalidOutput == RasterDecoderImageBoxTests.InvalidProviderOutput.WrongPixelFormat
            ? MarkdownRasterPixelFormat.Rgba8Premultiplied
            : request.PixelFormat;
        return new MarkdownRaster(owner, width, height, checked(width * 4), format);
    }
}
