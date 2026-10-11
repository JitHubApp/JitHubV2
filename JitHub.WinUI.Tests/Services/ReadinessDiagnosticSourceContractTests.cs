using System;
using System.IO;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class ReadinessDiagnosticSourceContractTests
{
    [Fact]
    public void CaptureRequestDefaultsToOrdinaryCaptureAndReadinessOnlyIsProductionAuditGated()
    {
        const string requestVariable = "JITHUB_MARKDOWN_CAPTURE_REQUEST_PATH";
        string path = Path.Combine(Path.GetTempPath(), $"markdown-readiness-request-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(requestVariable);
        try
        {
            Environment.SetEnvironmentVariable(requestVariable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: true,
                targetHost: "MarkdownHost_RepositoryReadme",
                productionAuditEnabled: false);

            File.WriteAllText(path,
                "{\"RequestId\":\"legacy\",\"OutputPath\":null,\"Save\":false}");
            Assert.True(MarkdownLifecycleAutomationBridge.TryReadCaptureRequest(
                "MarkdownHost_RepositoryReadme_Instance",
                out MarkdownLifecycleAutomationBridge.MarkdownAuditCaptureRequest? legacy));
            Assert.NotNull(legacy);
            Assert.False(legacy.ReadinessDiagnosticOnly);
            Assert.False(MarkdownLifecycleAutomationBridge.TryGetAuditReadinessDiagnosticOnly(legacy));

            File.WriteAllText(path,
                "{\"RequestId\":\"diagnostic\",\"OutputPath\":null,\"Save\":false," +
                "\"ReadinessDiagnosticOnly\":true}");
            Assert.True(MarkdownLifecycleAutomationBridge.TryReadCaptureRequest(
                "MarkdownHost_RepositoryReadme_Instance",
                out MarkdownLifecycleAutomationBridge.MarkdownAuditCaptureRequest? diagnostic));
            Assert.NotNull(diagnostic);
            Assert.Throws<InvalidOperationException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditReadinessDiagnosticOnly(diagnostic));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: "MarkdownHost_RepositoryReadme",
                productionAuditEnabled: true);
            Assert.True(MarkdownLifecycleAutomationBridge.TryGetAuditReadinessDiagnosticOnly(diagnostic));
            Assert.Throws<InvalidDataException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditReadinessDiagnosticOnly(
                    diagnostic with { Save = true }));
            Assert.Throws<InvalidDataException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditReadinessDiagnosticOnly(
                    diagnostic with { OutputPath = "capture.png" }));
            Assert.Throws<InvalidDataException>(() =>
                MarkdownLifecycleAutomationBridge.TryGetAuditReadinessDiagnosticOnly(
                    diagnostic with { ScrollViewportFraction = 0.5 }));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(requestVariable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadinessOnlyResponseCarriesDiagnosticWithoutCaptureDimensions()
    {
        const string responseVariable = "JITHUB_MARKDOWN_CAPTURE_RESPONSE_PATH";
        string path = Path.Combine(Path.GetTempPath(), $"markdown-readiness-response-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(responseVariable);
        try
        {
            Environment.SetEnvironmentVariable(responseVariable, path);
            MarkdownLifecycleAutomationBridge.RecordCaptureResponse(
                "diagnostic",
                succeeded: true,
                width: 0,
                height: 0,
                documentTop: 0,
                error: null,
                performance: null,
                readinessDiagnostic: "renderer=1234;peerItemStatus=Loading Image");

            string response = File.ReadAllText(path);
            Assert.Contains("\"succeeded\":true", response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"width\":0", response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"height\":0", response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"readinessDiagnostic\":\"renderer=1234;peerItemStatus=Loading Image\"", response, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(responseVariable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void RendererSnapshotUsesOneNonblockingInteractionLeaseAndBoundsImageDetails()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.AuditReadiness.cs"));

        Assert.Contains("CaptureAuditReadinessDiagnostic()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetItemStatus()", source, StringComparison.Ordinal);
        Assert.Contains("peerStatus = \"<not-queried>\"", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.GetHashCode(this)", source, StringComparison.Ordinal);
        Assert.Contains("_appliedLazyLayoutRevision", source, StringComparison.Ordinal);
        Assert.Contains("HasPendingLazyLayoutWork", source, StringComparison.Ordinal);
        Assert.Contains("TryBeginInteraction()", source, StringComparison.Ordinal);
        Assert.Contains("MaximumAuditReadinessImageDetails = 8", source, StringComparison.Ordinal);
        Assert.Contains("MaximumAuditReadinessImageScanCandidates", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBeginPaint()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureAuditViewportAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueLazyLayout", source, StringComparison.Ordinal);
        Assert.Contains("snapshotCurrentAfterRead", source, StringComparison.Ordinal);
        Assert.Contains("sceneResourcesMeasured", source, StringComparison.Ordinal);
        Assert.Contains("\"rawViewport\"", source, StringComparison.Ordinal);
        Assert.Contains("\"hasVectorCapacityAttempt\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hasCompletedVectorBand\"", source, StringComparison.Ordinal);
        // Finite fallback geometry is distinct from an actual measured
        // ScrollViewer/ancestor viewport, recorded in the separate field.
        Assert.Contains("\"viewportMeasured\", ownedViewportMeasured || externalViewportMeasured", source, StringComparison.Ordinal);
        Assert.Contains("viewportFinite ? \"finite-viewport\" : \"unknown\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("no-measured-viewport", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerRequestsReadinessOnlyAfterTimeoutAndAcceptsNoPaintResponse()
    {
        string root = FindRepositoryRoot();
        string probe = File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "ReadmeAuditProbe.cs"));
        string viewer = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"));
        string bridge = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Services", "Markdown", "MarkdownLifecycleAutomationBridge.cs"));

        int waitStart = probe.IndexOf("private static VisibleImageWaitResult WaitForVisibleImages(", StringComparison.Ordinal);
        int waitEnd = probe.IndexOf("private static ReadmeAuditVisibleImageTimeoutSnapshot CaptureVisibleImageTimeoutSnapshot(", waitStart, StringComparison.Ordinal);
        Assert.True(waitStart >= 0 && waitEnd > waitStart);
        string wait = probe[waitStart..waitEnd];
        Assert.True(wait.IndexOf("while (stopwatch.Elapsed < timeout)", StringComparison.Ordinal) <
            wait.IndexOf("CaptureRendererReadinessDiagnostic(", StringComparison.Ordinal));
        int diagnosticCall = wait.IndexOf("CaptureRendererReadinessDiagnostic(", StringComparison.Ordinal);
        int diagnosticStop = wait.IndexOf("diagnosticProbe.Stop();", diagnosticCall, StringComparison.Ordinal);
        Assert.True(diagnosticCall >= 0 && diagnosticStop > diagnosticCall,
            "The deadline-only readiness request must be included in harness diagnostic overhead.");
        Assert.Contains("[renderer-readiness unavailable=", probe, StringComparison.Ordinal);
        Assert.Contains("captureRequestPath: captureRequest", probe, StringComparison.Ordinal);
        Assert.Contains("captureResponsePath: captureResponse", probe, StringComparison.Ordinal);

        int requestStart = probe.IndexOf("private static RendererCaptureResponse RequestRendererCapture(", StringComparison.Ordinal);
        int requestEnd = probe.IndexOf("private static RendererCaptureResponse RequestRendererScroll(", requestStart, StringComparison.Ordinal);
        Assert.True(requestStart >= 0 && requestEnd > requestStart);
        string request = probe[requestStart..requestEnd];
        Assert.Contains("readinessDiagnosticOnly ? 1 : 5", request, StringComparison.Ordinal);
        Assert.Contains("readinessDiagnosticOnly: true", probe, StringComparison.Ordinal);
        Assert.Contains("response.ReadinessDiagnostic", request, StringComparison.Ordinal);
        Assert.Contains("outputPath is not null || save || scrollViewportFraction is not null", request, StringComparison.Ordinal);

        int diagnosticBranch = viewer.IndexOf("TryGetAuditReadinessDiagnosticOnly(request)", StringComparison.Ordinal);
        int scrollBranch = viewer.IndexOf("TryGetAuditScrollViewportFraction(", diagnosticBranch, StringComparison.Ordinal);
        int paintCall = viewer.IndexOf("renderer.CaptureAuditViewportAsync()", diagnosticBranch, StringComparison.Ordinal);
        Assert.True(diagnosticBranch >= 0 && scrollBranch > diagnosticBranch && paintCall > diagnosticBranch);
        Assert.Contains("width: 0", viewer[diagnosticBranch..scrollBranch], StringComparison.Ordinal);
        Assert.Contains("readinessDiagnostic: readinessDiagnostic", viewer[diagnosticBranch..scrollBranch], StringComparison.Ordinal);
        Assert.Contains("TryGetAuditReadinessDiagnosticOnly", bridge, StringComparison.Ordinal);
        Assert.Contains("_productionAuditEnabled", bridge[bridge.IndexOf("TryGetAuditReadinessDiagnosticOnly", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
