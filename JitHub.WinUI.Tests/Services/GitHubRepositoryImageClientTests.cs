using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositoryImageClientTests
{
    [Fact]
    public async Task RawContentsRequestUsesAuthenticatedEscapedPathAndTransfersSourceLease()
    {
        TrackingAdmission admission = new(32);
        TrackingReadStream stream = new([1, 2, 3], onRead: admission.RecordRead);
        RequestRecordingHandler handler = new(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer secret-token", request.Headers.Authorization?.ToString());
            Assert.Equal("application/vnd.github.raw+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal(
                "/repos/o%20cto/repo/contents/images/a%20b.png?ref=refs%2Fheads%2Ffeature%2Fnext",
                request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateContent(stream, 3, "image/png")
            };
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        GitHubImageDownload? download = await ((IGitHubRepositoryImageClient)client)
            .GetRepositoryImageContentAsync(
                "secret-token", "o cto", "repo", "images/a b.png", "refs/heads/feature/next", admission);

        Assert.NotNull(download);
        Assert.Equal(new byte[] { 1, 2, 3 }, download.Bytes);
        Assert.Equal(3, admission.ReservedBytes);
        Assert.True(admission.ReadStartedUnderReservation);

        using GitHubMarkdownImageCandidate candidate = GitHubMarkdownImageCandidate.Resolved(
            new MarkdownImageAsset(download.Bytes!, "image/png"),
            download.SourceByteLease);
        using (MarkdownImageResolutionLease rendererLease = candidate.DetachLease())
            Assert.Equal(3, admission.ReservedBytes);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task ChunkedRawContentReadsBoundedBytesAndKeepsItsReservationUntilReleased()
    {
        byte[] expected = [5, 4, 3, 2, 1];
        TrackingAdmission admission = new(16);
        using StreamContent content = CreateContent(expected, length: null, chunkSize: 2);

        (byte[] bytes, IDisposable? lease) = await AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission: admission,
            cancellationToken: CancellationToken.None);

        try
        {
            Assert.Equal(expected, bytes);
            Assert.Equal(16, admission.ReservedBytes);
            Assert.True(admission.ReadStartedUnderReservation);
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task LargeChunkedBodySpillsAndReleasesScratchBeforeFinalReservation()
    {
        byte[] expected = new byte[(1024 * 1024) + 4096];
        new Random(41).NextBytes(expected);
        TrackingAdmission admission = new(8L * 1024 * 1024);
        using StreamContent content = CreateContent(
            expected,
            length: null,
            chunkSize: 64 * 1024,
            onRead: admission.RecordRead);

        (byte[] bytes, IDisposable? lease) = await AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 4 * 1024 * 1024,
            admission: admission,
            cancellationToken: CancellationToken.None);

        try
        {
            Assert.Equal(expected, bytes);
            Assert.Equal(2, admission.ReserveCalls);
            Assert.Equal(0, admission.ReservedBeforeSecondRequest);
            Assert.Equal(expected.Length, admission.ReservedBytes);
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task FragmentedSpoolReadsFillTheAdmittedScratchBeforeWritingBatches()
    {
        byte[] expected = new byte[40];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = (byte)index;

        TrackingAdmission admission = new(64);
        TrackingReadStream stream = new(expected, chunkSize: 2);
        using StreamContent content = CreateContent(stream, length: null);

        (byte[] bytes, IDisposable? lease) = await AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            CancellationToken.None);

        try
        {
            Assert.Equal(expected, bytes);
            Assert.Equal(40, admission.ReservedBytes);
            // 32 bytes fill the initial scratch, followed by the one-byte spill
            // probe. The remaining seven bytes are assembled across partial
            // transport reads into one scratch batch; each request addresses
            // only the still-unused suffix of that same admitted array.
            Assert.Equal(
                new[] { 32, 30, 28, 26, 25 },
                stream.DestinationBuffers.TakeLast(5)
                    .Select(destination => destination.RequestedLength));
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task DeclaredOverLimitIsRejectedBeforeReservationOrBodyRead()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([1, 2, 3, 4], onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: 4);

        await Assert.ThrowsAsync<InvalidDataException>(() => AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 3,
            admission: admission,
            cancellationToken: CancellationToken.None));

        Assert.Equal(0, admission.ReserveCalls);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task KnownLengthAboveVisibleAdmissionIsRejectedBeforeRead()
    {
        TrackingAdmission admission = new(4);
        TrackingReadStream stream = new([1, 2, 3, 4, 5, 6], onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: 6);

        await Assert.ThrowsAsync<InvalidDataException>(() => AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission: admission,
            cancellationToken: CancellationToken.None));

        Assert.Equal(0, admission.ReserveCalls);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task KnownLengthAboveSpeculativeAdmissionIsDeferredBeforeRead()
    {
        TrackingAdmission admission = new(4, speculative: true);
        TrackingReadStream stream = new([1, 2, 3, 4, 5, 6], onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: 6);

        await Assert.ThrowsAsync<MarkdownImageSourceDeferredException>(() =>
            AdmittedImageContentReader.ReadAsync(
                content,
                maximumBytes: 8,
                admission: admission,
                cancellationToken: CancellationToken.None));

        Assert.Equal(0, admission.ReserveCalls);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task UnknownLengthOverLimitReleasesScratchReservation()
    {
        TrackingAdmission admission = new(8);
        TrackingReadStream stream = new([1, 2, 3, 4], chunkSize: 2, onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 3,
            admission: admission,
            cancellationToken: CancellationToken.None));

        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
        Assert.Equal(4, stream.BytesRead);
    }

    [Fact]
    public async Task ChunkedVisibleBodyStopsAfterAdmissionLimitProbeAndReleasesReservation()
    {
        TrackingAdmission admission = new(4);
        TrackingReadStream stream = new(
            new byte[32], chunkSize: 2, onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission: admission,
            cancellationToken: CancellationToken.None));

        Assert.Equal(5, stream.BytesRead); // Four admitted bytes plus one proof byte.
        Assert.True(stream.BytesRead < 32);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task ChunkedSpeculativeBodyDefersAtAdmissionLimitProbeAndReleasesReservation()
    {
        TrackingAdmission admission = new(4, speculative: true);
        TrackingReadStream stream = new(
            new byte[32], chunkSize: 2, onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: null);

        await Assert.ThrowsAsync<MarkdownImageSourceDeferredException>(() =>
            AdmittedImageContentReader.ReadAsync(
                content,
                maximumBytes: 64,
                admission: admission,
                cancellationToken: CancellationToken.None));

        Assert.Equal(5, stream.BytesRead); // Four admitted bytes plus one proof byte.
        Assert.True(stream.BytesRead < 32);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task FaultedKnownLengthReaderTaskDoesNotRetainItsDestinationBuffer()
    {
        ReaderRun run = StartTruncatedKnownLengthRead();
        using (run.Content)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => run.Task);
            Assert.NotEmpty(run.DestinationBuffers);
            AssertCollected(run.DestinationBuffers);
            Assert.Equal(0, run.Admission.ReservedBytes);
            GC.KeepAlive(run.Task);
        }
    }

    [Fact]
    public async Task FaultedChunkedReaderTaskDoesNotRetainScratchOrProbeBuffers()
    {
        ReaderRun run = StartChunkedAdmissionFailure(speculative: false);
        using (run.Content)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => run.Task);
            Assert.Contains(run.DestinationBuffers, destination => destination.RequestedLength == 1);
            AssertCollected(run.DestinationBuffers);
            Assert.Equal(0, run.Admission.ReservedBytes);
            GC.KeepAlive(run.Task);
        }
    }

    [Fact]
    public async Task SuccessfulShortCopyReleasesScratchWhileTaskResultAndLeaseRemainRetained()
    {
        ReaderRun run = StartChunkedShortRead();
        using (run.Content)
        {
            (byte[] bytes, IDisposable? lease) = await run.Task;
            try
            {
                Assert.Equal(new byte[] { 5, 4, 3, 2, 1 }, bytes);
                Assert.Equal(16, run.Admission.ReservedBytes);
                AssertCollected(run.DestinationBuffers);
                GC.KeepAlive(bytes);
                GC.KeepAlive(lease);
                GC.KeepAlive(run.Task);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        Assert.Equal(0, run.Admission.ReservedBytes);
    }

    [Fact]
    public async Task SuccessfulSpoolCopyDoesNotRetainScratchWhileTaskResultAndLeaseRemainRetained()
    {
        ReaderRun run = StartChunkedSpoolRead();
        using (run.Content)
        {
            (byte[] bytes, IDisposable? lease) = await run.Task;
            try
            {
                Assert.Equal(new byte[40], bytes);
                Assert.Equal(2, run.Admission.ReserveCalls);
                Assert.Equal(0, run.Admission.ReservedBeforeSecondRequest);
                Assert.Equal(40, run.Admission.ReservedBytes);
                AssertCollected(run.DestinationBuffers);
                GC.KeepAlive(bytes);
                GC.KeepAlive(lease);
                GC.KeepAlive(run.Task);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        Assert.Equal(0, run.Admission.ReservedBytes);
    }

    [Fact]
    public async Task CancellationWhileFinalSpoolAdmissionWaitsDoesNotRetainScratchBuffers()
    {
        using CancellationTokenSource cancellation = new();
        ReaderRun run = StartChunkedFinalAdmissionWait(cancellation.Token);
        using (run.Content)
        {
            await run.Admission.SecondReservationStarted.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, run.Admission.ReserveCalls);
            Assert.Equal(40, run.Admission.LastRequestedBytes);
            Assert.Equal(0, run.Admission.ReservedBeforeSecondRequest);
            Assert.Equal(0, run.Admission.ReservedBytes);

            // The reader task remains live while it waits for final admission;
            // its scratch destinations must already have been retired.
            AssertCollected(run.DestinationBuffers);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Task);
            Assert.Equal(0, run.Admission.ReservedBytes);
            Assert.Equal(0, run.Admission.InvalidReleaseCount);
            GC.KeepAlive(run.Task);
        }
    }

    [Fact]
    public async Task TruncatedKnownLengthReleasesReservation()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([1, 2], onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: 4);

        await Assert.ThrowsAsync<EndOfStreamException>(() => AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission: admission,
            cancellationToken: CancellationToken.None));

        Assert.Equal(4, admission.LastRequestedBytes);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task KnownLengthProbeRejectsBytesBeyondDeclaredEofAndReleasesReservation()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([1, 2, 3, 4], onRead: admission.RecordRead);
        using StreamContent content = CreateContent(stream, length: 3);

        await Assert.ThrowsAsync<InvalidDataException>(() => AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission: admission,
            cancellationToken: CancellationToken.None));

        Assert.Equal(3, admission.LastRequestedBytes);
        Assert.Equal(4, stream.BytesRead);
        Assert.Equal(0, admission.ReservedBytes);
    }

    [Fact]
    public async Task CancellationDuringKnownLengthReadReleasesReservation()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new(blockReads: true, onRead: () => admission.RecordRead());
        using StreamContent content = CreateContent(stream, length: 4);
        using CancellationTokenSource cancellation = new();
        Task<(byte[] Bytes, IDisposable? Lease)> read = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission: admission,
            cancellationToken: cancellation.Token);

        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, admission.ReservedBytes);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task RawClientRejectsDeclaredOversizeBeforeRequestReservationAndBodyRead()
    {
        TrackingAdmission admission = new(GitHubImageService.MaxImageBytes);
        TrackingReadStream stream = new([1]);
        RequestRecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateContent(stream, (long)GitHubImageService.MaxImageBytes + 1)
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ((IGitHubRepositoryImageClient)client).GetRepositoryImageContentAsync(
                "token", "owner", "repo", "image.png", "main", admission));

        Assert.Equal(0, admission.ReserveCalls);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task EmptyUnknownLengthRawResponseDoesNotLeakItsScratchReservation()
    {
        TrackingAdmission admission = new(8);
        RequestRecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateContent(Array.Empty<byte>(), length: null)
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        GitHubImageDownload? download = await ((IGitHubRepositoryImageClient)client)
            .GetRepositoryImageContentAsync(
            "token", "owner", "repo", "image.png", null, admission);

        Assert.Null(download);
        Assert.Equal(8, admission.LastRequestedBytes);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, admission.InvalidReleaseCount);
    }

    [Fact]
    public async Task OversizedErrorBodyIsNotReadAndStillMapsAuthenticationStatus()
    {
        byte[] body = new byte[70 * 1024];
        TrackingReadStream stream = new(body, chunkSize: 4096);
        RequestRecordingHandler handler = new(request => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = CreateContent(stream, body.Length, "application/json"),
            RequestMessage = request
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);
        TrackingAdmission admission = new(GitHubImageService.MaxImageBytes);

        GitHubAuthenticationException error = await Assert.ThrowsAsync<GitHubAuthenticationException>(() =>
            ((IGitHubRepositoryImageClient)client).GetRepositoryImageContentAsync(
                "secret-token", "owner", "repo", "image.png", null, admission));

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("secret-token"))),
            error.RejectedTokenFingerprint);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, admission.ReserveCalls);
    }

    [Fact]
    public async Task OversizedRateLimitBodyStillUsesStatusAndHeaders()
    {
        byte[] body = new byte[70 * 1024];
        TrackingReadStream stream = new(body, chunkSize: 4096);
        RequestRecordingHandler handler = new(request =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
            {
                Content = CreateContent(stream, length: null, contentType: "application/json"),
                RequestMessage = request
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(37));
            return response;
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);
        TrackingAdmission admission = new(GitHubImageService.MaxImageBytes);

        GitHubRateLimitException error = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            ((IGitHubRepositoryImageClient)client).GetRepositoryImageContentAsync(
                "token", "owner", "repo", "image.png", null, admission));

        Assert.Equal(TimeSpan.FromSeconds(37), error.RetryDelay);
        Assert.Equal((64 * 1024) + 1, stream.BytesRead);
    }

    private static StreamContent CreateContent(
        byte[] bytes,
        long? length,
        string? contentType = null,
        int chunkSize = int.MaxValue,
        Action? onRead = null) =>
        CreateContent(new TrackingReadStream(bytes, chunkSize, onRead: onRead), length, contentType);

    private static StreamContent CreateContent(
        TrackingReadStream stream,
        long? length,
        string? contentType = null)
    {
        StreamContent content = new(stream);
        if (length is long declaredLength)
            content.Headers.ContentLength = declaredLength;
        if (contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReaderRun StartTruncatedKnownLengthRead()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([1, 2], onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: 4);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReaderRun StartChunkedAdmissionFailure(bool speculative)
    {
        TrackingAdmission admission = new(4, speculative);
        TrackingReadStream stream = new(new byte[32], chunkSize: 2, onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReaderRun StartChunkedShortRead()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([5, 4, 3, 2, 1], chunkSize: 2, onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReaderRun StartChunkedFinalAdmissionWait(CancellationToken cancellationToken)
    {
        TrackingAdmission admission = new(64, blockSecondReservation: true);
        TrackingReadStream stream = new(new byte[40], onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            cancellationToken);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReaderRun StartChunkedSpoolRead()
    {
        TrackingAdmission admission = new(64);
        TrackingReadStream stream = new(new byte[40], onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(DestinationBuffer[] destinations)
    {
        foreach (DestinationBuffer destination in destinations)
        {
            for (int attempt = 0; attempt < 5 && IsAlive(destination.Buffer); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(IsAlive(destination.Buffer),
                $"A {destination.RequestedLength}-byte read destination remained rooted after completion.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> reference) => reference.TryGetTarget(out _);

    private sealed record ReaderRun(
        Task<(byte[] Bytes, IDisposable? Lease)> Task,
        TrackingReadStream Stream,
        TrackingAdmission Admission,
        StreamContent Content)
    {
        public DestinationBuffer[] DestinationBuffers => Stream.DestinationBuffers;
    }

    private sealed record DestinationBuffer(WeakReference<byte[]> Buffer, int RequestedLength);

    private sealed class TrackingAdmission(
        long maximumReservationBytes,
        bool speculative = false,
        bool blockSecondReservation = false) : IMarkdownImageSourceByteAdmission
    {
        private long _reservedBytes;
        private int _reserveCalls;
        private int _invalidReleaseCount;
        private long _lastRequestedBytes;
        private long _reservedBeforeSecondRequest;
        private bool _readStartedUnderReservation = true;
        private readonly bool _blockSecondReservation = blockSecondReservation;
        private readonly TaskCompletionSource _secondReservationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSpeculative { get; } = speculative;

        public long MaximumReservationBytes { get; } = maximumReservationBytes;

        public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

        public int ReserveCalls => Volatile.Read(ref _reserveCalls);

        public int InvalidReleaseCount => Volatile.Read(ref _invalidReleaseCount);

        public long LastRequestedBytes => Interlocked.Read(ref _lastRequestedBytes);

        public long ReservedBeforeSecondRequest => Interlocked.Read(ref _reservedBeforeSecondRequest);

        public bool ReadStartedUnderReservation => _readStartedUnderReservation;

        public Task SecondReservationStarted => _secondReservationStarted.Task;

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0 || bytes > MaximumReservationBytes)
                throw new InvalidOperationException("The test admission received an invalid reservation request.");

            int call = Interlocked.Increment(ref _reserveCalls);
            if (call == 2)
                Interlocked.Exchange(ref _reservedBeforeSecondRequest, ReservedBytes);
            Interlocked.Exchange(ref _lastRequestedBytes, bytes);
            if (call == 2 && _blockSecondReservation)
            {
                _secondReservationStarted.TrySetResult();
                return new ValueTask<IDisposable>(WaitForCancellationAsync(cancellationToken));
            }

            Interlocked.Add(ref _reservedBytes, bytes);
            return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
        }

        private static async Task<IDisposable> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The test reservation should only finish by cancellation.");
        }

        public void RecordRead()
        {
            if (ReservedBytes <= 0)
                _readStartedUnderReservation = false;
        }

        private void Release(long bytes)
        {
            long remaining = Interlocked.Add(ref _reservedBytes, -bytes);
            if (remaining < 0)
                Interlocked.Increment(ref _invalidReleaseCount);
        }

        private sealed class Reservation(TrackingAdmission owner, long bytes) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Release(bytes);
            }
        }
    }

    private sealed class RequestRecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TrackingReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _chunkSize;
        private readonly bool _blockReads;
        private readonly Action? _onRead;
        private readonly List<DestinationBuffer> _destinationBuffers = [];
        private int _position;
        private long _bytesRead;

        public TrackingReadStream(
            byte[]? bytes = null,
            int chunkSize = int.MaxValue,
            bool blockReads = false,
            Action? onRead = null)
        {
            _bytes = bytes ?? [];
            _chunkSize = chunkSize;
            _blockReads = blockReads;
            _onRead = onRead;
        }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public DestinationBuffer[] DestinationBuffers => _destinationBuffers.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _destinationBuffers.Add(new DestinationBuffer(new WeakReference<byte[]>(buffer), count));
            _onRead?.Invoke();
            int read = Copy(buffer.AsSpan(offset, count));
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment) && segment.Array is byte[] array)
                _destinationBuffers.Add(new DestinationBuffer(
                    new WeakReference<byte[]>(array), segment.Count));
            _onRead?.Invoke();
            ReadStarted.TrySetResult();
            return _blockReads
                ? new ValueTask<int>(WaitForCancellationAsync(cancellationToken))
                : ValueTask.FromResult(ReadAndCount(buffer));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        private int ReadAndCount(Memory<byte> buffer)
        {
            int read = Copy(buffer.Span);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        private int Copy(Span<byte> destination)
        {
            int count = Math.Min(Math.Min(destination.Length, _chunkSize), _bytes.Length - _position);
            _bytes.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
