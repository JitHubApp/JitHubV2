using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownRenderer;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;

internal static class Program
{
    private const string ExpectedManifestSha256 =
        "615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72";
    private const int ExpectedRepositoryCount = 500;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const long MaximumReadmeBytes = 8L * 1024 * 1024;
    private const string ReportScope = "headless-markdown-parse-and-semantic-snapshot-only";
    private const string Utf8Policy =
        "System.Text.Encoding.UTF8.GetString(bytes); mirrors RepoTreeService.DecodeText and RepoFileCacheService.TryDecodeText; BOM is not explicitly stripped; invalid UTF-8 uses .NET replacement fallback.";
    internal const string CommonSubsetFixtureHeadingAndCode =
        "😀 prefix\n\n# Plain heading\n\n\u0060\u0060\u0060csharp\nvar answer = 42;\n\u0060\u0060\u0060\n";
    internal const string CommonSubsetFixtureLinkAndImage =
        "[plain](https://example.test/path)\n\n![diagram](images/diagram.png)\n";
    internal const string CommonSubsetFixtureCodeAtEof =
        "\u0060\u0060\u0060csharp\nvar answer = 42;\n\u0060\u0060\u0060";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--self-test"])
                return await AuditSelfTests.RunAsync().ConfigureAwait(false);
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Readme correctness audit failed before completion: {exception}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[0] is "--help" or "-h")
        {
            Console.Error.WriteLine(
                "Usage: MarkdownRenderer.ReadmeCorrectnessAudit <frozen-manifest.json> <source-object-root> <fresh-output-directory>");
            Console.Error.WriteLine(
                "The object root is either source-acquisition-*/ or its objects/ directory. No app, UI, image resolver, or network is used.");
            return 2;
        }

        string manifestPath = Path.GetFullPath(args[0]);
        string sourceObjectRootArgument = Path.GetFullPath(args[1]);
        string outputDirectory = Path.GetFullPath(args[2]);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The required frozen README manifest does not exist.", manifestPath);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException("The output directory must be a fresh path that does not already exist.");

        string objectStoreRoot = ResolveObjectStoreRoot(sourceObjectRootArgument);
        EnsureOutputDoesNotOverlapInputs(outputDirectory, manifestPath, sourceObjectRootArgument);

        byte[] manifestBytes = await ReadBoundedBytesAsync(manifestPath, MaximumManifestBytes).ConfigureAwait(false);

        string manifestSha256 = ToLowerHex(SHA256.HashData(manifestBytes));
        if (!string.Equals(manifestSha256, ExpectedManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Frozen manifest SHA-256 mismatch: expected {ExpectedManifestSha256}, received {manifestSha256}.");
        }

        FrozenManifest manifest = JsonSerializer.Deserialize(
            manifestBytes,
            ReadmeAuditJsonContext.Default.FrozenManifest)
            ?? throw new InvalidDataException("The frozen manifest JSON is null.");
        ValidateManifest(manifest);

        string[] objectPaths = await PreflightAllSourceObjectsAsync(manifest, objectStoreRoot).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);

        var run = new AuditRunResult
        {
            SchemaVersion = 3,
            Scope = ReportScope,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ManifestPath = manifestPath,
            ManifestSha256 = manifestSha256,
            ManifestRepositoryCount = manifest.Repositories.Count,
            SourceObjectRoot = objectStoreRoot,
            SourceObjectVerification = "all 500 raw Git blob objects passed byte-size and Git blob SHA-1 validation before parsing",
            Utf8DecodePolicy = Utf8Policy,
            Utf8BomHandling = "recorded per rank; raw source is decoded exactly once with Encoding.UTF8.GetString",
            JitHubEngineComposition = "new MarkdownEngineBuilder().UseGitHubReadme().UseMathematics().UseMermaid().Build()",
            VisualQualification = "not performed: this runner does not create controls, layout, paint, select text, load images, or compare screenshots",
            CasesFile = "cases.jsonl",
            BinarySha256 = Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(static path => Path.GetExtension(path) is ".dll" or ".exe")
                .Order(StringComparer.Ordinal)
                .ToDictionary(static path => Path.GetFileName(path), static path =>
                {
                    using var stream = File.OpenRead(path);
                    return ToLowerHex(SHA256.HashData(stream));
                }, StringComparer.Ordinal),
        };
        string casesPath = Path.Combine(outputDirectory, run.CasesFile);
        using var casesWriter = new StreamWriter(
            new FileStream(casesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        MarkdownEngine? engine = null;
        try
        {
            engine = new MarkdownEngineBuilder()
                .UseGitHubReadme()
                .UseMathematics()
                .UseMermaid()
                .Build();
            run.EngineProfileId = engine.Profile.Id;
            run.EngineExtensionIds = engine.Extensions.ExtensionIds.ToArray();
            run.ParseMaximumSourceLength = engine.ParseLimits.MaximumSourceLength;
            ValidateEngineComposition(engine);

            for (int index = 0; index < manifest.Repositories.Count; index++)
            {
                FrozenRepository repository = manifest.Repositories[index];
                string objectPath = objectPaths[index];
                AuditCaseResult result = await ParseOneAsync(
                        engine,
                        repository,
                        objectPath,
                        index + 1)
                    .ConfigureAwait(false);

                string jsonLine = JsonSerializer.Serialize(
                    result,
                    ReadmeAuditJsonContext.Default.AuditCaseResult);
                await casesWriter.WriteLineAsync(jsonLine).ConfigureAwait(false);
                await casesWriter.FlushAsync().ConfigureAwait(false);
                AccumulateCase(run, result);

                Console.WriteLine(
                    $"[{index + 1}/{ExpectedRepositoryCount}] {repository.FullName}: {result.Status}; headings={result.HeadingCount?.ToString() ?? "n/a"}; links={result.LinkCount?.ToString() ?? "n/a"}; images={result.ImageCount?.ToString() ?? "n/a"}; mermaid={result.MermaidSceneCount}/{result.MermaidDiagramCount}; fallbacks={result.MermaidFallbackCount + result.MathFallbackDiagnosticCount}; diagnostics={result.Diagnostics.Count}");
            }
        }
        finally
        {
            if (engine is not null)
            {
                try
                {
                    engine.Dispose();
                }
                catch (Exception exception)
                {
                    run.EngineDisposalException = CaptureException(exception);
                }
            }
        }

        await casesWriter.DisposeAsync().ConfigureAwait(false);
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        run.ExecutionStatus = DetermineExecutionStatus(run);
        string summaryPath = Path.Combine(outputDirectory, "summary.json");
        await File.WriteAllTextAsync(
                summaryPath,
                JsonSerializer.Serialize(run, ReadmeAuditJsonContext.Default.AuditRunResult),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(false);

        Console.WriteLine($"Summary: {summaryPath}");
        Console.WriteLine(
            $"Status={run.ExecutionStatus}; attempted={run.AttemptedParseCount}/{ExpectedRepositoryCount}; parsed={run.SuccessfulParseCount}; caseFailures={run.CaseFailureCount}; diagnostics={run.DiagnosticCount}; errorDiagnostics={run.ErrorDiagnosticCount}; warnings={run.WarningDiagnosticCount}; information={run.InformationDiagnosticCount}; Mermaid scenes/fallbacks/unclassified={run.MermaidSceneCount}/{run.MermaidFallbackCount}/{run.MermaidUnclassifiedCount}; math fallback diagnostics={run.MathFallbackDiagnosticCount}");

        return run.ExecutionStatus == "completed-clean" ? 0 : 1;
    }

    private static void ValidateManifest(FrozenManifest manifest)
    {
        if (manifest.SchemaVersion != 2 || manifest.RequestedCount != ExpectedRepositoryCount ||
            manifest.Repositories.Count != ExpectedRepositoryCount)
        {
            throw new InvalidDataException(
                $"Expected schema v2 with exactly {ExpectedRepositoryCount} repositories; received schema {manifest.SchemaVersion}, requested {manifest.RequestedCount}, rows {manifest.Repositories.Count}.");
        }

        var ids = new HashSet<long>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < manifest.Repositories.Count; index++)
        {
            FrozenRepository repository = manifest.Repositories[index];
            FrozenReadme readme = repository.Readme;
            if (repository.Rank != index + 1 || repository.Id <= 0 ||
                !ids.Add(repository.Id) || string.IsNullOrWhiteSpace(repository.FullName) ||
                !names.Add(repository.FullName) || !IsHex(repository.CommitSha, 40) ||
                !readme.Available || readme.ByteSize < 0 || readme.ByteSize > MaximumReadmeBytes ||
                !IsHex(readme.Sha, 40) || string.IsNullOrWhiteSpace(readme.Path) ||
                readme.Path.Contains('\\') || Path.IsPathRooted(readme.Path) ||
                readme.Path.Split('/').Any(static segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException(
                    $"Frozen manifest identity or README metadata is invalid at rank {index + 1}.");
            }
        }
    }

    private static void ValidateEngineComposition(MarkdownEngine engine)
    {
        if (engine.Profile.Id != MarkdownProfiles.GitHubReadme.Id ||
            !engine.Extensions.ContainsExtension("MarkdownRenderer.Math") ||
            !engine.Extensions.ContainsExtension("MarkdownRenderer.Mermaid"))
        {
            throw new InvalidOperationException(
                "The engine does not match JitHub's current GitHubReadme + Math + Mermaid composition.");
        }
    }

    private static async Task<string[]> PreflightAllSourceObjectsAsync(FrozenManifest manifest, string objectStoreRoot)
    {
        var paths = new string[manifest.Repositories.Count];
        for (int index = 0; index < manifest.Repositories.Count; index++)
        {
            FrozenRepository repository = manifest.Repositories[index];
            FrozenReadme readme = repository.Readme;
            string sha = readme.Sha.ToLowerInvariant();
            string objectPath = Path.GetFullPath(Path.Combine(
                objectStoreRoot,
                "git-blob-sha1",
                sha[..2],
                $"{sha}.md"));
            if (!IsWithin(objectPath, objectStoreRoot))
                throw new InvalidDataException($"Computed source-object path escaped its root at rank {repository.Rank}.");

            var file = new FileInfo(objectPath);
            file.Refresh();
            if (!file.Exists || file.Length != readme.ByteSize || file.Length > MaximumReadmeBytes)
            {
                throw new InvalidDataException(
                    $"Raw README object size/missing check failed at rank {repository.Rank} ({repository.FullName}); expected {readme.ByteSize}, observed {(file.Exists ? file.Length : -1)}.");
            }

            byte[] bytes = await ReadBoundedBytesAsync(objectPath, MaximumReadmeBytes, readme.ByteSize).ConfigureAwait(false);
            string observedGitBlobSha1 = ComputeGitBlobSha1(bytes);
            if (!string.Equals(observedGitBlobSha1, sha, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Raw README Git blob SHA-1 mismatch at rank {repository.Rank} ({repository.FullName}); expected {sha}, observed {observedGitBlobSha1}.");
            }

            paths[index] = objectPath;
        }

        return paths;
    }

    private static async Task<AuditCaseResult> ParseOneAsync(
        MarkdownEngine engine,
        FrozenRepository repository,
        string objectPath,
        int ordinal)
    {
        byte[] bytes = await ReadBoundedBytesAsync(objectPath, MaximumReadmeBytes, repository.Readme.ByteSize).ConfigureAwait(false);
        string observedGitBlobSha1 = ComputeGitBlobSha1(bytes);
        string expectedSha = repository.Readme.Sha.ToLowerInvariant();
        if (bytes.LongLength != repository.Readme.ByteSize ||
            !string.Equals(observedGitBlobSha1, expectedSha, StringComparison.Ordinal))
        {
            return new AuditCaseResult
            {
                Ordinal = ordinal,
                Rank = repository.Rank,
                RepositoryId = repository.Id,
                FullName = repository.FullName,
                CommitSha = repository.CommitSha,
                ReadmePath = repository.Readme.Path,
                ExpectedByteSize = repository.Readme.ByteSize,
                ExpectedGitBlobSha1 = expectedSha,
                ObservedByteSize = bytes.LongLength,
                ObservedGitBlobSha1 = observedGitBlobSha1,
                Status = "source-mismatch",
                Errors = ["The source object changed after the complete batch preflight."],
            };
        }

        string source = Encoding.UTF8.GetString(bytes);
        byte[] roundTrip = Encoding.UTF8.GetBytes(source);
        bool hasUtf8Bom = bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        bool utf8RoundTripMatchesBytes = bytes.AsSpan().SequenceEqual(roundTrip);

        var result = new AuditCaseResult
        {
            Ordinal = ordinal,
            Rank = repository.Rank,
            RepositoryId = repository.Id,
            FullName = repository.FullName,
            CommitSha = repository.CommitSha,
            ReadmePath = repository.Readme.Path,
            ExpectedByteSize = repository.Readme.ByteSize,
            ExpectedGitBlobSha1 = expectedSha,
            ObservedByteSize = bytes.LongLength,
            ObservedGitBlobSha1 = observedGitBlobSha1,
            RawSourceSha256 = ToLowerHex(SHA256.HashData(bytes)),
            DecodedSourceUtf16Length = source.Length,
            HasUtf8Bom = hasUtf8Bom,
            Utf8RoundTripMatchesBytes = utf8RoundTripMatchesBytes,
            Status = "parse-exception",
        };

        try
        {
            MarkdownDocument document = await engine.ParseAsync(source).ConfigureAwait(false);
            bool sourceMatches = string.Equals(document.Source, source, StringComparison.Ordinal);
            List<string> semanticFailures = ValidateSemanticSnapshot(document, source.Length);
            List<DiagnosticResult> diagnostics = document.Diagnostics
                .Select(static diagnostic => new DiagnosticResult
                {
                    Code = diagnostic.Code,
                    Severity = diagnostic.Severity.ToString(),
                    SourceStart = diagnostic.SourceSpan.Start,
                    SourceLength = diagnostic.SourceSpan.Length,
                    Message = diagnostic.Message,
                })
                .ToList();
            MermaidAuditSummary mermaid = InspectMermaid(document);

            result.Status = !sourceMatches
                ? "source-mismatch"
                : semanticFailures.Count > 0
                    ? "semantic-invariant-failure"
                    : diagnostics.Count > 0 || mermaid.FallbackCount > 0 || mermaid.UnclassifiedCount > 0
                        ? "parsed-with-diagnostics-or-fallbacks"
                        : "parsed-clean";
            result.DocumentSourceMatchesInput = sourceMatches;
            result.HeadingCount = document.GetHeadings().Count;
            result.LinkCount = document.GetLinks().Count;
            result.CodeBlockCount = document.GetCodeBlocks().Count;
            result.ImageCount = document.GetImages().Count;
            result.HeadingProjections = document.GetHeadings()
                .Select(CreateHeadingProjection)
                .ToList();
            result.CodeBlockProjections = document.GetCodeBlocks()
                .Select(CreateCodeBlockProjection)
                .ToList();
            result.LinkProjections = document.GetLinks()
                .Select(CreateLinkProjection)
                .ToList();
            result.ImageProjections = document.GetImages()
                .Select(CreateImageProjection)
                .ToList();
            result.FootnoteCount = document.GetFootnotes().Count;
            result.DefinitionItemCount = document.GetDefinitionItems().Count;
            result.AbbreviationCount = document.GetAbbreviations().Count;
            result.FragmentCount = document.GetFragments().Count;
            result.SourceMapCount = document.SourceMap.Count;
            result.HasErrors = document.HasErrors;
            result.Diagnostics = diagnostics;
            result.SemanticInvariantFailures = semanticFailures;
            result.MermaidDiagramCount = mermaid.DiagramCount;
            result.MermaidSceneCount = mermaid.SceneCount;
            result.MermaidFallbackCount = mermaid.FallbackCount;
            result.MermaidUnclassifiedCount = mermaid.UnclassifiedCount;
            result.MathFallbackDiagnosticCount = diagnostics.Count(
                static diagnostic => diagnostic.Code.StartsWith("MATH", StringComparison.Ordinal));
            result.Errors = semanticFailures;
        }
        catch (Exception exception)
        {
            result.Status = "parse-exception";
            result.Exception = CaptureException(exception);
            result.Errors = [$"ParseAsync threw {exception.GetType().FullName}: {exception.Message}"];
        }

        return result;
    }

    private static string HashUtf8(string value)
        => ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeCodeDisplayText(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
    }

    internal static HeadingProjection CreateHeadingProjection(MarkdownHeading heading)
        => new()
        {
            SourceStart = heading.SourceSpan.Start,
            SourceLength = heading.SourceSpan.Length,
            Level = heading.Level,
            DisplayTextSha256 = HashUtf8(heading.DisplayText),
            DisplayTextUtf16Length = heading.DisplayText.Length,
        };

    internal static CodeBlockProjection CreateCodeBlockProjection(MarkdownCodeBlock codeBlock)
    {
        string normalized = NormalizeCodeDisplayText(codeBlock.DisplayText);
        return new CodeBlockProjection
        {
            SourceStart = codeBlock.SourceSpan.Start,
            SourceLength = codeBlock.SourceSpan.Length,
            Language = codeBlock.Language,
            DisplayTextSha256 = HashUtf8(normalized),
            DisplayTextUtf16Length = normalized.Length,
        };
    }

    internal static LinkProjection CreateLinkProjection(MarkdownLink link)
        => new()
        {
            SourceStart = link.SourceSpan.Start,
            SourceLength = link.SourceSpan.Length,
            DisplayTextSha256 = HashUtf8(link.DisplayText),
            DisplayTextUtf16Length = link.DisplayText.Length,
            UrlSha256 = HashUtf8(link.Url),
            UrlUtf16Length = link.Url.Length,
            TitleSha256 = link.Title is null ? null : HashUtf8(link.Title),
            TitleUtf16Length = link.Title?.Length,
        };

    internal static ImageProjection CreateImageProjection(MarkdownImage image)
        => new()
        {
            SourceStart = image.SourceSpan.Start,
            SourceLength = image.SourceSpan.Length,
            DisplayTextSha256 = HashUtf8(image.DisplayText),
            DisplayTextUtf16Length = image.DisplayText.Length,
            AltTextSha256 = HashUtf8(image.AltText),
            AltTextUtf16Length = image.AltText.Length,
            UrlSha256 = HashUtf8(image.Source),
            UrlUtf16Length = image.Source.Length,
            TitleSha256 = image.Title is null ? null : HashUtf8(image.Title),
            TitleUtf16Length = image.Title?.Length,
            IsInline = image.IsInline,
        };

    private static List<string> ValidateSemanticSnapshot(MarkdownDocument document, int sourceLength)
    {
        var failures = new List<string>();
        foreach (MarkdownSourceMapEntry entry in document.SourceMap)
        {
            long start = entry.SourceSpan.Start;
            long length = entry.SourceSpan.Length;
            if (start < 0 || length < 0 || start > sourceLength || start + length > sourceLength)
            {
                failures.Add(
                    $"Source map span is outside the {sourceLength}-UTF-16-unit source: {entry.Kind} [{start}..{start + length}).");
                if (failures.Count >= 32)
                {
                    failures.Add("Further source-map invariant failures were omitted after the 32-entry cap.");
                    break;
                }
            }
        }

        return failures;
    }

    private static MermaidAuditSummary InspectMermaid(MarkdownDocument document)
    {
        var summary = new MermaidAuditSummary();
        foreach (MarkdownCodeBlock codeBlock in document.GetCodeBlocks())
        {
            if (!string.Equals(codeBlock.Language, "mermaid", StringComparison.OrdinalIgnoreCase))
                continue;

            summary.DiagramCount++;
            IReadOnlyList<MarkdownContentFragment> fragments =
                document.GetBlockExtensionContent(codeBlock.SourceSpan);
            if (fragments.Count == 0)
            {
                summary.UnclassifiedCount++;
                continue;
            }

            bool hasDiagramScene = false;
            bool hasCodeFallback = false;
            foreach (MarkdownContentFragment fragment in fragments)
            {
                foreach (MarkdownContent item in Flatten(fragment.Items))
                {
                    hasDiagramScene |= item.Kind == MarkdownContentKind.VectorScene &&
                        item.AccessibilityRole == MarkdownAccessibilityRole.Diagram;
                    hasCodeFallback |= item.Kind == MarkdownContentKind.CodeBlock &&
                        string.Equals(item.Language, "mermaid", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (hasDiagramScene)
                summary.SceneCount++;
            else if (hasCodeFallback)
                summary.FallbackCount++;
            else
                summary.UnclassifiedCount++;
        }

        return summary;
    }

    private static IEnumerable<MarkdownContent> Flatten(IReadOnlyList<MarkdownContent> items)
    {
        foreach (MarkdownContent item in items)
        {
            yield return item;
            foreach (MarkdownContent child in Flatten(item.Children))
                yield return child;
        }
    }

    internal static void AccumulateCase(AuditRunResult run, AuditCaseResult result)
    {
        run.AttemptedParseCount++;
        if (result.DocumentSourceMatchesInput.HasValue)
            run.SuccessfulParseCount++;
        run.DiagnosticCount += result.Diagnostics.Count;
        run.ErrorDiagnosticCount += result.Diagnostics.Count(
            static diagnostic => diagnostic.Severity == nameof(MarkdownDiagnosticSeverity.Error));
        run.WarningDiagnosticCount += result.Diagnostics.Count(
            static diagnostic => diagnostic.Severity == nameof(MarkdownDiagnosticSeverity.Warning));
        run.InformationDiagnosticCount += result.Diagnostics.Count(
            static diagnostic => diagnostic.Severity == nameof(MarkdownDiagnosticSeverity.Information));
        run.MermaidDiagramCount += result.MermaidDiagramCount;
        run.MermaidSceneCount += result.MermaidSceneCount;
        run.MermaidFallbackCount += result.MermaidFallbackCount;
        run.MermaidUnclassifiedCount += result.MermaidUnclassifiedCount;
        run.MathFallbackDiagnosticCount += result.MathFallbackDiagnosticCount;
        if (result.Status is "parse-exception" or "source-mismatch" or "semantic-invariant-failure")
            run.CaseFailureCount++;
    }

    internal static async Task<byte[]> ReadBoundedBytesAsync(string path, long maximumBytes, long? expectedBytes = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > maximumBytes || length > int.MaxValue ||
            (expectedBytes.HasValue && length != expectedBytes.Value))
            throw new InvalidDataException("The input file exceeds its byte bound or does not match its declared size.");
        byte[] bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The input file grew during its bounded read.");
        return bytes;
    }

    internal static string DetermineExecutionStatus(AuditRunResult run)
    {
        if (run.AttemptedParseCount != ExpectedRepositoryCount ||
            run.SuccessfulParseCount != ExpectedRepositoryCount || run.CaseFailureCount > 0 ||
            run.EngineDisposalException is not null)
        {
            return "completed-with-exceptions-or-invariant-failures";
        }

        if (run.DiagnosticCount > 0 || run.ErrorDiagnosticCount > 0 || run.MermaidFallbackCount > 0 ||
            run.MermaidUnclassifiedCount > 0 || run.MathFallbackDiagnosticCount > 0 ||
            run.WarningDiagnosticCount > 0)
        {
            return "completed-with-diagnostics-or-fallbacks";
        }

        return "completed-clean";
    }

    private static string ResolveObjectStoreRoot(string rootArgument)
    {
        string acquisitionObjectsRoot = Path.Combine(rootArgument, "objects");
        if (Directory.Exists(Path.Combine(acquisitionObjectsRoot, "git-blob-sha1")))
            return Path.GetFullPath(acquisitionObjectsRoot);
        if (Directory.Exists(Path.Combine(rootArgument, "git-blob-sha1")))
            return rootArgument;
        throw new DirectoryNotFoundException(
            "The source-object root must contain git-blob-sha1/ directly or have an objects/git-blob-sha1/ child.");
    }

    private static void EnsureOutputDoesNotOverlapInputs(
        string outputDirectory,
        string manifestPath,
        string sourceRoot)
    {
        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The frozen manifest has no parent directory.");
        if (IsWithin(outputDirectory, sourceRoot) || IsWithin(outputDirectory, manifestDirectory))
        {
            throw new IOException(
                "The fresh output directory must not be nested under the source-object root or frozen manifest directory.");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        string canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string canonicalPath = Path.GetFullPath(path);
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        return string.Equals(canonicalPath, canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            canonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex(string? value, int length)
    {
        if (value is null || value.Length != length)
            return false;
        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private static string ComputeGitBlobSha1(ReadOnlySpan<byte> bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(bytes);
        return ToLowerHex(hash.GetHashAndReset());
    }

    private static string ToLowerHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    private static ExceptionResult CaptureException(Exception exception)
        => new()
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
        };

    private sealed record MermaidAuditSummary
    {
        public int DiagramCount { get; set; }
        public int SceneCount { get; set; }
        public int FallbackCount { get; set; }
        public int UnclassifiedCount { get; set; }
    }

    internal sealed class FrozenManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("requestedCount")]
        public int RequestedCount { get; set; }

        [JsonPropertyName("repositories")]
        public List<FrozenRepository> Repositories { get; set; } = [];
    }

    internal sealed class FrozenRepository
    {
        [JsonPropertyName("rank")]
        public int Rank { get; set; }

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("fullName")]
        public string FullName { get; set; } = string.Empty;

        [JsonPropertyName("commitSha")]
        public string CommitSha { get; set; } = string.Empty;

        [JsonPropertyName("readme")]
        public FrozenReadme Readme { get; set; } = new();
    }

    internal sealed class FrozenReadme
    {
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("byteSize")]
        public long ByteSize { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;
    }

    internal sealed class AuditCaseResult
    {
        public int Ordinal { get; set; }
        public int Rank { get; set; }
        public long RepositoryId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string CommitSha { get; set; } = string.Empty;
        public string ReadmePath { get; set; } = string.Empty;
        public long ExpectedByteSize { get; set; }
        public string ExpectedGitBlobSha1 { get; set; } = string.Empty;
        public long ObservedByteSize { get; set; }
        public string ObservedGitBlobSha1 { get; set; } = string.Empty;
        public string? RawSourceSha256 { get; set; }
        public int? DecodedSourceUtf16Length { get; set; }
        public bool? HasUtf8Bom { get; set; }
        public bool? Utf8RoundTripMatchesBytes { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool? DocumentSourceMatchesInput { get; set; }
        public int? HeadingCount { get; set; }
        public int? LinkCount { get; set; }
        public int? CodeBlockCount { get; set; }
        public int? ImageCount { get; set; }
        public int? FootnoteCount { get; set; }
        public int? DefinitionItemCount { get; set; }
        public int? AbbreviationCount { get; set; }
        public int? FragmentCount { get; set; }
        public int? SourceMapCount { get; set; }
        public List<HeadingProjection> HeadingProjections { get; set; } = [];
        public List<CodeBlockProjection> CodeBlockProjections { get; set; } = [];
        public List<LinkProjection> LinkProjections { get; set; } = [];
        public List<ImageProjection> ImageProjections { get; set; } = [];
        public bool? HasErrors { get; set; }
        public int MermaidDiagramCount { get; set; }
        public int MermaidSceneCount { get; set; }
        public int MermaidFallbackCount { get; set; }
        public int MermaidUnclassifiedCount { get; set; }
        public int MathFallbackDiagnosticCount { get; set; }
        public List<DiagnosticResult> Diagnostics { get; set; } = [];
        public List<string> SemanticInvariantFailures { get; set; } = [];
        public List<string> Errors { get; set; } = [];
        public ExceptionResult? Exception { get; set; }
    }

    internal sealed class HeadingProjection
    {
        public int SourceStart { get; set; }
        public int SourceLength { get; set; }
        public int Level { get; set; }
        public string DisplayTextSha256 { get; set; } = string.Empty;
        public int DisplayTextUtf16Length { get; set; }
    }

    internal sealed class CodeBlockProjection
    {
        public int SourceStart { get; set; }
        public int SourceLength { get; set; }
        public string? Language { get; set; }
        public string DisplayTextSha256 { get; set; } = string.Empty;
        public int DisplayTextUtf16Length { get; set; }
    }

    internal sealed class LinkProjection
    {
        public int SourceStart { get; set; }
        public int SourceLength { get; set; }
        public string DisplayTextSha256 { get; set; } = string.Empty;
        public int DisplayTextUtf16Length { get; set; }
        public string UrlSha256 { get; set; } = string.Empty;
        public int UrlUtf16Length { get; set; }
        public string? TitleSha256 { get; set; }
        public int? TitleUtf16Length { get; set; }
    }

    internal sealed class ImageProjection
    {
        public int SourceStart { get; set; }
        public int SourceLength { get; set; }
        public string DisplayTextSha256 { get; set; } = string.Empty;
        public int DisplayTextUtf16Length { get; set; }
        public string AltTextSha256 { get; set; } = string.Empty;
        public int AltTextUtf16Length { get; set; }
        public string UrlSha256 { get; set; } = string.Empty;
        public int UrlUtf16Length { get; set; }
        public string? TitleSha256 { get; set; }
        public int? TitleUtf16Length { get; set; }
        public bool IsInline { get; set; }
    }

    internal sealed class DiagnosticResult
    {
        public string Code { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public int SourceStart { get; set; }
        public int SourceLength { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    internal sealed class ExceptionResult
    {
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
    }

    internal sealed class AuditRunResult
    {
        public int SchemaVersion { get; set; }
        public string Scope { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string ManifestPath { get; set; } = string.Empty;
        public string ManifestSha256 { get; set; } = string.Empty;
        public int ManifestRepositoryCount { get; set; }
        public string SourceObjectRoot { get; set; } = string.Empty;
        public string SourceObjectVerification { get; set; } = string.Empty;
        public string Utf8DecodePolicy { get; set; } = string.Empty;
        public string Utf8BomHandling { get; set; } = string.Empty;
        public string JitHubEngineComposition { get; set; } = string.Empty;
        public string VisualQualification { get; set; } = string.Empty;
        public string CasesFile { get; set; } = string.Empty;
        public string EngineProfileId { get; set; } = string.Empty;
        public string[] EngineExtensionIds { get; set; } = [];
        public int ParseMaximumSourceLength { get; set; }
        public int AttemptedParseCount { get; set; }
        public int SuccessfulParseCount { get; set; }
        public Dictionary<string, string> BinarySha256 { get; set; } = new(StringComparer.Ordinal);
        public int CaseFailureCount { get; set; }
        public int DiagnosticCount { get; set; }
        public int ErrorDiagnosticCount { get; set; }
        public int WarningDiagnosticCount { get; set; }
        public int InformationDiagnosticCount { get; set; }
        public int MermaidDiagramCount { get; set; }
        public int MermaidSceneCount { get; set; }
        public int MermaidFallbackCount { get; set; }
        public int MermaidUnclassifiedCount { get; set; }
        public int MathFallbackDiagnosticCount { get; set; }
        public ExceptionResult? EngineDisposalException { get; set; }
        public string ExecutionStatus { get; set; } = string.Empty;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = false)]
[JsonSerializable(typeof(Program.FrozenManifest))]
[JsonSerializable(typeof(Program.AuditCaseResult))]
[JsonSerializable(typeof(Program.AuditRunResult))]
[JsonSerializable(typeof(List<Program.HeadingProjection>))]
[JsonSerializable(typeof(List<Program.CodeBlockProjection>))]
[JsonSerializable(typeof(List<Program.LinkProjection>))]
[JsonSerializable(typeof(List<Program.ImageProjection>))]
internal partial class ReadmeAuditJsonContext : JsonSerializerContext
{
}
