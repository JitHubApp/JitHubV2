using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;
using JitHub.Services;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Which hosts, schemes, addresses and redirects <c>GitHubImageService</c> may contact
/// for each fetch scope.
/// </summary>
public sealed class GitHubImageServiceDestinationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubImageServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetOrFetchAsync_AcceptsEveryAdvertisedMarkdownImageFormat()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        using GitHubImageService service = new(store, client);
        (string ContentType, byte[] Bytes)[] formats =
        [
            ("image/bmp", [0x42, 0x4D, 1, 2]),
            ("image/x-icon", [0, 0, 1, 0, 1, 2]),
            ("image/tiff", [(byte)'I', (byte)'I', 0x2A, 0, 1, 2])
        ];

        foreach ((string contentType, byte[] bytes) in formats)
        {
            GitHubCachedImage? image = await service.GetOrFetchAsync(
                $"https://content.example.com/{Guid.NewGuid():N}",
                (_, _) => Task.FromResult<GitHubImageDownload?>(new GitHubImageDownload(bytes, contentType)));
            Assert.NotNull(image);
            Assert.True(File.Exists(image!.FilePath));
        }
    }

    [Fact]
    public async Task GetAsync_DoesNotContactArbitraryHttpsHostsByDefault()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync("https://images.example.com/tracker.png");

        Assert.Null(image);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_UserApprovedHttpsScope_AllowsPublicThirdPartyHost()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps);

        Assert.NotNull(image);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/owner/repo/main/image.png")]
    [InlineData("https://localhost/image.png")]
    [InlineData("https://127.0.0.1/image.png")]
    [InlineData("https://10.0.0.8/image.png")]
    [InlineData("https://100.64.0.8/image.png")]
    [InlineData("https://192.0.2.8/image.png")]
    [InlineData("https://198.18.0.8/image.png")]
    [InlineData("https://198.51.100.8/image.png")]
    [InlineData("https://203.0.113.8/image.png")]
    [InlineData("https://[::1]/image.png")]
    [InlineData("https://[2001:db8::8]/image.png")]
    [InlineData("https://[fc00::8]/image.png")]
    [InlineData("https://host.local/image.png")]
    [InlineData("https://host.internal/image.png")]
    public async Task GetAsync_NeverContactsInsecureOrPrivateDestinations(string source)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            source,
            GitHubImageFetchScope.UserApprovedHttps);

        Assert.Null(image);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_TrustedRequestCannotRedirectToThirdParty()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new("https://images.example.com/tracker.png");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetAsync("https://raw.githubusercontent.com/owner/repo/main/image.png"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_TrustedUserAttachmentCanFollowGitHubSignedAssetRedirect()
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectThenImageHandler handler = new(signedAsset, PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/user-attachments/assets/9955dda9-1234-5678-9abc-def012345678");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_TrustedLegacyRepositoryAssetCanFollowGitHubSignedAssetRedirect()
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectThenImageHandler handler = new(signedAsset, PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/owner/repository/assets/13230914/5a17bdbe-097a-4100-8363-40255b70f6e3");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("https://github.com/owner/repository/assets/not-a-number/5a17bdbe-097a-4100-8363-40255b70f6e3")]
    [InlineData("https://github.com/owner/repository/assets/13230914/not-a-guid")]
    [InlineData("https://github.com/owner/repository/issues/assets/13230914/5a17bdbe-097a-4100-8363-40255b70f6e3")]
    public async Task GetAsync_TrustedMalformedLegacyRepositoryAssetRejectsSignedRedirect(string source)
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new(signedAsset);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(source));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg")]
    [InlineData("https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg?X-Amz-Signature=abc123")]
    [InlineData("https://github-production-user-asset-evil.s3.amazonaws.com/1/2.svg?X-Amz-Credential=x&X-Amz-Signature=y")]
    public async Task GetAsync_TrustedUserAttachmentRejectsUnsignedOrUnknownAssetRedirect(
        string destination)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new(destination);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://github.com/user-attachments/assets/9955dda9-1234-5678-9abc-def012345678"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_UserApprovedRequestCannotRedirectToPrivateAddress()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new("https://127.0.0.1/tracker.png");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsHostNameThatResolvesToPrivateAddress()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(
            store,
            client,
            hostAddressResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.8") }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsMixedPublicAndPrivateDnsAnswers()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(
            store,
            client,
            hostAddressResolver: (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("fd00::8")
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsRedirectHostNameThatResolvesToPrivateAddress()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new("https://private.example.com/tracker.png");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(
            store,
            client,
            hostAddressResolver: (host, _) => Task.FromResult(new[]
            {
                IPAddress.Parse(host.Equals("private.example.com", StringComparison.OrdinalIgnoreCase)
                    ? "192.168.1.20"
                    : "8.8.8.8")
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_StopsAfterBoundedRedirectCount()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        LoopingRedirectHandler handler = new();
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/loop-0.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(4, handler.RequestCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
