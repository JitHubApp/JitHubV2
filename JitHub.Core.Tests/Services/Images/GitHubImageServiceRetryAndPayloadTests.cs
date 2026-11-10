using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.GitHub.Caching;
using JitHub.Core.Services.GitHub.Transport;
using JitHub.Core.Services.Images;
using JitHub.Core.Services.Images.Caching;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.Core.Tests.Services.Images.GitHubImageServiceAdmissionDoubles;
using static JitHub.Core.Tests.Services.Images.GitHubImageServiceHttpDoubles;

namespace JitHub.Core.Tests.Services.Images;

/// <summary>
/// Transient-failure retries and payload validation (size, declared length, signatures
/// and SVG handling) in <c>GitHubImageService</c>.
/// </summary>
public sealed class GitHubImageServiceRetryAndPayloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubImageServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetAsync_RetriesOneTransientStatusAndCachesSuccessfulResponse()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        TransientStatusThenImageHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/owner/repository/releases/download/assets/image.png");

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RecoversAfterTwoTransientStatuses()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        TransientStatusThenImageHandler handler = new(PngBytes, failuresBeforeSuccess: 2);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://camo.githubusercontent.com/retry-example/image.png");

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RetriesOneTransportFailureAndCachesSuccessfulResponse()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        TransientExceptionThenImageHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://raw.githubusercontent.com/owner/repository/main/image.png");

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_DoesNotRetryPermanentHttpStatus()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        AlwaysStatusHandler handler = new(HttpStatusCode.NotFound);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync(
            "https://raw.githubusercontent.com/owner/repository/main/missing.png"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_StopsAfterTwoTransientRetries()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        AlwaysStatusHandler handler = new(HttpStatusCode.ServiceUnavailable);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync(
            "https://raw.githubusercontent.com/owner/repository/main/unavailable.png"));

        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_RetriesTransientGitHubApiFailure()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        using GitHubImageService service = new(store, client);
        int attempts = 0;

        GitHubCachedImage? image = await service.GetOrFetchAsync(
            "https://raw.githubusercontent.com/owner/repository/ref/assets/banner.png",
            (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new GitHubApiException(
                        HttpStatusCode.ServiceUnavailable,
                        "Transient repository content failure.");
                }

                return Task.FromResult<GitHubImageDownload?>(
                    new GitHubImageDownload(PngBytes, "image/png"));
            });

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task GetOrFetchAsync_DoesNotRetryPermanentGitHubApiFailure()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        using GitHubImageService service = new(store, client);
        int attempts = 0;

        await Assert.ThrowsAsync<GitHubApiException>(() => service.GetOrFetchAsync(
            "https://raw.githubusercontent.com/owner/repository/ref/assets/missing.png",
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new GitHubApiException(HttpStatusCode.NotFound, "Not found.");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GetAsync_RejectsOversizedPayloadBeforeCaching()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new OversizedImageHandler());
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/oversized.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task GetAsync_RejectsIncorrectDeclaredImageLength(int declaredLength)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new DeclaredLengthImageHandler(declaredLength));
        using GitHubImageService service = new(store, client);

        Task<GitHubCachedImage?> fetch = service.GetAsync(
            $"https://images.example.com/length-{declaredLength}.png",
            GitHubImageFetchScope.UserApprovedHttps);
        if (declaredLength < PngBytes.Length)
            await Assert.ThrowsAsync<InvalidDataException>(() => fetch);
        else
            await Assert.ThrowsAsync<EndOfStreamException>(() => fetch);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("image/svg+xml", "<html><body>not svg</body></html>")]
    [InlineData("image/png", "not a png")]
    public async Task GetAsync_RejectsMismatchedImageSignatures(string contentType, string body)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler(contentType, System.Text.Encoding.UTF8.GetBytes(body)));
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/malformed",
            GitHubImageFetchScope.UserApprovedHttps));
    }

    [Fact]
    public async Task GetAsync_RetriesTruncatedCamoSvgWithoutCachingOrLeakingAdmission()
    {
        const string source = "https://camo.githubusercontent.com/example-badge.svg";
        byte[] truncated = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        byte[] complete = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"124\" height=\"20\"></svg>"u8.ToArray();
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(truncated, complete);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(4096);

        GitHubCachedImage? image = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub, admission);
        GitHubCachedImage? cached = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub, admission);

        Assert.NotNull(image);
        Assert.Equal(complete, image.Bytes);
        Assert.Equal(complete, await File.ReadAllBytesAsync(image.FilePath));
        Assert.True(cached!.IsFromCache);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task GetAsync_AcceptsLegacyExternalDoctypeCamoSvgWithoutRetry()
    {
        const string source = "https://camo.githubusercontent.com/legacy-creative-commons.svg";
        byte[] complete = System.Text.Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='utf-8'?>" +
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.0//EN' " +
            "'http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'>" +
            "<rect width='64' height='64'/></svg>");
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(complete);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub);
        GitHubCachedImage? cached = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub);

        Assert.NotNull(image);
        Assert.Equal(complete, image.Bytes);
        Assert.True(cached!.IsFromCache);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_AcceptsLegacyExternalDoctypeFromSignedUserAttachment()
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        byte[] complete = System.Text.Encoding.UTF8.GetBytes(
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8' />");
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectThenImageHandler handler = new(signedAsset, complete, "image/svg+xml");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/user-attachments/assets/9955dda9-1234-5678-9abc-def012345678");

        Assert.NotNull(image);
        Assert.Equal(complete, image.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_ExhaustsBoundedMalformedCamoSvgRetryWithoutCaching()
    {
        byte[] truncated = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(truncated);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(4096);

        await Assert.ThrowsAnyAsync<IOException>(() => service.GetAsync(
            "https://camo.githubusercontent.com/always-truncated.svg",
            GitHubImageFetchScope.TrustedGitHub,
            admission));

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GetAsync_DoesNotRetryMalformedSvgFromUnrelatedHost()
    {
        byte[] truncated = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(truncated);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/invalid.svg",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_AcceptsRasterSignatureDespiteSvgMediaType()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler("image/svg+xml", PngBytes));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://camo.githubusercontent.com/mislabeled-raster",
            GitHubImageFetchScope.TrustedGitHub);

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(PngBytes, image.Bytes);
    }

    [Fact]
    public async Task GetAsync_AcceptsSignatureValidatedJpegWithGenericMediaType()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        using HttpClient client = new(new RawImageHandler("application/octet-stream", jpeg));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://images.example.com/release-asset",
            GitHubImageFetchScope.UserApprovedHttps);

        Assert.NotNull(image);
        Assert.Equal("image/jpeg", image!.ContentType);
        Assert.Equal(jpeg, image.Bytes);
    }

    [Fact]
    public async Task GetAsync_RejectsGenericMediaTypeWithoutSupportedImageSignature()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler(
            "application/octet-stream",
            "<html>not an image</html>"u8.ToArray()));
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/release-asset",
            GitHubImageFetchScope.UserApprovedHttps));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
