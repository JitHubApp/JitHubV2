using System.Text.Json;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
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
    public void PaintCoverageResetsForImageReadinessAndViewportChanges()
    {
        var coverage = new FirstViewportPaintCoverage();
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 100, 100));

        // The first region contained a placeholder. Once its image completes,
        // it cannot be credited to the all-resources-ready paint epoch.
        coverage.Reset();
        Assert.False(coverage.AddPaintedRegion(0, 0, 200, 100, 100, 0, 100, 100));
        Assert.False(coverage.Covers(0, 0, 200, 100));
        Assert.True(coverage.AddPaintedRegion(0, 0, 200, 100, 0, 0, 100, 100));

        Assert.False(coverage.Covers(0, 10, 200, 100));
        Assert.False(coverage.AddPaintedRegion(0, 10, 200, 100, 0, 10, 100, 100));
        Assert.True(coverage.AddPaintedRegion(0, 10, 200, 100, 100, 10, 100, 100));
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
