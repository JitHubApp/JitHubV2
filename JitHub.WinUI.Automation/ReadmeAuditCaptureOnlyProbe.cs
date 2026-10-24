using System.Text.Json;

internal static partial class ReadmeAuditProbe
{
    private const int PublicCaptureMaximumBrowserReportBytes = 4 * 1024 * 1024;

    internal static void RunPublicSameByteCapture(CaptureOptions options)
    {
        // This mode captures only the public pinned GitHub page and exact source
        // bytes. It deliberately has no app launch, native comparison, token, or
        // account partition. Remove inherited audit credentials before starting
        // Node/Edge and verify the process environment is clean.
        ClearOfflineGitHubCredentialEnvironment();
        if (!string.Equals(options.Probe, PublicSameByteCaptureProbeName, StringComparison.OrdinalIgnoreCase) ||
            !options.AuditCaptureSameByteCorpus || options.AuditResume || options.AuditReuseBrowserEvidence ||
            options.AuditOfflineSameByteCorpusRoot is not null ||
            !string.IsNullOrWhiteSpace(options.AppPath))
        {
            throw new InvalidOperationException("Public same-byte capture requires its isolated browser-only option set.");
        }

        if (options.AuditStartRank is < 1 or > 500 || options.AuditCount is < 1 or > 500 ||
            checked(options.AuditStartRank + options.AuditCount - 1) > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Public same-byte capture rank range must stay within 1..500.");
        }

        string repositoryRoot = FindRepositoryRoot();
        string manifestPath = options.AuditManifestPath
            ?? throw new ArgumentException("Public same-byte capture requires --manifest=<pinned-top500.json>.");
        string canonicalManifestPath = Path.Combine(repositoryRoot, "eng", "readme-audit", "pinned-top500.json");
        ReadmeAuditManifest manifest = ReadOfflinePinnedManifest(manifestPath, out string manifestSha256);
        _ = ReadOfflinePinnedManifest(canonicalManifestPath, out string canonicalManifestSha256);
        if (!string.Equals(manifestSha256, canonicalManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Public capture accepts only the exact repository-pinned top-500 manifest bytes.");
        }

        ReadmeAuditRepository[] selected = manifest.Repositories
            .Where(repository => repository.Rank >= options.AuditStartRank &&
                repository.Rank < options.AuditStartRank + options.AuditCount)
            .OrderBy(repository => repository.Rank)
            .ToArray();
        if (selected.Length != options.AuditCount ||
            selected.Where((repository, index) => repository.Rank != options.AuditStartRank + index).Any())
        {
            throw new InvalidDataException("The pinned manifest does not contain every requested rank exactly once.");
        }

        string expectedBrowserScript = Path.GetFullPath(Path.Combine(repositoryRoot, "eng", "readme-audit", "browser-oracle.mjs"));
        string browserScript = options.AuditBrowserScriptPath
            ?? throw new ArgumentException("Public same-byte capture requires --browser-script=<browser-oracle.mjs>.");
        if (!string.Equals(Path.GetFullPath(browserScript), expectedBrowserScript, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Public capture must use the repository-pinned browser oracle.");
        }
        RequirePinnedReadmeAuditFile(browserScript, HashSha256(File.ReadAllBytes(browserScript)), 16 * 1024 * 1024,
            "repository browser oracle");

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        _ = RequireOrdinaryDirectory(outputDirectory, "public same-byte capture output directory");
        string batchDirectory = Path.Combine(outputDirectory, "capture-batches");
        Directory.CreateDirectory(batchDirectory);
        _ = RequireOrdinaryDirectory(batchDirectory, "public same-byte capture batch directory");
        string casesDirectory = Path.Combine(outputDirectory, "cases");
        Directory.CreateDirectory(casesDirectory);
        _ = RequireOrdinaryDirectory(casesDirectory, "public same-byte capture cases directory");
        string batchReportPath = Path.Combine(
            batchDirectory,
            $"ranks-{options.AuditStartRank:D3}-{options.AuditStartRank + options.AuditCount - 1:D3}.json");
        if (File.Exists(batchReportPath))
        {
            throw new IOException("Capture batch evidence already exists; choose a fresh output root to preserve prior evidence.");
        }

        var caseResults = new List<PublicCaptureCaseResult>(selected.Length);
        int availableReadmeCount = 0;
        int sourceViewCount = 0;
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        foreach (ReadmeAuditRepository repository in selected)
        {
            string caseId = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
            string caseDirectory = Path.Combine(outputDirectory, "cases", caseId);
            Console.WriteLine($"Public same-byte capture {repository.Rank}/500: {repository.FullName}");
            try
            {
                if (Directory.Exists(caseDirectory) || File.Exists(caseDirectory))
                {
                    throw new IOException("Capture case already exists; refusing to overwrite or reuse earlier browser evidence.");
                }

                Directory.CreateDirectory(caseDirectory);
                _ = RequireOrdinaryDirectory(caseDirectory, "public same-byte capture case directory");
                BrowserAuditResult browser = RunBrowserOracle(options, browserScript, repository, caseDirectory);
                if (browser.SchemaVersion != 5 || browser.ReadmeRendered is null ||
                    !string.Equals(browser.RepositoryUrl, GetSnapshotUrl(repository), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(browser.ReadmeSha, repository.Readme.Available ? repository.Readme.Sha : "absent",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Edge browser report does not match the exact pinned repository snapshot.");
                }
                string? corpusManifestSha256 = null;
                long corpusByteCount = 0;
                if (repository.Readme.Available)
                {
                    if (!string.Equals(browser.ReadmeSha, repository.Readme.Sha, StringComparison.OrdinalIgnoreCase) ||
                        browser.ReadmeRendered is null || browser.SameByteCorpus is null || browser.SameByteHtmlReplay is null)
                    {
                        throw new InvalidDataException("Edge capture did not bind a browser report and same-byte corpus to the pinned README.");
                    }

                    string corpusDirectory = GetSameByteCorpusDirectory(
                        caseDirectory,
                        repository,
                        browser.SameByteCorpus,
                        browser.SameByteHtmlReplay,
                        browser.ReadmeRendered);
                    string corpusManifestPath = Path.Combine(corpusDirectory, "manifest.json");
                    corpusManifestSha256 = HashSha256(File.ReadAllBytes(corpusManifestPath));
                    corpusByteCount = GetBoundedCorpusByteCount(corpusDirectory);
                    availableReadmeCount++;
                    if (browser.ReadmeRendered is false) sourceViewCount++;
                }
                else if (browser.ReadmeRendered is not false ||
                    !string.Equals(browser.ReadmeSha, "absent", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Edge capture disagrees with the pinned manifest's absent-README entry.");
                }

                string browserReportPath = Path.Combine(caseDirectory, "browser", "browser.json");
                FileInfo browserReportInfo = new(browserReportPath);
                if (!browserReportInfo.Exists || browserReportInfo.Length is <= 0 or > PublicCaptureMaximumBrowserReportBytes ||
                    (browserReportInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Edge browser report is missing, redirected, or oversized.");
                }

                var result = new PublicCaptureCaseResult
                {
                    Rank = repository.Rank,
                    FullName = repository.FullName,
                    CommitSha = repository.CommitSha,
                    ReadmeSha = repository.Readme.Available ? repository.Readme.Sha : "absent",
                    ReadmeAvailable = repository.Readme.Available,
                    ReadmeRendered = browser.ReadmeRendered,
                    BrowserReportSha256 = HashSha256(File.ReadAllBytes(browserReportPath)),
                    CorpusManifestSha256 = corpusManifestSha256,
                    CorpusBytes = corpusByteCount,
                    Status = "captured",
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                };
                WriteJson(Path.Combine(caseDirectory, "capture.json"), result);
                caseResults.Add(result);
            }
            catch (Exception exception)
            {
                var result = new PublicCaptureCaseResult
                {
                    Rank = repository.Rank,
                    FullName = repository.FullName,
                    CommitSha = repository.CommitSha,
                    ReadmeSha = repository.Readme.Available ? repository.Readme.Sha : "absent",
                    ReadmeAvailable = repository.Readme.Available,
                    Status = "failed",
                    Failure = RedactUrls(exception.GetType().Name + ": " + exception.Message),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                };
                if (Directory.Exists(caseDirectory) && !File.Exists(Path.Combine(caseDirectory, "capture.json")))
                {
                    WriteJson(Path.Combine(caseDirectory, "capture.json"), result);
                }
                caseResults.Add(result);
                Console.Error.WriteLine($"Public same-byte capture failed at rank {repository.Rank}: {exception.GetType().Name}: {RedactUrls(exception.Message)}");
            }
        }

        bool passed = caseResults.Count == options.AuditCount && caseResults.All(result => result.Status == "captured");
        var report = new PublicCaptureBatchReport
        {
            SchemaVersion = 1,
            Scope = "public-pinned-same-byte-browser-capture-in-existing-windows-sandbox",
            ExecutionStatus = passed ? "captured-within-browser-only-scope" : "failed",
            OverallReleaseQualification = "not-qualified",
            AuthenticatedLiveAuditEquivalent = false,
            LiveTop500RankingFreshness = "not-checked",
            NativeJitHubRenderComparison = "not-run",
            SameByteEdgeFullPageReplay = "not-run",
            PinnedManifestSha256 = manifestSha256,
            RequestedStartRank = options.AuditStartRank,
            RequestedCount = options.AuditCount,
            CapturedCaseCount = caseResults.Count(result => result.Status == "captured"),
            FailedCaseCount = caseResults.Count(result => result.Status != "captured"),
            AvailableReadmeCount = availableReadmeCount,
            SourceViewCount = sourceViewCount,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = caseResults,
        };
        WriteJson(batchReportPath, report);
        Console.WriteLine(
            $"Public same-byte capture batch: {report.CapturedCaseCount}/{report.RequestedCount} captured; " +
            $"release qualification={report.OverallReleaseQualification}; report={batchReportPath}.");
        if (!passed)
        {
            throw new InvalidOperationException(
                $"Public same-byte capture failed {report.FailedCaseCount}/{report.RequestedCount} pinned case(s); partial evidence was preserved at '{outputDirectory}'.");
        }
    }

    private static long GetBoundedCorpusByteCount(string corpusDirectory)
    {
        const int maximumFiles = 15_016;
        const long maximumBytes = 308L * 1024 * 1024;
        long total = 0;
        int files = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(corpusDirectory));
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Captured corpus contains a missing or redirected directory.");
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Captured corpus contains a redirected entry.");
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }

                if (entry is not FileInfo file || ++files > maximumFiles || file.Length > 64L * 1024 * 1024 ||
                    file.Length > maximumBytes - total)
                    throw new InvalidDataException("Captured corpus exceeds its bounded file or byte budget.");
                total += file.Length;
            }
        }
        return total;
    }

    private sealed class PublicCaptureCaseResult
    {
        public int Rank { get; init; }
        public string FullName { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public string ReadmeSha { get; init; } = string.Empty;
        public bool ReadmeAvailable { get; init; }
        public bool? ReadmeRendered { get; init; }
        public string BrowserReportSha256 { get; init; } = string.Empty;
        public string? CorpusManifestSha256 { get; init; }
        public long CorpusBytes { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? Failure { get; init; }
        public DateTimeOffset CompletedAtUtc { get; init; }
    }

    private sealed class PublicCaptureBatchReport
    {
        public int SchemaVersion { get; init; }
        public string Scope { get; init; } = string.Empty;
        public string ExecutionStatus { get; init; } = string.Empty;
        public string OverallReleaseQualification { get; init; } = string.Empty;
        public bool AuthenticatedLiveAuditEquivalent { get; init; }
        public string LiveTop500RankingFreshness { get; init; } = string.Empty;
        public string NativeJitHubRenderComparison { get; init; } = string.Empty;
        public string SameByteEdgeFullPageReplay { get; init; } = string.Empty;
        public string PinnedManifestSha256 { get; init; } = string.Empty;
        public int RequestedStartRank { get; init; }
        public int RequestedCount { get; init; }
        public int CapturedCaseCount { get; init; }
        public int FailedCaseCount { get; init; }
        public int AvailableReadmeCount { get; init; }
        public int SourceViewCount { get; init; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; init; }
        public IReadOnlyList<PublicCaptureCaseResult> Cases { get; init; } = [];
    }
}
