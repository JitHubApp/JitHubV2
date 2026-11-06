using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JitHub.Markdown.Images;
using JitHub.Services;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceLeasedTestDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// How <c>GitHubImageService</c> hands out leased image bytes: which reservation each
/// handle owns, when it is released, and which scratch buffers stay reachable.
/// </summary>
public sealed class GitHubImageServiceLeasedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubImageServiceLeasedTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void InvalidCarrierConstructionDoesNotTakeReservationOwnership()
    {
        var reservation = new TrackingDisposable();

        Assert.Throws<ArgumentException>(() => new GitHubImageSourceLease(
            new GitHubCachedImage("test", [], "image/png", false),
            reservation));

        Assert.Equal(0, reservation.DisposeCount);
    }

    [Fact]
    public void DisposedHandlesDoNotKeepTheSourceArrayAlive()
    {
        (GitHubImageSourceLease[] handles, WeakReference<byte[]> bytes) = CreateDisposedHandles();

        for (int attempt = 0; attempt < 3 && IsAlive(bytes); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(IsAlive(bytes));
        GC.KeepAlive(handles);
    }

    [Fact]
    public async Task MemoryCacheHitKeepsExactReservationUntilLastHandleIsDisposed()
    {
        using var service = CreateService(new ImageResponseHandler(PngBytes));
        Assert.NotNull(await service.GetAsync("https://avatars.githubusercontent.com/u/leased.png"));
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        GitHubImageSourceLease? first = await leased.TryGetCachedLeasedAsync(
            "https://avatars.githubusercontent.com/u/leased.png",
            GitHubImageFetchScope.TrustedGitHub,
            admission);
        Assert.NotNull(first);
        Assert.Equal(PngBytes, first!.Image.Bytes);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes.Length, admission.LastReservationBytes);

        GitHubImageSourceLease second = first.Retain();
        first.Dispose();
        first.Dispose();
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes, second.Image.Bytes);

        second.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Throws<ObjectDisposedException>(() => _ = second.Image);
    }

    [Fact]
    public async Task ColdDiskCacheHitTransfersAdmissionReservationToConsumer()
    {
        const string source = "https://images.example.com/leased-disk.png";
        using (GitHubImageService populate = CreateService(new ImageResponseHandler(PngBytes)))
            Assert.NotNull(await populate.GetAsync(source, GitHubImageFetchScope.UserApprovedHttps));

        var noNetwork = new ImageResponseHandler(PngBytes);
        using GitHubImageService service = CreateService(noNetwork);
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        using GitHubImageSourceLease? image = await leased.TryGetCachedLeasedAsync(
            source,
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Image.Bytes);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(0, noNetwork.RequestCount);
        image.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task HttpFetchTransfersPreallocationReservationThroughCacheCommitAndConsumerLifetime()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        GitHubImageSourceLease? image = await leased.GetLeasedAsync(
            "https://images.example.com/leased-http.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Image.Bytes);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes.Length, admission.LastReservationBytes);
        image.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task UnknownLengthChunkedHttpFetchIsAdmittedBeforeItsFirstRead()
    {
        var admission = new TrackingAdmission(maximumReservationBytes: 64);
        var handler = new UnknownLengthImageResponseHandler(
            PngBytes,
            () => Assert.True(admission.ActiveBytes >= PngBytes.Length));
        using GitHubImageService service = CreateService(handler);
        IGitHubLeasedImageService leased = service;

        GitHubImageSourceLease? image = await leased.GetLeasedAsync(
            "https://images.example.com/leased-chunked.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Image.Bytes);
        Assert.Equal(64, admission.ActiveBytes);
        image.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task RejectedNonCamoSvgReleasesAdmissionAndFaultedTaskDoesNotRetainReadBuffer()
    {
        RejectedSvgRun run = StartRejectedNonCamoSvgFetch();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => run.Task);
            Assert.Equal(0, run.Admission.ActiveBytes);

            WeakReference<byte[]>[] readBuffers = Assert.IsType<SpillCaptureImageContent>(run.Handler.Content)
                .Stream.CapturedBuffers;
            Assert.NotEmpty(readBuffers);
            for (int attempt = 0; attempt < 5 && AreAnyBuffersAlive(readBuffers); attempt++)
                ForceCollection();

            Assert.False(AreAnyBuffersAlive(readBuffers),
                "The failed fetch task retained a source buffer after releasing its reservation.");
            GC.KeepAlive(run.Task);
        }
        finally
        {
            run.Service.Dispose();
        }
    }

    [Fact]
    public async Task UnknownLengthSpillDropsScratchBuffersBeforeWaitingForFinalAdmission()
    {
        const int sourceLength = (1024 * 1024) + 4096;
        byte[] sourceBytes = new byte[sourceLength];
        PngBytes.AsSpan(0, 8).CopyTo(sourceBytes);
        for (int index = PngBytes.Length; index < sourceBytes.Length; index++)
            sourceBytes[index] = (byte)(index * 31);

        var admission = new BlockingFinalGrantAdmission(maximumReservationBytes: 2L * 1024 * 1024);
        var handler = new SpillCaptureImageResponseHandler(sourceBytes);
        using GitHubImageService service = CreateService(handler);
        IGitHubLeasedImageService leased = service;
        Task<GitHubImageSourceLease?> pending = leased.GetLeasedAsync(
            "https://images.example.com/leased-spill-scratch.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        GitHubImageSourceLease? image = null;
        Exception? completionFailure = null;
        try
        {
            await admission.FinalGrantRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(sourceLength, admission.FinalGrantBytes);
            Assert.Equal(0, admission.ActiveBytes);
            WeakReference<byte[]>[] scratchBuffers = Assert.IsType<SpillCaptureImageContent>(
                handler.Content).Stream.CapturedBuffers;
            Assert.Equal(2, scratchBuffers.Length);

            ForceCollection();
            Assert.False(AreAnyBuffersAlive(scratchBuffers));
        }
        finally
        {
            admission.ReleaseFinalGrant.TrySetResult();
            try
            {
                image = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception)
            {
                completionFailure = exception;
            }
        }

        if (completionFailure is not null)
            throw new InvalidOperationException("The admitted spill fetch did not complete after releasing its final grant.", completionFailure);

        try
        {
            Assert.NotNull(image);
            Assert.Equal(sourceBytes, image!.Image.Bytes);
            Assert.Equal(sourceLength, admission.ActiveBytes);
            Assert.Equal(sourceLength, admission.LastGrantedBytes);
        }
        finally
        {
            image?.Dispose();
        }

        Assert.Equal(0, admission.ActiveBytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task StaleRefreshReturnsAConsumerLeasedCachedGeneration(HttpStatusCode refreshStatus)
    {
        const string source = "https://avatars.githubusercontent.com/u/leased-stale.png";
        var handler = new RevalidationHandler(PngBytes, refreshStatus);
        using GitHubImageService service = CreateService(handler);
        GitHubCachedImage initial = (await service.GetAsync(source))!;
        File.SetLastWriteTimeUtc(initial.FilePath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(8)));
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        using GitHubImageSourceLease? stale = await leased.GetLeasedAsync(
            source,
            GitHubImageFetchScope.TrustedGitHub,
            admission);

        Assert.NotNull(stale);
        Assert.Equal(PngBytes, stale!.Image.Bytes);
        Assert.True(stale.Image.IsFromCache);
        Assert.Equal(refreshStatus == HttpStatusCode.NotFound, stale.Image.IsStale);
        Assert.Null(stale.Image.RefreshTask);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        stale.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    private GitHubImageService CreateService(HttpMessageHandler handler)
    {
        Directory.CreateDirectory(_root);
        return new GitHubImageService(
            new GitHubImageCacheStore(_root, GitHubCachePolicy.Default),
            new HttpClient(handler),
            ownsHttpClient: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RejectedSvgRun StartRejectedNonCamoSvgFetch()
    {
        byte[] malformedSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        TrackingAdmission admission = new();
        var handler = new SpillCaptureImageResponseHandler(
            malformedSvg,
            contentType: "image/svg+xml",
            knownLength: true);
        GitHubImageService service = CreateService(handler);
        Task<GitHubCachedImage?> task = service.GetAsync(
            "https://images.example.com/malformed.svg",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);
        return new RejectedSvgRun(service, handler, admission, task);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
