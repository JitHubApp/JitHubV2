using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Maps renderer sources to the assemblies that actually compile them. Most
/// projects own their source directory, but the renderer packages also split
/// and link files between projects; treating every file under a project folder
/// as belonging to its same-named assembly causes false stale-build reports.
/// </summary>
internal static class AppArtifactFreshness
{
    private static readonly string[] SourceExtensions =
        [".cs", ".xaml", ".csproj", ".props", ".targets", ".rs", ".toml", ".otf"];
    private const long MaximumWorkerBytes = 64L * 1024 * 1024;
    private const long MaximumNativeAotProvenanceBytes = 4L * 1024 * 1024;
    private const int MaximumNativeAotArtifactEntries = 8192;
    private const int MaximumNativeAotProvenanceFiles = 4096;
    private const string ResvgWorkerFileName = "MarkdownRenderer.Svg.Resvg.Worker.exe";
    private const string NativeAotProvenanceFileName = "native-aot-provenance.json";
    private static readonly (string SourcePath, string OutputPath)[] JitHubAppContentInputs =
    [
        ("JitHub.WinUI/appsettings.json", "appsettings.json"),
        ("JitHub.WinUI/Assets/CodeViewer/language-map.json", "Assets/CodeViewer/language-map.json"),
        ("JitHub.WinUI/Strings/en-US/Resources.resw", "resources.pri")
    ];

    private static readonly IReadOnlyDictionary<string, MarkdownAssemblySourceMap> MarkdownAssemblySources =
        CreateMarkdownAssemblySourceMaps();

    internal static DateTime GetNewestMarkdownAssemblySourceWriteTimeUtc(
        string repositoryRoot,
        string assemblyName)
    {
        DateTime newest = DateTime.MinValue;
        foreach (string sourcePath in GetMarkdownAssemblySourcePaths(repositoryRoot, assemblyName))
        {
            DateTime writeTime = File.GetLastWriteTimeUtc(sourcePath);
            if (writeTime > newest)
            {
                newest = writeTime;
            }
        }

        return newest;
    }

    internal static IReadOnlyList<string> GetMarkdownAssemblySourcePaths(
        string repositoryRoot,
        string assemblyName)
    {
        if (!MarkdownAssemblySources.TryGetValue(assemblyName, out MarkdownAssemblySourceMap? sourceMap))
        {
            throw new ArgumentOutOfRangeException(
                nameof(assemblyName),
                assemblyName,
                "The assembly is not part of the managed MarkdownRenderer freshness check.");
        }

        string projectRoot = Path.GetFullPath(Path.Combine(repositoryRoot, sourceMap.ProjectRoot));
        HashSet<string> excluded = new(sourceMap.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(projectRoot))
        {
            foreach (string sourcePath in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(Path.GetRelativePath(projectRoot, sourcePath));
                if (IsBuildOutputPath(relativePath) ||
                    !SourceExtensions.Contains(Path.GetExtension(sourcePath), StringComparer.OrdinalIgnoreCase) ||
                    excluded.Contains(relativePath) ||
                    sourceMap.ExcludedPatterns.Any(pattern => MatchesPathPattern(relativePath, pattern)))
                {
                    continue;
                }

                included.Add(Path.GetFullPath(sourcePath));
            }
        }

        foreach (string relativePath in sourceMap.IncludedPaths)
        {
            string sourcePath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
            if (File.Exists(sourcePath))
            {
                included.Add(sourcePath);
            }
        }

        string rendererRoot = Path.Combine(repositoryRoot, "MarkdownRenderer");
        if (Directory.Exists(rendererRoot) && sourceMap.IncludedPatterns.Length > 0)
        {
            foreach (string sourcePath in Directory.EnumerateFiles(rendererRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, sourcePath));
                if (SourceExtensions.Contains(Path.GetExtension(sourcePath), StringComparer.OrdinalIgnoreCase) &&
                    sourceMap.IncludedPatterns.Any(pattern => MatchesPathPattern(relativePath, pattern)))
                {
                    included.Add(Path.GetFullPath(sourcePath));
                }
            }
        }

