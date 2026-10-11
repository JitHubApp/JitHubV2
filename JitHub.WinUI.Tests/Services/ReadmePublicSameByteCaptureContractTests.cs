using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmePublicSameByteCaptureContractTests
{
    [Fact]
    public void PublicCaptureIsAnIsolatedBrowserOnlyProbeAndUsesTheCanonicalPinnedManifest()
    {
        string root = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "Program.cs"));
        string probe = File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "ReadmeAuditProbe.cs"));
        string capture = File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "ReadmeAuditCaptureOnlyProbe.cs"));
        string offline = File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "OfflineSameByteReadmeAuditProbe.cs"));

        // Dispatch and parsing share the probe's canonical name instead of
        // duplicating the command-line literal in the program entry point.
        Assert.Contains("internal const string PublicSameByteCaptureProbeName = \"readme-public-same-byte-capture\";", probe, StringComparison.Ordinal);
        Assert.Contains("string.Equals(options.Probe, ReadmeAuditProbe.PublicSameByteCaptureProbeName, StringComparison.OrdinalIgnoreCase)", program, StringComparison.Ordinal);
        Assert.Contains("ReadmeAuditProbe.RunPublicSameByteCapture(options);", program, StringComparison.Ordinal);
        int parseStart = program.IndexOf("if (string.Equals(probe, ReadmeAuditProbe.PublicSameByteCaptureProbeName", StringComparison.Ordinal);
        int parseEnd = program.IndexOf("appPath ??= GuessAppPath();", parseStart, StringComparison.Ordinal);
        Assert.True(parseStart >= 0 && parseEnd > parseStart);
        string captureParse = program[parseStart..parseEnd];
        Assert.Contains("AppPath = string.Empty", captureParse, StringComparison.Ordinal);
        Assert.Contains("AuditCaptureSameByteCorpus = true", captureParse, StringComparison.Ordinal);
        Assert.Contains("!auditCaptureSameByteCorpus || auditResume || auditReuseBrowserEvidence", captureParse, StringComparison.Ordinal);
        Assert.DoesNotContain("GuessAppPath()", captureParse, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureAppBinaryIsFresh", captureParse, StringComparison.Ordinal);

        Assert.Contains("ClearOfflineGitHubCredentialEnvironment();", capture, StringComparison.Ordinal);
        Assert.Contains("ReadOfflinePinnedManifest(manifestPath", capture, StringComparison.Ordinal);
        Assert.Contains("ReadOfflinePinnedManifest(canonicalManifestPath", capture, StringComparison.Ordinal);
        Assert.Contains("GetSameByteCorpusDirectory(", capture, StringComparison.Ordinal);
        Assert.Contains("RunBrowserOracle(options, browserScript, repository, caseDirectory)", capture, StringComparison.Ordinal);
        Assert.Contains("OverallReleaseQualification = \"not-qualified\"", capture, StringComparison.Ordinal);
        Assert.Contains("AuthenticatedLiveAuditEquivalent = false", capture, StringComparison.Ordinal);
        Assert.Contains("NativeJitHubRenderComparison = \"not-run\"", capture, StringComparison.Ordinal);

        Assert.Contains("options.AuditCaptureSameByteCorpus", offline, StringComparison.Ordinal);
        Assert.Contains("ClearOfflineGitHubCredentialEnvironment();", offline, StringComparison.Ordinal);
        Assert.Contains("authenticatedLiveAuditEquivalent = false", offline, StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeChildEnvironmentFiltersAllGitHubCredentialVariables()
    {
        string root = FindRepositoryRoot();
        string helper = File.ReadAllText(Path.Combine(root, "eng", "readme-audit", "browser-environment.mjs"));
        string oracle = File.ReadAllText(Path.Combine(root, "eng", "readme-audit", "browser-oracle.mjs"));

        Assert.Contains("createCredentialFreeBrowserEnvironment(process.env)", oracle, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN", helper, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN", helper, StringComparison.Ordinal);
        Assert.Contains("JITHUB_README_AUDIT_GITHUB_TOKEN", helper, StringComparison.Ordinal);
        Assert.Contains("GH_CONFIG_DIR", helper, StringComparison.Ordinal);
        Assert.Contains("delete browserEnvironment[name]", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("delete edgeEnvironment.JITHUB_README_AUDIT_GITHUB_TOKEN", oracle, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicCaptureUsesOnlyTheExistingSandboxAndKeepsQualificationExplicit()
    {
        string root = FindRepositoryRoot();
        string sandboxCapture = File.ReadAllText(Path.Combine(root, "eng", "Invoke-TopReadmeSandboxPublicSameByteCapture.ps1"));
        string offlineReplay = File.ReadAllText(Path.Combine(root, "eng", "Invoke-TopReadmeSandboxOfflineSameByteReplay.ps1"));

        Assert.Contains("target snapshot sandbox --json", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("'target', 'push', 'sandbox'", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("'target', 'exec', 'sandbox'", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("'target', 'pull', 'sandbox'", sandboxCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("target create", sandboxCapture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target connect", sandboxCapture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FlaUI", sandboxCapture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--app=", sandboxCapture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--probe=readme-public-same-byte-capture", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("--capture-same-byte-corpus", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("authenticatedLiveAuditEquivalent = $false", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("overallReleaseQualification = 'not-qualified'", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("nativeJitHubRenderComparison = 'not-run'", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("sameByteEdgeFullPageReplay = 'not-run'", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("No host browser or UI automation were performed", sandboxCapture, StringComparison.Ordinal);
        Assert.Contains("ClearOfflineGitHubCredentialEnvironment();", File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "ReadmeAuditCaptureOnlyProbe.cs")), StringComparison.Ordinal);

        foreach (string credentialName in new[]
        {
            "JITHUB_README_AUDIT_GITHUB_TOKEN", "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID",
            "JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY", "GITHUB_TOKEN", "GH_TOKEN",
            "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
            "ACTIONS_ID_TOKEN_REQUEST_URL", "GITHUB_APP_TOKEN", "GITHUB_ACCESS_TOKEN",
            "GITHUB_CLIENT_SECRET", "GITHUB_APP_PRIVATE_KEY", "GH_ACCESS_TOKEN", "GH_CONFIG_DIR",
        })
        {
            Assert.Contains(credentialName, sandboxCapture, StringComparison.Ordinal);
        }

        Assert.Contains("offline-same-byte-pinned-corpus-in-existing-windows-sandbox", offlineReplay, StringComparison.Ordinal);
        Assert.Contains("ReadmeAuditProbe.PublicSameByteCaptureProbeName", File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "Program.cs")), StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
