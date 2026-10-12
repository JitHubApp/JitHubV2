using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.Services.Markdown;
using JitHub.WinUI.ViewModels.CodeViewer;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Trait("Category", "ReleaseSecurity")]
[Collection("Markdown renderer audit environment")]
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
    public async Task OfflinePinnedReadmeFlowsThroughRepositoryPageAndRemainsAnExplicitPartialTree()
    {
        await using var fixture = await Fixture.CreateAsync();
        await WithOfflineAuditEnvironmentAsync(fixture, readmeAvailable: true, async () =>
        {
            var query = Substitute.For<IGitHubRepoCodeQueryService>();
            var auth = Substitute.For<IAuthService>();
            var account = Substitute.For<IAccountService>();
            var service = new RepoTreeService(query, auth, account);

            RepoTree directTree = (await service.LoadTreeAsync(
                "example",
                "repo",
                fixture.CommitSha,
                CancellationToken.None)).Value;
            Assert.True(directTree.Truncated);
            Assert.False(directTree.RootIsAuthoritative);
            RepoTreeNode directReadme = Assert.Single(directTree.Root.Children);
            Assert.Equal("README.md", directReadme.Path);
            Assert.Equal(fixture.ReadmeGitBlobSha1, directReadme.Sha);

            LanguageIdResolver languageResolver = new();
            RepoCodePageViewModel viewModel = new(
                service,
                Substitute.For<IRepoFileCacheService>(),
                new FilePreviewResolver(languageResolver),
                languageResolver,
                account,
                Substitute.For<ITelemetryService>());

            await viewModel.InitializeAsync("example", "repo", fixture.CommitSha, CancellationToken.None);
            await viewModel.ReconciliationTask;
            await viewModel.DefaultPreviewTask;

            Assert.True(viewModel.UseRawReadmeSourceForSameByteAudit);
            Assert.True(viewModel.Tree.IsTruncated);
            Assert.False(viewModel.Tree.IsRootAuthoritative);
            Assert.Equal("README.md", viewModel.Preview.CurrentFile?.Path);
            Assert.Equal(fixture.ReadmeBytes, viewModel.Preview.Bytes);
            Assert.Equal(Encoding.UTF8.GetString(fixture.ReadmeBytes), viewModel.Preview.Text);
            Assert.Equal(viewModel.Preview.Text, viewModel.Preview.RenderedText);
            Assert.Equal(viewModel.Preview.Text, viewModel.Preview.RichPreviewDocument?.Markdown);
            Assert.Equal("README.md", viewModel.Preview.RichPreviewDocument?.DocumentPath);
            Assert.Equal(fixture.ReadmeGitBlobSha1, viewModel.Preview.CurrentFile?.Sha);
            Assert.Empty(query.ReceivedCalls());
            Assert.Empty(auth.ReceivedCalls());
        });
    }

    [Fact]
    public async Task OfflinePinnedNoReadmeStaysEmptyAndDoesNotClaimACompleteTree()
    {
        await using var fixture = await Fixture.CreateAsync();
        await WithOfflineAuditEnvironmentAsync(fixture, readmeAvailable: false, async () =>
        {
            var query = Substitute.For<IGitHubRepoCodeQueryService>();
            var auth = Substitute.For<IAuthService>();
            var account = Substitute.For<IAccountService>();
            var service = new RepoTreeService(query, auth, account);

            RepoTree directTree = (await service.LoadTreeAsync(
                "example",
                "repo",
                fixture.CommitSha,
                CancellationToken.None)).Value;
            Assert.True(directTree.Truncated);
            Assert.False(directTree.RootIsAuthoritative);
            Assert.Empty(directTree.Root.Children);
            Assert.Null(await service.LoadReadmeAsync(
                "example",
                "repo",
                fixture.CommitSha,
                CancellationToken.None));

            LanguageIdResolver languageResolver = new();
            RepoCodePageViewModel viewModel = new(
                service,
                Substitute.For<IRepoFileCacheService>(),
                new FilePreviewResolver(languageResolver),
                languageResolver,
                account,
                Substitute.For<ITelemetryService>());

            await viewModel.InitializeAsync("example", "repo", fixture.CommitSha, CancellationToken.None);
            await viewModel.ReconciliationTask;
            await viewModel.DefaultPreviewTask;

            Assert.True(viewModel.Tree.IsTruncated);
            Assert.False(viewModel.Tree.IsRootAuthoritative);
            Assert.Empty(viewModel.Tree.RootNodes);
            Assert.Null(viewModel.Preview.CurrentFile);
            Assert.Null(viewModel.Preview.RichPreviewDocument);
            Assert.Empty(query.ReceivedCalls());
            Assert.Empty(auth.ReceivedCalls());
        });
    }

    [Fact]
    public async Task RegistrySharesValidatedFixtureBetweenSourceAndImagePaths()
    {
        await using var fixture = await Fixture.CreateAsync();
        var registry = new MarkdownSameByteAuditResolverRegistry();
        MarkdownSameByteAuditImageResolver sourceResolver = registry.GetOrCreate(
            fixture.DirectoryPath, fixture.Repository, fixture.CommitSha, fixture.ReadmeGitBlobSha1);
        _ = await sourceResolver.LoadPinnedReadmeBytesAsync("README.md", CancellationToken.None);

        MarkdownSameByteAuditImageResolver imageResolver = registry.GetOrCreate(
            fixture.DirectoryPath, fixture.Repository, fixture.CommitSha, fixture.ReadmeGitBlobSha1);
        MarkdownImageResolution image = await imageResolver.ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);

        Assert.Same(sourceResolver, imageResolver);
        Assert.Equal(fixture.AssetBytes, image.Asset?.Bytes);
    }

    [Fact]
    public async Task RegistryRejectsIdentityDriftAndClearsBetweenAuditLaunches()
    {
        await using var fixture = await Fixture.CreateAsync();
        var registry = new MarkdownSameByteAuditResolverRegistry();
        MarkdownSameByteAuditImageResolver first = registry.GetOrCreate(
            fixture.DirectoryPath, fixture.Repository, fixture.CommitSha, fixture.ReadmeGitBlobSha1);

        Assert.Throws<InvalidDataException>(() => registry.GetOrCreate(
            fixture.DirectoryPath, fixture.Repository, "different-commit", fixture.ReadmeGitBlobSha1));
        Assert.Throws<InvalidDataException>(() => registry.GetOrCreate(
            fixture.DirectoryPath, fixture.Repository, fixture.CommitSha, "different-readme"));
        registry.Clear();
        MarkdownSameByteAuditImageResolver next = registry.GetOrCreate(
            fixture.DirectoryPath, fixture.Repository, fixture.CommitSha, fixture.ReadmeGitBlobSha1);
        Assert.NotSame(first, next);
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
    public async Task StageDiagnosticsReportWarmIndexAndVerifiedAssetSubstagesWithoutChangingBytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = fixture.Resolver();
        _ = await resolver.LoadPinnedReadmeBytesAsync("README.md", CancellationToken.None);

        MarkdownSameByteImageResolutionDiagnostic diagnostic = await resolver.ResolveWithStageDiagnosticsAsync(
            "docs/image.png",
            fixture.Context,
            correlationId: 73,
            CancellationToken.None);

        Assert.True(diagnostic.Resolution.IsHandled);
        Assert.NotNull(diagnostic.Resolution.Asset);
        Assert.Equal(fixture.AssetBytes, diagnostic.Resolution.Asset.Bytes);
        Assert.Equal(73, diagnostic.StageEvidence.CorrelationId);
        Assert.True(diagnostic.StageEvidence.IndexTaskWasCreatedAtStart);
        Assert.True(diagnostic.StageEvidence.IndexTaskWasCompletedBeforeAwait);
        Assert.True(diagnostic.StageEvidence.SourceHashCandidateCount > 0);
        Assert.Null(diagnostic.StageEvidence.FailureStage);
        Assert.True(diagnostic.StageEvidence.IndexWaitMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.SourceHashLookupMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.SourceHashIndexLookupMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetStatMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetOpenMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetLengthCheckMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetBufferAllocationMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetReadMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetReadExactlyMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetEofProbeMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetSha256VerificationMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.AssetDisposeMilliseconds >= 0);
        Assert.True(diagnostic.StageEvidence.ContentTypeValidationMilliseconds >= 0);
    }

    [Fact]
    public void SynchronousAssetReadPolicyIsLimitedToEightMiB()
    {
        const int threshold = 8 * 1024 * 1024;

        Assert.False(MarkdownSameByteAuditImageResolver.ShouldUseSynchronousAssetRead(0));
        Assert.False(MarkdownSameByteAuditImageResolver.ShouldUseSynchronousAssetRead(-1));
        Assert.True(MarkdownSameByteAuditImageResolver.ShouldUseSynchronousAssetRead(1));
        Assert.True(MarkdownSameByteAuditImageResolver.ShouldUseSynchronousAssetRead(threshold));
        Assert.False(MarkdownSameByteAuditImageResolver.ShouldUseSynchronousAssetRead(threshold + 1));
    }

    [Fact]
    public async Task ResolvesAssetAboveSynchronousReadLimitThroughAsyncPath()
    {
        byte[] largeAssetBytes = new byte[8 * 1024 * 1024 + 1];
        Array.Fill(largeAssetBytes, (byte)0x5A);
        await using var fixture = await Fixture.CreateAsync(
            new FixtureAsset("docs/image.png", largeAssetBytes, "image/png"));

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.NotNull(result.Asset);
        Assert.Equal(largeAssetBytes, result.Asset.Bytes);
    }

    [Fact]
    public async Task ResolvesRelativeAssetAtPinnedCommitWhenDocumentSourceUsesBranchName()
    {
        await using var fixture = await Fixture.CreateAsync();
        MarkdownDocumentSource branchSource = fixture.Context.DocumentSource! with { Ref = "main" };
        MarkdownImageResolveContext branchContext = fixture.Context with { DocumentSource = branchSource };

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png",
            branchContext,
            CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.NotNull(result.Asset);
        Assert.Equal(fixture.AssetBytes, result.Asset.Bytes);
        Assert.Equal($"same-byte://sha256/{fixture.AssetSha256}", result.Asset.ResolvedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task LeasedResolutionReservesExactValidatedSizeUntilCallerDisposes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admission = new RecordingSourceAdmission();

        MarkdownImageResolutionLease lease = await fixture.Resolver().ResolveWithSourceLeaseAsync(
            "docs/image.png", fixture.Context, admission, CancellationToken.None);

        Assert.Equal(new long[] { fixture.AssetBytes.Length }, admission.RequestedBytes);
        Assert.Equal(0, admission.DisposeCount);
        Assert.Equal(fixture.AssetBytes, lease.Resolution.Asset?.Bytes);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, admission.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Resolution);
    }

    [Fact]
    public async Task RejectedAdmissionPreventsAssetResolutionAndLeavesNoReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admission = new RecordingSourceAdmission
        {
            ReserveFailure = new InvalidOperationException("test admission rejection"),
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Resolver().ResolveWithSourceLeaseAsync(
                "docs/image.png", fixture.Context, admission, CancellationToken.None));

        Assert.Equal("test admission rejection", exception.Message);
        Assert.Equal(new long[] { fixture.AssetBytes.Length }, admission.RequestedBytes);
        Assert.Equal(0, admission.DisposeCount);
    }

    [Fact]
    public async Task CanceledLeasedResolutionDoesNotAcquireAdmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var admission = new RecordingSourceAdmission();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Resolver().ResolveWithSourceLeaseAsync(
                "docs/image.png", fixture.Context, admission, cancellation.Token));

        Assert.Empty(admission.RequestedBytes);
        Assert.Equal(0, admission.DisposeCount);
    }

    [Fact]
    public async Task CancellationImmediatelyAfterAdmissionReleasesReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var admission = new RecordingSourceAdmission
        {
            OnReserve = cancellation.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Resolver().ResolveWithSourceLeaseAsync(
                "docs/image.png", fixture.Context, admission, cancellation.Token));

        Assert.Equal(new long[] { fixture.AssetBytes.Length }, admission.RequestedBytes);
        Assert.Equal(1, admission.DisposeCount);
    }

    [Fact]
    public async Task LeasedStageDiagnosticsKeepsTheReservationWithItsResult()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = fixture.Resolver();
        var admission = new RecordingSourceAdmission();

        MarkdownSameByteLeasedImageResolutionDiagnostic diagnostic =
            await resolver.ResolveWithLeasedStageDiagnosticsAsync(
                "docs/image.png", fixture.Context, 105, admission, CancellationToken.None);

        Assert.Equal(fixture.AssetBytes, diagnostic.Lease.Resolution.Asset?.Bytes);
        Assert.Equal(105, diagnostic.StageEvidence.CorrelationId);
        Assert.True(diagnostic.StageEvidence.AssetBufferAllocationMilliseconds >= 0);
        Assert.Equal(new long[] { fixture.AssetBytes.Length }, admission.RequestedBytes);
        Assert.Equal(0, admission.DisposeCount);
        diagnostic.Lease.Dispose();
        Assert.Equal(1, admission.DisposeCount);
    }

    [Fact]
    public async Task InvalidAssetHashReleasesAdmissionBeforeReturningUnavailableLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllBytesAsync(
            fixture.AssetPath,
            Enumerable.Repeat((byte)0xA5, fixture.AssetBytes.Length).ToArray());
        var admission = new RecordingSourceAdmission();

        using MarkdownImageResolutionLease lease = await fixture.Resolver().ResolveWithSourceLeaseAsync(
            "docs/image.png", fixture.Context, admission, CancellationToken.None);

        Assert.True(lease.Resolution.IsHandled);
        Assert.Null(lease.Resolution.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, lease.Resolution.UnavailableReason);
        Assert.Equal(new long[] { fixture.AssetBytes.Length }, admission.RequestedBytes);
        Assert.Equal(1, admission.DisposeCount);
    }

    [Fact]
    public async Task UnmatchedLeasedSourceStaysOfflineWithoutAdmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admission = new RecordingSourceAdmission();

        using MarkdownImageResolutionLease lease = await fixture.Resolver().ResolveWithSourceLeaseAsync(
            "https://images.example.invalid/not-captured.png", fixture.Context, admission, CancellationToken.None);

        Assert.True(lease.Resolution.IsHandled);
        Assert.Null(lease.Resolution.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Offline, lease.Resolution.UnavailableReason);
        Assert.Empty(admission.RequestedBytes);
        Assert.Equal(0, admission.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedFailedDecorativeSourceRemainsUnavailableWithoutOfflinePolicy(bool leased)
    {
        await using var fixture = await Fixture.CreateAsync();
        const string failedSource = "https://images.example.invalid/captured-broken.png";
        await fixture.SetDecorativeExclusionsAsync("[" + FailedDecorativeEvidence(SourceHash(failedSource)) + "]");
        var admission = new RecordingSourceAdmission();
        var resolver = fixture.Resolver();

        MarkdownImageResolution result;
        if (leased)
        {
            using MarkdownImageResolutionLease lease = await resolver.ResolveWithSourceLeaseAsync(
                failedSource, fixture.Context, admission, CancellationToken.None);
            result = lease.Resolution;
        }
        else
        {
            result = await resolver.ResolveAsync(failedSource, fixture.Context, CancellationToken.None);
        }

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
        Assert.Empty(admission.RequestedBytes);
        Assert.Equal(0, admission.DisposeCount);
        MarkdownImageResolution uncaptured = await resolver.ResolveAsync(
            "https://images.example.invalid/different-broken.png", fixture.Context, CancellationToken.None);
        Assert.Equal(MarkdownImageUnavailableReason.Offline, uncaptured.UnavailableReason);
    }

    [Fact]
    public async Task CapturedAssetWinsOverDecorativeFailureEvidenceForTheSameSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        string rawSource = $"https://raw.githubusercontent.com/example/repo/{fixture.CommitSha}/docs/image.png";
        await fixture.SetDecorativeExclusionsAsync("[" + FailedDecorativeEvidence(SourceHash(rawSource)) + "]");

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.NotNull(result.Asset);
        Assert.Equal(fixture.AssetBytes, result.Asset.Bytes);
    }

    [Fact]
    public async Task CapturedDecorativeFailuresAcceptBoundedAliasesAndNormalizeHashCase()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string failedSource = "https://images.example.invalid/captured-broken.png";
        string[] aliases = Enumerable.Range(0, 15).Select(index => SourceHash($"alias-{index}")).ToArray();
        string entry = FailedDecorativeEvidence([.. aliases, SourceHash(failedSource).ToUpperInvariant()]);
        await fixture.SetDecorativeExclusionsAsync("[" + string.Join(",", Enumerable.Repeat(entry, 128)) + "]");

        MarkdownImageResolution result = await fixture.Resolver().ResolveAsync(
            failedSource, fixture.Context, CancellationToken.None);

        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
        // The valid image must still resolve: the failure verdict above alone
        // would not distinguish a valid index from a rejected malformed fixture.
        Assert.NotNull((await fixture.Resolver().ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None)).Asset);
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-entry")]
    [InlineData("null-hashes")]
    [InlineData("empty-hashes")]
    [InlineData("null-hash")]
    [InlineData("invalid-hash")]
    [InlineData("duplicate-hash")]
    [InlineData("duplicate-case-hash")]
    [InlineData("too-many-hashes")]
    [InlineData("too-many-entries")]
    [InlineData("not-empty-alt")]
    [InlineData("not-failed")]
    [InlineData("not-zero")]
    [InlineData("missing-proof")]
    [InlineData("source-view")]
    [InlineData("null-browser-render")]
    public async Task RejectsUnprovenOrUnboundedDecorativeFailureEvidence(string invalidCase)
    {
        await using var fixture = await Fixture.CreateAsync();
        string hash = SourceHash("https://images.example.invalid/captured-broken.png");
        string entry = FailedDecorativeEvidence(hash);
        string entries = invalidCase switch
        {
            "null-list" => "null",
            "null-entry" => "[null]",
            "null-hashes" => "[" + entry.Replace($"[\"{hash}\"]", "null") + "]",
            "empty-hashes" => "[" + entry.Replace($"[\"{hash}\"]", "[]") + "]",
            "null-hash" => "[" + entry.Replace($"\"{hash}\"", "null") + "]",
            "invalid-hash" => "[" + FailedDecorativeEvidence("not-a-hash") + "]",
            "duplicate-hash" => "[" + FailedDecorativeEvidence(hash, hash) + "]",
            "duplicate-case-hash" => "[" + FailedDecorativeEvidence(hash, hash.ToUpperInvariant()) + "]",
            "too-many-hashes" => "[" + FailedDecorativeEvidence(
                Enumerable.Range(0, 17).Select(index => SourceHash($"alias-{index}")).ToArray()) + "]",
            "too-many-entries" => "[" + string.Join(",", Enumerable.Repeat(entry, 129)) + "]",
            "not-empty-alt" => "[" + entry.Replace("\"explicitEmptyAlt\":true", "\"explicitEmptyAlt\":false") + "]",
            "not-failed" => "[" + entry.Replace("\"failedDecode\":true", "\"failedDecode\":false") + "]",
            "not-zero" => "[" + entry.Replace("\"zeroBounds\":true", "\"zeroBounds\":false") + "]",
            "missing-proof" => "[" + entry.Replace(",\"zeroBounds\":true", string.Empty) + "]",
            "source-view" or "null-browser-render" => "[" + entry + "]",
            _ => throw new InvalidOperationException(invalidCase),
        };
        await fixture.SetDecorativeExclusionsAsync(entries, sourceView: invalidCase == "source-view");
        if (invalidCase == "null-browser-render")
        {
            string path = Path.Combine(fixture.DirectoryPath, "manifest.json");
            JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            manifest["browserRender"] = null;
            await File.WriteAllTextAsync(path, manifest.ToJsonString());
        }
        var resolver = fixture.Resolver();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await resolver.GetPinnedReadmePathAsync(CancellationToken.None));
        MarkdownImageResolution result = await resolver.ResolveAsync(
            "docs/image.png", fixture.Context, CancellationToken.None);
        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, result.UnavailableReason);
    }

    private static string SourceHash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static string FailedDecorativeEvidence(params string[] hashes) =>
        "{\"urlSha256s\":[" + string.Join(",", hashes.Select(hash => $"\"{hash}\"")) +
        "],\"explicitEmptyAlt\":true,\"failedDecode\":true,\"zeroBounds\":true}";

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
    public async Task StageDiagnosticsAttributeSameLengthTamperingToHashVerificationBeforeDisposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        byte[] tampered = Enumerable.Repeat((byte)0xA5, fixture.AssetBytes.Length).ToArray();
        await File.WriteAllBytesAsync(fixture.AssetPath, tampered);

        MarkdownSameByteImageResolutionDiagnostic diagnostic = await fixture.Resolver()
            .ResolveWithStageDiagnosticsAsync(
                "docs/image.png",
                fixture.Context,
                correlationId: 91,
                CancellationToken.None);

        Assert.True(diagnostic.Resolution.IsHandled);
        Assert.Null(diagnostic.Resolution.Asset);
        Assert.Equal(MarkdownImageUnavailableReason.Unavailable, diagnostic.Resolution.UnavailableReason);
        Assert.Equal("asset-sha256-verification", diagnostic.StageEvidence.FailureStage);
        Assert.True(diagnostic.StageEvidence.AssetDisposeMilliseconds >= 0);
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

    private sealed class RecordingSourceAdmission : IMarkdownImageSourceByteAdmission
    {
        private int _disposeCount;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => 64 * 1024 * 1024;
        public List<long> RequestedBytes { get; } = [];
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Exception? ReserveFailure { get; init; }
        public Action? OnReserve { get; init; }

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedBytes.Add(bytes);
            Exception? reserveFailure = ReserveFailure;
            if (reserveFailure is not null)
                return ValueTask.FromException<IDisposable>(reserveFailure);

            var reservation = new Reservation(this);
            OnReserve?.Invoke();
            return ValueTask.FromResult<IDisposable>(reservation);
        }

        private sealed class Reservation(RecordingSourceAdmission owner) : IDisposable
        {
            private RecordingSourceAdmission? _owner = owner;

            public void Dispose()
            {
                RecordingSourceAdmission? owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                    Interlocked.Increment(ref owner._disposeCount);
            }
        }
    }

    private static async Task WithOfflineAuditEnvironmentAsync(
        Fixture fixture,
        bool readmeAvailable,
        Func<Task> action)
    {
        string[] variableNames =
        [
            "JITHUB_README_AUDIT_PINNED_MANIFEST_PATH",
            "JITHUB_README_AUDIT_PINNED_MANIFEST_RANK",
            "JITHUB_README_AUDIT_PINNED_MANIFEST_SHA256",
            "JITHUB_README_AUDIT_GITHUB_TOKEN",
            "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID",
            "JITHUB_README_AUDIT_SAME_BYTE_CORPUS",
            "JITHUB_README_AUDIT_SAME_BYTE_README_SHA",
            "JITHUB_README_AUDIT_SAME_BYTE_README_ABSENT",
        ];
        string?[] originalValues = variableNames
            .Select(Environment.GetEnvironmentVariable)
            .ToArray();
        string manifestPath = Path.Combine(fixture.DirectoryPath, "top500-pinned.json");
        byte[] manifestBytes = CreatePinnedTop500Manifest(fixture, readmeAvailable);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        try
        {
            Environment.SetEnvironmentVariable(variableNames[0], Path.GetFullPath(manifestPath));
            Environment.SetEnvironmentVariable(variableNames[1], "1");
            Environment.SetEnvironmentVariable(variableNames[2], Convert.ToHexString(SHA256.HashData(manifestBytes)));
            Environment.SetEnvironmentVariable(variableNames[3], null);
            Environment.SetEnvironmentVariable(variableNames[4], null);
            Environment.SetEnvironmentVariable(
                variableNames[5],
                readmeAvailable ? fixture.DirectoryPath : null);
            Environment.SetEnvironmentVariable(
                variableNames[6],
                readmeAvailable ? fixture.ReadmeGitBlobSha1 : null);
            Environment.SetEnvironmentVariable(variableNames[7], readmeAvailable ? null : "1");
            var launchOptions = new JitHub.WinUI.LaunchOptions(
                Page: "repo-code",
                Repository: fixture.Repository,
                Branch: fixture.CommitSha,
                ReadmeProductionAudit: true,
                OfflineSameByteReadmeAudit: true);
            JitHub.WinUI.OfflineSameByteReadmeAuditPin pin = Assert.IsType<JitHub.WinUI.OfflineSameByteReadmeAuditPin>(
                launchOptions.ValidateOfflineSameByteReadmeAuditPin());
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: null,
                productionAuditEnabled: true,
                offlineSameBytePublicAuditEnabled: true,
                offlineSameByteReadmeAuditPin: pin);

            await action();
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(fixtureEnabled: false, targetHost: null);
            for (int index = 0; index < variableNames.Length; index++)
            {
                Environment.SetEnvironmentVariable(variableNames[index], originalValues[index]);
            }
        }
    }

    private static byte[] CreatePinnedTop500Manifest(Fixture fixture, bool readmeAvailable)
    {
        using MemoryStream stream = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WritePropertyName("repositories");
            writer.WriteStartArray();
            for (int rank = 1; rank <= 500; rank++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("rank", rank);
                writer.WriteString("fullName", rank == 1 ? fixture.Repository : $"owner/repo-{rank}");
                writer.WriteString("commitSha", fixture.CommitSha);
                writer.WritePropertyName("readme");
                writer.WriteStartObject();
                writer.WriteBoolean("available", rank == 1 && readmeAvailable);
                writer.WriteNumber("byteSize", rank == 1 && readmeAvailable ? fixture.ReadmeBytes.Length : 0);
                writer.WriteString("path", rank == 1 && readmeAvailable ? "README.md" : string.Empty);
                writer.WriteString("sha", rank == 1 && readmeAvailable ? fixture.ReadmeGitBlobSha1 : string.Empty);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
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

        public async Task SetDecorativeExclusionsAsync(string entries, bool sourceView = false)
        {
            string path = Path.Combine(DirectoryPath, "manifest.json");
            JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            manifest["nonRenderedDecorativeImageExclusions"] = JsonNode.Parse(entries);
            if (sourceView)
                manifest["browserRender"] = JsonNode.Parse(
                    "{\"status\":\"not-applicable\",\"reason\":\"github-source-view\"}");
            await File.WriteAllTextAsync(path, manifest.ToJsonString());
        }

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
