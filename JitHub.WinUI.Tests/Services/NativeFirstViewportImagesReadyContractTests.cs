using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Windows.Foundation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class NativeFirstViewportImagesReadyContractTests
{
    private const string VisualProfileFingerprint =
        NativeFirstViewportImagesReadyContract.ExpectedVisualProfileCssSha256;

    [Fact]
    public void AlreadyRenderedReadmeFastPathRequiresCurrentSourceBoundIdentity()
    {
        const int processId = 428308;
        const string host = "MarkdownHost_RepositoryReadme_RepoCodeReadme";
        string gitSha = new('a', 40);
        string rawSha = new('b', 64);
        var admission = new NativeSameByteReadmeSourceAdmissionSignal(
            processId, host, 4, 9863, rawSha, 9863, rawSha, gitSha,
            StopwatchTicks: 100, StopwatchFrequency: 10_000_000,
            SourceOwner: "nilbuild", SourceRepository: "developer-roadmap",
            SourceRef: new string('c', 40), SourcePath: "README.md");
        var visualProfile = new NativeVisualProfileSignal(
            NativeFirstViewportImagesReadyContract.ExpectedVisualProfileId,
            NativeFirstViewportImagesReadyContract.ExpectedVisualProfileVersion,
            VisualProfileFingerprint);
        var render = new NativeRenderCompleteSignal(
            processId, host, 4, DateTimeOffset.UtcNow,
            StopwatchTicks: 200, StopwatchFrequency: 10_000_000,
            SameByteReadmeSourceAdmission: admission,
            VisualProfile: visualProfile);

        static bool Matches(
            NativeRenderCompleteSignal candidate,
            int pid,
            string expectedHost,
            string expectedRepository,
            string expectedRef,
            string expectedPath,
            string expectedGitSha,
            string expectedRawSha,
            long expectedBytes) =>
            NativeFirstViewportImagesReadyContract.MatchesAlreadyRenderedReadme(
                candidate, pid, expectedHost, expectedRepository, expectedRef,
                expectedPath, expectedGitSha, expectedRawSha, expectedBytes);

        bool MatchesExpected(NativeRenderCompleteSignal candidate) => Matches(
            candidate, processId, host, "nilbuild/developer-roadmap",
            new string('c', 40), "README.md", gitSha, rawSha, 9863);

        Assert.True(MatchesExpected(render));
        Assert.False(Matches(render, processId + 1, host, "nilbuild/developer-roadmap",
            new string('c', 40), "README.md", gitSha, rawSha, 9863));
        Assert.False(Matches(render, processId, "other-host", "nilbuild/developer-roadmap",
            new string('c', 40), "README.md", gitSha, rawSha, 9863));
        Assert.False(Matches(render, processId, host, "other/developer-roadmap",
            new string('c', 40), "README.md", gitSha, rawSha, 9863));
        Assert.False(Matches(render, processId, host, "nilbuild/developer-roadmap",
            new string('d', 40), "README.md", gitSha, rawSha, 9863));
        Assert.False(Matches(render, processId, host, "nilbuild/developer-roadmap",
            new string('c', 40), "docs/README.md", gitSha, rawSha, 9863));
        Assert.False(Matches(render, processId, host, "nilbuild/developer-roadmap",
            new string('c', 40), "README.md", new string('d', 40), rawSha, 9863));
        Assert.False(Matches(render, processId, host, "nilbuild/developer-roadmap",
            new string('c', 40), "README.md", gitSha, new string('d', 64), 9863));
        Assert.False(Matches(render, processId, host, "nilbuild/developer-roadmap",
            new string('c', 40), "README.md", gitSha, rawSha, 9864));
        Assert.False(MatchesExpected(render with { Generation = 5 }));
        Assert.False(MatchesExpected(render with { VisualProfile = null }));
        Assert.False(MatchesExpected(render with { VisualProfile = visualProfile with { Id = "other-profile" } }));
        Assert.False(MatchesExpected(render with { VisualProfile = visualProfile with { Version = 1 } }));
        Assert.False(MatchesExpected(render with { VisualProfile = visualProfile with {
            CssSha256 = new string('d', 64) } }));
        Assert.False(Matches(render with { SameByteReadmeSourceAdmission = null },
            processId, host, "nilbuild/developer-roadmap", new string('c', 40),
            "README.md", gitSha, rawSha, 9863));
        Assert.False(MatchesExpected(render with { SameByteReadmeSourceAdmissionFailure =
            new NativeSameByteReadmeSourceAdmissionFailureSignal(host, 4, "source-mismatch") }));
        Assert.False(MatchesExpected(render with { SameByteReadmeSourceAdmission = admission with {
            StopwatchTicks = 201 } }));
        Assert.False(MatchesExpected(render with { SameByteReadmeSourceAdmission = admission with {
            SourcePath = "docs/README.md" } }));
    }

    [Fact]
    public void ExpectedVisualProfileFingerprintMatchesCanonicalCss()
    {
        string root = FindRepositoryRoot();
        string cssPath = Path.Combine(
            root,
            "eng",
            "readme-audit",
            "profiles",
            "github-primer-light-v5.css");

        string actualFingerprint = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(cssPath))).ToLowerInvariant();

        Assert.Equal("github-primer-light", NativeFirstViewportImagesReadyContract.ExpectedVisualProfileId);
        Assert.Equal(5, NativeFirstViewportImagesReadyContract.ExpectedVisualProfileVersion);
        Assert.Equal(
            NativeFirstViewportImagesReadyContract.ExpectedVisualProfileCssSha256,
            actualFingerprint);
    }

    [Fact]
    public void ShortDocumentRequiresPaintOfEveryDocumentPixelButNotBlankViewportSpace()
    {
        var coverage = new FirstViewportPaintCoverage();
        Rect viewport = new(0, 0, 660, 499);
        Assert.True(FirstViewportPaintCoverage.TryGetPaintableViewport(
            viewport, 660, 189, out Rect paintable));
        Assert.Equal(new Rect(0, 0, 660, 189), paintable);

        object snapshot = new();
        Assert.True(coverage.EnsureIdentity(snapshot, 6, 0, 1, paintable));
        Assert.False(coverage.AddPaintedRegionExcluding(
            paintable.Left, paintable.Top, paintable.Width, paintable.Height,
            0, 0, 660, 100, []));
        Assert.False(coverage.Covers(0, 0, 660, 189));
        Assert.True(coverage.AddPaintedRegionExcluding(
            paintable.Left, paintable.Top, paintable.Width, paintable.Height,
            0, 100, 660, 89, []));
        Assert.True(coverage.Covers(0, 0, 660, 189));
        Assert.False(coverage.Covers(0, 0, 660, 499));
        Assert.Equal(124_740, coverage.ViewportArea);
    }

    [Fact]
    public void NarrowOrScrolledDocumentCoverageClipsBothAxesAndTracksExtentChanges()
    {
        Rect viewport = new(100, 50, 800, 500);
        Assert.True(FirstViewportPaintCoverage.TryGetPaintableViewport(
            viewport, 600, 250, out Rect paintable));
        Assert.Equal(new Rect(100, 50, 500, 200), paintable);

        var coverage = new FirstViewportPaintCoverage();
        object snapshot = new();
        Assert.True(coverage.EnsureIdentity(snapshot, 3, 7, 1.5, paintable));
        Assert.True(coverage.AddPaintedRegion(
            paintable.Left, paintable.Top, paintable.Width, paintable.Height,
            0, 0, 600, 250));
        Assert.True(coverage.Covers(100, 50, 500, 200));

        Assert.True(FirstViewportPaintCoverage.TryGetPaintableViewport(
            viewport, 600, 350, out Rect grown));
        Assert.True(coverage.EnsureIdentity(snapshot, 3, 8, 1.5, grown));
        Assert.False(coverage.Covers(grown.Left, grown.Top, grown.Width, grown.Height));
        Assert.Equal(new Rect(100, 50, 500, 300), grown);
    }

    [Fact]
    public void PaintableViewportRejectsInvalidOrNonIntersectingDocumentBounds()
    {
        Assert.False(FirstViewportPaintCoverage.TryGetPaintableViewport(
            new Rect(0, 300, 600, 100), 600, 189, out _));
        Assert.False(FirstViewportPaintCoverage.TryGetPaintableViewport(
            new Rect(0, 0, 600, 100), double.NaN, 189, out _));
        Assert.False(FirstViewportPaintCoverage.TryGetPaintableViewport(
            new Rect(0, 0, 600, 100), 600, -1, out _));
        Assert.False(FirstViewportPaintCoverage.TryGetPaintableViewport(
            new Rect(0, 0, double.PositiveInfinity, 100), 600, 189, out _));
        Assert.True(FirstViewportPaintCoverage.TryGetPaintableViewport(
            new Rect(0, 0, 600, 100), 0, 0, out Rect emptyCanvas));
        Assert.Equal(new Rect(0, 0, 1, 1), emptyCanvas);
    }

    [Fact]
    public void PaintCoverageRequiresEveryDisjointViewportRegion()
    {
        var coverage = new FirstViewportPaintCoverage();

        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 200, 20));
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 80, 200, 20));
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 20, 20, 60));
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 180, 20, 20, 60));
        Assert.False(coverage.Covers(0, 0, 200, 100));

        // The bounding union covered the viewport after the first two calls,
        // but the middle did not actually paint until this final region.
        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 20, 20, 160, 60));
        Assert.True(coverage.Covers(0, 0, 200, 100));
    }

    [Fact]
    public void PendingOverlappingImagesStayUncoveredUntilTheirPixelsAreRepainted()
    {
        var coverage = new FirstViewportPaintCoverage();
        Rect[] pendingImageRects =
        [
            new Rect(40, 30, 50, 40),
            new Rect(60, 40, 50, 40),
        ];

        // A full post-arm paint may contain placeholders. It proves coverage
        // outside the pending images while those overlapping pixels remain
        // uncovered (the overlap is unioned once, not double-counted).
        Assert.False(coverage.AddPaintedRegionExcluding(
            0, 0, 200, 100,
            0, 0, 200, 100,
            pendingImageRects));
        Assert.Equal(16_900, coverage.CoveredArea);
        Assert.Equal(3_100, coverage.ViewportArea - coverage.CoveredArea);
        Assert.False(coverage.Covers(0, 0, 200, 100));

        // Readiness changing is not itself paint evidence. Unrelated partial
        // regions after the transition still cannot cover the image pixels.
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 40, 100));
        Assert.False(coverage.Covers(0, 0, 200, 100));

        // The settled images repaint their actual destinations in separate
        // regions; the first is insufficient, and the second completes coverage.
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 40, 30, 50, 40));
        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 60, 40, 50, 40));
        Assert.True(coverage.Covers(0, 0, 200, 100));
    }

    [Fact]
    public void PendingSvgTileMaskDoesNotInvalidateAlreadyPaintedNeighborTiles()
    {
        var coverage = new FirstViewportPaintCoverage();
        Rect oneMissingTile = new(100, 100, 20, 20);

        Assert.False(coverage.AddPaintedRegionExcluding(
            0, 0, 400, 200,
            0, 0, 400, 200,
            [oneMissingTile]));
        Assert.Equal(79_600, coverage.CoveredArea);
        Assert.Equal(1, coverage.UncoveredRegionCount);
        Assert.False(coverage.Covers(0, 0, 400, 200));

        Assert.True(coverage.AddPaintedRegion(0, 0, 400, 200, 100, 100, 20, 20));
        Assert.True(coverage.Covers(0, 0, 400, 200));
    }

    [Fact]
    public void PendingMasksReopenPreviouslyPaintedPixelsWithoutDiscardingNeighborCoverage()
    {
        var coverage = new FirstViewportPaintCoverage();
        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 200, 100));

        Assert.False(coverage.AddPaintedRegionExcluding(
            0, 0, 200, 100,
            0, 0, 200, 100,
            [new Rect(40, 30, 50, 40), new Rect(60, 40, 50, 40)]));
        Assert.Equal(16_900, coverage.CoveredArea);
        Assert.False(coverage.Covers(0, 0, 200, 100));

        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 40, 30, 70, 50));
        Assert.True(coverage.Covers(0, 0, 200, 100));
    }

    [Fact]
    public void ExcessiveVisiblePendingMasksFailClosedBeforePaintingWork()
    {
        var coverage = new FirstViewportPaintCoverage();
        Rect[] masks = Enumerable.Range(0, FirstViewportPaintCoverage.MaximumPendingMasks + 1)
            .Select(index => new Rect(index % 200, index % 100, 1, 1))
            .ToArray();

        Assert.False(coverage.AddPaintedRegionExcluding(
            0, 0, 200, 100,
            0, 0, 200, 100,
            masks));
        Assert.Equal(0, coverage.CoveredArea);
        Assert.Equal("pending-mask-count-budget-exceeded", coverage.LastFailureReason);
    }

    [Fact]
    public void FragmentedPendingMasksFailClosedAtThePerPaintWorkBudget()
    {
        var coverage = new FirstViewportPaintCoverage();
        Rect[] masks = Enumerable.Range(0, FirstViewportPaintCoverage.MaximumPendingMasks)
            .Select(index => new Rect(index * 2, 0, 1, 1))
            .ToArray();

        Assert.False(coverage.AddPaintedRegionExcluding(
            0, 0, 1024, 2,
            0, 0, 1024, 2,
            masks));
        Assert.Equal("coverage-work-budget-exceeded", coverage.LastFailureReason);
        Assert.Equal(0, coverage.CoveredArea);
    }

    [Fact]
    public void IdentityChangesResetCoverageForViewportRevisionAndDpi()
    {
        var coverage = new FirstViewportPaintCoverage();
        object snapshot = new();
        Rect viewport = new(0, 0, 200, 100);
        Assert.True(coverage.EnsureIdentity(snapshot, 4, 8, 1, viewport));
        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 200, 100));
        Assert.True(coverage.Covers(0, 0, 200, 100));

        Assert.True(coverage.EnsureIdentity(snapshot, 4, 9, 1, viewport));
        Assert.False(coverage.Covers(0, 0, 200, 100));
        Assert.Equal(20_000, coverage.ViewportArea - coverage.CoveredArea);

        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 200, 100));
        Assert.True(coverage.EnsureIdentity(snapshot, 4, 9, 1.5, viewport));
        Assert.False(coverage.Covers(0, 0, 200, 100));

        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 200, 100));
        Assert.True(coverage.EnsureIdentity(snapshot, 5, 9, 1.5, viewport));
        Assert.False(coverage.Covers(0, 0, 200, 100));

        Rect movedViewport = new(0, 10, 200, 100);
        Assert.True(coverage.EnsureIdentity(snapshot, 5, 9, 1.5, movedViewport));
        Assert.False(coverage.Covers(0, 10, 200, 100));
        Assert.True(coverage.EnsureIdentity(new object(), 5, 9, 1.5, movedViewport));
        Assert.False(coverage.Covers(0, 10, 200, 100));
    }

    [Fact]
    public void ExclusionAndCoverageFragmentationRemainBounded()
    {
        var coverage = new FirstViewportPaintCoverage();
        Rect[] excessiveMasks = Enumerable.Range(0, 4097)
            .Select(index => new Rect(index % 200, index % 100, 0.1, 0.1))
            .ToArray();

        Assert.False(coverage.AddPaintedRegionExcluding(
            0, 0, 200, 100,
            0, 0, 200, 100,
            excessiveMasks));
        Assert.Equal(0, coverage.CoveredArea);
        Assert.Equal(0, coverage.UncoveredRegionCount);
    }

    [Fact]
    public void PaintCoverageRejectsInvalidRegionsAndDoesNotCountOutsidePaint()
    {
        var coverage = new FirstViewportPaintCoverage();
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 300, 0, 50, 100));
        Assert.False(coverage.Covers(0, 0, 200, 100));
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, double.NaN, 100));
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 100, 100));
        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 100, 0, 100, 100));
    }

    private const int ProcessId = 4821;
    private const string Host = "MarkdownHost_RepositoryReadme_RepoCodeReadme";
    private const long Generation = 7;
    private static readonly string ReadmeGitBlobSha1 = new('a', 40);
    private static readonly string RawReadmeSha256 = new('c', 64);
    private static readonly string DecodedReadmeSha256 = new('d', 64);
    private static readonly DateTimeOffset HostReadyAt = new(2026, 9, 28, 15, 54, 34, TimeSpan.Zero);
    private static readonly DateTimeOffset RenderCompleteAt = HostReadyAt.AddMilliseconds(95);
    private static readonly DateTimeOffset ImagesReadyAt = RenderCompleteAt.AddMilliseconds(18);
    private const long StopwatchFrequency = 10_000_000;
    private const long SourceAdmissionStopwatchTicks = 1_100_000_000;
    private const long RenderCompleteStopwatchTicks = 1_100_950_000;
    private const long ImagesReadyStopwatchTicks = 1_101_130_000;

    [Fact]
    public void SourceGeneratedWriterPublishesOnlyAuditReadySignalsAndContractAcceptsTheValidSignal()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string path = Path.Combine(temporaryDirectory, "first-viewport-images-ready.json");
        try
        {
            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWrite(
                path,
                auditEnabled: false,
                processId: ProcessId,
                host: Host,
                generation: Generation,
                viewportPaintGeneration: Generation,
                pollCount: 3,
                probeWorkMilliseconds: 0.04,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: true,
                hasVisibleLoadingImages: false,
                readmeGitBlobSha1: ReadmeGitBlobSha1,
                timestamp: ImagesReadyAt,
                stopwatchTicks: ImagesReadyStopwatchTicks,
                stopwatchFrequency: StopwatchFrequency));
            Assert.False(File.Exists(path));

            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWrite(
                path,
                auditEnabled: true,
                processId: ProcessId,
                host: Host,
                generation: Generation,
                viewportPaintGeneration: Generation,
                pollCount: 3,
                probeWorkMilliseconds: 0.04,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: true,
                hasVisibleLoadingImages: true,
                readmeGitBlobSha1: ReadmeGitBlobSha1,
                timestamp: ImagesReadyAt,
                stopwatchTicks: ImagesReadyStopwatchTicks,
                stopwatchFrequency: StopwatchFrequency));
            Assert.False(File.Exists(path));

            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWrite(
                path,
                auditEnabled: true,
                processId: ProcessId,
                host: Host,
                generation: Generation,
                viewportPaintGeneration: Generation,
                pollCount: 3,
                probeWorkMilliseconds: 0.04,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: false,
                hasVisibleLoadingImages: false,
                readmeGitBlobSha1: ReadmeGitBlobSha1,
                timestamp: ImagesReadyAt,
                stopwatchTicks: ImagesReadyStopwatchTicks,
                stopwatchFrequency: StopwatchFrequency));
            Assert.False(File.Exists(path));

            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWrite(
                path,
                auditEnabled: true,
                processId: ProcessId,
                host: Host,
                generation: Generation,
                viewportPaintGeneration: Generation,
                pollCount: 3,
                probeWorkMilliseconds: 0.04,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: true,
                hasVisibleLoadingImages: false,
                readmeGitBlobSha1: ReadmeGitBlobSha1,
                timestamp: ImagesReadyAt,
                stopwatchTicks: ImagesReadyStopwatchTicks,
                stopwatchFrequency: StopwatchFrequency));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement json = document.RootElement;
            Assert.Equal(ProcessId, json.GetProperty("ProcessId").GetInt32());
            Assert.Equal(Host, json.GetProperty("Host").GetString());
            Assert.Equal(Generation, json.GetProperty("Generation").GetInt64());
            Assert.Equal(Generation, json.GetProperty("ViewportPaintGeneration").GetInt64());
            Assert.Equal(3, json.GetProperty("PollCount").GetInt32());
            Assert.Equal(0.04, json.GetProperty("ProbeWorkMilliseconds").GetDouble());
            Assert.Equal(0, json.GetProperty("ViewportTop").GetDouble());
            Assert.Equal(600, json.GetProperty("ViewportHeight").GetDouble());
            Assert.True(json.GetProperty("ViewportMeasured").GetBoolean());
            Assert.False(json.GetProperty("HasVisibleLoadingImages").GetBoolean());
            Assert.Equal(ReadmeGitBlobSha1, json.GetProperty("ReadmeGitBlobSha1").GetString());
            Assert.Equal(ImagesReadyAt, json.GetProperty("Timestamp").GetDateTimeOffset());
            Assert.Equal(ImagesReadyStopwatchTicks, json.GetProperty("StopwatchTicks").GetInt64());
            Assert.Equal(StopwatchFrequency, json.GetProperty("StopwatchFrequency").GetInt64());

            NativeFirstViewportImagesReadySignal ready =
                NativeFirstViewportImagesReadyContract.ReadImagesReadySignal(path);
            NativeFirstViewportImagesReadyContract.ValidateRenderIdentity(
                ProcessId,
                Host,
                ValidHostReady(),
                ValidRenderComplete());
            NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId,
                Host,
                ReadmeGitBlobSha1,
                ValidHostReady(),
                ValidRenderComplete(),
                ready);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public void InitialViewportIdentityRequiresReadinessAndFirstCaptureAtDocumentTop()
    {
        NativeFirstViewportImagesReadySignal ready = ValidImagesReady();

        NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(ready, capturedInitialDocumentTop: 0);
        NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
            ready with { ViewportTop = 0.25 },
            capturedInitialDocumentTop: 0);
        NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
            ready with { ViewportTop = 0.25 },
            capturedInitialDocumentTop: 0.25);
        NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
            ready with { ViewportTop = 0.5 },
            capturedInitialDocumentTop: 0);

        InvalidDataException mismatched = Assert.Throws<InvalidDataException>(() =>
            NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
                ready with { ViewportTop = 8383.744 },
                capturedInitialDocumentTop: 0));
        Assert.Contains("ViewportTopMatchesInitialCapture", mismatched.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("8383.744", mismatched.Message, StringComparison.Ordinal);

        InvalidDataException nonInitial = Assert.Throws<InvalidDataException>(() =>
            NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
                ready with { ViewportTop = 8383.744 },
                capturedInitialDocumentTop: 8383.744));
        Assert.Contains("InitialCaptureStartsAtDocumentTop", nonInitial.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
                ready,
                capturedInitialDocumentTop: double.NaN));
    }

    [Theory]
    [InlineData("validating-paint")]
    [InlineData("painted-snapshot-no-longer-current")]
    [InlineData("painted-snapshot-identity-changed")]
    [InlineData("visible-deferred-scene-pending-or-unverified")]
    public void ProgressWriterPublishesPrivacySafePaintAndCoverageDiagnostics(string probeReason)
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string path = Path.Combine(temporaryDirectory, "first-viewport-images-ready-progress.ndjson");
        FirstViewportImagesReadyProgress progress = new(
            ImagesReadyAt,
            Stage: "paint-callback",
            Reason: "visible-images-loading",
            PollCount: 3,
            PaintCallbackCount: 2,
            PaintedRegionCount: 2,
            Generation: Generation,
            CurrentGeneration: Generation,
            PublishedGeneration: Generation,
            SnapshotGeneration: Generation,
            LayoutRevision: 5,
            RasterizationScale: 1.25,
            ViewportLeft: 0,
            ViewportTop: 0,
            ViewportWidth: 800,
            ViewportHeight: 600,
            ViewportMeasured: true,
            HasVisibleLoadingImages: true,
            VisibleLoadingImageCount: 1,
            PendingImageRegionCount: 1,
            CoveredArea: 478_000,
            ViewportArea: 480_000,
            UncoveredRegionCount: 1,
            LastPaintedIntersectionArea: 40_000,
            LastPaintedRegionLeft: 0,
            LastPaintedRegionTop: 0,
            LastPaintedRegionWidth: 200,
            LastPaintedRegionHeight: 200,
            HasPostArmPaintedRegion: true,
            PaintProbeAccepted: false,
            PaintAcknowledgementRecorded: null,
            PaintRequestedGeneration: Generation,
            PaintedGeneration: 0,
            PaintedAtStopwatchTicks: 0,
            PaintAcknowledgementIsCurrent: null,
            PaintPollAction: (int)FirstViewportImagesReadyPaintPollAction.NotPolled);
        try
        {
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                progress));
            string jsonLine = File.ReadAllText(path).Trim();
            using JsonDocument document = JsonDocument.Parse(jsonLine);
            JsonElement json = document.RootElement;
            Assert.Equal("paint-callback", json.GetProperty("Stage").GetString());
            Assert.Equal("visible-images-loading", json.GetProperty("Reason").GetString());
            Assert.Equal(2, json.GetProperty("PaintCallbackCount").GetInt64());
            Assert.Equal(2, json.GetProperty("PaintedRegionCount").GetInt64());
            Assert.Equal(Generation, json.GetProperty("CurrentGeneration").GetInt64());
            Assert.Equal(Generation, json.GetProperty("PublishedGeneration").GetInt64());
            Assert.Equal(Generation, json.GetProperty("SnapshotGeneration").GetInt64());
            Assert.Equal(5, json.GetProperty("LayoutRevision").GetInt64());
            Assert.Equal(1.25, json.GetProperty("RasterizationScale").GetDouble());
            Assert.Equal(478_000, json.GetProperty("CoveredArea").GetDouble());
            Assert.Equal(480_000, json.GetProperty("ViewportArea").GetDouble());
            Assert.True(json.GetProperty("HasVisibleLoadingImages").GetBoolean());
            Assert.True(json.GetProperty("HasPostArmPaintedRegion").GetBoolean());
            Assert.False(json.GetProperty("PaintProbeAccepted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("PaintAcknowledgementRecorded").ValueKind);
            Assert.Equal(Generation, json.GetProperty("PaintRequestedGeneration").GetInt64());
            Assert.Equal(0, json.GetProperty("PaintedGeneration").GetInt64());
            Assert.Equal(0, json.GetProperty("PaintedAtStopwatchTicks").GetInt64());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("PaintAcknowledgementIsCurrent").ValueKind);
            Assert.Equal(
                (int)FirstViewportImagesReadyPaintPollAction.NotPolled,
                json.GetProperty("PaintPollAction").GetInt32());
            Assert.DoesNotContain("Host", jsonLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Readme", jsonLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Url", jsonLine, StringComparison.OrdinalIgnoreCase);

            FirstViewportImagesReadyProgress callbackWithoutLifecycleAck = progress with
            {
                Reason = "viewport-covered-after-ready-paint",
                HasVisibleLoadingImages = false,
                VisibleLoadingImageCount = 0,
                PendingImageRegionCount = 0,
                CoveredArea = 480_000,
                UncoveredRegionCount = 0,
                LastPaintedIntersectionArea = 480_000,
                LastPaintedRegionWidth = 800,
                LastPaintedRegionHeight = 600,
                PaintProbeAccepted = true,
                PaintAcknowledgementRecorded = false,
            };
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                callbackWithoutLifecycleAck));
            string[] lines = File.ReadAllLines(path);
            using JsonDocument handoffDocument = JsonDocument.Parse(lines[1]);
            JsonElement handoff = handoffDocument.RootElement;
            Assert.True(handoff.GetProperty("PaintProbeAccepted").GetBoolean());
            Assert.False(handoff.GetProperty("PaintAcknowledgementRecorded").GetBoolean());
            Assert.Equal(Generation, handoff.GetProperty("PaintRequestedGeneration").GetInt64());
            Assert.Equal(0, handoff.GetProperty("PaintedGeneration").GetInt64());
            Assert.Equal(0, handoff.GetProperty("PaintedAtStopwatchTicks").GetInt64());
            Assert.Equal((int)FirstViewportImagesReadyPaintPollAction.NotPolled, handoff.GetProperty("PaintPollAction").GetInt32());

            FirstViewportImagesReadyProgress failedHandoffRearm = callbackWithoutLifecycleAck with
            {
                Stage = "polling",
                Reason = "paint-rearmed",
                PaintAcknowledgementIsCurrent = false,
                PaintPollAction = (int)FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            };
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                failedHandoffRearm));
            lines = File.ReadAllLines(path);
            using JsonDocument rearmPollDocument = JsonDocument.Parse(lines[2]);
            JsonElement rearmPoll = rearmPollDocument.RootElement;
            Assert.Equal(Generation, rearmPoll.GetProperty("PaintRequestedGeneration").GetInt64());
            Assert.True(rearmPoll.GetProperty("PaintProbeAccepted").GetBoolean());
            Assert.False(rearmPoll.GetProperty("PaintAcknowledgementRecorded").GetBoolean());
            Assert.Equal(0, rearmPoll.GetProperty("PaintedGeneration").GetInt64());
            Assert.Equal(0, rearmPoll.GetProperty("PaintedAtStopwatchTicks").GetInt64());
            Assert.False(rearmPoll.GetProperty("PaintAcknowledgementIsCurrent").GetBoolean());
            Assert.Equal(
                (int)FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
                rearmPoll.GetProperty("PaintPollAction").GetInt32());

            FirstViewportImagesReadyProgress waitingPoll = failedHandoffRearm with
            {
                Reason = "waiting-for-paint",
                PaintPollAction = (int)FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
            };
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                waitingPoll));
            lines = File.ReadAllLines(path);
            using JsonDocument pollDocument = JsonDocument.Parse(lines[3]);
            JsonElement poll = pollDocument.RootElement;
            Assert.True(poll.GetProperty("PaintProbeAccepted").GetBoolean());
            Assert.False(poll.GetProperty("PaintAcknowledgementRecorded").GetBoolean());
            Assert.Equal(Generation, poll.GetProperty("PaintRequestedGeneration").GetInt64());
            Assert.Equal(0, poll.GetProperty("PaintedGeneration").GetInt64());
            Assert.Equal(0, poll.GetProperty("PaintedAtStopwatchTicks").GetInt64());
            Assert.False(poll.GetProperty("PaintAcknowledgementIsCurrent").GetBoolean());
            Assert.Equal(
                (int)FirstViewportImagesReadyPaintPollAction.WaitingForPaint,
                poll.GetProperty("PaintPollAction").GetInt32());

            FirstViewportImagesReadyProgress staleRearm = callbackWithoutLifecycleAck with
            {
                Stage = "polling",
                Reason = "paint-rearmed",
                PaintProbeAccepted = true,
                PaintAcknowledgementRecorded = true,
                PaintedGeneration = Generation,
                PaintedAtStopwatchTicks = ImagesReadyStopwatchTicks,
                PaintAcknowledgementIsCurrent = false,
                PaintPollAction = (int)FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            };
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                staleRearm));
            lines = File.ReadAllLines(path);
            using JsonDocument rearmDocument = JsonDocument.Parse(lines[4]);
            JsonElement rearm = rearmDocument.RootElement;
            Assert.Equal("paint-rearmed", rearm.GetProperty("Reason").GetString());
            Assert.Equal(Generation, rearm.GetProperty("PaintedGeneration").GetInt64());
            Assert.Equal(ImagesReadyStopwatchTicks, rearm.GetProperty("PaintedAtStopwatchTicks").GetInt64());
            Assert.False(rearm.GetProperty("PaintAcknowledgementIsCurrent").GetBoolean());
            Assert.Equal(
                (int)FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
                rearm.GetProperty("PaintPollAction").GetInt32());

            FirstViewportImagesReadyProgress partialPaintRearm = waitingPoll with
            {
                Reason = "paint-rearmed",
                PaintProbeAccepted = false,
                PaintAcknowledgementRecorded = false,
                PaintAcknowledgementIsCurrent = false,
                PaintPollAction = (int)FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
            };
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                partialPaintRearm));
            lines = File.ReadAllLines(path);
            using JsonDocument partialRearmDocument = JsonDocument.Parse(lines[5]);
            JsonElement partialRearm = partialRearmDocument.RootElement;
            Assert.Equal("paint-rearmed", partialRearm.GetProperty("Reason").GetString());
            Assert.False(partialRearm.GetProperty("PaintProbeAccepted").GetBoolean());
            Assert.False(partialRearm.GetProperty("PaintAcknowledgementRecorded").GetBoolean());
            Assert.Equal(
                (int)FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport,
                partialRearm.GetProperty("PaintPollAction").GetInt32());

            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                progress with
                {
                    Reason = probeReason,
                    HasVisibleLoadingImages = false,
                    VisibleLoadingImageCount = 0,
                    PendingImageRegionCount = 0,
                }));
            using JsonDocument probeProgress = JsonDocument.Parse(File.ReadAllLines(path)[^1]);
            Assert.Equal(probeReason, probeProgress.RootElement.GetProperty("Reason").GetString());
            Assert.False(probeProgress.RootElement.GetProperty("HasVisibleLoadingImages").GetBoolean());

            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                callbackWithoutLifecycleAck with { PaintAcknowledgementRecorded = true }));

            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                progress with { Stage = "private-owner-name" }));
            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                progress with { Reason = "https://private.invalid/readme" }));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public async Task QueuedReadinessSignalWaitsForTheReadyProgressDrain()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string progressPath = Path.Combine(temporaryDirectory, "first-viewport-images-ready-progress.ndjson");
        string signalPath = Path.Combine(temporaryDirectory, "first-viewport-images-ready.json");
        try
        {
            FirstViewportImagesReadyEvidenceWriter.TryQueueProgressWrite(
                progressPath,
                auditEnabled: true,
                new FirstViewportImagesReadyProgress(
                    ImagesReadyAt,
                    Stage: "ready",
                    Reason: "viewport-covered-after-ready-paint",
                    PollCount: 3,
                    PaintCallbackCount: 2,
                    PaintedRegionCount: 2,
                    Generation: Generation,
                    CurrentGeneration: Generation,
                    PublishedGeneration: Generation,
                    SnapshotGeneration: Generation,
                    LayoutRevision: 5,
                    RasterizationScale: 1,
                    ViewportLeft: 0,
                    ViewportTop: 0,
                    ViewportWidth: 800,
                    ViewportHeight: 600,
                    ViewportMeasured: true,
                    HasVisibleLoadingImages: false,
                    VisibleLoadingImageCount: 0,
                    PendingImageRegionCount: 0,
                    CoveredArea: 480_000,
                    ViewportArea: 480_000,
                    UncoveredRegionCount: 0,
                    LastPaintedIntersectionArea: 480_000,
                    LastPaintedRegionLeft: 0,
                    LastPaintedRegionTop: 0,
                    LastPaintedRegionWidth: 800,
                    LastPaintedRegionHeight: 600,
                    HasPostArmPaintedRegion: true,
                    PaintProbeAccepted: true,
                    PaintAcknowledgementRecorded: true,
                    PaintRequestedGeneration: Generation,
                    PaintedGeneration: Generation,
                    PaintedAtStopwatchTicks: ImagesReadyStopwatchTicks,
                    PaintAcknowledgementIsCurrent: true,
                    PaintPollAction: (int)FirstViewportImagesReadyPaintPollAction.Ready));

            Task<bool>? queuedWrite = FirstViewportImagesReadyEvidenceWriter.TryQueueWrite(
                signalPath,
                auditEnabled: true,
                ProcessId,
                Host,
                Generation,
                Generation,
                pollCount: 3,
                probeWorkMilliseconds: 0.04,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: true,
                hasVisibleLoadingImages: false,
                ReadmeGitBlobSha1,
                ImagesReadyAt,
                ImagesReadyStopwatchTicks,
                StopwatchFrequency);

            Assert.NotNull(queuedWrite);
            Assert.True(await queuedWrite);
            Assert.True(File.Exists(signalPath));
            using JsonDocument progressDocument = JsonDocument.Parse(File.ReadAllText(progressPath).Trim());
            JsonElement progressJson = progressDocument.RootElement;
            Assert.Equal("ready", progressJson.GetProperty("Stage").GetString());
            Assert.True(progressJson.GetProperty("PaintProbeAccepted").GetBoolean());
            Assert.True(progressJson.GetProperty("PaintAcknowledgementRecorded").GetBoolean());
            Assert.Equal(Generation, progressJson.GetProperty("PaintRequestedGeneration").GetInt64());
            Assert.Equal(Generation, progressJson.GetProperty("PaintedGeneration").GetInt64());
            Assert.Equal(ImagesReadyStopwatchTicks, progressJson.GetProperty("PaintedAtStopwatchTicks").GetInt64());
            Assert.True(progressJson.GetProperty("PaintAcknowledgementIsCurrent").GetBoolean());
            Assert.Equal(
                (int)FirstViewportImagesReadyPaintPollAction.Ready,
                progressJson.GetProperty("PaintPollAction").GetInt32());
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public async Task ReadinessWriteHelperDoesNotInvokeSignalBeforeProgressDrainCompletes()
    {
        var progressDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool signalWritten = false;

        Task<bool> signalWrite = FirstViewportImagesReadyEvidenceWriter.WriteAfterProgressDrainAsync(
            progressDrain.Task,
            () =>
            {
                signalWritten = true;
                return true;
            });

        Assert.False(signalWrite.IsCompleted);
        Assert.False(signalWritten);
        progressDrain.SetResult(true);

        Assert.True(await signalWrite);
        Assert.True(signalWritten);
    }

    [Fact]
    public async Task ReadinessWriteHelperSuppressesSignalWhenGenerationIsSupersededDuringDrain()
    {
        var progressDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var generationCancellation = new CancellationTokenSource();
        bool signalWritten = false;

        Task<bool> signalWrite = FirstViewportImagesReadyEvidenceWriter.WriteAfterProgressDrainAsync(
            progressDrain.Task,
            () =>
            {
                signalWritten = true;
                return true;
            },
            generationCancellation.Token);

        Assert.False(signalWrite.IsCompleted);
        generationCancellation.Cancel();
        Assert.False(await signalWrite);
        progressDrain.SetResult(true);

        Assert.False(signalWritten);
    }

    [Fact]
    public async Task NewGenerationIsHandedOffWhenActiveReadinessWriteIsCanceled()
    {
        var oldProgressDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oldGenerationCancellation = new CancellationTokenSource();
        bool oldSignalWritten = false;
        bool newSignalWritten = false;

        Task<bool> oldWrite = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
            oldProgressDrain.Task,
            () =>
            {
                oldSignalWritten = true;
                return true;
            },
            oldGenerationCancellation.Token);
        Assert.False(oldWrite.IsCompleted);

        Task<bool> newWrite = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
            Task.CompletedTask,
            () =>
            {
                newSignalWritten = true;
                return true;
            });
        Assert.False(newWrite.IsCompleted);

        oldGenerationCancellation.Cancel();

        Assert.False(await oldWrite.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await newWrite.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(oldSignalWritten);
        Assert.True(newSignalWritten);
    }

    [Fact]
    public void SupersededGenerationCannotPublishReadinessSignal()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string signalPath = Path.Combine(temporaryDirectory, "first-viewport-images-ready.json");
        using var generationCancellation = new CancellationTokenSource();
        generationCancellation.Cancel();

        try
        {
            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWrite(
                signalPath,
                auditEnabled: true,
                ProcessId,
                Host,
                Generation,
                Generation,
                pollCount: 3,
                probeWorkMilliseconds: 0.04,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: true,
                hasVisibleLoadingImages: false,
                ReadmeGitBlobSha1,
                ImagesReadyAt,
                ImagesReadyStopwatchTicks,
                StopwatchFrequency,
                generationCancellation.Token));
            Assert.False(File.Exists(signalPath));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public void ProbeContractFailsClosedUntilTheCurrentViewportLayoutIsPublishedAndMeasured()
    {
        Assert.True(FirstViewportImagesReadyProbeContract.IsCurrentPublishedGeneration(
            Generation,
            Generation,
            Generation));
        Assert.False(FirstViewportImagesReadyProbeContract.IsCurrentPublishedGeneration(
            Generation,
            Generation + 1,
            Generation));
        Assert.False(FirstViewportImagesReadyProbeContract.IsCurrentPublishedGeneration(
            Generation,
            Generation,
            Generation - 1));

        Assert.True(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: 0,
            viewportHeight: 600,
            viewportMeasured: true));
        Assert.False(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: false,
            viewportTop: 0,
            viewportHeight: 600,
            viewportMeasured: true));
        Assert.False(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: 0,
            viewportHeight: 600,
            viewportMeasured: false));
        Assert.False(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: double.NaN,
            viewportHeight: 600,
            viewportMeasured: true));
        Assert.False(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: 0,
            viewportHeight: 0,
            viewportMeasured: true));

        Assert.True(FirstViewportImagesReadyProbeContract.IsViewportPaintAcknowledged(
            Generation,
            Generation,
            hasVisibleLoadingImages: false));
        Assert.False(FirstViewportImagesReadyProbeContract.IsViewportPaintAcknowledged(
            Generation,
            Generation - 1,
            hasVisibleLoadingImages: false));
        Assert.False(FirstViewportImagesReadyProbeContract.IsViewportPaintAcknowledged(
            Generation,
            Generation,
            hasVisibleLoadingImages: true));
    }

    [Fact]
    public void ImageFreeReadinessRequiresTheCurrentPaintAndMeasuredViewport()
    {
        const bool hasLoadingImages = false;
        Assert.True(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: 0,
            viewportHeight: 600,
            viewportMeasured: true));
        Assert.True(FirstViewportImagesReadyProbeContract.IsViewportPaintAcknowledged(
            Generation,
            Generation,
            hasLoadingImages));
        Assert.False(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: 0,
            viewportHeight: 600,
            viewportMeasured: false));
        Assert.False(FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop: double.NaN,
            viewportHeight: 600,
            viewportMeasured: true));
        Assert.False(FirstViewportImagesReadyProbeContract.IsViewportPaintAcknowledged(
            Generation,
            Generation - 1,
            hasLoadingImages));
    }

    [Fact]
    public async Task ProductionAuditBridgeEmitsSourceGeneratedReadinessJson()
    {
        const string evidenceVariable = "JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_EVIDENCE_PATH";
        const string corpusVariable = "JITHUB_README_AUDIT_SAME_BYTE_CORPUS";
        const string readmeShaVariable = "JITHUB_README_AUDIT_SAME_BYTE_README_SHA";
        string temporaryDirectory = CreateTemporaryDirectory();
        string path = Path.Combine(temporaryDirectory, "first-viewport-images-ready.json");
        string? previousEvidencePath = Environment.GetEnvironmentVariable(evidenceVariable);
        string? previousCorpusPath = Environment.GetEnvironmentVariable(corpusVariable);
        string? previousReadmeSha = Environment.GetEnvironmentVariable(readmeShaVariable);
        try
        {
            Environment.SetEnvironmentVariable(evidenceVariable, path);
            Environment.SetEnvironmentVariable(corpusVariable, "same-byte-test-corpus.json");
            Environment.SetEnvironmentVariable(readmeShaVariable, ReadmeGitBlobSha1);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: Host,
                productionAuditEnabled: true);

            Assert.True(MarkdownLifecycleAutomationBridge.IsFirstViewportImagesReadyEvidenceEnabled);
            Task<bool>? writeTask = MarkdownLifecycleAutomationBridge.RecordFirstViewportImagesReady(
                Host,
                Generation,
                viewportPaintGeneration: Generation,
                pollCount: 4,
                probeWorkMilliseconds: 0.06,
                viewportTop: 0,
                viewportHeight: 600,
                viewportMeasured: true,
                readyAt: ImagesReadyAt,
                viewportPaintStopwatchTicks: ImagesReadyStopwatchTicks,
                generationCancellationToken: default);

            Assert.NotNull(writeTask);
            Assert.True(await writeTask);
            Assert.True(File.Exists(path));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement json = document.RootElement;
            Assert.Equal(Environment.ProcessId, json.GetProperty("ProcessId").GetInt32());
            Assert.Equal(Host, json.GetProperty("Host").GetString());
            Assert.Equal(Generation, json.GetProperty("Generation").GetInt64());
            Assert.Equal(Generation, json.GetProperty("ViewportPaintGeneration").GetInt64());
            Assert.Equal(4, json.GetProperty("PollCount").GetInt32());
            Assert.Equal(0.06, json.GetProperty("ProbeWorkMilliseconds").GetDouble());
            Assert.Equal(0, json.GetProperty("ViewportTop").GetDouble());
            Assert.Equal(600, json.GetProperty("ViewportHeight").GetDouble());
            Assert.True(json.GetProperty("ViewportMeasured").GetBoolean());
            Assert.False(json.GetProperty("HasVisibleLoadingImages").GetBoolean());
            Assert.Equal(ReadmeGitBlobSha1, json.GetProperty("ReadmeGitBlobSha1").GetString());
            Assert.Equal(ImagesReadyAt, json.GetProperty("Timestamp").GetDateTimeOffset());
            Assert.Equal(ImagesReadyStopwatchTicks, json.GetProperty("StopwatchTicks").GetInt64());
            Assert.Equal(Stopwatch.Frequency, json.GetProperty("StopwatchFrequency").GetInt64());
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(evidenceVariable, previousEvidencePath);
            Environment.SetEnvironmentVariable(corpusVariable, previousCorpusPath);
            Environment.SetEnvironmentVariable(readmeShaVariable, previousReadmeSha);
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public void ReadinessContractRejectsWrongIdentityLoadingStateAndNonMonotonicTimestamps()
    {
        NativeLifecycleReadySignal hostReady = ValidHostReady();
        NativeRenderCompleteSignal renderComplete = ValidRenderComplete();
        NativeFirstViewportImagesReadySignal valid = ValidImagesReady();
        Action[] invalidSignals =
        [
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { ProcessId = ProcessId + 1 }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { Host = "other-host" }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { Generation = Generation + 1 }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { ViewportPaintGeneration = Generation - 1 }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { ReadmeGitBlobSha1 = new string('b', 40) }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { HasVisibleLoadingImages = true }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { ViewportMeasured = false }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { ViewportHeight = 0 }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { Timestamp = RenderCompleteAt.AddTicks(-1) }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { Timestamp = HostReadyAt.AddTicks(-1) }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { PollCount = 0 }),
            () => NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId, Host, ReadmeGitBlobSha1, hostReady, renderComplete, valid with { ProbeWorkMilliseconds = double.NaN }),
        ];

        foreach (Action validate in invalidSignals)
            Assert.Throws<InvalidDataException>(validate);

        Assert.Throws<InvalidDataException>(() => NativeFirstViewportImagesReadyContract.ValidateRenderIdentity(
            ProcessId + 1,
            Host,
            hostReady,
            renderComplete));
    }

    [Fact]
    public void ReadinessFailureNamesMismatchedFieldsWithoutEchoingIdentityValues()
    {
        NativeLifecycleReadySignal hostReady = ValidHostReady();
        NativeRenderCompleteSignal renderComplete = ValidRenderComplete();
        NativeFirstViewportImagesReadySignal valid = ValidImagesReady();

        (string Field, Action Validate)[] mismatches =
        [
            ("ProcessId", () => Validate(valid with { ProcessId = ProcessId + 1 })),
            ("Host", () => Validate(valid with { Host = "private-host-value" })),
            ("Generation", () => Validate(valid with { Generation = Generation + 1 })),
            ("ViewportPaintGeneration", () => Validate(valid with { ViewportPaintGeneration = Generation - 1 })),
            ("PollCount", () => Validate(valid with { PollCount = 0 })),
            ("ProbeWorkMilliseconds", () => Validate(valid with { ProbeWorkMilliseconds = double.NaN })),
            ("ViewportTop", () => Validate(valid with { ViewportTop = -1 })),
            ("ViewportHeight", () => Validate(valid with { ViewportHeight = 0 })),
            ("ViewportMeasured", () => Validate(valid with { ViewportMeasured = false })),
            ("HasVisibleLoadingImages", () => Validate(valid with { HasVisibleLoadingImages = true })),
            ("TimestampAfterRenderComplete", () => Validate(valid with { Timestamp = RenderCompleteAt.AddTicks(-1) })),
            ("TimestampAfterHostReady", () => Validate(valid with { Timestamp = HostReadyAt.AddTicks(-1) })),
            ("ReadmeGitBlobSha1", () => Validate(valid with { ReadmeGitBlobSha1 = new string('b', 40) })),
        ];

        foreach ((string field, Action validate) in mismatches)
        {
            InvalidDataException exception = Assert.Throws<InvalidDataException>(validate);
            Assert.Contains(field, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-host-value", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain((ProcessId + 1).ToString(), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('b', 40), exception.Message, StringComparison.Ordinal);
        }

        void Validate(NativeFirstViewportImagesReadySignal signal) =>
            NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                ProcessId,
                Host,
                ReadmeGitBlobSha1,
                hostReady,
                renderComplete,
                signal);
    }

    [Fact]
    public void SameByteSourceTimingRequiresRawAndDecodedIdentityOnTheSameGeneration()
    {
        NativeRenderCompleteSignal renderComplete = ValidRenderComplete() with
        {
            SameByteReadmeSourceAdmission = ValidSourceAdmission(),
        };
        NativeFirstViewportImagesReadySignal ready = ValidImagesReady();

        double elapsedMilliseconds = NativeFirstViewportImagesReadyContract.ValidateSameByteSourceTiming(
            ProcessId,
            Host,
            ReadmeGitBlobSha1,
            expectedRawByteSize: 128,
            RawReadmeSha256,
            expectedDecodedTextUtf8ByteSize: 124,
            DecodedReadmeSha256,
            renderComplete,
            ready);

        Assert.Equal((ImagesReadyStopwatchTicks - SourceAdmissionStopwatchTicks) * 1000d / StopwatchFrequency,
            elapsedMilliseconds);

        NativeSameByteReadmeSourceAdmissionSignal admission = ValidSourceAdmission();
        (string Field, NativeSameByteReadmeSourceAdmissionSignal? Admission, NativeRenderCompleteSignal Render, NativeFirstViewportImagesReadySignal Ready)[] invalid =
        [
            ("SameByteReadmeSourceAdmission", null, renderComplete, ready),
            ("RawSha256", admission with { RawSha256 = new string('e', 64) }, renderComplete, ready),
            ("DecodedTextSha256", admission with { DecodedTextSha256 = new string('e', 64) }, renderComplete, ready),
            ("Generation", admission with { Generation = Generation + 1 }, renderComplete, ready),
            ("StopwatchFrequency", admission with { StopwatchFrequency = StopwatchFrequency + 1 }, renderComplete, ready),
            ("StopwatchTicksBeforeRenderComplete", admission with { StopwatchTicks = RenderCompleteStopwatchTicks + 1 }, renderComplete, ready),
            ("RenderCompleteBeforeReadyStopwatchTicks", admission, renderComplete, ready with { StopwatchTicks = RenderCompleteStopwatchTicks - 1 }),
        ];

        foreach ((string field, NativeSameByteReadmeSourceAdmissionSignal? sourceAdmission, NativeRenderCompleteSignal render, NativeFirstViewportImagesReadySignal imageReady) in invalid)
        {
            NativeRenderCompleteSignal candidate = render with { SameByteReadmeSourceAdmission = sourceAdmission };
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                NativeFirstViewportImagesReadyContract.ValidateSameByteSourceTiming(
                    ProcessId,
                    Host,
                    ReadmeGitBlobSha1,
                    128,
                    RawReadmeSha256,
                    124,
                    DecodedReadmeSha256,
                    candidate,
                    imageReady));
            Assert.Contains(field, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(RawReadmeSha256, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(DecodedReadmeSha256, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SameByteBridgeBindsDecodedSourceDigestToTheRenderGeneration()
    {
        const string sourceEvidenceVariable = "JITHUB_README_AUDIT_NATIVE_SOURCE_EVIDENCE_PATH";
        const string renderCompleteVariable = "JITHUB_MARKDOWN_RENDER_COMPLETE_EVIDENCE_PATH";
        const string corpusVariable = "JITHUB_README_AUDIT_SAME_BYTE_CORPUS";
        const string readmeShaVariable = "JITHUB_README_AUDIT_SAME_BYTE_README_SHA";
        string temporaryDirectory = CreateTemporaryDirectory();
        string sourcePath = Path.Combine(temporaryDirectory, "readme-source.json");
        string renderPath = Path.Combine(temporaryDirectory, "render-complete.json");
        string? previousSourcePath = Environment.GetEnvironmentVariable(sourceEvidenceVariable);
        string? previousRenderPath = Environment.GetEnvironmentVariable(renderCompleteVariable);
        string? previousCorpusPath = Environment.GetEnvironmentVariable(corpusVariable);
        string? previousReadmeSha = Environment.GetEnvironmentVariable(readmeShaVariable);
        try
        {
            Environment.SetEnvironmentVariable(sourceEvidenceVariable, sourcePath);
            Environment.SetEnvironmentVariable(renderCompleteVariable, renderPath);
            Environment.SetEnvironmentVariable(corpusVariable, "same-byte-test-corpus.json");
            Environment.SetEnvironmentVariable(readmeShaVariable, ReadmeGitBlobSha1);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: Host,
                productionAuditEnabled: true);

            byte[] rawBytes = [0xef, 0xbb, 0xbf, (byte)'#', (byte)' ', (byte)'R', (byte)'E', (byte)'A', (byte)'D', (byte)'M', (byte)'E'];
            string decodedMarkdown = Encoding.UTF8.GetString(rawBytes);
            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSource(
                rawBytes,
                decodedMarkdown,
                ReadmeGitBlobSha1);
            SameByteReadmeSourceIdentity? identity = MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                decodedMarkdown,
                ReadmeGitBlobSha1,
                out string matchedStatus);
            Assert.NotNull(identity);
            Assert.Equal("matched", matchedStatus);
            WeakReference<string> decodedMarkdownReference =
                Assert.IsType<WeakReference<string>>(identity.DecodedMarkdownReference);
            Assert.True(decodedMarkdownReference.TryGetTarget(out string? recordedMarkdown));
            Assert.Same(decodedMarkdown, recordedMarkdown);

            string reconstructedMarkdown = new(decodedMarkdown.ToCharArray());
            Assert.NotSame(decodedMarkdown, reconstructedMarkdown);
            Assert.Same(
                identity,
                MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                    reconstructedMarkdown,
                    ReadmeGitBlobSha1,
                    out string reconstructedStatus));
            Assert.Equal("matched", reconstructedStatus);
            Assert.Null(MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                decodedMarkdown.Replace("README", "READMX", StringComparison.Ordinal),
                ReadmeGitBlobSha1,
                out string digestMismatchStatus));
            Assert.Equal("decoded-text-digest-mismatch", digestMismatchStatus);
            Assert.Null(MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                decodedMarkdown + " changed",
                ReadmeGitBlobSha1,
                out string textMismatchStatus));
            Assert.Equal("decoded-text-size-mismatch", textMismatchStatus);
            Assert.Null(MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                decodedMarkdown,
                "0123456789abcdef0123456789abcdef01234567",
                out string documentIdentityStatus));
            Assert.Equal("git-blob-mismatch", documentIdentityStatus);
            Assert.Null(MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                decodedMarkdown,
                null,
                out string missingDocumentIdentityStatus));
            Assert.Equal("document-identity-missing", missingDocumentIdentityStatus);

            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSource(
                rawBytes,
                reconstructedMarkdown,
                ReadmeGitBlobSha1);
            Assert.True(decodedMarkdownReference.TryGetTarget(out recordedMarkdown));
            Assert.Same(reconstructedMarkdown, recordedMarkdown);
            Assert.Same(
                identity,
                MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                    reconstructedMarkdown,
                    ReadmeGitBlobSha1,
                    out _));

            using (JsonDocument sourceDocument = JsonDocument.Parse(File.ReadAllText(sourcePath)))
            {
                JsonElement source = sourceDocument.RootElement;
                Assert.Equal(rawBytes.Length, source.GetProperty("ByteSize").GetInt32());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant(), source.GetProperty("Sha256").GetString());
                byte[] decodedBytes = Encoding.UTF8.GetBytes(decodedMarkdown);
                Assert.Equal(decodedBytes.Length, source.GetProperty("DecodedTextUtf8ByteSize").GetInt32());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(decodedBytes)).ToLowerInvariant(), source.GetProperty("DecodedTextSha256").GetString());
                Assert.Equal(ReadmeGitBlobSha1, source.GetProperty("ReadmeGitBlobSha1").GetString());
            }

            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSourceAdmission(
                Host,
                Generation,
                SourceAdmissionStopwatchTicks,
                StopwatchFrequency,
                identity,
                "matched",
                new MarkdownDocumentSource("test-readme", "nilbuild", "developer-roadmap",
                    new string('c', 40), "README.md"));
            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSourceAdmission(
                "MarkdownHost_Unrelated",
                Generation,
                SourceAdmissionStopwatchTicks,
                StopwatchFrequency,
                identity,
                "matched");
            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSource(
                rawBytes,
                decodedMarkdown,
                ReadmeGitBlobSha1);
            MarkdownLifecycleAutomationBridge.RecordRenderComplete(
                Host,
                Generation,
                RenderCompleteAt,
                RenderCompleteStopwatchTicks,
                StopwatchFrequency,
                performance: null,
                visualProfileId: "github-primer-light",
                visualProfileVersion: 1,
                visualProfileCssSha256: VisualProfileFingerprint);

            NativeRenderCompleteSignal matched = NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            Assert.NotNull(matched.SameByteReadmeSourceAdmission);
            Assert.Equal(Generation, matched.SameByteReadmeSourceAdmission.Generation);
            Assert.Equal(SourceAdmissionStopwatchTicks, matched.SameByteReadmeSourceAdmission.StopwatchTicks);
            Assert.Equal(identity.DecodedTextSha256, matched.SameByteReadmeSourceAdmission.DecodedTextSha256);
            Assert.Equal("nilbuild", matched.SameByteReadmeSourceAdmission.SourceOwner);
            Assert.Equal("developer-roadmap", matched.SameByteReadmeSourceAdmission.SourceRepository);
            Assert.Equal(new string('c', 40), matched.SameByteReadmeSourceAdmission.SourceRef);
            Assert.Equal("README.md", matched.SameByteReadmeSourceAdmission.SourcePath);
            Assert.Null(matched.SameByteReadmeSourceAdmissionFailure);
            Assert.Equal("github-primer-light", matched.VisualProfile?.Id);
            Assert.Equal(1, matched.VisualProfile?.Version);
            Assert.Equal(
                VisualProfileFingerprint,
                matched.VisualProfile?.CssSha256);

            MarkdownLifecycleAutomationBridge.RecordRenderComplete(
                Host,
                Generation + 1,
                RenderCompleteAt.AddMilliseconds(1),
                RenderCompleteStopwatchTicks + 1,
                StopwatchFrequency,
                performance: null);
            NativeRenderCompleteSignal unmatched = NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            Assert.Null(unmatched.SameByteReadmeSourceAdmission);
            Assert.Null(unmatched.VisualProfile);

            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSourceAdmission(
                Host,
                Generation + 2,
                0,
                0,
                identity: null,
                failureReason: "document-identity-missing");
            MarkdownLifecycleAutomationBridge.RecordRenderComplete(
                Host,
                Generation + 2,
                RenderCompleteAt.AddMilliseconds(2),
                RenderCompleteStopwatchTicks + 2,
                StopwatchFrequency,
                performance: null);
            NativeRenderCompleteSignal rejected = NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            Assert.Null(rejected.SameByteReadmeSourceAdmission);
            Assert.Equal("document-identity-missing", rejected.SameByteReadmeSourceAdmissionFailure?.Status);
            Assert.Equal(Generation + 2, rejected.SameByteReadmeSourceAdmissionFailure?.Generation);

            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSourceAdmission(
                Host,
                Generation + 3,
                SourceAdmissionStopwatchTicks,
                StopwatchFrequency,
                identity: null,
                failureReason: "assignment-generation-superseded");
            MarkdownLifecycleAutomationBridge.RecordRenderComplete(
                Host,
                Generation + 3,
                RenderCompleteAt.AddMilliseconds(3),
                RenderCompleteStopwatchTicks + 3,
                StopwatchFrequency,
                performance: null);
            NativeRenderCompleteSignal superseded = NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            Assert.Null(superseded.SameByteReadmeSourceAdmission);
            Assert.Equal("assignment-generation-superseded", superseded.SameByteReadmeSourceAdmissionFailure?.Status);
            Assert.Equal(Generation + 3, superseded.SameByteReadmeSourceAdmissionFailure?.Generation);

            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSourceAdmission(
                Host,
                Generation + 4,
                0,
                0,
                identity: null,
                failureReason: "assignment-generation-unavailable");
            MarkdownLifecycleAutomationBridge.RecordRenderComplete(
                Host,
                Generation + 4,
                RenderCompleteAt.AddMilliseconds(4),
                RenderCompleteStopwatchTicks + 4,
                StopwatchFrequency,
                performance: null);
            NativeRenderCompleteSignal unavailable = NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            Assert.Null(unavailable.SameByteReadmeSourceAdmission);
            Assert.Equal("assignment-generation-unavailable", unavailable.SameByteReadmeSourceAdmissionFailure?.Status);
            Assert.Equal(Generation + 4, unavailable.SameByteReadmeSourceAdmissionFailure?.Generation);

            MarkdownLifecycleAutomationBridge.RecordSameByteReadmeSource(
                rawBytes,
                decodedMarkdown,
                "0123456789abcdef0123456789abcdef01234567");
            Assert.Null(MarkdownLifecycleAutomationBridge.MatchSameByteReadmeSource(
                decodedMarkdown,
                ReadmeGitBlobSha1,
                out string sourceMismatchStatus));
            Assert.Equal("source-evidence-missing", sourceMismatchStatus);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(sourceEvidenceVariable, previousSourcePath);
            Environment.SetEnvironmentVariable(renderCompleteVariable, previousRenderPath);
            Environment.SetEnvironmentVariable(corpusVariable, previousCorpusPath);
            Environment.SetEnvironmentVariable(readmeShaVariable, previousReadmeSha);
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public void TerminalScrollNoOpAllowsExtentChangeButRequiresStableReadyRecheck()
    {
        // UIA can report 100% while lazy layout is still changing the view
        // size. This is terminal evidence, but only a candidate until the next
        // image-ready/stability pass observes the same extent.
        Assert.True(NativeScrollTerminalContract.IsTerminalNoOpCandidate(
            currentPercent: 100,
            settledPercent: 100,
            settledViewSize: 12,
            verticallyScrollable: true));

        Assert.False(NativeScrollTerminalContract.IsStableTerminalConfirmation(
            candidatePercent: 100,
            candidateViewSize: 12,
            currentPercent: 100,
            currentViewSize: 13,
            verticallyScrollable: true));
        Assert.True(NativeScrollTerminalContract.IsStableTerminalConfirmation(
            candidatePercent: 100,
            candidateViewSize: 12,
            currentPercent: 100,
            currentViewSize: 12,
            verticallyScrollable: true));

        Assert.False(NativeScrollTerminalContract.IsTerminalNoOpCandidate(
            currentPercent: 96,
            settledPercent: 96,
            settledViewSize: 12,
            verticallyScrollable: true));
        Assert.False(NativeScrollTerminalContract.IsTerminalNoOpCandidate(
            currentPercent: 100,
            settledPercent: 100,
            settledViewSize: 0,
            verticallyScrollable: true));
    }

    [Fact]
    public void SignalParserFailsClosedForMissingAndPartialJson()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string path = Path.Combine(temporaryDirectory, "first-viewport-images-ready.json");
        try
        {
            File.WriteAllText(path, "{");
            Assert.ThrowsAny<JsonException>(() => NativeFirstViewportImagesReadyContract.ReadImagesReadySignal(path));

            File.WriteAllText(path, "{\"ProcessId\":4821,\"Host\":\"" + Host + "\"}");
            Assert.Throws<InvalidDataException>(() => NativeFirstViewportImagesReadyContract.ReadImagesReadySignal(path));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public void HostAndRenderSignalParsersRequireCompleteIdentityFields()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string hostPath = Path.Combine(temporaryDirectory, "host-ready.json");
        string renderPath = Path.Combine(temporaryDirectory, "render-complete.json");
        try
        {
            File.WriteAllText(hostPath, "{\"ProcessId\":4821,\"Stage\":\"" + Host + "\",\"Timestamp\":\"" + HostReadyAt.ToString("O") + "\"}");
            File.WriteAllText(renderPath, "{\"ProcessId\":4821,\"Host\":\"" + Host + "\",\"Generation\":7,\"Timestamp\":\"" + RenderCompleteAt.ToString("O") + "\",\"StopwatchTicks\":" + RenderCompleteStopwatchTicks + ",\"StopwatchFrequency\":" + StopwatchFrequency + "}");

            NativeLifecycleReadySignal hostReady =
                NativeFirstViewportImagesReadyContract.ReadLifecycleReadySignal(hostPath);
            NativeRenderCompleteSignal renderComplete =
                NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            NativeFirstViewportImagesReadyContract.ValidateRenderIdentity(
                ProcessId,
                Host,
                hostReady,
                renderComplete);

            File.WriteAllText(renderPath,
                "{\"ProcessId\":4821,\"Host\":\"" + Host +
                "\",\"Generation\":7,\"Timestamp\":\"" + RenderCompleteAt.ToString("O") +
                "\",\"StopwatchTicks\":" + RenderCompleteStopwatchTicks +
                ",\"StopwatchFrequency\":" + StopwatchFrequency +
                ",\"VisualProfile\":{\"Id\":\"github-primer-light\",\"Version\":1,\"CssSha256\":\"" +
                VisualProfileFingerprint + "\"}}");
            Assert.Equal(
                VisualProfileFingerprint,
                NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath).VisualProfile?.CssSha256);

            File.WriteAllText(renderPath,
                "{\"ProcessId\":4821,\"Host\":\"" + Host +
                "\",\"Generation\":7,\"Timestamp\":\"" + RenderCompleteAt.ToString("O") +
                "\",\"StopwatchTicks\":" + RenderCompleteStopwatchTicks +
                ",\"StopwatchFrequency\":" + StopwatchFrequency +
                ",\"VisualProfile\":{\"Id\":\"github-primer-light\",\"Version\":1,\"CssSha256\":\"invalid\"}}");
            Assert.Throws<InvalidDataException>(
                () => NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath));

            File.WriteAllText(renderPath, "{\"ProcessId\":4821}");
            Assert.Throws<InvalidDataException>(
                () => NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static NativeLifecycleReadySignal ValidHostReady() =>
        new(ProcessId, Host, HostReadyAt);

    private static NativeRenderCompleteSignal ValidRenderComplete() =>
        new(ProcessId, Host, Generation, RenderCompleteAt, RenderCompleteStopwatchTicks, StopwatchFrequency);

    private static NativeSameByteReadmeSourceAdmissionSignal ValidSourceAdmission() =>
        new(
            ProcessId,
            Host,
            Generation,
            RawByteSize: 128,
            RawSha256: RawReadmeSha256,
            DecodedTextUtf8ByteSize: 124,
            DecodedTextSha256: DecodedReadmeSha256,
            ReadmeGitBlobSha1,
            SourceAdmissionStopwatchTicks,
            StopwatchFrequency);

    private static NativeFirstViewportImagesReadySignal ValidImagesReady() =>
        new(ProcessId, Host, Generation, Generation, 3, 0.04, 0, 600, true, false, ReadmeGitBlobSha1, ImagesReadyAt, ImagesReadyStopwatchTicks, StopwatchFrequency);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jithub-first-viewport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