        return included.ToArray();
    }

    /// <summary>
    /// Returns the app-local output artifact for the first configured JSON or
    /// PRI resource input whose output is missing or older. This deliberately
    /// lists only JitHub app content inputs; package manifests, test data, and
    /// renderer provenance JSON do not participate in app freshness.
    /// </summary>
    internal static string? GetStaleJitHubAppContentArtifact(
        string repositoryRoot,
        string appOutputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(appOutputDirectory);

        foreach ((string sourceRelativePath, string outputRelativePath) in JitHubAppContentInputs)
        {
            string sourcePath = Path.GetFullPath(Path.Combine(repositoryRoot, sourceRelativePath));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            string outputPath = Path.GetFullPath(Path.Combine(appOutputDirectory, outputRelativePath));
            if (!File.Exists(outputPath) ||
                File.GetLastWriteTimeUtc(outputPath) < File.GetLastWriteTimeUtc(sourcePath))
            {
                return outputPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Revalidates the canonical workflow provenance before a downloaded
    /// NativeAOT artifact is allowed to ignore checkout-local file timestamps.
    /// The artifact payload remains content-addressed and is verified again in
    /// this process; a caller cannot turn an ordinary local binary into a
    /// provenance-qualified artifact by supplying only a command-line switch.
    /// </summary>
    internal static void VerifyCanonicalNativeAotProvenance(
        string appPath,
        string provenancePath,
        string expectedProvenanceSha256,
        NativeAotWorkflowIdentity expectedWorkflowIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenancePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProvenanceSha256);
        ArgumentNullException.ThrowIfNull(expectedWorkflowIdentity);

        string fullAppPath = Path.GetFullPath(appPath);
        string artifactRoot = Path.GetDirectoryName(fullAppPath)
            ?? throw new InvalidDataException("The NativeAOT app has no artifact directory.");
        string fullProvenancePath = Path.GetFullPath(provenancePath);
        string canonicalProvenancePath = Path.Combine(artifactRoot, NativeAotProvenanceFileName);
        if (!string.Equals(fullProvenancePath, canonicalProvenancePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullAppPath), "JitHub.WinUI.exe", StringComparison.Ordinal) ||
            !IsSha256(expectedProvenanceSha256) ||
            !IsSha1(expectedWorkflowIdentity.SourceCommitSha) ||
            !IsPositiveInteger(expectedWorkflowIdentity.WorkflowRunId) ||
            !IsWorkflowRepository(expectedWorkflowIdentity.WorkflowRepository) ||
            !IsWorkflowRef(expectedWorkflowIdentity.WorkflowRef))
        {
            throw new InvalidDataException("NativeAOT provenance was not bound to a canonical workflow artifact identity.");
        }

        FileInfo provenanceFile = new(fullProvenancePath);
        EnsureBoundedRegularFile(provenanceFile, MaximumNativeAotProvenanceBytes, allowEmpty: false);
        byte[] provenanceBytes = File.ReadAllBytes(fullProvenancePath);
        string actualProvenanceSha256 = Convert.ToHexString(SHA256.HashData(provenanceBytes));
        if (!actualProvenanceSha256.Equals(expectedProvenanceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NativeAOT provenance changed after the wrapper verified it.");

        using JsonDocument document = JsonDocument.Parse(provenanceBytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            ReadRequiredInt32(root, "schemaVersion") != 1 ||
            !ReadRequiredBoolean(root, "provenanceVerified") ||
            !ReadRequiredBoolean(root, "workingTreeClean") ||
            !ReadRequiredString(root, "configuration").Equals("Release", StringComparison.Ordinal) ||
            !ReadRequiredString(root, "runtimeFlavor").Equals("native-aot", StringComparison.Ordinal) ||
            !ReadRequiredString(root, "architecture").Equals("x64", StringComparison.Ordinal) ||
            !ReadRequiredString(root, "runtimeIdentifier").Equals("win-x64", StringComparison.Ordinal) ||
            !ReadRequiredString(root, "executableName").Equals("JitHub.WinUI.exe", StringComparison.Ordinal))
        {
            throw new InvalidDataException("NativeAOT provenance has an invalid or unqualified build identity.");
        }

        string sourceCommitSha = ReadRequiredString(root, "sourceCommitSha");
        string workflowRunId = ReadRequiredString(root, "workflowRunId");
        string workflowRepository = ReadRequiredString(root, "workflowRepository");
        string workflowRef = ReadRequiredString(root, "workflowRef");
        if (!sourceCommitSha.Equals(expectedWorkflowIdentity.SourceCommitSha, StringComparison.OrdinalIgnoreCase) ||
            !workflowRunId.Equals(expectedWorkflowIdentity.WorkflowRunId, StringComparison.Ordinal) ||
            !workflowRepository.Equals(expectedWorkflowIdentity.WorkflowRepository, StringComparison.Ordinal) ||
            !workflowRef.Equals(expectedWorkflowIdentity.WorkflowRef, StringComparison.Ordinal))
        {
            throw new InvalidDataException("NativeAOT provenance does not match this workflow checkout and run.");
        }

        string executableSha256 = ReadRequiredString(root, "executableSha256");
        string reportedManifestSha256 = ReadRequiredString(root, "artifactManifestSha256");
        if (!IsSha256(executableSha256) || !IsSha256(reportedManifestSha256) ||
            !root.TryGetProperty("artifactFileSha256", out JsonElement fileManifest) ||
            fileManifest.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("NativeAOT provenance has an invalid payload manifest.");
        }

        SortedDictionary<string, string> reportedFiles = ReadReportedPayloadFiles(fileManifest);
        if (!reportedFiles.TryGetValue("JitHub.WinUI.exe", out string? reportedExecutableSha256) ||
            !reportedExecutableSha256.Equals(executableSha256, StringComparison.OrdinalIgnoreCase) ||
            !ComputePayloadManifestSha256(reportedFiles).Equals(reportedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("NativeAOT provenance payload digest is internally inconsistent.");
        }

        SortedDictionary<string, string> actualFiles = HashNativeAotPayload(artifactRoot);
        if (actualFiles.Count != reportedFiles.Count)
            throw new InvalidDataException("NativeAOT provenance payload file count changed after verification.");

        foreach ((string relativePath, string reportedSha256) in reportedFiles)
        {
            if (!actualFiles.TryGetValue(relativePath, out string? actualSha256) ||
                !actualSha256.Equals(reportedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"NativeAOT provenance payload does not match: '{relativePath}'.");
            }
        }
    }

    internal static bool IsNativeAotTimestampFresh(
        DateTime appWriteTimeUtc,
        DateTime newestSourceWriteTimeUtc,
        bool hasVerifiedCanonicalProvenance) =>
        hasVerifiedCanonicalProvenance || appWriteTimeUtc >= newestSourceWriteTimeUtc;

    /// <summary>
    /// Returns the app-local resvg worker path when it is missing or does not
    /// byte-match the worker selected for the executable's architecture. The
    /// worker is a content file, not a managed assembly, so assembly timestamps
    /// cannot establish that an app output contains the current renderer.
    /// </summary>
    internal static string? GetStaleResvgWorkerArtifact(
        string appPath,
        string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string architecture;
        try
        {
            using var stream = new FileStream(
                appPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            using var reader = new PEReader(stream);
            architecture = reader.PEHeaders.CoffHeader.Machine switch
            {
                Machine.I386 => "win-x86",
                Machine.Amd64 => "win-x64",
                Machine.Arm64 => "win-arm64",
                _ => string.Empty,
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appPath))!, ResvgWorkerFileName);
        }

        if (architecture.Length == 0)
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appPath))!, ResvgWorkerFileName);

        return GetStaleResvgWorkerArtifact(
            repositoryRoot,
            Path.GetDirectoryName(Path.GetFullPath(appPath))!,
            architecture);
    }

    /// <summary>
    /// Architecture-specific core used by the freshness check and its focused
    /// tests. Only the selected RID is examined; other checked-in worker
    /// binaries cannot spuriously invalidate this app output.
    /// </summary>
    internal static string? GetStaleResvgWorkerArtifact(
        string repositoryRoot,
        string appOutputDirectory,
        string runtimeIdentifier)
    {
        string relativeWorkerPath = runtimeIdentifier switch
        {
            "win-x86" => "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/runtimes/win-x86/native/" + ResvgWorkerFileName,
            "win-x64" => "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/runtimes/win-x64/native/" + ResvgWorkerFileName,
            "win-arm64" => "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/runtimes/win-arm64/native/" + ResvgWorkerFileName,
            _ => throw new ArgumentOutOfRangeException(
                nameof(runtimeIdentifier), runtimeIdentifier, "Unsupported resvg worker architecture."),
        };

        string sourcePath = Path.GetFullPath(Path.Combine(repositoryRoot, relativeWorkerPath));
        string outputPath = Path.GetFullPath(Path.Combine(appOutputDirectory, ResvgWorkerFileName));
        if (!TryGetBoundedRegularFile(sourcePath, out long sourceLength) ||
            !TryGetBoundedRegularFile(outputPath, out long outputLength) ||
            sourceLength != outputLength)
        {
            return outputPath;
        }

        try
        {
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(
                outputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Security.Cryptography.SHA256.HashData(source),
                System.Security.Cryptography.SHA256.HashData(output))
                ? null
                : outputPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return outputPath;
        }
    }

    private static bool TryGetBoundedRegularFile(string path, out long length)
    {
        length = 0;
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MaximumWorkerBytes ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            length = file.Length;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static SortedDictionary<string, string> ReadReportedPayloadFiles(JsonElement fileManifest)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (JsonProperty property in fileManifest.EnumerateObject())
        {
            count++;
            string path = property.Name;
            string hash = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : string.Empty;
            if (count > MaximumNativeAotProvenanceFiles || !IsSafeArtifactRelativePath(path) ||
                !canonicalPaths.Add(path) || !IsSha256(hash))
            {
                throw new InvalidDataException("NativeAOT provenance contains an invalid payload path or hash.");
            }

            files.Add(path, hash.ToLowerInvariant());
        }

        if (count == 0)
            throw new InvalidDataException("NativeAOT provenance payload manifest is empty.");
        return files;
    }

    private static SortedDictionary<string, string> HashNativeAotPayload(string artifactRoot)
    {
        DirectoryInfo root = new(artifactRoot);
        root.Refresh();
        if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("NativeAOT provenance root is missing or redirected.");

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(root);
        int entryCount = 0;
        while (pendingDirectories.Count > 0)
        {
            DirectoryInfo directory = pendingDirectories.Pop();
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entryCount++;
                if (entryCount > MaximumNativeAotArtifactEntries ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "NativeAOT provenance payload has too many entries or contains a redirect.");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pendingDirectories.Push(childDirectory);
                    continue;
                }

                if (entry is not FileInfo file ||
                    file.FullName.Equals(
                        Path.Combine(artifactRoot, NativeAotProvenanceFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = Path.GetRelativePath(artifactRoot, file.FullName).Replace('\\', '/');
                if (!IsSafeArtifactRelativePath(relativePath) || !canonicalPaths.Add(relativePath))
                    throw new InvalidDataException("NativeAOT provenance payload contains an unsafe or ambiguous path.");

                using FileStream stream = new(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                files.Add(relativePath, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            }
        }

        return files;
    }

    private static string ComputePayloadManifestSha256(IReadOnlyDictionary<string, string> files)
    {
        string canonical = string.Join('\n', files.Select(pair => $"{pair.Key}\0{pair.Value.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void EnsureBoundedRegularFile(FileInfo file, long maximumBytes, bool allowEmpty)
    {
        file.Refresh();
        if (!file.Exists || (!allowEmpty && file.Length == 0) || file.Length < 0 || file.Length > maximumBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("NativeAOT provenance file is missing, oversized, or redirected.");
        }
    }

    private static bool IsSafeArtifactRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith("/", StringComparison.Ordinal) &&
        !path.Contains('\\') &&
        !path.Contains(':') &&
        path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static bool IsSha1(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool IsPositiveInteger(string value) =>
        value.Length is > 0 and <= 19 && value[0] != '0' && value.All(char.IsAsciiDigit);

    private static bool IsWorkflowRepository(string value)
    {
        string[] segments = value.Split('/');
        return segments.Length == 2 && segments.All(segment =>
            segment.Length > 0 && !segment.Any(char.IsWhiteSpace));
    }

    private static bool IsWorkflowRef(string value) =>
        value.StartsWith("refs/", StringComparison.Ordinal) &&
        value.Length > "refs/".Length &&
        !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"NativeAOT provenance is missing '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"NativeAOT provenance is missing '{propertyName}'.");
        return result;
    }

    private static bool ReadRequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"NativeAOT provenance is missing '{propertyName}'.");
        }

        return value.GetBoolean();
    }

    private static IReadOnlyDictionary<string, MarkdownAssemblySourceMap> CreateMarkdownAssemblySourceMaps()
    {
        string rendererRoot = "MarkdownRenderer/MarkdownRenderer";
        string gfmRoot = "MarkdownRenderer/MarkdownRenderer.Gfm";

        return new Dictionary<string, MarkdownAssemblySourceMap>(StringComparer.Ordinal)
        {
            ["MarkdownRenderer.Core"] = new(
                "MarkdownRenderer/MarkdownRenderer.Core",
                includedPatterns:
                [
                    "MarkdownRenderer/MarkdownRenderer/SourceSpan.cs",
                    "MarkdownRenderer/MarkdownRenderer/Document/**/*.cs",
                    "MarkdownRenderer/MarkdownRenderer/Engine/**/*.cs",
                    "MarkdownRenderer/MarkdownRenderer/Extensions/**/*.cs",
                    "MarkdownRenderer/MarkdownRenderer/Theming/MarkdownStyleRole.cs"
                ]),
            ["MarkdownRenderer"] = new(
                rendererRoot,
                excludedPaths:
                [
                    "SourceSpan.cs",
                    "Theming/MarkdownStyleRole.cs",
                    "Performance/MarkdownPerformanceSession.cs",
                    "Performance/MarkdownStyleRoleDemandCollector.cs",
                    "Performance/MarkdownProgressiveScenePlan.cs",
                    "Diagnostics/MarkdownRasterPreparationEventSource.cs",
                    "Layout/Boxes/SvgResourceBudget.cs",
                    "Layout/Boxes/SvgStaticSnapshot.cs",
                    "Layout/Boxes/SvgThemeInjector.cs",
                    "Layout/Boxes/RasterImageTilePlanner.cs"
                ],
                excludedPatterns: ["Document/**/*.cs", "Engine/**/*.cs", "Extensions/**/*.cs"]),
            ["MarkdownRenderer.Gfm"] = new(
                gfmRoot,
                excludedPaths:
                [
                    "Renderers/AlertRenderer.cs",
                    "Renderers/FootnoteRenderer.cs",
                    "Renderers/HtmlBlockRenderer.cs"
                ]),
            ["MarkdownRenderer.Html"] = new(
                "MarkdownRenderer/MarkdownRenderer.Html",
                includedPaths:
                [
                    $"{gfmRoot}/GfmChildBuilder.cs",
                    $"{gfmRoot}/Renderers/HtmlBlockRenderer.cs"
                ]),
            ["MarkdownRenderer.Math"] = new(
                "MarkdownRenderer/MarkdownRenderer.Math",
                excludedPaths:
                [
                    "ThirdParty/CSharpMath.Rendering/FrontEnd/MathKeyboard.cs",
                    "ThirdParty/CSharpMath.Rendering/FrontEnd/TextPainter.cs",
                    "ThirdParty/CSharpMath.Rendering/Settings.cs"
                ],
                excludedPatterns:
                [
                    "ThirdParty/CSharpMath.Rendering/Text/**/*.cs",
                    "ThirdParty/Typography/**/*.cs"
                ],
                includedPaths:
                [
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Contours/GlyphOutlineBuilderBase.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Bounds.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Glyph.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/IGlyphTranslator.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/OpenFontReader.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Typeface.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/IO/ByteOrderSwappingBinaryReader.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/CharacterMap.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/Cmap.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/Head.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/HorizontalHeader.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/HorizontalMetrics.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/MaxProfile.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/TableEntry.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/TableHeader.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables/Utils.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.AdvancedLayout/CoverageTable.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.AdvancedLayout/MathTable.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.CFF/CFF.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.CFF/CffEvaluationEngine.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.CFF/CFFTable.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.CFF/Type2CharStringParser.cs",
                    "MarkdownRenderer/MarkdownRenderer.Math/ThirdParty/Typography/Typography.OpenFont/Tables.CFF/Type2InstructionCompacter.cs"
                ]),
            ["MarkdownRenderer.Mermaid"] = new("MarkdownRenderer/MarkdownRenderer.Mermaid"),
            ["MarkdownRenderer.Performance"] = new(
                "MarkdownRenderer/MarkdownRenderer.Performance",
                includedPaths:
                [
                    $"{rendererRoot}/Performance/MarkdownPerformanceSession.cs",
                    $"{rendererRoot}/Performance/MarkdownProgressiveScenePlan.cs",
                    $"{rendererRoot}/Performance/MarkdownStyleRoleDemandCollector.cs",
                    $"{rendererRoot}/Diagnostics/MarkdownRasterPreparationEventSource.cs"
                ]),
            ["MarkdownRenderer.GitHub"] = new(
                "MarkdownRenderer/MarkdownRenderer.GitHub",
                includedPaths:
                [
                    $"{gfmRoot}/Renderers/AlertRenderer.cs",
                    $"{gfmRoot}/Renderers/FootnoteRenderer.cs"
                ]),
            ["MarkdownRenderer.Svg.Resvg"] = new(
                "MarkdownRenderer/MarkdownRenderer.Svg.Resvg",
                includedPaths:
                [
                    $"{rendererRoot}/Layout/Boxes/SvgResourceBudget.cs",
                    $"{rendererRoot}/Layout/Boxes/SvgStaticSnapshot.cs",
                    "MarkdownRenderer/MarkdownRenderer.Svg.Resvg/native/Cargo.lock"
                ]),
            ["MarkdownRenderer.SyntaxHighlighting.TextMate"] = new(
                "MarkdownRenderer/MarkdownRenderer.SyntaxHighlighting.TextMate"),
            ["MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common"] = new(
                "MarkdownRenderer/MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common",
                includedPaths:
                [
                    "MarkdownRenderer/MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Shared/BundledTextMateGrammarProvider.cs"
                ])
        };
    }

    private static bool IsBuildOutputPath(string relativePath)
    {
        string firstSegment = relativePath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return firstSegment.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.Equals("artifacts", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static bool MatchesPathPattern(string path, string pattern)
    {
        string normalizedPattern = NormalizeRelativePath(pattern);
        string regex = "^" + Regex.Escape(normalizedPattern)
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed class MarkdownAssemblySourceMap(
        string projectRoot,
        string[]? excludedPaths = null,
        string[]? excludedPatterns = null,
        string[]? includedPaths = null,
        string[]? includedPatterns = null)
    {
        public string ProjectRoot { get; } = projectRoot;
        public string[] ExcludedPaths { get; } = excludedPaths ?? [];
        public string[] ExcludedPatterns { get; } = excludedPatterns ?? [];
        public string[] IncludedPaths { get; } = includedPaths ?? [];
        public string[] IncludedPatterns { get; } = includedPatterns ?? [];
    }
}

internal sealed record NativeAotWorkflowIdentity(
    string SourceCommitSha,
    string WorkflowRunId,
    string WorkflowRepository,
    string WorkflowRef);
