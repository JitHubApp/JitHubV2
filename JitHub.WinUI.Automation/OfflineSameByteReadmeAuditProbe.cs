using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class ReadmeAuditProbe
{
    private const int OfflineManifestMaximumBytes = 4 * 1024 * 1024;
    private const int OfflineBrowserReportMaximumBytes = 4 * 1024 * 1024;
    private const int OfflineCorpusMaximumFiles = 15_016;
    private const int OfflineCorpusMaximumEntries = 16_032;
    private const long OfflineCorpusMaximumBytes = 308L * 1024 * 1024;
    private const long OfflineCorpusMaximumFileBytes = 64L * 1024 * 1024;

    private static readonly string[] OfflineGitHubCredentialEnvironmentNames =
    [
        "JITHUB_README_AUDIT_GITHUB_TOKEN",
        "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID",
        "GITHUB_TOKEN",
        "GH_TOKEN",
        "GH_ENTERPRISE_TOKEN",
        "GITHUB_ENTERPRISE_TOKEN",
        "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
        "ACTIONS_ID_TOKEN_REQUEST_URL",
        "GITHUB_APP_TOKEN",
        "GITHUB_ACCESS_TOKEN",
        "GITHUB_CLIENT_SECRET",
        "GITHUB_APP_PRIVATE_KEY",
        "GH_ACCESS_TOKEN",
        "GH_CONFIG_DIR",
        "JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY",
    ];

    internal static void RunOfflineSameByte(CaptureOptions options)
    {
        ClearOfflineGitHubCredentialEnvironment();
        if (options.AuditResume || options.AuditReuseBrowserEvidence || options.AuditCaptureSameByteCorpus)
        {
            throw new InvalidOperationException(
                "Offline same-byte replay accepts only the immutable captured corpus; live capture/resume switches are not supported.");
        }

        string manifestPath = options.AuditManifestPath
            ?? throw new ArgumentException("Offline same-byte replay requires --manifest=<pinned-top500.json>.");
        string captureRoot = options.AuditOfflineSameByteCorpusRoot
            ?? throw new ArgumentException(
                "Offline same-byte replay requires --offline-same-byte-corpus-root=<captured-run-root>.");
        if (options.AuditStartRank <= 0 || options.AuditCount <= 0 ||
            options.AuditStartRank > 500 || options.AuditCount > 500 ||
            checked(options.AuditStartRank + options.AuditCount - 1) > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "Offline same-byte rank range must stay within 1..500.");
        }

        ReadmeAuditManifest manifest = ReadOfflinePinnedManifest(manifestPath, out string manifestSha256);
        ReadmeAuditRepository[] selected = manifest.Repositories
            .Where(repository => repository.Rank >= options.AuditStartRank &&
                repository.Rank < options.AuditStartRank + options.AuditCount)
            .OrderBy(repository => repository.Rank)
            .ToArray();
        if (selected.Length != options.AuditCount ||
            selected.Where((repository, index) => repository.Rank != options.AuditStartRank + index).Any())
        {
            throw new InvalidDataException(
                "The pinned manifest does not contain every requested rank exactly once.");
        }

        string fullCaptureRoot = RequireOrdinaryDirectory(captureRoot, "offline corpus root");
        string runDirectory = Path.Combine(
            options.OutputDirectory,
            "offline-same-byte",
            $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        var cases = new List<OfflineSameByteCaseResult>(selected.Length);
        foreach (ReadmeAuditRepository repository in selected)
        {
            string caseId = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
            string outputCaseDirectory = Path.Combine(runDirectory, "cases", caseId);
            Directory.CreateDirectory(outputCaseDirectory);
            var failures = new List<string>();
            BrowserAuditResult? browser = null;
            NativeAuditResult? native = null;
            ReadmeAuditComparison? comparison = null;
            string status;

            try
            {
                if (!repository.Readme.Available)
                {
                    native = RunNativeAudit(
                        options,
                        repository,
                        outputCaseDirectory,
                        accessToken: string.Empty,
                        renderedBrowserImages: null,
                        sameByteCorpusPath: null,
                        expectedReadmeSha256: null,
                        expectedReadmeBytes: null,
                        expectRenderedReadme: false,
                        removeGitHubCredentials: true,
                        offlineSameBytePublicAudit: true,
                        pinnedManifestPath: manifestPath,
                        pinnedManifestRank: repository.Rank,
                        pinnedManifestSha256: manifestSha256);
                    AddNativeFailures(native, failures);
                    status = failures.Count == 0 ? "not-applicable-no-readme" : "failed";
                }
                else
                {
                    BrowserAuditResult capturedBrowser = ReadOfflineBrowserCapture(
                        fullCaptureRoot, repository);
                    string sourceCaseDirectory = Path.Combine(fullCaptureRoot, "cases", caseId);
                    string sourceBrowserDirectory = Path.Combine(sourceCaseDirectory, "browser");
                    string originalCorpusDirectory = GetSameByteCorpusDirectory(
                        sourceCaseDirectory,
                        repository,
                        capturedBrowser.SameByteCorpus,
                        capturedBrowser.SameByteHtmlReplay,
                        capturedBrowser.ReadmeRendered);

                    string browserOutputDirectory = Path.Combine(outputCaseDirectory, "browser");
                    Directory.CreateDirectory(browserOutputDirectory);
                    string copiedCorpusDirectory = Path.Combine(browserOutputDirectory, "same-byte-corpus-input");
                    CopyBoundedOrdinaryDirectory(originalCorpusDirectory, copiedCorpusDirectory);
                    BrowserSameByteCorpusEvidence copiedEvidence = new()
                    {
                        Manifest = Path.Combine("same-byte-corpus-input", "manifest.json"),
                        ManifestSha256 = capturedBrowser.SameByteCorpus!.ManifestSha256,
                        ReadmeSha256 = capturedBrowser.SameByteCorpus.ReadmeSha256,
                        ReadmeBytes = capturedBrowser.SameByteCorpus.ReadmeBytes,
                        AssetCount = capturedBrowser.SameByteCorpus.AssetCount,
                        AssetBytes = capturedBrowser.SameByteCorpus.AssetBytes,
                    };

                    // The standard native runner opens the real JitHub repository
                    // page and substitutes only the exact admitted README/assets
                    // through the production same-byte resolver. No GitHub token
                    // or account partition is inherited or supplied.
                    native = RunNativeAudit(
                        options,
                        repository,
                        outputCaseDirectory,
                        accessToken: string.Empty,
                        renderedBrowserImages: null,
                        sameByteCorpusPath: copiedCorpusDirectory,
                        expectedReadmeSha256: copiedEvidence.ReadmeSha256,
                        expectedReadmeBytes: copiedEvidence.ReadmeBytes,
                        expectRenderedReadme: capturedBrowser.ReadmeRendered is not false,
                        removeGitHubCredentials: true,
                        offlineSameBytePublicAudit: true,
                        pinnedManifestPath: manifestPath,
                        pinnedManifestRank: repository.Rank,
                        pinnedManifestSha256: manifestSha256);
                    AddNativeFailures(native, failures);
                    if (!string.Equals(
                            native.ReadmeSourceSha256,
                            copiedEvidence.ReadmeSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add("JitHub did not render the exact pinned README bytes in the offline case.");
                    }

                    browser = new BrowserAuditResult
                    {
                        SchemaVersion = 5,
                        ReadmeSha = repository.Readme.Sha,
                        ReadmeRendered = capturedBrowser.ReadmeRendered,
                        SameByteCorpus = copiedEvidence,
                        SameByteHtmlReplay = capturedBrowser.SameByteHtmlReplay,
                        Semantic = new BrowserSemantic(),
                        Tiles = [],
                    };
                    if (capturedBrowser.ReadmeRendered is false)
                    {
                        browser.SameByteReplay = new BrowserSameByteReplayEvidence
                        {
                            SchemaVersion = 2,
                            Status = "not-applicable",
                            Reason = "github-source-view",
                        };
                        status = failures.Count == 0 ? "not-applicable-source-view" : "failed";
                    }
                    else
                    {
                        browser.SameByteReplay = RunSourceBoundEdgeReplay(
                            options,
                            repository,
                            outputCaseDirectory,
                            copiedCorpusDirectory,
                            copiedEvidence,
                            capturedBrowser.SameByteHtmlReplay,
                            native,
                            capturedGitHubArticleSemantic: null);

                        comparison = Compare(browser, native, outputCaseDirectory);
                        AddComparisonFailures(comparison, failures);
                        status = failures.Count == 0 ? "passed" : "failed";
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(RedactUrls(exception.Message));
                status = "failed";
            }

            OfflineSameByteCaseResult result = new()
            {
                Rank = repository.Rank,
                FullName = repository.FullName,
                CommitSha = repository.CommitSha,
                ReadmeSha = repository.Readme.Available ? repository.Readme.Sha : "absent",
                Status = status,
                Failures = failures,
                Browser = SanitizeBrowserAuditResult(browser),
                Native = SanitizeNativeAuditResult(native),
                Comparison = comparison,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            };
            WriteJson(Path.Combine(outputCaseDirectory, "offline-case-result.json"), result);
            cases.Add(result);
            Console.WriteLine(
                $"Offline same-byte README {repository.Rank}/500: {repository.FullName} ({status}).");
        }

        OfflineSameByteAggregate performance = EvaluateOfflineSameBytePerformance(
            manifest, selected, cases, options.AuditStartRank, options.AuditCount);
        string[] failedCases = cases
            .Where(result => result.Status == "failed")
            .Select(result => $"rank {result.Rank} ({result.FullName}) failed")
            .ToArray();
        bool isFullTop500 = options.AuditStartRank == 1 && options.AuditCount == 500 &&
            selected.Length == manifest.Repositories.Count;
        var report = new
        {
            schemaVersion = 1,
            scope = "offline-same-byte-pinned-corpus",
            executionStatus = failedCases.Length == 0 && performance.Failures.Count == 0
                ? "completed-within-offline-scope"
                : "failed",
            overallReleaseQualification = "not-qualified",
            liveGitHubArticleSemanticComparison = "not-run",
            liveTop500RankingFreshness = "not-checked",
            authenticatedLiveAuditEquivalent = false,
            rankingQuery = manifest.Query,
            corpusGeneratedAtUtc = manifest.GeneratedAtUtc,
            pinnedManifestSha256 = manifestSha256,
            requestedStartRank = options.AuditStartRank,
            requestedCount = options.AuditCount,
            completedCaseCount = cases.Count,
            failedCaseCount = failedCases.Length,
            notApplicableNoReadmeCount = cases.Count(result => result.Status == "not-applicable-no-readme"),
            notApplicableSourceViewCount = cases.Count(result => result.Status == "not-applicable-source-view"),
            liveGitHubComparison = "not-run",
            performanceAggregate = new
            {
                status = performance.Evaluated
                    ? performance.Failures.Count == 0 ? "evaluated" : "failed"
                    : "not-evaluated-partial-selection",
                evaluated = performance.Evaluated,
                ratioReference = "same-byte Edge",
                expectedRenderedReadmeCount = performance.ExpectedRenderedReadmeCount,
                firstViewportRatioCount = performance.FirstViewportRatios.Count,
                fullTraversalRatioCount = performance.FullTraversalRatios.Count,
                firstViewportP95 = Percentile(
                    performance.FirstViewportRatios.OrderBy(value => value).ToArray(), 0.95),
                fullTraversalP95 = Percentile(
                    performance.FullTraversalRatios.OrderBy(value => value).ToArray(), 0.95),
                maximumRatio = 1.10,
                failures = performance.Failures,
            },
            failures = failedCases.Concat(performance.Failures).ToArray(),
            cases = cases,
            completedAtUtc = DateTimeOffset.UtcNow,
        };
        string reportPath = Path.Combine(runDirectory, "offline-same-byte-summary.json");
        WriteJson(reportPath, report);
        Console.WriteLine($"Offline same-byte report: {reportPath}");
        Console.WriteLine($"Offline same-byte scope: {options.AuditStartRank}..{options.AuditStartRank + options.AuditCount - 1}; " +
            $"release qualification is not-qualified; full top-500 selection={isFullTop500}.");

        if (failedCases.Length != 0 || performance.Failures.Count != 0)
        {
            throw new InvalidOperationException(
                $"Offline same-byte replay failed for {failedCases.Length} case(s) and {performance.Failures.Count} aggregate gate(s). " +
                "The separate offline report is diagnostic only and does not qualify the live top-500 release gate.");
        }
    }

    private static ReadmeAuditManifest ReadOfflinePinnedManifest(
        string manifestPath,
        out string manifestSha256)
    {
        FileInfo info = new(manifestPath);
        if (!info.Exists || info.Length is <= 0 or > OfflineManifestMaximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Offline pinned manifest is missing, redirected, or oversized.");
        }

        byte[] bytes = File.ReadAllBytes(manifestPath);
        manifestSha256 = HashSha256(bytes);
        ReadmeAuditManifest manifest = JsonSerializer.Deserialize<ReadmeAuditManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Offline pinned manifest is empty.");
        if (manifest.SchemaVersion != 2 || manifest.Repositories.Count != 500 ||
            manifest.GeneratedAtUtc == default || string.IsNullOrWhiteSpace(manifest.Query))
        {
            throw new InvalidDataException("Offline replay requires the complete schema-v2 pinned top-500 manifest.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadmeAuditRepository[] orderedRepositories = manifest.Repositories
            .OrderBy(item => item.Rank)
            .ToArray();
        for (int index = 0; index < orderedRepositories.Length; index++)
        {
            if (orderedRepositories[index].Rank != index + 1)
            {
                throw new InvalidDataException("Offline top-500 manifest ranks must be unique and exactly 1..500.");
            }
        }
        foreach (ReadmeAuditRepository repository in manifest.Repositories)
        {
            string[] nameParts = repository.FullName.Split('/');
            if (repository.Rank is < 1 or > 500 ||
                !names.Add(repository.FullName) ||
                nameParts.Length != 2 || nameParts.Any(part =>
                    part.Length is < 1 or > 100 || part.Any(character =>
                        !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))) ||
                !Regex.IsMatch(repository.CommitSha, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant) ||
                !Uri.TryCreate(repository.Url, UriKind.Absolute, out Uri? repositoryUri) ||
                repositoryUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Offline manifest contains an invalid or duplicate pinned repository identity.");
            }

            if (repository.Readme.Available)
            {
                if (!Regex.IsMatch(repository.Readme.Sha, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant) ||
                    repository.Readme.ByteSize is <= 0 or > 16 * 1024 * 1024 ||
                    string.IsNullOrWhiteSpace(repository.Readme.Path) ||
                    repository.Readme.Path.StartsWith('/') ||
                    repository.Readme.Path.Contains('\\') ||
                    repository.Readme.Path.Split('/').Any(segment => segment is "" or "." or ".."))
                {
                    throw new InvalidDataException("Offline manifest contains an invalid pinned README identity.");
                }
            }
            else if (repository.Readme.ByteSize != 0 ||
                !string.IsNullOrEmpty(repository.Readme.Sha) ||
                !string.IsNullOrEmpty(repository.Readme.Path))
            {
                throw new InvalidDataException("Offline manifest has inconsistent absent-README metadata.");
            }
        }

        return manifest;
    }

    private static BrowserAuditResult ReadOfflineBrowserCapture(
        string captureRoot,
        ReadmeAuditRepository repository)
    {
        string caseId = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
        string browserDirectory = Path.Combine(captureRoot, "cases", caseId, "browser");
        _ = RequireOrdinaryDirectory(browserDirectory, "captured browser evidence directory");
        string reportPath = Path.Combine(browserDirectory, "browser.json");
        FileInfo info = new(reportPath);
        if (!info.Exists || info.Length is <= 0 or > OfflineBrowserReportMaximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Captured same-byte browser report is missing, redirected, or oversized.");
        }

        BrowserAuditResult captured = JsonSerializer.Deserialize<BrowserAuditResult>(
            File.ReadAllBytes(reportPath), JsonOptions)
            ?? throw new InvalidDataException("Captured same-byte browser report is empty.");
        if (captured.SchemaVersion != 5 || captured.ReadmeRendered is null ||
            !string.Equals(captured.ReadmeSha, repository.Readme.Sha, StringComparison.OrdinalIgnoreCase) ||
            captured.SameByteCorpus is null || captured.SameByteHtmlReplay is null)
        {
            throw new InvalidDataException("Captured browser report does not bind the requested README to a complete corpus.");
        }

        return captured;
    }

    private static string RequireOrdinaryDirectory(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        DirectoryInfo directory = new(fullPath);
        directory.Refresh();
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} is missing or redirected.");
        return fullPath;
    }

    private static void CopyBoundedOrdinaryDirectory(string sourcePath, string destinationPath)
    {
        string sourceRoot = RequireOrdinaryDirectory(sourcePath, "same-byte corpus directory");
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
            throw new IOException("Offline same-byte corpus destination already exists.");
        Directory.CreateDirectory(destinationPath);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(sourceRoot));
        int fileCount = 0;
        int entryCount = 0;
        long copiedBytes = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo sourceDirectory = pending.Pop();
            string relativeDirectory = Path.GetRelativePath(sourceRoot, sourceDirectory.FullName);
            string destinationDirectory = relativeDirectory == "."
                ? destinationPath
                : Path.Combine(destinationPath, relativeDirectory);
            Directory.CreateDirectory(destinationDirectory);
            foreach (FileSystemInfo entry in sourceDirectory.GetFileSystemInfos())
            {
                if (++entryCount > OfflineCorpusMaximumEntries)
                    throw new InvalidDataException("Same-byte corpus exceeds its immutable filesystem-entry budget.");

                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Same-byte corpus contains a redirected file or directory.");

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                    continue;
                }
                if (entry is not FileInfo sourceFile)
                    throw new InvalidDataException("Same-byte corpus contains an unsupported filesystem entry.");

                sourceFile.Refresh();
                long sourceLength = sourceFile.Length;
                if (sourceLength is < 0 or > OfflineCorpusMaximumFileBytes ||
                    ++fileCount > OfflineCorpusMaximumFiles ||
                    checked(copiedBytes + sourceLength) > OfflineCorpusMaximumBytes)
                {
                    throw new InvalidDataException("Same-byte corpus exceeds its immutable file or byte transfer budget.");
                }

                string destinationFile = Path.Combine(destinationDirectory, sourceFile.Name);
                using FileStream input = new(
                    sourceFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, FileOptions.SequentialScan);
                using FileStream output = new(
                    destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 64 * 1024, FileOptions.SequentialScan);
                if (input.Length != sourceLength)
                    throw new InvalidDataException("Same-byte corpus file changed before it was copied.");
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
                if (input.Length != sourceLength || output.Length != sourceLength)
                    throw new InvalidDataException("Same-byte corpus file changed while it was copied.");
                copiedBytes = checked(copiedBytes + sourceLength);
            }
        }
    }

    private static void AddNativeFailures(NativeAuditResult native, List<string> failures)
    {
        if (native.UnavailableImages != 0)
            failures.Add($"JitHub reported {native.UnavailableImages} unavailable image(s).");
        if (!string.IsNullOrWhiteSpace(native.RenderFailure))
            failures.Add("JitHub raised a renderer exception.");
        if (!native.CleanExit)
            failures.Add("JitHub did not close cleanly after the offline case.");
        if (native.LoadingImagesAfterTraversal != 0)
            failures.Add($"JitHub still exposed {native.LoadingImagesAfterTraversal} loading image(s) after traversal.");
        if (native.ImageTransportDiagnosticsEnabled)
            failures.Add("Image transport diagnostics were enabled; the case is excluded from performance evidence.");
    }

    private static void AddComparisonFailures(ReadmeAuditComparison comparison, List<string> failures)
    {
        if (comparison.TextTokenCoverage < 0.985)
            failures.Add($"Same-byte visible text coverage was {comparison.TextTokenCoverage:P2}, below 98.5%.");
        if (comparison.VisualStructureScore < 0.95)
            failures.Add($"Same-byte full-page structural fidelity was {comparison.VisualStructureScore:P2}, below 95%.");
        if (comparison.SourceBoundLayoutExtentRatio is not double extentRatio ||
            extentRatio < 0.90 || extentRatio > 1.10)
        {
            failures.Add("Same-byte native/Edge page-height ratio was unavailable or outside 0.90–1.10.");
        }
        if (comparison.NativeImageSourceCount < comparison.BrowserDistinctAtomicMediaCount)
        {
            failures.Add(
                $"JitHub audited {comparison.NativeImageSourceCount} distinct image/media sources, " +
                $"but same-byte Edge rendered {comparison.BrowserDistinctAtomicMediaCount}.");
        }
        if (comparison.SameByteTileCoverage is not double tileCoverage || tileCoverage < 1.0 ||
            comparison.SameByteMeanTileSsim is not double meanSsim || !double.IsFinite(meanSsim) ||
            comparison.SameByteMinimumTileSsim is not double minimumSsim || !double.IsFinite(minimumSsim))
        {
            failures.Add("Same-byte visual comparison did not cover every native viewport.");
        }
    }

    private static OfflineSameByteAggregate EvaluateOfflineSameBytePerformance(
        ReadmeAuditManifest manifest,
        IReadOnlyList<ReadmeAuditRepository> selected,
        IReadOnlyList<OfflineSameByteCaseResult> cases,
        int startRank,
        int count)
    {
        OfflineSameByteCaseResult[] rendered = cases
            .Where(result => result.Status == "passed" && result.Comparison is not null)
            .ToArray();
        double[] firstRatios = rendered
            .Select(result => result.Comparison!.NativeToSameByteFirstRenderRatio)
            .Where(value => value.HasValue && IsFinitePositive(value.Value))
            .Select(value => value!.Value)
            .ToArray();
        double[] fullRatios = rendered
            .Select(result => result.Comparison!.NativeToSameByteFullPageRatio)
            .Where(value => value.HasValue && IsFinitePositive(value.Value))
            .Select(value => value!.Value)
            .ToArray();
        int expectedRenderedCount = cases.Count(result => result.Browser is { ReadmeRendered: not false });
        bool fullTop500 = startRank == 1 && count == 500 && selected.Count == manifest.Repositories.Count;
        var failures = new List<string>();
        bool complete = firstRatios.Length == expectedRenderedCount && fullRatios.Length == expectedRenderedCount;
        if (fullTop500 && !complete)
            failures.Add($"Same-byte performance ratios were incomplete ({firstRatios.Length}/{expectedRenderedCount} first, {fullRatios.Length}/{expectedRenderedCount} full).");
        double firstP95 = Percentile(firstRatios.OrderBy(value => value).ToArray(), 0.95);
        double fullP95 = Percentile(fullRatios.OrderBy(value => value).ToArray(), 0.95);
        if (fullTop500 && complete && firstP95 > 1.10)
            failures.Add($"Native first-viewport p95 against same-byte Edge was {firstP95:P1}, above 110%.");
        if (fullTop500 && complete && fullP95 > 1.10)
            failures.Add($"Native full-traversal p95 against same-byte Edge was {fullP95:P1}, above 110%.");
        if (fullTop500)
        {
            foreach (int sentinelRank in new[] { 2, 5, 8, 22, 26, 60, 102, 149, 153, 201, 203, 295, 395, 476 })
            {
                OfflineSameByteCaseResult? sentinel = cases.FirstOrDefault(result => result.Rank == sentinelRank);
                double? first = sentinel?.Comparison?.NativeToSameByteFirstRenderRatio;
                double? full = sentinel?.Comparison?.NativeToSameByteFullPageRatio;
                if (first is not double firstRatio || !IsFinitePositive(firstRatio) || firstRatio > 1.10)
                    failures.Add($"Same-byte first-viewport performance sentinel rank {sentinelRank} was missing or above 110%.");
                if (full is not double fullRatio || !IsFinitePositive(fullRatio) || fullRatio > 1.10)
                    failures.Add($"Same-byte full-traversal performance sentinel rank {sentinelRank} was missing or above 110%.");
            }
        }
        return new OfflineSameByteAggregate(
            fullTop500,
            expectedRenderedCount,
            firstRatios,
            fullRatios,
            failures);
    }

    private static void ClearOfflineGitHubCredentialEnvironment()
    {
        foreach (string name in OfflineGitHubCredentialEnvironmentNames)
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        if (OfflineGitHubCredentialEnvironmentNames.Any(name =>
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process))))
        {
            throw new InvalidOperationException("GitHub audit credentials could not be removed from the offline runner environment.");
        }
    }

    private static void RemoveOfflineGitHubCredentialEnvironment(ProcessStartInfo startInfo)
    {
        foreach (string name in OfflineGitHubCredentialEnvironmentNames)
            startInfo.Environment.Remove(name);
    }

    private sealed record OfflineSameByteAggregate(
        bool Evaluated,
        int ExpectedRenderedReadmeCount,
        IReadOnlyList<double> FirstViewportRatios,
        IReadOnlyList<double> FullTraversalRatios,
        IReadOnlyList<string> Failures);

    private sealed class OfflineSameByteCaseResult
    {
        public int Rank { get; init; }
        public string FullName { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public string ReadmeSha { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public IReadOnlyList<string> Failures { get; init; } = [];
        public BrowserAuditResult? Browser { get; init; }
        public NativeAuditResult? Native { get; init; }
        public ReadmeAuditComparison? Comparison { get; init; }
        public DateTimeOffset CompletedAtUtc { get; init; }
    }
}
