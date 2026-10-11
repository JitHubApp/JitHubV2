using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AppArtifactFreshnessTests
{
    [Fact]
    public void LinkedAndRemovedGfmSourcesBelongToTheirCompilingAssemblies()
    {
        using TemporaryRepository repository = new();
        string gfmSource = repository.Write("MarkdownRenderer/MarkdownRenderer.Gfm/Extensions/GfmExtension.cs");
        string alertRenderer = repository.Write("MarkdownRenderer/MarkdownRenderer.Gfm/Renderers/AlertRenderer.cs");
        string footnoteRenderer = repository.Write("MarkdownRenderer/MarkdownRenderer.Gfm/Renderers/FootnoteRenderer.cs");
        string htmlBlockRenderer = repository.Write("MarkdownRenderer/MarkdownRenderer.Gfm/Renderers/HtmlBlockRenderer.cs");

        string[] gfmSources = AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Gfm").ToArray();
        string[] githubSources = AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.GitHub").ToArray();
        string[] htmlSources = AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Html").ToArray();

        Assert.Contains(gfmSource, gfmSources);
        Assert.DoesNotContain(alertRenderer, gfmSources);
        Assert.DoesNotContain(footnoteRenderer, gfmSources);
        Assert.DoesNotContain(htmlBlockRenderer, gfmSources);
        Assert.Contains(alertRenderer, githubSources);
        Assert.Contains(footnoteRenderer, githubSources);
        Assert.Contains(htmlBlockRenderer, htmlSources);

        DateTime gfmTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime movedSourceTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(gfmSource, gfmTime);
        File.SetLastWriteTimeUtc(htmlBlockRenderer, movedSourceTime);

        Assert.Equal(gfmTime, AppArtifactFreshness.GetNewestMarkdownAssemblySourceWriteTimeUtc(
            repository.Root,
            "MarkdownRenderer.Gfm"));
        Assert.Equal(movedSourceTime, AppArtifactFreshness.GetNewestMarkdownAssemblySourceWriteTimeUtc(
            repository.Root,
            "MarkdownRenderer.Html"));
    }

    [Fact]
    public void SplitRendererProjectsTrackTheirLinkedInputsAndIgnoreRemovedInputs()
    {
        using TemporaryRepository repository = new();
        string coreLinkedSource = repository.Write("MarkdownRenderer/MarkdownRenderer/Document/MarkdownDocument.cs");
        string rendererExcludedSource = repository.Write("MarkdownRenderer/MarkdownRenderer/Document/Unused.cs");
        string resvgLinkedSource = repository.Write("MarkdownRenderer/MarkdownRenderer/Layout/Boxes/SvgStaticSnapshot.cs");
        string performanceLinkedSource = repository.Write("MarkdownRenderer/MarkdownRenderer/Performance/MarkdownPerformanceSession.cs");
        string grammarLinkedSource = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Shared/BundledTextMateGrammarProvider.cs");

        Assert.Contains(coreLinkedSource, AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Core"));
        Assert.DoesNotContain(rendererExcludedSource, AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer"));
        Assert.Contains(resvgLinkedSource, AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Svg.Resvg"));
        Assert.Contains(performanceLinkedSource, AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Performance"));
        Assert.Contains(grammarLinkedSource, AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common"));
    }

    [Theory]
    [InlineData("appsettings.json", "appsettings.json")]
    [InlineData("Assets/CodeViewer/language-map.json", "Assets/CodeViewer/language-map.json")]
    [InlineData("Strings/en-US/Resources.resw", "resources.pri")]
    public void JitHubAppContentFreshnessRejectsOlderOutputAndAcceptsMatchingOutput(
        string sourceRelativePath,
        string outputRelativePath)
    {
        using TemporaryRepository repository = new();
        string sourcePath = repository.Write($"JitHub.WinUI/{sourceRelativePath}");
        string outputDirectory = Path.Combine(repository.Root, "app-output");
        string outputPath = Path.Combine(outputDirectory, outputRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, "built output");

        DateTime sourceTime = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourcePath, sourceTime);
        File.SetLastWriteTimeUtc(outputPath, sourceTime.AddTicks(-1));

        Assert.Equal(
            Path.GetFullPath(outputPath),
            AppArtifactFreshness.GetStaleJitHubAppContentArtifact(repository.Root, outputDirectory));

        File.SetLastWriteTimeUtc(outputPath, sourceTime);
        Assert.Null(AppArtifactFreshness.GetStaleJitHubAppContentArtifact(repository.Root, outputDirectory));
    }

    [Fact]
    public void JitHubAppContentFreshnessIgnoresUnrelatedJsonAndInactiveResources()
    {
        using TemporaryRepository repository = new();
        string[] unrelatedInputs =
        [
            "JitHub.WinUI/packages.lock.json",
            "JitHub.WinUI/Properties/launchSettings.json",
            "JitHub.WinUI/Assets/Other/tool.json",
            "JitHub.WinUI/Strings/de/Resources.resw",
            "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/RESVG_PROVENANCE.json"
        ];
        DateTime future = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string path in unrelatedInputs)
        {
            File.SetLastWriteTimeUtc(repository.Write(path), future);
        }

        Assert.Null(AppArtifactFreshness.GetStaleJitHubAppContentArtifact(
            repository.Root,
            Path.Combine(repository.Root, "app-output")));
    }

    [Fact]
    public void MathFreshnessHonorsCompileRemovalsAndExplicitTypographyIncludes()
    {
        using TemporaryRepository repository = new();
        string removedCSharpMath = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/CSharpMath.Rendering/Text/UnusedText.cs");
        string removedTypography = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Unused/Unused.cs");
        string includedTypography = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/Head.cs");
        string embeddedMathFont = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/CSharpMath.Rendering/Reference Fonts/latinmodern-math.otf");

        string[] mathSources = AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Math").ToArray();

        Assert.DoesNotContain(removedCSharpMath, mathSources);
        Assert.DoesNotContain(removedTypography, mathSources);
        Assert.Contains(includedTypography, mathSources);
        Assert.Contains(embeddedMathFont, mathSources);
    }

    [Fact]
    public void ResvgFreshnessTracksWorkerBuildInputsButNotPackageMetadataOrGeneratedBins()
    {
        using TemporaryRepository repository = new();
        string cargoLock = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/native/Cargo.lock");
        string provenance = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/RESVG_PROVENANCE.json");
        string dependencyManifest = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/NATIVE_RUST_DEPENDENCIES.json");
        string generatedSource = repository.Write(
            "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/bin/GeneratedWorkerBuild.cs");

        string[] sources = AppArtifactFreshness.GetMarkdownAssemblySourcePaths(
            repository.Root,
            "MarkdownRenderer.Svg.Resvg").ToArray();

        Assert.Contains(cargoLock, sources);
        Assert.DoesNotContain(provenance, sources);
        Assert.DoesNotContain(dependencyManifest, sources);
        Assert.DoesNotContain(generatedSource, sources);
    }

    [Fact]
    public void ResvgFreshnessChecksOnlyTheSelectedWorkerAndRequiresExactPayloadBytes()
    {
        using TemporaryRepository repository = new();
        const string workerFileName = "MarkdownRenderer.Svg.Resvg.Worker.exe";
        string x64Worker = repository.Write(
            $"MarkdownRenderer/MarkdownRenderer.Svg.Resvg/runtimes/win-x64/native/{workerFileName}");
        string x86Worker = repository.Write(
            $"MarkdownRenderer/MarkdownRenderer.Svg.Resvg/runtimes/win-x86/native/{workerFileName}");
        string outputDirectory = Path.Combine(repository.Root, "app-output");
        Directory.CreateDirectory(outputDirectory);
        string outputWorker = Path.Combine(outputDirectory, workerFileName);

        Assert.Equal(
            outputWorker,
            AppArtifactFreshness.GetStaleResvgWorkerArtifact(
                repository.Root,
                outputDirectory,
                "win-x64"));

        File.Copy(x64Worker, outputWorker);
        Assert.Null(AppArtifactFreshness.GetStaleResvgWorkerArtifact(
            repository.Root,
            outputDirectory,
            "win-x64"));

        File.WriteAllText(x86Worker, "updated x86 worker only");
        Assert.Null(AppArtifactFreshness.GetStaleResvgWorkerArtifact(
            repository.Root,
            outputDirectory,
            "win-x64"));

        File.WriteAllText(x64Worker, "updated x64 worker");
        Assert.Equal(
            outputWorker,
            AppArtifactFreshness.GetStaleResvgWorkerArtifact(
                repository.Root,
                outputDirectory,
                "win-x64"));
    }

    [Fact]
    public void ResvgFreshnessRejectsRuntimeIdentifiersOutsideTheFixedRidSet()
    {
        using TemporaryRepository repository = new();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AppArtifactFreshness.GetStaleResvgWorkerArtifact(
                repository.Root,
                repository.Root,
                "../../outside"));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void CanonicalProvenanceQualifiesAnArtifactOlderThanASeparateJobCheckoutOnlyAfterExactPayloadVerification()
    {
        using TemporaryRepository repository = new();
        string artifactRoot = Path.Combine(repository.Root, "downloaded-native-aot");
        Directory.CreateDirectory(artifactRoot);
        string appPath = Path.Combine(artifactRoot, "JitHub.WinUI.exe");
        string settingsPath = Path.Combine(artifactRoot, "appsettings.json");
        File.WriteAllText(appPath, "native app payload");
        File.WriteAllText(settingsPath, "{\"qualified\":true}");

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["JitHub.WinUI.exe"] = Sha256(appPath),
            ["appsettings.json"] = Sha256(settingsPath),
        };
        const string sourceCommitSha = "0123456789012345678901234567890123456789";
        const string workflowRunId = "12345";
        const string workflowRepository = "JitHubApp/JitHubV2";
        const string workflowRef = "refs/pull/42/merge";
        var identity = new NativeAotWorkflowIdentity(
            sourceCommitSha,
            workflowRunId,
            workflowRepository,
            workflowRef);
        string provenancePath = Path.Combine(artifactRoot, "native-aot-provenance.json");
        string provenance = CreateProvenanceJson(files, identity);
        File.WriteAllText(provenancePath, provenance, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string provenanceSha256 = Sha256(provenancePath);

        AppArtifactFreshness.VerifyCanonicalNativeAotProvenance(
            appPath,
            provenancePath,
            provenanceSha256,
            identity);

        DateTime artifactBuiltUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime laterCheckoutUtc = artifactBuiltUtc.AddMinutes(5);
        Assert.False(AppArtifactFreshness.IsNativeAotTimestampFresh(
            artifactBuiltUtc,
            laterCheckoutUtc,
            hasVerifiedCanonicalProvenance: false));
        Assert.True(AppArtifactFreshness.IsNativeAotTimestampFresh(
            artifactBuiltUtc,
            laterCheckoutUtc,
            hasVerifiedCanonicalProvenance: true));

        File.AppendAllText(settingsPath, "tampered");
        Assert.Throws<InvalidDataException>(() =>
            AppArtifactFreshness.VerifyCanonicalNativeAotProvenance(
                appPath,
                provenancePath,
                provenanceSha256,
                identity));
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ManifestSha256(IReadOnlyDictionary<string, string> files)
    {
        string canonical = string.Join('\n', files.Select(pair => $"{pair.Key}\0{pair.Value.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string CreateProvenanceJson(
        IReadOnlyDictionary<string, string> files,
        NativeAotWorkflowIdentity identity)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteBoolean("provenanceVerified", true);
            writer.WriteBoolean("workingTreeClean", true);
            writer.WriteString("sourceCommitSha", identity.SourceCommitSha);
            writer.WriteString("configuration", "Release");
            writer.WriteString("runtimeFlavor", "native-aot");
            writer.WriteString("architecture", "x64");
            writer.WriteString("runtimeIdentifier", "win-x64");
            writer.WriteString("executableName", "JitHub.WinUI.exe");
            writer.WriteString("executableSha256", files["JitHub.WinUI.exe"]);
            writer.WriteString("workflowRunId", identity.WorkflowRunId);
            writer.WriteString("workflowRepository", identity.WorkflowRepository);
            writer.WriteString("workflowRef", identity.WorkflowRef);
            writer.WriteStartObject("artifactFileSha256");
            foreach ((string relativePath, string sha256) in files)
            {
                writer.WriteString(relativePath, sha256);
            }

            writer.WriteEndObject();
            writer.WriteString("artifactManifestSha256", ManifestSha256(files));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class TemporaryRepository : IDisposable
    {
        private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory("app-artifact-freshness-").FullName;

        public string Root => _temporaryDirectory;

        public string Write(string relativePath)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "source");
            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
