using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// These ownership checks observe the process-wide admission gate. Run them
// without unrelated image decodes and drain asynchronous retirement between
// cases; a moving count sampled from another test is not our lease baseline.
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class RasterDecoderImageBoxTests : IAsyncLifetime
{
    public Task InitializeAsync() => WaitForAdmissionIdleAsync();

    public Task DisposeAsync() => WaitForAdmissionIdleAsync();

    private static Task WaitForAdmissionIdleAsync() =>
        DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
            MarkdownImagePreparationAdmission.Shared.ActiveCount == 0 &&
            MarkdownImagePreparationAdmission.Shared.ActiveBytes == 0 &&
            MarkdownImagePreparationAdmission.Shared.PendingRequests == 0);

    [Fact]
    public async Task RegisteredProviderGetsExactDisplayPlanAndPixelOwnerIsDisposedAfterUpload()
    {
        byte[] source = CreateWebP(640, 360);
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var image = CreateImage(source, decoder, scale: 1);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, decoder.Calls);
            MarkdownRasterDecodeRequest request = Assert.IsType<MarkdownRasterDecodeRequest>(decoder.LastRequest);
            var sizing = new RasterImageSizingRequest(200, 1, null, null,
                RasterImageResourceBudget.MaxPixelsPerFrame);
            Assert.True(RasterImageDecodePlan.TryCreate(
                source, RasterImageResourceBudget.Validate(source), sizing, out RasterImageDecodePlan plan));
            Assert.Equal(MarkdownRasterFormat.WebP, request.Format);
            Assert.Equal(plan.OutputWidth, request.TargetWidthPixels);
            Assert.Equal(plan.OutputHeight, request.TargetHeightPixels);
            Assert.Equal(source, request.EncodedBytes.ToArray());
            Assert.Equal(IntPtr.Size == 4 ? 32L * 1024 * 1024 : 64L * 1024 * 1024,
                request.MaximumWorkingBytes);
            Assert.Equal(MarkdownRasterPixelFormat.Bgra8Premultiplied, request.PixelFormat);
            Assert.Equal((uint)plan.OutputWidth, image.Bitmap!.SizeInPixels.Width);
            Assert.Equal((uint)plan.OutputHeight, image.Bitmap.SizeInPixels.Height);
            Assert.True(decoder.LastOwner!.IsDisposed,
                "The provider-owned pixel mapping must retire immediately after synchronous Win2D upload.");
        }
        finally
        {
            image.Dispose();
        }

        Assert.Equal(0, decoder.DisposeCount);
    }

    [Fact]
    public async Task ProviderHandlesAdmittedOversizedStaticPreviewAtDisplayDimensions()
    {
        const int sourceWidth = 2048;
        const int sourceHeight = 8193;
        byte[] source = CreateWebP(sourceWidth, sourceHeight);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview);

        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = 65_536,
            });
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var image = CreateImage(source, decoder, scale: 1, session: session);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            MarkdownRasterDecodeRequest request = Assert.IsType<MarkdownRasterDecodeRequest>(decoder.LastRequest);
            var sizing = new RasterImageSizingRequest(200, 1, null, null, 65_536);
            Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out RasterImageDecodePlan plan));
            Assert.Equal(plan.OutputWidth, request.TargetWidthPixels);
            Assert.Equal(plan.OutputHeight, request.TargetHeightPixels);
            Assert.True((long)request.TargetWidthPixels * request.TargetHeightPixels <= 65_536);
            Assert.NotEqual((int)budget.Width, request.TargetWidthPixels);
            Assert.NotEqual((int)budget.Height, request.TargetHeightPixels);
            Assert.True(decoder.LastOwner!.IsDisposed);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task UnsupportedProviderFormatLeavesExistingPlatformDecodePathAlone()
    {
        byte[] source = DisplaySizedRasterDecodeTests.CreatePng(32, 16);
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var image = CreateImage(source, decoder, scale: 1);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(0, decoder.Calls);
            Assert.Equal(32u, image.Bitmap!.SizeInPixels.Width);
            Assert.Equal(16u, image.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(InvalidProviderOutput.Null)]
    [InlineData(InvalidProviderOutput.WrongGeometry)]
    [InlineData(InvalidProviderOutput.WrongPixelFormat)]
    public async Task InvalidProviderOutputFailsClosedAndReleasesAnyOwner(InvalidProviderOutput output)
    {
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP) { InvalidOutput = output };
        var image = CreateImage(CreateWebP(64, 32), decoder, scale: 1);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.Null(image.Bitmap);
            Assert.Equal(1, decoder.Calls);
            if (output == InvalidProviderOutput.Null)
                Assert.Null(decoder.LastOwner);
            else
                Assert.True(decoder.LastOwner!.IsDisposed);
            await WaitForAdmissionIdleAsync();
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task SelectedProviderFailureDoesNotFallBackAndTypedCancellationIsNotPublishedAsFailure()
    {
        var failedDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            Failure = new MarkdownRasterException(MarkdownRasterFailureReason.UnsupportedContent),
        };
        var failedImage = CreateImage(CreateWebP(64, 32), failedDecoder, scale: 1);
        try
        {
            failedImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(failedImage);
            Assert.Null(failedImage.Bitmap);
            Assert.Equal(1, failedDecoder.Calls);
        }
        finally
        {
            failedImage.Dispose();
        }

        int unavailable = 0;
        var canceledDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            Failure = new MarkdownRasterException(MarkdownRasterFailureReason.Canceled),
        };
        var canceledImage = CreateImage(
            CreateWebP(64, 32), canceledDecoder, scale: 1,
            imageUnavailable: (_, _, _) => Interlocked.Increment(ref unavailable));
        try
        {
            canceledImage.Measure(200);
            canceledImage.EnsureLoading();
            await canceledDecoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAdmissionIdleAsync();

            Assert.Null(canceledImage.Bitmap);
            Assert.Equal(0, Volatile.Read(ref unavailable));
            Assert.Equal(1, canceledDecoder.Calls);
        }
        finally
        {
            canceledImage.Dispose();
        }
    }

    [Fact]
    public async Task RasterDecodeFailureIsPartitionedByProviderIdentity()
    {
        byte[] source = CreateWebP(64, 32);
        string cacheKey = $"raster-provider-retry-{Guid.NewGuid():N}";
        var firstDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            Failure = new MarkdownRasterException(MarkdownRasterFailureReason.UnsupportedContent),
        };

        var firstImage = CreateImage(source, firstDecoder, scale: 1, cacheKey: cacheKey);
        try
        {
            firstImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(firstImage);

            Assert.Null(firstImage.Bitmap);
            Assert.Equal(1, firstDecoder.Calls);
        }
        finally
        {
            firstImage.Dispose();
        }

        // The same failed provider remains negatively cached for this exact
        // source/format, avoiding repeated deterministic decoder work.
        var repeatedImage = CreateImage(source, firstDecoder, scale: 1, cacheKey: cacheKey);
        try
        {
            repeatedImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(repeatedImage);

            Assert.Null(repeatedImage.Bitmap);
            Assert.Equal(1, firstDecoder.Calls);
        }
        finally
        {
            repeatedImage.Dispose();
        }

        // Installing a different provider must not inherit the first
        // provider's failure for an otherwise identical resolved asset.
        var replacementDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var replacementImage = CreateImage(source, replacementDecoder, scale: 1, cacheKey: cacheKey);
        try
        {
            replacementImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(replacementImage);

            Assert.NotNull(replacementImage.Bitmap);
            Assert.Equal(1, replacementDecoder.Calls);
        }
        finally
        {
            replacementImage.Dispose();
        }

        Assert.Equal(0, firstDecoder.DisposeCount);
        Assert.Equal(0, replacementDecoder.DisposeCount);
    }

    [Fact]
    public async Task ResourceLimitFailureCanRetrySameProviderAtSmallerDisplaySize()
    {
        byte[] source = CreateWebP(640, 360);
        string cacheKey = $"raster-provider-smaller-retry-{Guid.NewGuid():N}";
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            FailAboveTargetWidth = 300,
        };

        var oversizedImage = CreateImage(source, decoder, scale: 2, cacheKey: cacheKey);
        try
        {
            oversizedImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(oversizedImage);

            Assert.NotNull(decoder.LastRequest);
            Assert.Equal((400, 225),
                (decoder.LastRequest!.TargetWidthPixels, decoder.LastRequest.TargetHeightPixels));
            Assert.Null(oversizedImage.Bitmap);
            Assert.Equal(1, decoder.Calls);
        }
        finally
        {
            oversizedImage.Dispose();
        }

        var smallerImage = CreateImage(source, decoder, scale: 1, cacheKey: cacheKey);
        try
        {
            smallerImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(smallerImage);

            Assert.NotNull(decoder.LastRequest);
            Assert.Equal((200, 113),
                (decoder.LastRequest!.TargetWidthPixels, decoder.LastRequest.TargetHeightPixels));
            Assert.NotNull(smallerImage.Bitmap);
            Assert.Equal(2, decoder.Calls);
        }
        finally
        {
            smallerImage.Dispose();
        }
    }

    [Fact]
    public async Task StaleGenerationDisposesLateProviderPixelsAndReleasesAdmission()
    {
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            PendingResult = new TaskCompletionSource<MarkdownRaster>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var image = CreateImage(CreateWebP(64, 32), decoder, scale: 1);
        image.Measure(200);
        try
        {
            image.EnsureLoading();
            await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, MarkdownImagePreparationAdmission.Shared.ActiveCount);
            Assert.True(MarkdownImagePreparationAdmission.Shared.ActiveBytes > 0);

            image.Dispose();
            MarkdownRaster lateRaster = decoder.CreateRaster(decoder.LastRequest!);
            decoder.PendingResult!.SetResult(lateRaster);
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => decoder.LastOwner!.IsDisposed);
            await WaitForAdmissionIdleAsync();
            Assert.Null(image.Bitmap);
        }
        finally
        {
            image.Dispose();
            decoder.PendingResult!.TrySetCanceled();
        }
    }

    private static ImageBox CreateImage(
        byte[] source,
        IMarkdownRasterDecoder? decoder,
        double scale,
        MarkdownPerformanceSession? session = null,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null,
        string? cacheKey = null)
    {
        var resolver = new RasterResolver(source, cacheKey ?? $"raster-provider-{Guid.NewGuid():N}");
        MarkdownLayoutContext context = DisplaySizedRasterDecodeTests.CreateContext(
            resolver, session, scale, imageUnavailable: imageUnavailable, rasterDecoder: decoder);
        return new ImageBox(context, "https://images.example/asset", string.Empty);
    }

    private static byte[] CreateWebP(int width, int height)
    {
        using var body = new MemoryStream();
        Span<byte> extendedHeader = stackalloc byte[10];
        extendedHeader.Clear();
        WriteUInt24LittleEndian(extendedHeader[4..], checked((uint)(width - 1)));
        WriteUInt24LittleEndian(extendedHeader[7..], checked((uint)(height - 1)));
        WriteWebPChunk(body, "VP8X"u8, extendedHeader);
        WriteWebPChunk(body, "VP8 "u8, [0, 0, 0, 0x9D, 0x01, 0x2A, 0, 0, 0, 0]);
        byte[] chunks = body.ToArray();
        byte[] result = new byte[checked(12 + chunks.Length)];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
        "WEBP"u8.CopyTo(result.AsSpan(8));
        chunks.CopyTo(result, 12);
        return result;
    }

    private static void WriteWebPChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        type.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void WriteUInt24LittleEndian(Span<byte> bytes, uint value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
    }

    private sealed class RasterResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/webp", CacheKey: cacheKey)));
    }

    private sealed class FakeRasterDecoder(params MarkdownRasterFormat[] formats) : IMarkdownRasterDecoder, IDisposable
    {
        private readonly MarkdownRasterFormat[] _formats = formats;
        private int _calls;
        private int _disposeCount;

        internal int Calls => Volatile.Read(ref _calls);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal MarkdownRasterDecodeRequest? LastRequest { get; private set; }
        internal TrackingMemoryOwner? LastOwner { get; private set; }
        internal InvalidProviderOutput InvalidOutput { get; init; }
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
            if (InvalidOutput == InvalidProviderOutput.Null)
                return ValueTask.FromResult<MarkdownRaster>(null!);
            return ValueTask.FromResult(CreateRaster(request));
        }

        internal MarkdownRaster CreateRaster(MarkdownRasterDecodeRequest request)
        {
            int width = InvalidOutput == InvalidProviderOutput.WrongGeometry
                ? checked(request.TargetWidthPixels + 1)
                : request.TargetWidthPixels;
            int height = request.TargetHeightPixels;
            int length = checked(width * height * 4);
            var owner = new TrackingMemoryOwner(length);
            LastOwner = owner;
            MarkdownRasterPixelFormat format = InvalidOutput == InvalidProviderOutput.WrongPixelFormat
                ? MarkdownRasterPixelFormat.Rgba8Premultiplied
                : request.PixelFormat;
            return new MarkdownRaster(owner, width, height, checked(width * 4), format);
        }
    }

    private sealed class TrackingMemoryOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _buffer = new byte[length];

        internal bool IsDisposed => Volatile.Read(ref _buffer) is null;
        public Memory<byte> Memory => Volatile.Read(ref _buffer)
            ?? throw new ObjectDisposedException(nameof(TrackingMemoryOwner));

        public void Dispose() => Interlocked.Exchange(ref _buffer, null);
    }

    public enum InvalidProviderOutput
    {
        None,
        Null,
        WrongGeometry,
        WrongPixelFormat,
    }
}
