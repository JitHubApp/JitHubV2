using System.Security.Cryptography;
using System.Text;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownSameByteAuditImageResolverTests
{
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
        private static readonly byte[] ReadmeBytes = "![example](docs/image.png)\n"u8.ToArray();
        private static readonly byte[] ImageBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
        private const string PinnedCommitSha = "0123456789012345678901234567890123456789";
        private const string PinnedRepository = "example/repo";

        private Fixture(string directoryPath)
        {
            DirectoryPath = directoryPath;
            AssetSha256 = Convert.ToHexString(SHA256.HashData(ImageBytes)).ToLowerInvariant();
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
        public MarkdownImageResolveContext Context { get; }
        public string Repository => PinnedRepository;
        public string CommitSha => PinnedCommitSha;
        public byte[] AssetBytes => ImageBytes;

        public static async Task<Fixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"jithub-same-byte-test-{Guid.NewGuid():N}");
            var fixture = new Fixture(directory);
            Directory.CreateDirectory(Path.Combine(directory, "assets"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "readme.md"), ReadmeBytes);
            await File.WriteAllBytesAsync(fixture.AssetPath, ImageBytes);
            string rawUrl = GetRawImageUrl();
            string readmeSha256 = Convert.ToHexString(SHA256.HashData(ReadmeBytes)).ToLowerInvariant();
            string urlSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawUrl))).ToLowerInvariant();
            string manifest = $$"""
                {
                  "schemaVersion": 1,
                  "complete": true,
                  "repository": {
                    "fullName": "{{PinnedRepository}}",
                    "commitSha": "{{PinnedCommitSha}}",
                    "readmeGitBlobSha1": "{{fixture.ReadmeGitBlobSha1}}"
                  },
                  "readme": {
                    "file": "readme.md",
                    "byteSize": {{ReadmeBytes.Length}},
                    "sha256": "{{readmeSha256}}"
                  },
                  "assets": [
                    {
                      "urlSha256": "{{urlSha256}}",
                      "sha256": "{{fixture.AssetSha256}}",
                      "byteSize": {{ImageBytes.Length}},
                      "mimeType": "image/png"
                    }
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

        private static string GetRawImageUrl()
        {
            var source = new MarkdownDocumentSource(
                "audit-readme", "example", "repo", PinnedCommitSha, "README.md");
            Assert.True(GitHubMarkdownImageUrlResolver.TryResolve("docs/image.png", source, out var reference));
            return GitHubMarkdownImageUrlResolver.CreateRawUri(reference).AbsoluteUri;
        }

        private static string GitBlobSha1(byte[] bytes)
        {
            byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
            return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
        }
    }
}
