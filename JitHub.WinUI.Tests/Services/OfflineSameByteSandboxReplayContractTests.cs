using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class OfflineSameByteSandboxReplayContractTests
{
    [Fact]
    public void SourceAdmissionTraceIsOptInForwardedToGuestAndRecorded()
    {
        string wrapper = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "eng",
            "Invoke-TopReadmeSandboxOfflineSameByteReplay.ps1"));
        Assert.Contains("[switch]$SourceAdmissionTrace", wrapper, StringComparison.Ordinal);
        Assert.Contains("`$env:JITHUB_MARKDOWN_SOURCE_ADMISSION_TRACE = $(if ($SourceAdmissionTrace) { \"'1'\" } else { '$null' })",
            wrapper, StringComparison.Ordinal);
        Assert.Contains("sourceAdmissionTraceEnabled = [bool]$SourceAdmissionTrace", wrapper, StringComparison.Ordinal);
        Assert.Contains("overallReleaseQualification = 'not-qualified'", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineSameByteReplayIsExplicitSandboxOnlyAndNeverClaimsReleaseQualification()
    {
        string root = FindRepositoryRoot();
        string wrapper = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "Invoke-TopReadmeSandboxOfflineSameByteReplay.ps1"));
        string program = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "Program.cs"));
        string probe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "OfflineSameByteReadmeAuditProbe.cs"));
        string nativeProbe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "ReadmeAuditProbe.cs"));
        string launchOptions = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "LaunchOptions.cs"));
        string app = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml.cs"));
        string auth = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Services", "AuthService.cs"));
        string repoTree = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "CodeViewer",
            "RepoTreeService.cs"));
        string repoPageViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));

        Assert.Contains("[switch]$Run", wrapper, StringComparison.Ordinal);
        Assert.Contains("if (-not $Run)", wrapper, StringComparison.Ordinal);
        Assert.Contains("target snapshot sandbox --json", wrapper, StringComparison.Ordinal);
        Assert.Contains("'target', 'exec', 'sandbox'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'target', 'push', 'sandbox'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'target', 'pull', 'sandbox'", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Attach", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'target', 'start'", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'target', 'create'", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'target', 'reset'", wrapper, StringComparison.Ordinal);
        Assert.Contains("overallReleaseQualification = 'not-qualified'", wrapper, StringComparison.Ordinal);
        Assert.Contains("liveGitHubArticleSemanticComparison = 'not-run'", wrapper, StringComparison.Ordinal);
        Assert.Contains("authenticatedLiveAuditEquivalent = $false", wrapper, StringComparison.Ordinal);

        Assert.Contains("OfflineSameByteProbeName", program, StringComparison.Ordinal);
        Assert.Contains("RunOfflineSameByte(options)", program, StringComparison.Ordinal);
        Assert.Contains("RunNativeAudit(", probe, StringComparison.Ordinal);
        Assert.Contains("accessToken: string.Empty", probe, StringComparison.Ordinal);
        Assert.Contains("offlineSameBytePublicAudit: true", probe, StringComparison.Ordinal);
        Assert.Contains("pinnedManifestSha256: manifestSha256", probe, StringComparison.Ordinal);
        Assert.Contains("RunSourceBoundEdgeReplay(", probe, StringComparison.Ordinal);
        Assert.Contains("capturedGitHubArticleSemantic: null", probe, StringComparison.Ordinal);
        Assert.Contains("Status = \"not-applicable\"", probe, StringComparison.Ordinal);
        Assert.Contains("Reason = \"github-source-view\"", probe, StringComparison.Ordinal);
        Assert.Contains("overallReleaseQualification = \"not-qualified\"", probe, StringComparison.Ordinal);
        Assert.Contains("liveGitHubArticleSemanticComparison = \"not-run\"", probe, StringComparison.Ordinal);
        Assert.Contains("liveTop500RankingFreshness = \"not-checked\"", probe, StringComparison.Ordinal);
        Assert.Contains("authenticatedLiveAuditEquivalent = false", probe, StringComparison.Ordinal);

        Assert.Contains("--readme-offline-same-byte-audit", launchOptions, StringComparison.Ordinal);
        Assert.Contains("ValidateOfflineSameByteReadmeAuditPin", launchOptions, StringComparison.Ordinal);
        Assert.Contains("pinnedManifestSha256", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("JITHUB_README_AUDIT_SAME_BYTE_README_ABSENT", nativeProbe, StringComparison.Ordinal);
        Assert.Contains("!Program.CurrentLaunchOptions.OfflineSameByteReadmeAudit", app, StringComparison.Ordinal);
        Assert.Contains("IsOfflineSameBytePublicAuditEnabled", auth, StringComparison.Ordinal);
        Assert.Contains("AuthenticatedUser = null", auth, StringComparison.Ordinal);
        Assert.Contains("LoadOfflinePinnedReadmeAsync", repoTree, StringComparison.Ordinal);
        Assert.Contains("LoadBlobAsync", repoTree, StringComparison.Ordinal);
        Assert.Contains("UseRawReadmeSourceForSameByteAudit = MarkdownLifecycleAutomationBridge.IsSameByteReplayEnabled;",
            repoPageViewModel,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "JitHub.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate JitHub.slnx from the test output directory.");
    }
}
