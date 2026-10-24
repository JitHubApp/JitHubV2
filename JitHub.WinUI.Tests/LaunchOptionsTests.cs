using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace JitHub.WinUI.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public void Parse_ReadsPackagedLaunchArgumentsAndQuotedCorpusPath()
    {
        LaunchOptions options = LaunchOptions.Parse(
            [],
            "--page=repo --theme=dark --palette=github --repo=sindresorhus/awesome " +
            "--markdown-lifecycle-fixture " +
            "--readme-production-audit " +
            "--markdown-lifecycle-host=MarkdownHost_RepositoryReadme " +
            "--markdown-corpus=\"C:\\readmes\\awesome README.md\"");

        Assert.Equal("repo", options.Page);
        Assert.Equal("dark", options.Theme);
        Assert.Equal("github", options.Palette);
        Assert.Equal("sindresorhus/awesome", options.RepositoryFullName);
        Assert.True(options.MarkdownLifecycleFixture);
        Assert.True(options.ReadmeProductionAudit);
        Assert.Equal("MarkdownHost_RepositoryReadme", options.MarkdownLifecycleHost);
        Assert.Equal("C:\\readmes\\awesome README.md", options.MarkdownCorpusPath);
    }

    [Fact]
    public void Parse_ReadmeProductionAuditRequiresExplicitFlag()
    {
        Assert.True(LaunchOptions.Parse(["--readme-production-audit"]).ReadmeProductionAudit);
        Assert.False(LaunchOptions.Parse([]).ReadmeProductionAudit);
    }

    [Fact]
    public void Parse_OfflineSameByteAuditIsAnExplicitProductionAuditMode()
    {
        LaunchOptions options = LaunchOptions.Parse(
        [
            "--page=repo-code",
            "--readme-production-audit",
            "--readme-offline-same-byte-audit"
        ]);

        Assert.True(options.ReadmeProductionAudit);
        Assert.True(options.OfflineSameByteReadmeAudit);
        Assert.False(LaunchOptions.Parse([]).OfflineSameByteReadmeAudit);
        Assert.Throws<InvalidOperationException>(() => options.ResolveReadmeAuditAccountId());
    }

    [Fact]
    public void ValidateOfflineSameByteReadmeAuditPin_RequiresManifestConfirmedAbsentReadme()
    {
        const string repository = "owner/repo";
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string directory = Path.Combine(Path.GetTempPath(), $"jithub-offline-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "pinned-top500.json");
        string[] environmentNames = OfflineSameByteReadmeAuditEnvironmentNames();
        string?[] previousValues = environmentNames
            .Select(static name => Environment.GetEnvironmentVariable(name))
            .ToArray();
        try
        {
            byte[] manifest = CreatePinnedTop500Manifest(
                repository,
                commit,
                readmeAvailable: false,
                readmePath: string.Empty,
                readmeSha: string.Empty,
                readmeByteSize: 0);
            File.WriteAllBytes(manifestPath, manifest);
            Environment.SetEnvironmentVariable(environmentNames[0], null);
            Environment.SetEnvironmentVariable(environmentNames[1], null);
            Environment.SetEnvironmentVariable(environmentNames[2], manifestPath);
            Environment.SetEnvironmentVariable(environmentNames[3], "1");
            Environment.SetEnvironmentVariable(environmentNames[4], Convert.ToHexString(SHA256.HashData(manifest)));
            Environment.SetEnvironmentVariable(environmentNames[5], "1");
            Environment.SetEnvironmentVariable(environmentNames[6], null);
            Environment.SetEnvironmentVariable(environmentNames[7], null);

            LaunchOptions options = LaunchOptions.Parse(
            [
                "--page=repo-code",
                $"--repo={repository}",
                $"--branch={commit}",
                "--readme-production-audit",
                "--readme-offline-same-byte-audit"
            ]);
            OfflineSameByteReadmeAuditPin pin = Assert.IsType<OfflineSameByteReadmeAuditPin>(
                options.ValidateOfflineSameByteReadmeAuditPin());

            Assert.Equal(1, pin.Rank);
            Assert.Equal(repository, pin.Repository);
            Assert.Equal(commit, pin.CommitSha);
            Assert.False(pin.ReadmeAvailable);
            Assert.Equal(string.Empty, pin.ReadmePath);

            Environment.SetEnvironmentVariable(environmentNames[5], null);
            Assert.Throws<InvalidDataException>(() => options.ValidateOfflineSameByteReadmeAuditPin());
        }
        finally
        {
            for (int index = 0; index < environmentNames.Length; index++)
                Environment.SetEnvironmentVariable(environmentNames[index], previousValues[index]);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateOfflineSameByteReadmeAuditPin_BindsAvailableReadmeAndRejectsCredentialOrPathDrift()
    {
        const string repository = "owner/repo";
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string readmeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string readmePath = "docs/README.md";
        const long readmeBytes = 1234;
        string directory = Path.Combine(Path.GetTempPath(), $"jithub-offline-audit-{Guid.NewGuid():N}");
        string corpusDirectory = Path.Combine(directory, "corpus");
        Directory.CreateDirectory(corpusDirectory);
        string manifestPath = Path.Combine(directory, "pinned-top500.json");
        File.WriteAllText(Path.Combine(corpusDirectory, "manifest.json"), "{}");
        string[] environmentNames = OfflineSameByteReadmeAuditEnvironmentNames();
        string?[] previousValues = environmentNames
            .Select(static name => Environment.GetEnvironmentVariable(name))
            .ToArray();
        try
        {
            byte[] manifest = CreatePinnedTop500Manifest(
                repository,
                commit,
                readmeAvailable: true,
                readmePath: readmePath,
                readmeSha: readmeSha,
                readmeByteSize: readmeBytes);
            File.WriteAllBytes(manifestPath, manifest);
            Environment.SetEnvironmentVariable(environmentNames[0], null);
            Environment.SetEnvironmentVariable(environmentNames[1], null);
            Environment.SetEnvironmentVariable(environmentNames[2], manifestPath);
            Environment.SetEnvironmentVariable(environmentNames[3], "1");
            Environment.SetEnvironmentVariable(environmentNames[4], Convert.ToHexString(SHA256.HashData(manifest)));
            Environment.SetEnvironmentVariable(environmentNames[5], null);
            Environment.SetEnvironmentVariable(environmentNames[6], corpusDirectory);
            Environment.SetEnvironmentVariable(environmentNames[7], readmeSha);

            LaunchOptions options = LaunchOptions.Parse(
            [
                "--page=repo-code",
                $"--repo={repository}",
                $"--branch={commit}",
                "--readme-production-audit",
                "--readme-offline-same-byte-audit"
            ]);
            OfflineSameByteReadmeAuditPin pin = Assert.IsType<OfflineSameByteReadmeAuditPin>(
                options.ValidateOfflineSameByteReadmeAuditPin());

            Assert.Equal(1, pin.Rank);
            Assert.Equal(repository, pin.Repository);
            Assert.Equal(commit, pin.CommitSha);
            Assert.True(pin.ReadmeAvailable);
            Assert.Equal(readmePath, pin.ReadmePath);
            Assert.Equal(readmeSha, pin.ReadmeGitBlobSha1);
            Assert.Equal(readmeBytes, pin.ReadmeByteSize);

            Environment.SetEnvironmentVariable(environmentNames[0], "must-not-be-inherited");
            Assert.Throws<InvalidOperationException>(() => options.ValidateOfflineSameByteReadmeAuditPin());
            Environment.SetEnvironmentVariable(environmentNames[0], null);
            Environment.SetEnvironmentVariable(environmentNames[1], "9843127");
            Assert.Throws<InvalidOperationException>(() => options.ValidateOfflineSameByteReadmeAuditPin());
            Environment.SetEnvironmentVariable(environmentNames[1], null);

            byte[] escapedPathManifest = CreatePinnedTop500Manifest(
                repository,
                commit,
                readmeAvailable: true,
                readmePath: "../README.md",
                readmeSha: readmeSha,
                readmeByteSize: readmeBytes);
            File.WriteAllBytes(manifestPath, escapedPathManifest);
            Environment.SetEnvironmentVariable(environmentNames[4], Convert.ToHexString(SHA256.HashData(escapedPathManifest)));
            Assert.Throws<InvalidDataException>(() => options.ValidateOfflineSameByteReadmeAuditPin());
        }
        finally
        {
            for (int index = 0; index < environmentNames.Length; index++)
                Environment.SetEnvironmentVariable(environmentNames[index], previousValues[index]);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadmeProductionAudit_RequiresStablePositiveAccountPartition()
    {
        const string variable = "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        try
        {
            LaunchOptions options = LaunchOptions.Parse(["--readme-production-audit"]);
            System.Environment.SetEnvironmentVariable(variable, "9843127");
            Assert.Equal(9_843_127, options.ResolveReadmeAuditAccountId());

            System.Environment.SetEnvironmentVariable(variable, "current");
            Assert.Throws<System.InvalidOperationException>(() => options.ResolveReadmeAuditAccountId());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Parse_ProcessArgumentsOverridePackagedActivationArguments()
    {
        LaunchOptions options = LaunchOptions.Parse(
            ["--theme=light", "--repo=JitHubApp/JitHubV2"],
            "--page=repo --theme=dark --repo=sindresorhus/awesome");

        Assert.Equal("repo", options.Page);
        Assert.Equal("light", options.Theme);
        Assert.Equal("JitHubApp/JitHubV2", options.RepositoryFullName);
    }

    [Fact]
    public void Parse_EnablesWebsiteShowcaseOnlyForTheExplicitCaptureFlag()
    {
        LaunchOptions capture = LaunchOptions.Parse(["--website-showcase"]);
        LaunchOptions normal = LaunchOptions.Parse([]);

        Assert.True(capture.WebsiteShowcase);
        Assert.False(normal.WebsiteShowcase);
    }

    [Fact]
    public void TokenizeActivationArguments_BoundsUntrustedInput()
    {
        Assert.Empty(LaunchOptions.TokenizeActivationArguments(new string('x', 32_768)));
        Assert.Equal(
            ["--scenario=quoted value", "", "--branch=main"],
            LaunchOptions.TokenizeActivationArguments("--scenario=\"quoted value\" \"\" --branch=main"));
    }

    private static string[] OfflineSameByteReadmeAuditEnvironmentNames() =>
    [
        "JITHUB_README_AUDIT_GITHUB_TOKEN",
        "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID",
        "JITHUB_README_AUDIT_PINNED_MANIFEST_PATH",
        "JITHUB_README_AUDIT_PINNED_MANIFEST_RANK",
        "JITHUB_README_AUDIT_PINNED_MANIFEST_SHA256",
        "JITHUB_README_AUDIT_SAME_BYTE_README_ABSENT",
        "JITHUB_README_AUDIT_SAME_BYTE_CORPUS",
        "JITHUB_README_AUDIT_SAME_BYTE_README_SHA"
    ];

    private static byte[] CreatePinnedTop500Manifest(
        string selectedRepository,
        string commit,
        bool readmeAvailable,
        string readmePath,
        string readmeSha,
        long readmeByteSize)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WritePropertyName("repositories");
            writer.WriteStartArray();
            for (int rank = 1; rank <= 500; rank++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("rank", rank);
                writer.WriteString("fullName", rank == 1 ? selectedRepository : $"owner/repo-{rank}");
                writer.WriteString("commitSha", commit);
                writer.WritePropertyName("readme");
                writer.WriteStartObject();
                writer.WriteBoolean("available", rank == 1 && readmeAvailable);
                writer.WriteNumber("byteSize", rank == 1 && readmeAvailable ? readmeByteSize : 0);
                writer.WriteString("path", rank == 1 && readmeAvailable ? readmePath : string.Empty);
                writer.WriteString("sha", rank == 1 && readmeAvailable ? readmeSha : string.Empty);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
