using System.Security.Cryptography;
using System.Text;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownSameByteAuditImageResolverTests
{
    private sealed record FixtureAsset(string Source, byte[] Bytes, string MimeType);

    [Fact]
    public async Task LoadsExactPinnedReadmeBytesForNativeDocument()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = fixture.Resolver();
        string path = await resolver.GetPinnedReadmePathAsync(CancellationToken.None);
        byte[] bytes = await resolver.LoadPinnedReadmeBytesAsync(
            path, CancellationToken.None);

        Assert.Equal("README.md", path);
        Assert.Equal(fixture.PinnedReadmeBytes, bytes);
    }

    [Fact]
    public async Task ReadmePathMismatchFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Resolver().LoadPinnedReadmeBytesAsync(
                "other/README.md", CancellationToken.None));
    }

    [Fact]
    public async Task ReadmeChangedAfterAdmissionFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = fixture.Resolver();
        _ = await resolver.LoadPinnedReadmeBytesAsync("README.md", CancellationToken.None);
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.DirectoryPath, "readme.md"),
            "![changed](docs/image.png)\n"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await resolver.LoadPinnedReadmeBytesAsync("README.md", CancellationToken.None));
    }

    [Fact]
    public async Task ResolvesPinnedRelativeAssetFromContentAddressedBytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png",
            fixture.Context,
            CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.NotNull(result.Asset);
        Assert.Equal(fixture.AssetBytes, result.Asset.Bytes);
        Assert.Equal("image/png", result.Asset.ContentType);
        Assert.Equal($"same-byte://sha256/{fixture.AssetSha256}", result.Asset.ResolvedUri?.AbsoluteUri);
        Assert.Equal($"same-byte:{fixture.AssetSha256}", result.Asset.CacheKey);
    }

    [Fact]
    public async Task ResolvesSniffedPngWhenCapturedMimeIsOctetStreamAlongsideDeclaredSvg()
    {
        byte[] svgBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"></svg>"u8.ToArray();
        await using var fixture = await Fixture.CreateAsync(
            new FixtureAsset("docs/image.png", Fixture.PngImageBytes, "application/octet-stream"),
            new FixtureAsset("docs/logo.svg", svgBytes, "image/svg+xml"));
        var resolver = fixture.Resolver();

        MarkdownImageResolution png = await resolver.ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);
        MarkdownImageResolution svg = await resolver.ResolveAsync(
            "docs/logo.svg", fixture.Context, CancellationToken.None);

        Assert.True(png.IsHandled);
        Assert.NotNull(png.Asset);
        Assert.Equal(Fixture.PngImageBytes, png.Asset.Bytes);
        Assert.Equal("image/png", png.Asset.ContentType);
        Assert.True(svg.IsHandled);
        Assert.NotNull(svg.Asset);
        Assert.Equal(svgBytes, svg.Asset.Bytes);
        Assert.Equal("image/svg+xml", svg.Asset.ContentType);
    }

    [Fact]
    public async Task RejectsOctetStreamAssetWithoutSupportedImageSignature()
    {
        await using var fixture = await Fixture.CreateAsync(
            new FixtureAsset("docs/image.png", "not an image"u8.ToArray(), "application/octet-stream"));

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    [Fact]
    public async Task UnmatchedSourceIsHandledOfflineWithoutNetworkFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "https://images.example.invalid/not-captured.png?access_token=private",
            fixture.Context,
            CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Offline, result.UnavailableReason);
    }

    [Fact]
    public async Task AssetHashMismatchIsUnavailableAndNeverFallsBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllBytesAsync(fixture.AssetPath, "tampered"u8.ToArray());

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png",
            fixture.Context,
            CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    [Fact]
    public async Task ReadmeHashMismatchFailsClosedBeforeResolvingAnyAsset()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new MarkdownSameByteAuditImageResolver(
            fixture.DirectoryPath,
            fixture.Repository,
            fixture.CommitSha,
            new string('f', 40));

        MarkdownImageResolution result = await resolver.ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);
        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    [Fact]
    public async Task RepositoryCommitMismatchFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new MarkdownSameByteAuditImageResolver(
            fixture.DirectoryPath,
            fixture.Repository,
            new string('f', 40),
            fixture.ReadmeGitBlobSha1);

        MarkdownImageResolution result = await resolver.ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);
        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    [Fact]
    public async Task MissingAuditPinsFailClosedWithoutThrowingDuringResolverConstruction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new MarkdownSameByteAuditImageResolver(
            fixture.DirectoryPath,
            string.Empty,
            string.Empty,
            string.Empty);

        MarkdownImageResolution result = await resolver.ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    [Fact]
    public async Task MissingFixtureFailsClosedWithoutNetworkFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.Delete(Path.Combine(fixture.DirectoryPath, "manifest.json"));

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=");
        private const string PinnedCommitSha = "0123456789012345678901234567890123456789";
        private const string PinnedRepository = "example/repo";
        private readonly IReadOnlyList<FixtureAsset> _assets;

        private Fixture(string directoryPath, IReadOnlyList<FixtureAsset> assets)
        {
            DirectoryPath = directoryPath;
            _assets = assets;
            ReadmeBytes = Encoding.UTF8.GetBytes(string.Join(
                "\n",
                assets.Select(asset => $"![example]({asset.Source})")) + "\n");
            AssetSha256 = Convert.ToHexString(SHA256.HashData(assets[0].Bytes)).ToLowerInvariant();
            ReadmeGitBlobSha1 = GitBlobSha1(ReadmeBytes);
            AssetPath = Path.Combine(DirectoryPath, "assets", AssetSha256);
            Context = new MarkdownImageResolveContext(
                BaseUri: null,
                DocumentPath: "README.md",
                AllowThirdPartyRemoteImages: false,
                DocumentSource: new MarkdownDocumentSource(
                    "audit-readme",
                    "example",
                    "repo",
                    PinnedCommitSha,
                    "README.md"));
        }

        public string DirectoryPath { get; }
        public string AssetPath { get; }
        public string AssetSha256 { get; }
        public string ReadmeGitBlobSha1 { get; }
        public byte[] ReadmeBytes { get; }
        public MarkdownImageResolveContext Context { get; }
        public string Repository => PinnedRepository;
        public string CommitSha => PinnedCommitSha;
        public byte[] PinnedReadmeBytes => ReadmeBytes;
        public byte[] AssetBytes => _assets[0].Bytes;

        public static byte[] PngImageBytes => ValidPngBytes;

        public static Task<Fixture> CreateAsync() => CreateAsync(
            new FixtureAsset("docs/image.png", PngImageBytes, "image/png"));

        public static async Task<Fixture> CreateAsync(params FixtureAsset[] assets)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"jithub-same-byte-test-{Guid.NewGuid():N}");
            var fixture = new Fixture(directory, assets);
            Directory.CreateDirectory(Path.Combine(directory, "assets"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "readme.md"), fixture.ReadmeBytes);
            foreach (FixtureAsset asset in assets)
            {
                string assetSha256 = Convert.ToHexString(SHA256.HashData(asset.Bytes)).ToLowerInvariant();
                string assetPath = Path.Combine(directory, "assets", assetSha256);
                if (!File.Exists(assetPath))
                    await File.WriteAllBytesAsync(assetPath, asset.Bytes);
            }
            byte[] renderedBytes = "<article>fixture</article>"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(directory, "rendered.html"), renderedBytes);
            string readmeSha256 = Convert.ToHexString(SHA256.HashData(fixture.ReadmeBytes)).ToLowerInvariant();
            string renderedSha256 = Convert.ToHexString(SHA256.HashData(renderedBytes)).ToLowerInvariant();
            string assetEntries = string.Join(",\n", assets.Select(asset =>
            {
                string assetSha256 = Convert.ToHexString(SHA256.HashData(asset.Bytes)).ToLowerInvariant();
                string rawUrl = GetRawImageUrl(asset.Source);
                string urlSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawUrl))).ToLowerInvariant();
                return $$"""
                    {
                      "urlSha256": "{{urlSha256}}",
                      "sha256": "{{assetSha256}}",
                      "byteSize": {{asset.Bytes.Length}},
                      "mimeType": "{{asset.MimeType}}"
                    }
                    """;
            }));
            string manifest = $$"""
                {
                  "schemaVersion": 2,
                  "complete": true,
                  "repository": {
                    "fullName": "{{PinnedRepository}}",
                    "commitSha": "{{PinnedCommitSha}}",
                    "readmePath": "README.md",
                    "readmeGitBlobSha1": "{{fixture.ReadmeGitBlobSha1}}"
                  },
                  "readme": {
                    "file": "readme.md",
                    "byteSize": {{fixture.ReadmeBytes.Length}},
                    "sha256": "{{readmeSha256}}"
                  },
                  "browserRender": {
                    "status": "passed",
                    "file": "rendered.html",
                    "byteSize": {{renderedBytes.Length}},
                    "sha256": "{{renderedSha256}}"
                  },
                  "assets": [
                {{assetEntries}}
                  ]
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(directory, "manifest.json"),
                manifest);
            return fixture;
        }

        public MarkdownSameByteAuditImageResolver Resolver() => new(
            DirectoryPath,
            Repository,
            CommitSha,
            ReadmeGitBlobSha1);

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
            await Task.CompletedTask;
        }

        private static string GetRawImageUrl(string imageSource)
        {
            var documentSource = new MarkdownDocumentSource(
                "audit-readme", "example", "repo", PinnedCommitSha, "README.md");
            Assert.True(GitHubMarkdownImageUrlResolver.TryResolve(imageSource, documentSource, out var reference));
            return GitHubMarkdownImageUrlResolver.CreateRawUri(reference).AbsoluteUri;
        }

        private static string GitBlobSha1(byte[] bytes)
        {
            byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
            return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
        }
    }
}
