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
using JitHub.Core.Services.GitHub;
using JitHub.Core.Services.GitHub.Transport;
using JitHub.Core.Services.Images;
using JitHub.Core.Services.Images.Fetching;
using JitHub.Core.Tests.TestSupport;
using JitHub.Markdown.Images;
using JitHub.Markdown.Images.Caching;
using JitHub.Markdown.Images.Repository;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;
using static JitHub.Core.Tests.Services.Images.Fetching.GitHubRepositoryImageClientTestDoubles;

namespace JitHub.Core.Tests.Services.Images.Fetching;

/// <summary>
/// Requests, size bounds, byte reservations and status mapping of the repository image
/// client and the admitted content reader behind it.
/// </summary>
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

        await stream.ReadStarted.Task.WaitAsync(AsyncTestWait.HangGuard);
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
}
