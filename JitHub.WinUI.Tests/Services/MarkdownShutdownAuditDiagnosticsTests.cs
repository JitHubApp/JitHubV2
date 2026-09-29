using System.Text.Json;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class MarkdownShutdownAuditDiagnosticsTests
{
    [Fact]
    public void CaptureIpcScrollRequestIsBackwardCompatibleAndProductionAuditOnly()
    {
        const string variable = "JITHUB_MARKDOWN_CAPTURE_REQUEST_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-markdown-capture-request-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: true,
                targetHost: "MarkdownHost_RepositoryReadme",
                productionAuditEnabled: false);

            File.WriteAllText(
                path,
                "{\"RequestId\":\"legacy-capture\",\"OutputPath\":null,\"Save\":false}");
            Assert.True(MarkdownLifecycleAutomationBridge.TryReadCaptureRequest(
                "MarkdownHost_RepositoryReadme_Instance",
                out MarkdownLifecycleAutomationBridge.MarkdownAuditCaptureRequest? legacyRequest));
            Assert.NotNull(legacyRequest);
            Assert.Null(legacyRequest.ScrollViewportFraction);
            Assert.False(MarkdownLifecycleAutomationBridge.TryGetAuditScrollViewportFraction(
                legacyRequest,
                out _));

            File.WriteAllText(
                path,
                "{\"RequestId\":\"fixture-scroll\",\"OutputPath\":null,\"Save\":false," +
                "\"ScrollViewportFraction\":0.9}");
            Assert.True(MarkdownLifecycleAutomationBridge.TryReadCaptureRequest(
                "MarkdownHost_RepositoryReadme_Instance",
                out MarkdownLifecycleAutomationBridge.MarkdownAuditCaptureRequest? fixtureRequest));
            Assert.Throws<InvalidOperationException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditScrollViewportFraction(
                    fixtureRequest!,
                    out _));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: "MarkdownHost_RepositoryReadme",
                productionAuditEnabled: true);
            Assert.True(MarkdownLifecycleAutomationBridge.TryGetAuditScrollViewportFraction(
                fixtureRequest!,
                out double viewportFraction));
            Assert.Equal(0.9, viewportFraction);

            var reverseLimitRequest = new MarkdownLifecycleAutomationBridge.MarkdownAuditCaptureRequest(
                "reverse-limit",
                OutputPath: null,
                Save: false,
                ScrollViewportFraction: -MarkdownLifecycleAutomationBridge.MaximumAuditScrollViewportFraction);
            Assert.True(MarkdownLifecycleAutomationBridge.TryGetAuditScrollViewportFraction(
                reverseLimitRequest,
                out double reverseLimit));
            Assert.Equal(-0.95, reverseLimit);
            Assert.Throws<InvalidDataException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditScrollViewportFraction(
                    reverseLimitRequest with { ScrollViewportFraction = double.NaN },
                    out _));

            File.WriteAllText(
                path,
                "{\"RequestId\":\"oversized-scroll\",\"OutputPath\":null,\"Save\":false," +
                "\"ScrollViewportFraction\":1.0}");
            Assert.True(MarkdownLifecycleAutomationBridge.TryReadCaptureRequest(
                "MarkdownHost_RepositoryReadme_Instance",
                out MarkdownLifecycleAutomationBridge.MarkdownAuditCaptureRequest? oversizedRequest));
            Assert.Throws<InvalidDataException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditScrollViewportFraction(
                    oversizedRequest!,
                    out _));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ShutdownAuditSnapshotSurvivesLaterStageWritesWithoutSourceOrUrlData()
    {
        const string variable = "JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-markdown-shutdown-audit-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordMarkdownViewerLoaded();
            MarkdownLifecycleAutomationBridge.RecordMarkdownViewerUnloaded();
            MarkdownLifecycleAutomationBridge.RecordMarkdownShutdownAuditSnapshot(
                new MarkdownShutdownAuditPerformanceSnapshot(
                    PendingImageFetches: 2,
                    ActiveImageFetches: 3,
                    ScenePreparationsTotal: 11,
                    PendingSourceByteRequests: 4,
                    InFlightSourceBytes: 2_048),
                pageUnloadWaitTimedOut: true,
                shellContentUnloadWaitTimedOut: true,
                markdownRendererDisposalWaitTimedOut: true,
                markdownRenderersForceDisposed: 1);

            MarkdownLifecycleAutomationBridge.SignalShutdownStage("svg-renderer-disposal-started");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            string stage = document.RootElement.GetProperty("Stage").GetString()!;
            Assert.StartsWith("svg-renderer-disposal-started;markdown-shutdown-audit:", stage, StringComparison.Ordinal);
            Assert.Contains("pageUnloadWaitTimedOut=true", stage, StringComparison.Ordinal);
            Assert.Contains("shellContentUnloadWaitTimedOut=true;markdownRendererDisposalWaitTimedOut=true;markdownRenderersForceDisposed=1", stage, StringComparison.Ordinal);
            Assert.Contains("markdownViewsLoaded=1;markdownViewsUnloaded=1;markdownViewsStillLoaded=0", stage, StringComparison.Ordinal);
            Assert.Contains("pendingImageFetches=2;activeImageFetches=3;scenePreparationsTotal=11", stage, StringComparison.Ordinal);
            Assert.Contains("pendingSourceByteRequests=4;inFlightSourceBytes=2048", stage, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", stage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw-source-sentinel", stage, StringComparison.Ordinal);
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ShutdownAuditSnapshotIsNotCachedWithoutTheOptInEvidencePath()
    {
        const string variable = "JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-markdown-shutdown-stage-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordMarkdownShutdownAuditSnapshot(
                new MarkdownShutdownAuditPerformanceSnapshot(1, 2, 3, 4, 5),
                pageUnloadWaitTimedOut: true);

            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("markdown-shutdown-started");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                "markdown-shutdown-started",
                document.RootElement.GetProperty("Stage").GetString());
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
