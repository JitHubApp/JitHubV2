using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using Windows.Foundation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class NativeFirstViewportImagesReadyContractTests
{
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
    private static readonly DateTimeOffset HostReadyAt = new(2026, 9, 28, 15, 54, 34, TimeSpan.Zero);
    private static readonly DateTimeOffset RenderCompleteAt = HostReadyAt.AddMilliseconds(95);
    private static readonly DateTimeOffset ImagesReadyAt = RenderCompleteAt.AddMilliseconds(18);

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
                timestamp: ImagesReadyAt));
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
                timestamp: ImagesReadyAt));
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
                timestamp: ImagesReadyAt));
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
                timestamp: ImagesReadyAt));

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

    [Fact]
    public void ProgressWriterPublishesPrivacySafePaintAndCoverageDiagnostics()
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
            HasPostArmPaintedRegion: true);
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
            Assert.DoesNotContain("Host", jsonLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Readme", jsonLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Url", jsonLine, StringComparison.OrdinalIgnoreCase);

            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryWriteProgress(
                path,
                auditEnabled: true,
                progress with { Stage = "private-owner-name" }));
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
                    HasPostArmPaintedRegion: true));

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
                ImagesReadyAt);

            Assert.NotNull(queuedWrite);
            Assert.True(await queuedWrite);
            Assert.True(File.Exists(signalPath));
            using JsonDocument progressDocument = JsonDocument.Parse(File.ReadAllText(progressPath).Trim());
            Assert.Equal("ready", progressDocument.RootElement.GetProperty("Stage").GetString());
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
                readyAt: ImagesReadyAt);

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
            File.WriteAllText(renderPath, "{\"ProcessId\":4821,\"Host\":\"" + Host + "\",\"Generation\":7,\"Timestamp\":\"" + RenderCompleteAt.ToString("O") + "\"}");

            NativeLifecycleReadySignal hostReady =
                NativeFirstViewportImagesReadyContract.ReadLifecycleReadySignal(hostPath);
            NativeRenderCompleteSignal renderComplete =
                NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderPath);
            NativeFirstViewportImagesReadyContract.ValidateRenderIdentity(
                ProcessId,
                Host,
                hostReady,
                renderComplete);

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
        new(ProcessId, Host, Generation, RenderCompleteAt);

    private static NativeFirstViewportImagesReadySignal ValidImagesReady() =>
        new(ProcessId, Host, Generation, Generation, 3, 0.04, 0, 600, true, false, ReadmeGitBlobSha1, ImagesReadyAt);

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
}
