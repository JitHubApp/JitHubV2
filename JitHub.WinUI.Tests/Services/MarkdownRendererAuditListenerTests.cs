using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[CollectionDefinition("Markdown renderer audit environment", DisableParallelization = true)]
public sealed class MarkdownRendererAuditEnvironmentCollection
{
}

[Collection("Markdown renderer audit environment")]
public sealed class MarkdownRendererAuditListenerTests
{
    [Fact]
    public void ImageResolutionRecordsExactPayloadIdentityWithoutRetainingBytes()
    {
        const string variable = "JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-image-resolution-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        byte[] bytes = [1, 2, 3, 4, 5];
        const string credentialBearingSource =
            "https://source-user:source-password@images.example.test/private/source.png?token=source-secret";
        var credentialBearingResolvedUri = new Uri(
            "https://cdn-user:cdn-password@cdn.example.test/private/resolved.png?signature=resolved-secret");
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordImageResolution(
                credentialBearingSource,
                MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                    bytes,
                    "image/svg+xml",
                    credentialBearingResolvedUri)),
                DateTimeOffset.UtcNow,
                12.5);

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)),
                document.RootElement.GetProperty("ContentSha256").GetString());
            Assert.Equal(bytes.Length, document.RootElement.GetProperty("ByteLength").GetInt32());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialBearingSource))).ToLowerInvariant(),
                document.RootElement.GetProperty("SourceSha256").GetString());
            Assert.Equal("absolute-https", document.RootElement.GetProperty("SourceKind").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialBearingResolvedUri.AbsoluteUri))).ToLowerInvariant(),
                document.RootElement.GetProperty("ResolvedUriSha256").GetString());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("ResolvedUri", out _));
            Assert.False(document.RootElement.TryGetProperty("Bytes", out _));
            Assert.False(document.RootElement.TryGetProperty("Payload", out _));
            Assert.DoesNotContain("source-password", line, StringComparison.Ordinal);
            Assert.DoesNotContain("source-secret", line, StringComparison.Ordinal);
            Assert.DoesNotContain("cdn-password", line, StringComparison.Ordinal);
            Assert.DoesNotContain("resolved-secret", line, StringComparison.Ordinal);
            Assert.DoesNotContain("private/source.png", line, StringComparison.Ordinal);
            Assert.DoesNotContain("private/resolved.png", line, StringComparison.Ordinal);

            MarkdownLifecycleAutomationBridge.RecordImageResolution(
                "missing.svg",
                MarkdownImageResolution.Unavailable,
                DateTimeOffset.UtcNow,
                1,
                imageResolveContext: new MarkdownImageResolveContext(
                    null,
                    "docs/README.md",
                    AllowThirdPartyRemoteImages: false));
            string unavailableLine = File.ReadAllLines(path)[1];
            using JsonDocument unavailableDocument = JsonDocument.Parse(unavailableLine);
            Assert.Equal(JsonValueKind.Null,
                unavailableDocument.RootElement.GetProperty("ContentSha256").ValueKind);
            Assert.Equal(
                "missing-document-context",
                unavailableDocument.RootElement.GetProperty("ResolutionDiagnostic")
                    .GetProperty("FailureClass").GetString());
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ImageUnavailableEvidencePreservesReasonAndSourceIdentityWithoutRawUrl()
    {
        const string evidenceVariable = "JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH";
        const string targetHostVariable = "JITHUB_MARKDOWN_LIFECYCLE_HOST";
        const string source =
            "https://image-user:image-password@cdn.example.test/private/avatar.png?token=do-not-log";
        const string automationId = "MarkdownViewer-RepoReadme";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-image-unavailable-audit-{Guid.NewGuid():N}.ndjson");
        string? previousEvidencePath = Environment.GetEnvironmentVariable(evidenceVariable);
        string? previousTargetHost = Environment.GetEnvironmentVariable(targetHostVariable);

        try
        {
            Environment.SetEnvironmentVariable(evidenceVariable, path);
            Environment.SetEnvironmentVariable(targetHostVariable, null);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordImageUnavailable(
                automationId,
                source,
                MarkdownImageUnavailableReason.InsecureRemoteContent);

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement signal = document.RootElement;
            Assert.Equal(automationId, signal.GetProperty("Host").GetString());
            Assert.Equal("InsecureRemoteContent", signal.GetProperty("Reason").GetString());
            Assert.Equal("absolute-https", signal.GetProperty("SourceKind").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),
                signal.GetProperty("SourceSha256").GetString());
            Assert.Equal(
                MarkdownImageResolutionAuditDiagnostic.GetSourceMatchSha256(source),
                signal.GetProperty("SourceMatchSha256").EnumerateArray()
                    .Select(static value => value.GetString()!)
                    .ToArray());
            Assert.False(signal.TryGetProperty("Source", out _));
            Assert.DoesNotContain("image-password", line, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-log", line, StringComparison.Ordinal);
            Assert.DoesNotContain("private/avatar.png", line, StringComparison.Ordinal);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(evidenceVariable, previousEvidencePath);
            Environment.SetEnvironmentVariable(targetHostVariable, previousTargetHost);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ImageUnavailableIdentityCanMatchRepositoryRelativeSourceToBrowserRoute()
    {
        string markdownSource = "./assets/images/character.png";
        string browserSource = "https://github.com/example/project/blob/main/assets/images/character.png?raw=true";

        string[] sourceHashes = MarkdownImageResolutionAuditDiagnostic.GetSourceMatchSha256(markdownSource);
        string[] browserHashes = MarkdownImageResolutionAuditDiagnostic.GetSourceMatchSha256(browserSource);

        Assert.NotEmpty(sourceHashes);
        Assert.NotEmpty(browserHashes);
        Assert.NotEmpty(sourceHashes.Intersect(browserHashes, StringComparer.Ordinal));
        Assert.All(sourceHashes.Concat(browserHashes), static hash => Assert.Equal(64, hash.Length));
        Assert.DoesNotContain("character.png", string.Join(string.Empty, sourceHashes), StringComparison.Ordinal);
    }

    [Fact]
    public void ImageResolutionAuditBudget_IsPerDocumentBoundedAndResettable()
    {
        var budget = new MarkdownImageResolutionAuditBudget(
            maximumDocuments: 2,
            maximumDiagnosticsPerDocument: 2);
        MarkdownImageResolutionAuditDiagnostic firstDocument = CreateDiagnostic("docs/README.md");
        MarkdownImageResolutionAuditDiagnostic secondDocument = CreateDiagnostic("examples/README.md");
        MarkdownImageResolutionAuditDiagnostic thirdDocument = CreateDiagnostic("nested/README.md");

        Assert.True(budget.TryReserve(firstDocument));
        Assert.True(budget.TryReserve(firstDocument));
        Assert.False(budget.TryReserve(firstDocument));
        Assert.True(budget.TryReserve(secondDocument));
        Assert.True(budget.TryReserve(secondDocument));
        Assert.False(budget.TryReserve(thirdDocument));

        budget.Reset();
        Assert.True(budget.TryReserve(thirdDocument));

        var baseUriBudget = new MarkdownImageResolutionAuditBudget(
            maximumDocuments: 2,
            maximumDiagnosticsPerDocument: 1);
        MarkdownImageResolutionAuditDiagnostic baseUriDocumentA = CreateBaseUriDiagnostic(
            "https://user:secret@example.test/docs/a/README.md?token=one");
        MarkdownImageResolutionAuditDiagnostic baseUriDocumentB = CreateBaseUriDiagnostic(
            "https://other:secret@example.test/docs/b/README.md?token=two");
        Assert.True(baseUriBudget.TryReserve(baseUriDocumentA));
        Assert.False(baseUriBudget.TryReserve(baseUriDocumentA));
        Assert.True(baseUriBudget.TryReserve(baseUriDocumentB));
        Assert.DoesNotContain("secret", baseUriDocumentA.DocumentPathSha256, StringComparison.Ordinal);
        Assert.DoesNotContain("token=one", baseUriDocumentA.DocumentPathSha256, StringComparison.Ordinal);

        static MarkdownImageResolutionAuditDiagnostic CreateDiagnostic(string path)
        {
            var source = new MarkdownDocumentSource(
                $"repository-file:octo/repo:main:{path}",
                "octo",
                "repo",
                "main",
                path);
            var context = new MarkdownImageResolveContext(
                new Uri($"https://github.com/octo/repo/blob/main/{path}"),
                path,
                AllowThirdPartyRemoteImages: false,
                DocumentSource: source);
            return MarkdownImageResolutionAuditDiagnostic.Create(
                "missing.png",
                context,
                MarkdownImageResolution.Unavailable);
        }

        static MarkdownImageResolutionAuditDiagnostic CreateBaseUriDiagnostic(string baseUri)
        {
            var context = new MarkdownImageResolveContext(new Uri(baseUri));
            return MarkdownImageResolutionAuditDiagnostic.Create(
                "missing.png",
                context,
                MarkdownImageResolution.Unavailable);
        }
    }

    [Fact]
    public void ImageResolutionAuditDiagnostic_HashesDocumentAndRequestPathsWithoutUrlCredentials()
    {
        var documentSource = new MarkdownDocumentSource(
            "repository-file:sherlock-project/sherlock:commit:docs/README.md",
            "sherlock-project",
            "sherlock",
            "e40a45ec2a074b90703b3b4b842c8a3adbd6ada3",
            "docs/README.md");
        var context = new MarkdownImageResolveContext(
            new Uri("https://github.com/sherlock-project/sherlock/blob/e40a45ec2a074b90703b3b4b842c8a3adbd6ada3/docs/README.md"),
            "docs/README.md",
            AllowThirdPartyRemoteImages: false,
            DocumentSource: documentSource);

        MarkdownImageResolutionAuditDiagnostic diagnostic = MarkdownImageResolutionAuditDiagnostic.Create(
            "images/sherlock-logo.png",
            context,
            MarkdownImageResolution.Unavailable);

        Assert.Equal("fetch-or-resolver-miss", diagnostic.FailureClass);
        Assert.Equal("repository", diagnostic.DocumentContextKind);
        Assert.Equal("relative", diagnostic.SourceKind);
        Assert.Equal("repository-relative", diagnostic.RouteClass);
        Assert.Equal(64, diagnostic.DocumentPathSha256?.Length);
        Assert.Equal(64, diagnostic.DocumentRefIdentitySha256?.Length);
        Assert.Equal("raw.githubusercontent.com", diagnostic.AttemptedUriHost);
        Assert.Equal(64, diagnostic.AttemptedUriPathSha256?.Length);
        Assert.DoesNotContain("sherlock-project", diagnostic.DocumentPathSha256, StringComparison.Ordinal);
        Assert.DoesNotContain("sherlock-logo.png", diagnostic.AttemptedUriPathSha256, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", diagnostic.AttemptedUriHost, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageResolutionAuditDiagnostic_ClassifiesMissingContextAndPolicyWithoutLoggingQuery()
    {
        MarkdownImageResolutionAuditDiagnostic missingContext = MarkdownImageResolutionAuditDiagnostic.Create(
            "./assets/screenshot-1.png",
            null,
            MarkdownImageResolution.Unavailable);

        Assert.Equal("missing-document-context", missingContext.FailureClass);
        Assert.Equal("missing", missingContext.DocumentContextKind);
        Assert.Equal("relative-missing-document-context", missingContext.RouteClass);
        Assert.Null(missingContext.DocumentPathSha256);
        Assert.Null(missingContext.DocumentRefIdentitySha256);
        Assert.Null(missingContext.AttemptedUriHost);
        Assert.Null(missingContext.AttemptedUriPathSha256);

        const string secretQuery = "token=not-for-evidence";
        MarkdownImageResolutionAuditDiagnostic policyBlocked = MarkdownImageResolutionAuditDiagnostic.Create(
            $"https://images.example.test/private.png?{secretQuery}",
            null,
            MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.RemoteContentBlocked));

        Assert.Equal("policy", policyBlocked.FailureClass);
        Assert.Equal("absolute-uri", policyBlocked.RouteClass);
        Assert.Equal("images.example.test", policyBlocked.AttemptedUriHost);
        Assert.Equal(64, policyBlocked.AttemptedUriPathSha256?.Length);
        Assert.DoesNotContain(secretQuery, policyBlocked.AttemptedUriPathSha256, StringComparison.Ordinal);
        Assert.DoesNotContain("private.png", policyBlocked.AttemptedUriPathSha256, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", policyBlocked.AttemptedUriHost, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightRejectionRecordsOnlyPolicyAndContentIdentity()
    {
        const string variable = "JITHUB_MARKDOWN_SVG_PREFLIGHT_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-preflight-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            using var listener = new MarkdownRendererAuditListener();

            TestPreflightEventSource.Log.Rejected("missing-root", 469, new string('A', 64));

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("missing-root", document.RootElement.GetProperty("Reason").GetString());
            Assert.Equal(469, document.RootElement.GetProperty("SourceByteLength").GetInt32());
            Assert.Equal(new string('A', 64), document.RootElement.GetProperty("SourceSha256").GetString());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ShutdownStageSignalIsAuditOnlyAndKeepsOnlyTheLatestStage()
    {
        const string variable = "JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-markdown-shutdown-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("not-an-audit");
            Assert.False(File.Exists(path));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("markdown-shutdown-started");
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("performance-session-disposal-started");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                "performance-session-disposal-started",
                document.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(
                Environment.ProcessId,
                document.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WorkerTimeoutIsRecordedWithoutSourceData()
    {
        const string variable = "JITHUB_MARKDOWN_SVG_WORKER_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-worker-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: true,
                targetHost: null);
            using var listener = new MarkdownRendererAuditListener();

            TestWorkerEventSource.Log.Timeout(
                stage: 2,
                deadlineMilliseconds: 3_000,
                workerProcessCpuMilliseconds: 1_250,
                transportPhase: 2,
                requestWriteMilliseconds: 4,
                workerExited: 0,
                openProgressPhase: 4,
                workerWorkingSetKiB: 120_832,
                workerPrivateCommitKiB: 94_208,
                workerPageFaults: 6_400);
            for (int progressPhase = 8; progressPhase <= 12; progressPhase++)
            {
                TestWorkerEventSource.Log.Timeout(
                    stage: 2,
                    deadlineMilliseconds: 3_000,
                    workerProcessCpuMilliseconds: 1_250,
                    transportPhase: 2,
                    requestWriteMilliseconds: 4,
                    workerExited: 0,
                    openProgressPhase: progressPhase,
                    workerWorkingSetKiB: 120_832,
                    workerPrivateCommitKiB: 94_208,
                    workerPageFaults: 6_400);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(6, lines.Length);
            string line = lines[0];
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("open", document.RootElement.GetProperty("Phase").GetString());
            Assert.Equal(3_000, document.RootElement.GetProperty("DeadlineMilliseconds").GetInt32());
            Assert.Equal(1_250, document.RootElement.GetProperty("WorkerProcessCpuMilliseconds").GetInt32());
            Assert.Equal("response-read", document.RootElement.GetProperty("TransportPhase").GetString());
            Assert.Equal(4, document.RootElement.GetProperty("RequestWriteMilliseconds").GetInt32());
            Assert.False(document.RootElement.GetProperty("WorkerExited").GetBoolean());
            Assert.Equal("security-inspected", document.RootElement.GetProperty("OpenProgressPhase").GetString());
            Assert.Equal(120_832, document.RootElement.GetProperty("WorkerWorkingSetKiB").GetInt32());
            Assert.Equal(94_208, document.RootElement.GetProperty("WorkerPrivateCommitKiB").GetInt32());
            Assert.Equal(6_400, document.RootElement.GetProperty("WorkerPageFaults").GetInt32());
            Assert.Equal(2_987, document.RootElement.GetProperty("ElapsedWallMilliseconds").GetInt32());
            Assert.Equal(new string('a', 64), document.RootElement.GetProperty("WorkerInputSha256").GetString());
            Assert.Equal(new string('b', 64), document.RootElement.GetProperty("WorkerExecutableSha256").GetString());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));

            string[] expectedProgressPhases =
            [
                "svg-options-constructed",
                "svg-resolver-configured",
                "svg-theme-ready",
                "usvg-input-ready",
                "usvg-tree-built",
            ];
            for (int index = 0; index < expectedProgressPhases.Length; index++)
            {
                using JsonDocument progressDocument = JsonDocument.Parse(lines[index + 1]);
                Assert.Equal(expectedProgressPhases[index], progressDocument.RootElement.GetProperty("OpenProgressPhase").GetString());
            }
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void FontCatalogDeadlineRecordsOnlyKnownPhase()
    {
        const string variable = "JITHUB_MARKDOWN_SVG_WORKER_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-font-phase-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            using var listener = new MarkdownRendererAuditListener();

            TestWorkerEventSource.Log.FontCatalogDeadlinePhase("user-roaming-fonts");
            TestWorkerEventSource.Log.FontCatalogDeadlinePhase("unexpected source payload");

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using JsonDocument known = JsonDocument.Parse(lines[0]);
            Assert.Equal("user-roaming-fonts", known.RootElement.GetProperty("InitializationPhase").GetString());
            Assert.Equal(Environment.ProcessId, known.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.False(known.RootElement.TryGetProperty("Source", out _));
            using JsonDocument unknown = JsonDocument.Parse(lines[1]);
            Assert.Equal("unknown", unknown.RootElement.GetProperty("InitializationPhase").GetString());
            Assert.DoesNotContain("unexpected source payload", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RasterPreparationStagesRecordOnlyNumericAuditEvidence()
    {
        const string variable = "JITHUB_MARKDOWN_RASTER_PREPARATION_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-raster-preparation-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            using var listener = new MarkdownRendererAuditListener();

            TestRasterPreparationEventSource.Log.Started(
                17, 8_948, ((long)313 << 32) | 38u);
            TestRasterPreparationEventSource.Log.Stage(17, 7, 10_000);

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using JsonDocument started = JsonDocument.Parse(lines[0]);
            Assert.Equal(17, started.RootElement.GetProperty("PreparationId").GetInt64());
            Assert.Equal(0, started.RootElement.GetProperty("Stage").GetInt32());
            Assert.Equal(8_948, started.RootElement.GetProperty("SourceBytes").GetInt64());
            Assert.Equal(313, started.RootElement.GetProperty("SourceWidth").GetInt32());
            Assert.Equal(38, started.RootElement.GetProperty("SourceHeight").GetInt32());
            Assert.False(started.RootElement.TryGetProperty("Source", out _));
            Assert.False(started.RootElement.TryGetProperty("Url", out _));
            Assert.False(started.RootElement.TryGetProperty("Bytes", out _));

            using JsonDocument stage = JsonDocument.Parse(lines[1]);
            Assert.Equal(7, stage.RootElement.GetProperty("Stage").GetInt32());
            Assert.True(stage.RootElement.GetProperty("ElapsedMilliseconds").GetDouble() > 0);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SvgStageTracePersistsOnlyNumericMetadataWhenExplicitlyEnabled()
    {
        const string pathVariable = "JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH";
        const string fixtureVariable = "JITHUB_MARKDOWN_LIFECYCLE_FIXTURE";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-stage-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(pathVariable);
        string? previousFixture = Environment.GetEnvironmentVariable(fixtureVariable);
        try
        {
            Environment.SetEnvironmentVariable(fixtureVariable, null);
            Environment.SetEnvironmentVariable(pathVariable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.RecordSvgWorkStage(
                workId: 14,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                packedMetrics: PackSvgStageMetrics(stageId: 6, sourceBytes: 777, targetPixels: 12_345));
            Assert.False(File.Exists(path));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordSvgWorkStage(
                workId: 14,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                packedMetrics: PackSvgStageMetrics(stageId: 6, sourceBytes: 777, targetPixels: 12_345));
            MarkdownLifecycleAutomationBridge.RecordSvgWorkStage(
                workId: 14,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                packedMetrics: PackSvgStageMetrics(stageId: 15, sourceBytes: 777, targetPixels: 12_345));
            MarkdownLifecycleAutomationBridge.RecordSvgWorkStage(
                workId: 14,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                packedMetrics: PackSvgStageMetrics(stageId: 16, sourceBytes: 777, targetPixels: 12_345));

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            using JsonDocument document = JsonDocument.Parse(lines[0]);
            JsonElement signal = document.RootElement;
            Assert.Equal(
                new[]
                {
                    "ProcessId", "EventId", "WorkId", "StageId", "SourceBytes", "TargetPixels",
                    "ElapsedStopwatchTicks", "ElapsedMilliseconds", "Timestamp",
                },
                signal.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(Environment.ProcessId, signal.GetProperty("ProcessId").GetInt32());
            Assert.Equal(12, signal.GetProperty("EventId").GetInt32());
            Assert.Equal(14, signal.GetProperty("WorkId").GetInt64());
            Assert.Equal(6, signal.GetProperty("StageId").GetInt32());
            Assert.Equal(777, signal.GetProperty("SourceBytes").GetInt64());
            Assert.Equal(12_345, signal.GetProperty("TargetPixels").GetInt64());
            Assert.Equal(Stopwatch.Frequency / 4, signal.GetProperty("ElapsedStopwatchTicks").GetInt64());
            Assert.InRange(signal.GetProperty("ElapsedMilliseconds").GetDouble(), 249, 251);
            Assert.Equal(JsonValueKind.String, signal.GetProperty("Timestamp").ValueKind);
            Assert.False(signal.TryGetProperty("Source", out _));
            Assert.False(signal.TryGetProperty("Url", out _));
            Assert.False(signal.TryGetProperty("ContentSha256", out _));
            Assert.False(signal.TryGetProperty("Formula", out _));

            using JsonDocument extendedStageDocument = JsonDocument.Parse(lines[1]);
            JsonElement extendedStage = extendedStageDocument.RootElement;
            Assert.Equal(12, extendedStage.GetProperty("EventId").GetInt32());
            Assert.Equal(15, extendedStage.GetProperty("StageId").GetInt32());
            Assert.Equal(777, extendedStage.GetProperty("SourceBytes").GetInt64());
            Assert.Equal(12_345, extendedStage.GetProperty("TargetPixels").GetInt64());

            using JsonDocument dispatchStageDocument = JsonDocument.Parse(lines[2]);
            JsonElement dispatchStage = dispatchStageDocument.RootElement;
            Assert.Equal(16, dispatchStage.GetProperty("StageId").GetInt32());
            Assert.Equal(777, dispatchStage.GetProperty("SourceBytes").GetInt64());
            Assert.Equal(12_345, dispatchStage.GetProperty("TargetPixels").GetInt64());
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(pathVariable, previousPath);
            Environment.SetEnvironmentVariable(fixtureVariable, previousFixture);
            if (File.Exists(path)) File.Delete(path);
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [Fact]
    public void SvgPipeStagesAreBoundedNumericAuditEvidence()
    {
        const string variable = "JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-pipe-stage-{Guid.NewGuid():N}.ndjson");
        long workId = CreateUniqueWorkId();
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            foreach (int stageId in new[] { 117, 118, 119, 120 })
            {
                MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                    workId,
                    stageId,
                    workerInstanceId: 1,
                    requestId: 2,
                    documentId: 3,
                    elapsedStopwatchTicks: Stopwatch.Frequency / 50,
                    workerCpuTicks: -1,
                    sourceBytes: 0,
                    targetPixels: 0);
            }
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 117,
                workerInstanceId: 1,
                requestId: 2,
                documentId: 3,
                elapsedStopwatchTicks: Stopwatch.Frequency / 50,
                workerCpuTicks: -1,
                sourceBytes: 1,
                targetPixels: 0);
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 121,
                workerInstanceId: 1,
                requestId: 2,
                documentId: 3,
                elapsedStopwatchTicks: Stopwatch.Frequency / 50,
                workerCpuTicks: -1,
                sourceBytes: 0,
                targetPixels: 0);

            string[] lines = ReadCorrelatedSvgWorkerStageRows(path, workId, 1, 2, 3);
            Assert.Equal(4, lines.Length);
            for (int index = 0; index < lines.Length; index++)
            {
                using JsonDocument document = JsonDocument.Parse(lines[index]);
                JsonElement signal = document.RootElement;
                Assert.Equal(117 + index, signal.GetProperty("StageId").GetInt32());
                Assert.Equal(Environment.ProcessId, signal.GetProperty("ProcessId").GetInt32());
                Assert.Equal(13, signal.GetProperty("EventId").GetInt32());
                Assert.Equal(workId, signal.GetProperty("WorkId").GetInt64());
                Assert.Equal(1, signal.GetProperty("WorkerInstanceId").GetInt64());
                Assert.Equal(2, signal.GetProperty("RequestId").GetInt64());
                Assert.Equal(3, signal.GetProperty("DocumentId").GetInt64());
                Assert.Equal(Stopwatch.Frequency / 50, signal.GetProperty("ElapsedStopwatchTicks").GetInt64());
                Assert.Equal(-1, signal.GetProperty("WorkerCpuTicks").GetInt64());
                Assert.Equal(0, signal.GetProperty("SourceBytes").GetInt64());
                Assert.Equal(0, signal.GetProperty("TargetPixels").GetInt64());
                Assert.InRange(signal.GetProperty("ElapsedMilliseconds").GetDouble(), 19, 21);
                Assert.Equal(-1d, signal.GetProperty("WorkerCpuMilliseconds").GetDouble());
                Assert.Equal(JsonValueKind.String, signal.GetProperty("Timestamp").ValueKind);
                Assert.False(signal.TryGetProperty("Source", out _));
                Assert.False(signal.TryGetProperty("Url", out _));
                Assert.False(signal.TryGetProperty("Hash", out _));
            }
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }
#endif

    [Fact]
    public void ParsePipelineRecorderIsAuditOnlyAndScopedToLatestOwner()
    {
        MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
        try
        {
            const int ownerIdentity = 41;
            const int otherOwnerIdentity = 42;

            MarkdownLifecycleAutomationBridge.RecordParsePipelineStage(ownerIdentity, 0, 0);
            MarkdownLifecycleAutomationBridge.RecordParsePipelineStage(
                ownerIdentity, 1, Stopwatch.Frequency / 4);
            MarkdownLifecycleAutomationBridge.RecordParsePipelineStage(
                ownerIdentity, 5, Stopwatch.Frequency / 2);

            MarkdownLifecycleAutomationBridge.MarkdownAuditParsePreparationTiming timing =
                MarkdownLifecycleAutomationBridge.GetParsePreparationTiming(ownerIdentity);
            Assert.Equal(250, timing.EngineParseAndCacheMilliseconds, precision: 6);
            Assert.Equal(500, timing.SessionTotalMilliseconds, precision: 6);
            Assert.Equal(default, MarkdownLifecycleAutomationBridge.GetParsePreparationTiming(otherOwnerIdentity));

            MarkdownLifecycleAutomationBridge.RecordParsePipelineStage(otherOwnerIdentity, 0, 0);
            Assert.Equal(default, MarkdownLifecycleAutomationBridge.GetParsePreparationTiming(ownerIdentity));
            Assert.Equal(0, MarkdownLifecycleAutomationBridge.GetParsePreparationTiming(otherOwnerIdentity).SessionTotalMilliseconds);

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.RecordParsePipelineStage(ownerIdentity, 0, 0);
            Assert.Equal(default, MarkdownLifecycleAutomationBridge.GetParsePreparationTiming(ownerIdentity));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
        }
    }

    [EventSource(Name = "MarkdownRenderer.Svg.Resvg.Worker")]
    private sealed class TestWorkerEventSource : EventSource
    {
        public static readonly TestWorkerEventSource Log = new();

        [Event(1, Level = EventLevel.Warning)]
        public void Timeout(
            int stage,
            int deadlineMilliseconds,
            int workerProcessCpuMilliseconds,
            int transportPhase,
            int requestWriteMilliseconds,
            int workerExited,
            int openProgressPhase,
            int workerWorkingSetKiB,
            int workerPrivateCommitKiB,
            int workerPageFaults,
            int elapsedWallMilliseconds = 2_987,
            string workerInputSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            string workerExecutableSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") =>
            WriteEvent(1, [stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                transportPhase, requestWriteMilliseconds, workerExited,
                openProgressPhase, workerWorkingSetKiB, workerPrivateCommitKiB,
                workerPageFaults, elapsedWallMilliseconds, workerInputSha256,
                workerExecutableSha256]);

        [Event(2, Level = EventLevel.Warning)]
        public void FontCatalogDeadlinePhase(string initializationPhase) =>
            WriteEvent(2, initializationPhase);
    }

    [EventSource(Name = "MarkdownRenderer.Svg.Resvg.Preflight")]
    private sealed class TestPreflightEventSource : EventSource
    {
        public static readonly TestPreflightEventSource Log = new();

        [Event(1, Level = EventLevel.Warning)]
        public void Rejected(string reason, int sourceByteLength, string sourceSha256) =>
            WriteEvent(1, reason, sourceByteLength, sourceSha256);
    }

    [EventSource(Name = "MarkdownRenderer-RasterPreparation")]
    private sealed class TestRasterPreparationEventSource : EventSource
    {
        public static readonly TestRasterPreparationEventSource Log = new();

        [Event(1, Level = EventLevel.Informational)]
        public void Started(long id, long sourceBytes, long packedDimensions) =>
            WriteEvent(1, id, sourceBytes, packedDimensions);

        [Event(2, Level = EventLevel.Informational)]
        public void Stage(long id, long stage, long elapsedStopwatchTicks) =>
            WriteEvent(2, id, stage, elapsedStopwatchTicks);
    }

    private static long PackSvgStageMetrics(int stageId, long sourceBytes, long targetPixels) =>
        (((long)stageId & 0x1F) << 49) |
        ((Math.Min(sourceBytes, 0x00FF_FFFFL) & 0x00FF_FFFFL) << 25) |
        (Math.Min(targetPixels, 0x01FF_FFFFL) & 0x01FF_FFFFL);

    private static long CreateUniqueWorkId() => Random.Shared.NextInt64(1, long.MaxValue);

    private static string[] ReadCorrelatedSvgWorkerStageRows(
        string path,
        long workId,
        long workerInstanceId,
        long requestId,
        long documentId) =>
        File.ReadAllLines(path)
            .Where(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement signal = document.RootElement;
                return signal.GetProperty("ProcessId").GetInt32() == Environment.ProcessId &&
                    signal.GetProperty("EventId").GetInt32() == 13 &&
                    signal.GetProperty("WorkId").GetInt64() == workId &&
                    signal.GetProperty("WorkerInstanceId").GetInt64() == workerInstanceId &&
                    signal.GetProperty("RequestId").GetInt64() == requestId &&
                    signal.GetProperty("DocumentId").GetInt64() == documentId;
            })
            .ToArray();

}
