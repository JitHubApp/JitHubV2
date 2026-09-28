using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;

internal static partial class ReadmeAuditProbe
{
    internal const string ProbeName = "readme-production-audit";
    private const string HostAutomationId = "MarkdownHost_RepositoryReadme_RepoCodeReadme";
    private const int ViewportWidth = 1000;
    private const int ViewportHeight = 900;
    private const int SlowVisibleImageWaitMilliseconds = 500;
    private const double SameByteTraversalViewportStepRatio = 0.9;
    private const double MaximumNativeScrollViewportFraction = 0.95;
    private const int MaximumTraversalViewports = 512;
    private const int MaximumViewportPositionCorrections = 16;
    private const int MaximumNativeScrollAttempts = 3;
    private const int MaximumNativeViewportPositions =
        MaximumTraversalViewports * (MaximumViewportPositionCorrections + 2);
    private const int UiaOperationTimeoutHResult = unchecked((int)0x80131505);
    private const double MinimumScrollPercentChange = 0.000001;
    private static readonly TimeSpan UiaProviderConnectionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UiaProviderTransactionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NativeTraversalTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static void Run(CaptureOptions options)
    {
        string manifestPath = options.AuditManifestPath
            ?? throw new ArgumentException("README audit requires --manifest=<path>.");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("README audit manifest was not found.", manifestPath);
        }
        if (options.AuditStartRank <= 0 || options.AuditCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "--start-rank and --count must be positive integers.");
        }
        string? accessToken = Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "Set JITHUB_README_AUDIT_GITHUB_TOKEN to a read-only GitHub token before running the audit.");
        }
        string? accountId = Environment.GetEnvironmentVariable(
            "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID");
        if (!long.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedAccountId) ||
            parsedAccountId <= 0)
        {
            throw new InvalidOperationException(
                "Set JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID to the stable positive audit cache partition.");
        }

        ReadmeAuditManifest manifest = JsonSerializer.Deserialize<ReadmeAuditManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions) ?? throw new InvalidDataException("README audit manifest is empty.");
        if (manifest.SchemaVersion != 2 || manifest.Repositories.Count == 0)
        {
            throw new InvalidDataException("README audit manifest schema or repository list is invalid.");
        }
        if (options.AuditCaptureSameByteCorpus && options.AuditReuseBrowserEvidence)
        {
            throw new InvalidOperationException(
                "Same-byte capture cannot reuse an earlier Edge report; capture requires a fresh response trace.");
        }

        string browserScript = options.AuditBrowserScriptPath ?? Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "readme-audit",
            "browser-oracle.mjs");
        if (!File.Exists(browserScript))
        {
            throw new FileNotFoundException("README browser oracle was not found.", browserScript);
        }

        Directory.CreateDirectory(options.OutputDirectory);
        int lastRank = checked(options.AuditStartRank + options.AuditCount - 1);
        IReadOnlyList<ReadmeAuditRepository> selected = manifest.Repositories
            .Where(repository => repository.Rank >= options.AuditStartRank && repository.Rank <= lastRank)
            .OrderBy(repository => repository.Rank)
            .ToArray();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"Manifest contains no repositories in rank range {options.AuditStartRank}..{lastRank}.");
        }

        var results = new List<ReadmeAuditCaseResult>();
        foreach (ReadmeAuditRepository repository in selected)
        {
            string caseId = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
            string caseDirectory = Path.Combine(options.OutputDirectory, "cases", caseId);
            string caseResultPath = Path.Combine(caseDirectory, "result.json");
            if (!options.AuditCaptureSameByteCorpus && options.AuditResume &&
                TryReadCompletedCase(caseResultPath, manifest, repository, out ReadmeAuditCaseResult? resumed))
            {
                results.Add(resumed!);
                Console.WriteLine($"README audit {repository.Rank}/{manifest.Repositories.Count}: resume {repository.FullName} ({resumed!.Status}).");
                continue;
            }

            Directory.CreateDirectory(caseDirectory);
            Console.WriteLine($"README audit {repository.Rank}/{manifest.Repositories.Count}: {repository.FullName}");
            ReadmeAuditCaseResult result = RunCase(
                options,
                browserScript,
                manifest,
                repository,
                caseDirectory,
                accessToken.Trim());
            WriteJson(caseResultPath, result);
            results.Add(result);
            Console.WriteLine(
                $"README audit {repository.FullName}: {result.Status}; " +
                $"text={result.Comparison?.TextTokenCoverage:P2}; structure={result.Comparison?.VisualStructureScore:P2}; " +
                $"native unavailable={result.Native?.UnavailableImages ?? -1}.");
            if (result.InfrastructureFailure)
            {
                ReadmeAuditSummary partial = BuildSummary(
                    manifest, selected, results, options.AuditCaptureSameByteCorpus);
                WriteJson(Path.Combine(options.OutputDirectory, "summary.json"), partial);
                WriteSummaryMarkdown(Path.Combine(options.OutputDirectory, "summary.md"), partial, results);
                throw new InvalidOperationException(
                    $"Native README audit infrastructure failed at rank {repository.Rank} " +
                    $"({repository.FullName}); the remaining repositories cannot produce valid comparisons.");
            }
        }

        ReadmeAuditSummary summary = BuildSummary(
            manifest, selected, results, options.AuditCaptureSameByteCorpus);
        WriteJson(Path.Combine(options.OutputDirectory, "summary.json"), summary);
        WriteSummaryMarkdown(Path.Combine(options.OutputDirectory, "summary.md"), summary, results);
        if (!summary.Passed)
        {
            throw new InvalidOperationException(
                $"README audit failed: {summary.FailedCases}/{summary.TotalCases} cases failed. " +
                $"See '{Path.Combine(options.OutputDirectory, "summary.md")}'.");
        }
    }

    private static ReadmeAuditCaseResult RunCase(
        CaptureOptions options,
        string browserScript,
        ReadmeAuditManifest manifest,
        ReadmeAuditRepository repository,
        string caseDirectory,
        string accessToken)
    {
        BrowserAuditResult? browser = null;
        NativeAuditResult? native = null;
        bool infrastructureFailure = false;
        var failures = new List<string>();
        try
        {
            browser = RunBrowserOracle(options, browserScript, repository, caseDirectory);
            if (!repository.Readme.Available && browser.ReadmeRendered != false)
            {
                failures.Add("GitHub unexpectedly rendered a README for a repository without one.");
            }
            // GitHub deliberately leaves some available README formats (most
            // notably torvalds/linux's extensionless README) out of the
            // repository-page Markdown article. Those cases exercise JitHub's
            // source-preview path below; the absence of an article is a valid
            // oracle result, not an incomplete capture.
            // Broken resources in the reference page remain evidence, but do not
            // fail JitHub. Native parity is evaluated only against image resources
            // Edge actually loaded and rendered successfully.
        }
        catch (Exception exception)
        {
            failures.Add("Browser oracle failed: " + exception.Message);
        }

        try
        {
            // Each native attempt is a fresh production-app process. GitHub can
            // temporarily reject an otherwise valid repository-tree request
            // with its secondary "gitmon" network limit during a parallel audit.
            // Retry only that upstream admission failure; never reinterpret a
            // renderer failure, incomplete image, or failed comparison as a pass.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    native = RunNativeAudit(
                        options,
                        repository,
                        caseDirectory,
                        accessToken,
                        browser?.Semantic.Images,
                        options.AuditCaptureSameByteCorpus && repository.Readme.Available
                            ? GetSameByteCorpusDirectory(caseDirectory, repository, browser?.SameByteCorpus,
                                browser?.SameByteHtmlReplay, browser?.ReadmeRendered)
                            : null,
                        browser?.SameByteCorpus?.ReadmeSha256,
                        browser?.SameByteCorpus?.ReadmeBytes,
                        // GitHub intentionally displays some available README files as
                        // source (for example, extensionless or over-sized Markdown).
                        // Such cases have no comparable browser article. JitHub may
                        // still render them richly; its chosen native surface must
                        // remain complete and healthy even though ratios are omitted.
                        expectRenderedReadme: browser?.ReadmeRendered is not false);
                    break;
                }
                catch (Exception exception) when (IsTransientGitHubAdmissionFailure(exception))
                {
                    if (attempt == 2)
                        throw new NativeAuditInfrastructureException(
                            "GitHub rejected all three native repository-tree attempts " +
                            "with its transient network admission limit.", exception);

                    TimeSpan delay = attempt == 0
                        ? TimeSpan.FromSeconds(15)
                        : TimeSpan.FromSeconds(45);
                    Console.WriteLine(
                        $"README audit {repository.FullName}: GitHub temporarily rejected " +
                        $"the native repository request; retrying in {delay.TotalSeconds:F0}s " +
                        $"({attempt + 1}/2).");
                    Thread.Sleep(delay);
                }
            }

            if (native is null)
                throw new InvalidOperationException("Native README audit produced no result.");
            if (native.UnavailableImages != 0)
            {
                failures.Add($"JitHub reported {native.UnavailableImages} unavailable image(s).");
            }
            if (!string.IsNullOrWhiteSpace(native.RenderFailure))
            {
                failures.Add("JitHub raised a renderer exception.");
            }
            if (!native.CleanExit)
            {
                failures.Add(
                    "JitHub did not close cleanly after the case" +
                    (native.CloseFailure is null ? "." : $" ({native.CloseFailure})."));
            }
            if (native.LoadingImagesAfterTraversal != 0)
            {
                failures.Add($"JitHub still exposed {native.LoadingImagesAfterTraversal} loading image(s) after traversal.");
            }
        }
        catch (NativeAuditInfrastructureException exception)
        {
            infrastructureFailure = true;
            failures.Add("Native JitHub audit infrastructure failed: " + exception);
        }
        catch (Exception exception)
        {
            failures.Add("Native JitHub audit failed: " + exception);
        }

        if (options.AuditCaptureSameByteCorpus && repository.Readme.Available &&
            browser is not null && native is not null)
        {
            try
            {
                if (browser.ReadmeRendered is false)
                {
                    browser.SameByteReplay = new BrowserSameByteReplayEvidence
                    {
                        SchemaVersion = 2,
                        Status = "not-applicable",
                        Reason = "github-source-view",
                    };
                }
                else
                {
                    string corpusDirectory = GetSameByteCorpusDirectory(
                        caseDirectory, repository, browser.SameByteCorpus,
                        browser.SameByteHtmlReplay, browser.ReadmeRendered);
                    browser.SameByteReplay = RunSourceBoundEdgeReplay(
                        options, repository, caseDirectory, corpusDirectory,
                        browser.SameByteCorpus!, browser.SameByteHtmlReplay, native,
                        browser.Semantic);
                    double? capturedStructureScoreValue =
                        browser.SameByteReplay.Semantic.CapturedGitHubSourceStructureScore;
                    if (capturedStructureScoreValue is not double sourceStructureScore ||
                        !double.IsFinite(sourceStructureScore) || sourceStructureScore < 0.95)
                    {
                        string scoreText = capturedStructureScoreValue?.ToString("P2", CultureInfo.InvariantCulture)
                            ?? "n/a";
                        failures.Add(
                            $"Captured GitHub/source stable-structure fidelity was {scoreText}, below 95%.");
                    }
                }

                WriteJson(Path.Combine(caseDirectory, "browser", "browser.json"),
                    SanitizeBrowserAuditResult(browser));
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"Source-bound Edge replay failed ({GetSourceBoundReplayFailureCategory(exception)}).");
            }
        }

        ReadmeAuditComparison? comparison = null;
        if (browser is { ReadmeRendered: not false } && native is not null)
        {
            comparison = Compare(browser, native, caseDirectory);
            if (comparison.TextTokenCoverage < 0.985)
            {
                failures.Add($"Visible text token coverage was {comparison.TextTokenCoverage:P2}, below 98.5%.");
            }
            if (comparison.VisualStructureScore < 0.95)
            {
                failures.Add(
                    $"Full-page structural fidelity was {comparison.VisualStructureScore:P2}, below 95%. " +
                    $"(raw cross-style SSIM {comparison.MeanTileSsim:F3}).");
            }
            if (comparison.SourceBoundLayoutExtentRatio is double sourceExtentRatio &&
                (sourceExtentRatio < 0.90 || sourceExtentRatio > 1.10))
            {
                failures.Add(
                    $"Source-bound native/Edge page-height ratio was {sourceExtentRatio:F3}, " +
                    "outside the 0.90–1.10 full-page fidelity envelope.");
            }
            if (comparison.NativeImageSourceCount < comparison.BrowserDistinctAtomicMediaCount)
            {
                failures.Add(
                    $"JitHub audited {comparison.NativeImageSourceCount} distinct image/media sources, " +
                    $"but Edge rendered {comparison.BrowserDistinctAtomicMediaCount}.");
            }
        }

        return new ReadmeAuditCaseResult
        {
            SchemaVersion = 4,
            CorpusGeneratedAtUtc = manifest.GeneratedAtUtc,
            Rank = repository.Rank,
            FullName = repository.FullName,
            ReadmeSha = repository.Readme.Sha,
            Status = failures.Count == 0 ? "passed" : "failed",
            Failures = options.AuditCaptureSameByteCorpus
                ? failures.Select(RedactUrls).ToArray()
                : failures,
            InfrastructureFailure = infrastructureFailure,
            Browser = options.AuditCaptureSameByteCorpus
                ? SanitizeBrowserAuditResult(browser)
                : browser,
            Native = options.AuditCaptureSameByteCorpus
                ? SanitizeNativeAuditResult(native)
                : native,
            Comparison = comparison,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static bool IsTransientGitHubAdmissionFailure(Exception exception)
    {
        // The app writes the original exception and its stack into the audit
        // failure signal; it crosses a process boundary as message text. Match
        // both the typed transport error and GitHub's distinctive response so
        // unrelated API, UI, or Markdown failures never enter this retry path.
        string message = exception.ToString();
        return message.Contains("GitHubRateLimitException:", StringComparison.Ordinal) &&
            message.Contains(
                "gitmon refuses to schedule us: fail-fast:network",
                StringComparison.OrdinalIgnoreCase);
    }

    private static BrowserAuditResult RunBrowserOracle(
        CaptureOptions options,
        string browserScript,
        ReadmeAuditRepository repository,
        string caseDirectory)
    {
        string output = Path.Combine(caseDirectory, "browser");
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "browser.json");
        if (options.AuditReuseBrowserEvidence && File.Exists(reportPath))
        {
            BrowserAuditResult cached = JsonSerializer.Deserialize<BrowserAuditResult>(
                File.ReadAllText(reportPath),
                JsonOptions) ?? throw new InvalidDataException("Cached Edge README oracle report is empty.");
            string snapshotUrl = GetSnapshotUrl(repository);
            if (cached.SchemaVersion != 5 ||
                !string.Equals(cached.RepositoryUrl, snapshotUrl, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(cached.ReadmeSha, GetReadmeEvidenceIdentity(repository), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Cached Edge README oracle report does not match the requested repository.");
            }
            return cached;
        }

        var startInfo = new ProcessStartInfo(options.AuditNodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = FindRepositoryRoot(),
        };
        bool captureThisRepository = options.AuditCaptureSameByteCorpus && repository.Readme.Available;
        string? rawComparisonReportPath = captureThisRepository
            ? Path.Combine(Path.GetTempPath(), $"jithub-same-byte-edge-{Guid.NewGuid():N}.json")
            : null;
        startInfo.ArgumentList.Add(browserScript);
        startInfo.ArgumentList.Add($"--url={GetSnapshotUrl(repository)}");
        startInfo.ArgumentList.Add($"--readme-sha={GetReadmeEvidenceIdentity(repository)}");
        startInfo.ArgumentList.Add($"--out={output}");
        startInfo.ArgumentList.Add($"--width={ViewportWidth}");
        startInfo.ArgumentList.Add("--height=700");
        startInfo.ArgumentList.Add("--max-tiles=512");
        if (captureThisRepository)
        {
            if (!repository.Readme.Available)
            {
                throw new InvalidDataException(
                    $"Cannot capture a pinned README source for {repository.FullName}.");
            }
            Uri readmeDownloadUrl = GetPinnedReadmeDownloadUri(repository);

            string corpusDirectory = Path.Combine(output, $"same-byte-corpus-{Guid.NewGuid():N}");
            startInfo.ArgumentList.Add($"--capture-same-byte-corpus={corpusDirectory}");
            startInfo.ArgumentList.Add($"--same-byte-comparison-report={rawComparisonReportPath}");
            startInfo.ArgumentList.Add($"--repo-full-name={repository.FullName}");
            startInfo.ArgumentList.Add($"--commit-sha={repository.CommitSha}");
            startInfo.ArgumentList.Add($"--readme-url={readmeDownloadUrl.AbsoluteUri}");
            startInfo.ArgumentList.Add($"--readme-path={repository.Readme.Path}");
            startInfo.ArgumentList.Add($"--readme-byte-size={repository.Readme.ByteSize.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(options.AuditEdgePath))
        {
            startInfo.ArgumentList.Add($"--edge={options.AuditEdgePath}");
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Edge README oracle.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            // Full-page evidence for unusually tall READMEs can contain hundreds
            // of 8K screenshot tiles. That capture is deliberately complete and
            // must not be truncated merely because audit overhead exceeds the
            // ordinary three-minute render budget. Per-page renderer timings are
            // captured before screenshotting and remain independently enforced.
            if (!process.WaitForExit(600_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Edge README oracle exceeded its 10-minute full-page evidence deadline.");
            }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Edge README oracle exited with {process.ExitCode}: {stderr.Result.Trim()}");
            }

            string sourceReportPath = rawComparisonReportPath ?? reportPath;
            BrowserAuditResult report = JsonSerializer.Deserialize<BrowserAuditResult>(
                File.ReadAllText(sourceReportPath),
                JsonOptions) ?? throw new InvalidDataException("Edge README oracle produced an empty report.");
            if (captureThisRepository)
            {
                _ = GetSameByteCorpusDirectory(caseDirectory, repository, report.SameByteCorpus,
                    report.SameByteHtmlReplay, report.ReadmeRendered);
                WriteJson(reportPath, SanitizeBrowserAuditResult(report));
            }
            else if (options.AuditCaptureSameByteCorpus)
            {
                WriteJson(reportPath, SanitizeBrowserAuditResult(report));
            }
            return report;
        }
        finally
        {
            if (rawComparisonReportPath is not null && File.Exists(rawComparisonReportPath))
                File.Delete(rawComparisonReportPath);
        }
    }

    private static string GetSnapshotUrl(ReadmeAuditRepository repository) =>
        $"{repository.Url.TrimEnd('/')}/tree/{repository.CommitSha}";

    private static string GetReadmeEvidenceIdentity(ReadmeAuditRepository repository) =>
        repository.Readme.Available ? repository.Readme.Sha : "absent";

    private static Uri GetPinnedReadmeDownloadUri(ReadmeAuditRepository repository)
    {
        string[] repositoryParts = repository.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repositoryParts.Length != 2 ||
            !repository.CommitSha.All(Uri.IsHexDigit) || repository.CommitSha.Length != 40)
        {
            throw new InvalidDataException("README capture repository identity is not pinned to a commit.");
        }

        string[] pathParts = repository.Readme.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length == 0 || pathParts.Any(part => part is "." or ".."))
            throw new InvalidDataException("Pinned README path is invalid.");

        string escapedRepository = string.Join('/', repositoryParts.Select(Uri.EscapeDataString));
        string escapedPath = string.Join('/', pathParts.Select(Uri.EscapeDataString));
        return new Uri(
            $"https://raw.githubusercontent.com/{escapedRepository}/{repository.CommitSha}/{escapedPath}",
            UriKind.Absolute);
    }

    private static string GetSameByteCorpusDirectory(
        string caseDirectory,
        ReadmeAuditRepository expected,
        BrowserSameByteCorpusEvidence? evidence,
        BrowserSameByteHtmlReplayEvidence? replay,
        bool? readmeRendered)
    {
        if (evidence is null || evidence.ReadmeBytes < 0 || evidence.AssetCount < 0 ||
            evidence.AssetBytes < 0 || !IsSha256(evidence.ManifestSha256) ||
            !IsSha256(evidence.ReadmeSha256) ||
            string.IsNullOrWhiteSpace(evidence.Manifest) || Path.IsPathRooted(evidence.Manifest))
        {
            throw new InvalidDataException("Edge did not produce a valid same-byte corpus manifest reference.");
        }

        string browserRoot = Path.GetFullPath(Path.Combine(caseDirectory, "browser"));
        string manifestPath = Path.GetFullPath(Path.Combine(browserRoot, evidence.Manifest));
        string browserPrefix = browserRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!manifestPath.StartsWith(browserPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(manifestPath), "manifest.json", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Same-byte corpus manifest path escaped the audit case directory.");
        }

        FileInfo manifestInfo = new(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length <= 0 || manifestInfo.Length > 4 * 1024 * 1024 ||
            (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Same-byte corpus manifest is missing, redirected, or oversized.");
        }

        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        if (!string.Equals(HashSha256(manifestBytes), evidence.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Same-byte corpus manifest changed after Edge capture.");

        using JsonDocument document = JsonDocument.Parse(manifestBytes);
        JsonElement root = document.RootElement;
        JsonElement pinnedRepository = root.GetProperty("repository");
        JsonElement readme = root.GetProperty("readme");
        JsonElement browserRender = root.GetProperty("browserRender");
        JsonElement assets = root.GetProperty("assets");
        if (root.GetProperty("schemaVersion").GetInt32() != 2 ||
            !root.GetProperty("complete").GetBoolean() ||
            !string.Equals(pinnedRepository.GetProperty("fullName").GetString(), expected.FullName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pinnedRepository.GetProperty("commitSha").GetString(), expected.CommitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pinnedRepository.GetProperty("readmePath").GetString(), expected.Readme.Path, StringComparison.Ordinal) ||
            !string.Equals(pinnedRepository.GetProperty("readmeGitBlobSha1").GetString(), expected.Readme.Sha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(readme.GetProperty("file").GetString(), "readme.md", StringComparison.Ordinal) ||
            readme.GetProperty("byteSize").GetInt64() != evidence.ReadmeBytes ||
            !string.Equals(readme.GetProperty("sha256").GetString(), evidence.ReadmeSha256,
                StringComparison.OrdinalIgnoreCase) ||
            assets.ValueKind != System.Text.Json.JsonValueKind.Array || assets.GetArrayLength() > 100_000)
        {
            throw new InvalidDataException("Same-byte corpus manifest failed its capture identity checks.");
        }

        string? browserRenderStatus = browserRender.GetProperty("status").GetString();
        if (browserRenderStatus == "passed")
        {
            if (readmeRendered is false || replay is null || replay.SchemaVersion != 1 ||
                replay.Status != "passed" ||
                !string.Equals(replay.ReadmeGitBlobSha1, expected.Readme.Sha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(replay.ReadmeSha256, evidence.ReadmeSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(replay.RenderedHtmlSha256,
                    browserRender.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !IsSha256(replay.AssetUrlMapSha256) ||
                !string.Equals(replay.AssetUrlMapSha256,
                    HashSha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(assets,
                        new JsonSerializerOptions
                        {
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        }))),
                    StringComparison.OrdinalIgnoreCase) ||
                replay.Viewport.Width != ViewportWidth || replay.Viewport.Height != 700 ||
                replay.Viewport.DeviceScaleFactor != 1 ||
                replay.Assets.ReplayMissCount != 0 || replay.Assets.BlockedExternalRequestCount != 0 ||
                replay.Assets.DistinctServedUrlHashes != replay.Assets.DistinctExpectedUrlHashes ||
                replay.Assets.DistinctExpectedUrlHashes < 0 ||
                replay.Assets.ExpectedVisibleImageCount < 0 ||
                !IsFinitePositive(replay.Timing.FirstViewportPaintMs) ||
                !IsFinitePositive(replay.Timing.FirstViewportImagesReadyMs) ||
                !IsFinitePositive(replay.Timing.FullTraversalMs) ||
                replay.Timing.FirstViewportImagesReadyMs < replay.Timing.FirstViewportPaintMs ||
                replay.Timing.FullTraversalMs < replay.Timing.FirstViewportImagesReadyMs ||
                Math.Abs(replay.Timing.ViewportStepRatio - SameByteTraversalViewportStepRatio) > 0.000001 ||
                replay.Timing.TraversalViewportCount is < 1 or > MaximumTraversalViewports ||
                replay.Tiles.Count is < 1 or > 512)
            {
                throw new InvalidDataException("Same-byte offline Edge replay did not qualify for comparison.");
            }
            if (!string.Equals(browserRender.GetProperty("file").GetString(), "rendered.html", StringComparison.Ordinal) ||
                browserRender.GetProperty("byteSize").GetInt64() is <= 0 or > 33_554_432 ||
                !IsSha256(browserRender.GetProperty("sha256").GetString()))
            {
                throw new InvalidDataException("Same-byte browser render metadata is invalid.");
            }
            string renderedPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "rendered.html");
            FileInfo renderedInfo = new(renderedPath);
            if (!renderedInfo.Exists || renderedInfo.Length != browserRender.GetProperty("byteSize").GetInt64() ||
                (renderedInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Same-byte browser render bytes changed after Edge capture.");
            }
            using FileStream renderedStream = new(
                renderedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            if (renderedStream.Length != browserRender.GetProperty("byteSize").GetInt64() ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(renderedStream)),
                    browserRender.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Same-byte browser render bytes changed after Edge capture.");
            }
        }
        else if (browserRenderStatus != "not-applicable" || readmeRendered is not false ||
            replay is null || replay.SchemaVersion != 1 || replay.Status != "not-applicable" ||
            replay.Reason != "github-source-view" ||
            !string.Equals(browserRender.GetProperty("reason").GetString(), "github-source-view", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Same-byte browser render status is invalid.");
        }

        return Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Same-byte corpus manifest has no containing directory.");
    }

    private static BrowserSameByteReplayEvidence RunSourceBoundEdgeReplay(
        CaptureOptions options,
        ReadmeAuditRepository repository,
        string caseDirectory,
        string corpusDirectory,
        BrowserSameByteCorpusEvidence corpusEvidence,
        BrowserSameByteHtmlReplayEvidence? htmlReplay,
        NativeAuditResult native,
        BrowserSemantic capturedGitHubArticleSemantic)
    {
        if (native.ContentViewportWidth is < 64 or > 8192 ||
            native.ContentViewportHeight is < 64 or > 8192 ||
            native.RasterizationScale is < 0.5 or > 4 ||
            Math.Abs(native.Width - native.ContentViewportWidth) > 2 ||
            !IsFinitePositive(native.FirstViewportImagesReadyMs))
        {
            throw new InvalidDataException(
                "The native visible content viewport or image-ready boundary is not qualified for Edge comparison.");
        }
        string semanticDigestKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        string script = Path.Combine(FindRepositoryRoot(), "eng", "readme-audit", "same-byte-edge-source.mjs");
        string parser = Path.Combine(FindRepositoryRoot(), "eng", "readme-audit", "vendor", "marked-18.0.5.umd.js");
        const string parserSha256 = "2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b";
        FileInfo parserInfo = new(parser);
        if (!parserInfo.Exists || parserInfo.Length is <= 0 or > 2_097_152 ||
            (parserInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !string.Equals(HashSha256(File.ReadAllBytes(parser)), parserSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The pinned Edge GFM parser is missing or changed.");
        }

        string output = Path.Combine(caseDirectory, "browser", "source-replay");
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "same-byte-source-replay.json");
        string viewportProfilePath = Path.Combine(output, "native-viewport-profile.json");
        int expectedMovementCount = native.Tiles.Sum(tile => tile.NativeViewportMovementOffsets.Count);
        WriteJson(viewportProfilePath, new
        {
            schemaVersion = 1,
            viewportHeight = native.ContentViewportHeight,
            viewports = native.Tiles.Select(tile => new
            {
                movementOffsetsViewportUnits = tile.NativeViewportMovementOffsets,
                captureOffsetViewportUnits = tile.ScrollTopViewportUnits,
            }).ToArray(),
        });
        string viewportProfileSha256 = HashSha256(File.ReadAllBytes(viewportProfilePath));
        var startInfo = new ProcessStartInfo(options.AuditNodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = FindRepositoryRoot(),
        };
        // Offline replay needs only the captured corpus. Do not pass the
        // wrapper's live GitHub credential or account partition to Node/Edge.
        startInfo.Environment.Remove("JITHUB_README_AUDIT_GITHUB_TOKEN");
        startInfo.Environment.Remove("JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID");
        startInfo.Environment["JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY"] = semanticDigestKey;
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add($"--corpus={corpusDirectory}");
        startInfo.ArgumentList.Add($"--out={output}");
        startInfo.ArgumentList.Add($"--report={reportPath}");
        startInfo.ArgumentList.Add($"--width={native.ContentViewportWidth}");
        startInfo.ArgumentList.Add($"--height={native.ContentViewportHeight}");
        startInfo.ArgumentList.Add($"--device-scale-factor={native.RasterizationScale.ToString(CultureInfo.InvariantCulture)}");
        startInfo.ArgumentList.Add($"--native-viewport-profile={viewportProfilePath}");
        startInfo.ArgumentList.Add("--color-scheme=light");
        if (!string.IsNullOrWhiteSpace(options.AuditEdgePath))
            startInfo.ArgumentList.Add($"--edge={options.AuditEdgePath}");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the source-bound Edge README replay.");
        startInfo.Environment.Remove("JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Source-bound Edge replay exceeded its 10-minute deadline.");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            throw new SourceBoundEdgeReplayException(ReadSourceBoundReplayFailureCategory(reportPath));
        }

        FileInfo reportInfo = new(reportPath);
        if (!reportInfo.Exists || reportInfo.Length is <= 0 or > 4_194_304 ||
            (reportInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Source-bound Edge replay report is missing or invalid.");
        }
        BrowserSameByteReplayEvidence replay = JsonSerializer.Deserialize<BrowserSameByteReplayEvidence>(
            File.ReadAllText(reportPath), JsonOptions)
            ?? throw new InvalidDataException("Source-bound Edge replay report is empty.");

        string validatedCorpus = GetSameByteCorpusDirectory(
            caseDirectory, repository, corpusEvidence,
            replay: htmlReplay,
            readmeRendered: true);
        // Revalidate the immutable capture after the child process exits. The
        // source-runner itself independently hashes all served bytes.
        if (!string.Equals(validatedCorpus, corpusDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source-bound Edge replay changed corpus identity.");
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(corpusDirectory, "manifest.json")));
        JsonElement assets = manifest.RootElement.GetProperty("assets");
        string assetMapSha256 = HashSha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(assets,
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            })));
        if (replay.SchemaVersion != 3 || replay.Status != "passed" ||
            !string.Equals(replay.Source.ReadmeGitBlobSha1, repository.Readme.Sha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(replay.Source.ReadmeSha256, corpusEvidence.ReadmeSha256, StringComparison.OrdinalIgnoreCase) ||
            replay.Source.ByteSize != corpusEvidence.ReadmeBytes ||
            replay.Parser.Name != "marked" || replay.Parser.Version != "18.0.5" ||
            replay.Parser.License != "MIT" ||
            !string.Equals(replay.Parser.Sha256, parserSha256, StringComparison.OrdinalIgnoreCase) ||
            replay.Viewport.Width != native.ContentViewportWidth ||
            replay.Viewport.Height != native.ContentViewportHeight ||
            replay.Viewport.EdgeInnerWidth != native.ContentViewportWidth ||
            replay.Viewport.EdgeInnerHeight != native.ContentViewportHeight ||
            replay.RenderedExtent.Width != native.ContentViewportWidth ||
            replay.RenderedExtent.Height < native.ContentViewportHeight ||
            native.EstimatedContentHeight <= 0 ||
            Math.Abs(replay.Viewport.DeviceScaleFactor - native.RasterizationScale) > 0.000001 ||
            Math.Abs(replay.Viewport.EdgeDeviceScaleFactor - native.RasterizationScale) > 0.000001 ||
            replay.Viewport.ColorScheme != "light" ||
            !string.Equals(replay.Assets.AssetUrlMapSha256, assetMapSha256, StringComparison.OrdinalIgnoreCase) ||
            replay.Assets.ExpectedImageCount < 0 ||
            replay.Assets.VerifiedImageCount != replay.Assets.ExpectedImageCount ||
            replay.Assets.DistinctExpectedUrlHashes < 0 ||
            replay.Assets.DistinctServedUrlHashes != replay.Assets.DistinctExpectedUrlHashes ||
            replay.Assets.ReplayMissCount != 0 || replay.Assets.BlockedExternalRequestCount != 0 ||
            !IsFinitePositive(replay.Timing.FirstViewportPaintMs) ||
            !IsFinitePositive(replay.Timing.FirstViewportImagesReadyMs) ||
            !IsFinitePositive(replay.Timing.FullTraversalMs) ||
            !IsFinitePositive(replay.Timing.ChargedTraversalMs) ||
            !IsFinitePositive(replay.Timing.AuditOnlyFrameWaitMs) ||
            replay.Timing.AuditOnlyFrameWaitCount is < 2 or > MaximumTraversalViewports * 17 * 3 + 2 ||
            replay.Timing.FirstViewportImagesReadyMs < replay.Timing.FirstViewportPaintMs ||
            replay.Timing.FullTraversalMs < replay.Timing.FirstViewportImagesReadyMs ||
            replay.Timing.ChargedTraversalMs < replay.Timing.FirstViewportImagesReadyMs ||
            replay.Timing.FullTraversalMs < replay.Timing.ChargedTraversalMs ||
            Math.Abs(replay.Timing.FullTraversalMs - replay.Timing.ChargedTraversalMs -
                replay.Timing.AuditOnlyFrameWaitMs) > 0.001 ||
            Math.Abs(replay.Timing.ViewportStepRatio - native.FullTraversalViewportStepRatio) >
                1.0 / native.ContentViewportHeight ||
            !string.Equals(replay.Timing.ViewportProfileSha256, viewportProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            replay.Timing.TraversalViewportCount != native.FullTraversalViewportCount ||
            replay.Timing.MovementCount != expectedMovementCount ||
            replay.Timing.MovementCount is < 0 or > MaximumNativeViewportPositions ||
            replay.Timing.CaptureOffsetsViewportUnits is null ||
            replay.Timing.CaptureOffsetsViewportUnits.Count != native.Tiles.Count ||
            replay.Timing.CaptureOffsetsViewportUnits.Where((offset, index) =>
                !double.IsFinite(offset) ||
                Math.Abs(offset - native.Tiles[index].ScrollTopViewportUnits) > 1.0 / native.ContentViewportHeight).Any() ||
            !HasCompleteSourceReplayTail(
                replay.Timing,
                native.Tiles,
                replay.RenderedExtent.Height,
                native.ContentViewportHeight) ||
            replay.Tiles.Count is < 1 or > 512 ||
            !HasCompleteSourceReplayTiles(replay, output) ||
            !HasCompleteSourceReplaySemantics(
                replay.Semantic,
                semanticDigestKey,
                native.ContentViewportWidth,
                replay.RenderedExtent.Height,
                replay.Assets.ExpectedImageCount))
        {
            throw new InvalidDataException("Source-bound Edge replay did not qualify for comparison.");
        }

        if (!TryGetBidirectionalSourceArticleTextCoverage(
                replay.Semantic,
                capturedGitHubArticleSemantic.VisibleText,
                semanticDigestKey,
                out double sourceToGitHubCoverage,
                out double gitHubToSourceCoverage) ||
            sourceToGitHubCoverage < 0.985 || gitHubToSourceCoverage < 0.985)
        {
            throw new InvalidDataException(
                "Source-bound Edge visible text did not match the captured GitHub article in both directions at 98.5%.");
        }

        replay.Semantic.SemanticDigestKeyHex = semanticDigestKey;
        replay.Semantic.SourceReplayToCapturedGitHubVisibleTextTokenCoverage = sourceToGitHubCoverage;
        replay.Semantic.CapturedGitHubToSourceReplayVisibleTextTokenCoverage = gitHubToSourceCoverage;
        replay.Semantic.CapturedGitHubSourceStructureScore = ComputeCapturedGitHubSourceStructureScore(
            capturedGitHubArticleSemantic,
            replay.Semantic);
        return replay;
    }

    private static string ReadSourceBoundReplayFailureCategory(string reportPath)
    {
        try
        {
            FileInfo reportInfo = new(reportPath);
            if (!reportInfo.Exists || reportInfo.Length is <= 0 or > 4_194_304 ||
                (reportInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "worker-failure";
            }

            using JsonDocument report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            JsonElement root = report.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("status").GetString() != "failed" ||
                !root.TryGetProperty("failureCategory", out JsonElement categoryElement) ||
                categoryElement.ValueKind != JsonValueKind.String)
            {
                return "worker-failure";
            }

            return categoryElement.GetString() switch
            {
                "browser-startup" => "browser-startup",
                "external-resource" => "external-resource",
                "integrity" => "integrity",
                "image" => "image",
                "resource-limit" => "resource-limit",
                "replay" => "replay",
                "timeout" => "timeout",
                _ => "worker-failure",
            };
        }
        catch
        {
            return "worker-failure";
        }
    }

    private static string GetSourceBoundReplayFailureCategory(Exception exception) => exception switch
    {
        SourceBoundEdgeReplayException replayFailure => replayFailure.Category,
        TimeoutException => "timeout",
        InvalidDataException => "integrity",
        IOException or UnauthorizedAccessException => "io",
        _ => "replay",
    };

    private sealed class SourceBoundEdgeReplayException : InvalidOperationException
    {
        public SourceBoundEdgeReplayException(string category) : base("Source-bound Edge replay failed.") =>
            Category = category;

        public string Category { get; }
    }

    private static bool HasCompleteSourceReplayTiles(
        BrowserSameByteReplayEvidence replay,
        string outputDirectory)
    {
        if (replay.RenderedExtent.Height is < 1 or > 4_194_304)
            return false;

        double coveredEnd = 0;
        for (int index = 0; index < replay.Tiles.Count; index++)
        {
            AuditTile tile = replay.Tiles[index];
            double end = tile.RelativeY + tile.Height;
            string expectedFile = $"same-byte-source-tile-{index:D4}.png";
            if (tile.Index != index || tile.File != expectedFile ||
                tile.Width != replay.RenderedExtent.Width ||
                !double.IsFinite(tile.RelativeY) || !double.IsFinite(tile.Height) ||
                tile.RelativeY < 0 || tile.RelativeY > coveredEnd ||
                tile.Height is < 1 or > 8192 ||
                end > replay.RenderedExtent.Height ||
                !File.Exists(Path.Combine(outputDirectory, expectedFile)))
            {
                return false;
            }

            coveredEnd = Math.Max(coveredEnd, end);
        }

        return coveredEnd == replay.RenderedExtent.Height;
    }

    private static bool HasCompleteSourceReplayTail(
        BrowserSameByteReplayTiming timing,
        IReadOnlyList<AuditTile> nativeTiles,
        int renderedHeight,
        int viewportHeight)
    {
        IReadOnlyList<double>? tailOffsets = timing.SourceTailCaptureOffsetsViewportUnits;
        if (nativeTiles.Count < 1 || tailOffsets is null ||
            viewportHeight < 1 || renderedHeight < viewportHeight ||
            timing.SourceTailViewportCount != tailOffsets.Count ||
            timing.SourceTailMovementCount != tailOffsets.Count ||
            timing.SourceTailViewportCount is < 0 or > MaximumTraversalViewports ||
            timing.TraversalViewportCount < 1 ||
            timing.TraversalViewportCount + timing.SourceTailViewportCount > MaximumTraversalViewports)
        {
            return false;
        }

        double positionTolerance = 1.0 / viewportHeight + 0.000001;
        double previousOffset = nativeTiles[^1].ScrollTopViewportUnits;
        if (!double.IsFinite(previousOffset) || previousOffset < 0)
            return false;

        foreach (double offset in tailOffsets)
        {
            double step = offset - previousOffset;
            if (!double.IsFinite(offset) || offset < 0 ||
                !double.IsFinite(step) || step <= 0 ||
                step > SameByteTraversalViewportStepRatio + 2 * positionTolerance)
            {
                return false;
            }

            previousOffset = offset;
        }

        double expectedBottom = Math.Max(0, renderedHeight - viewportHeight) / (double)viewportHeight;
        return Math.Abs(previousOffset - expectedBottom) <= positionTolerance;
    }

    private static bool HasCompleteSourceReplaySemantics(
        BrowserSameByteReplaySemanticEvidence semantic,
        string semanticDigestKey,
        int viewportWidth,
        int renderedHeight,
        int expectedImageCount)
    {
        if (semantic is null || !semantic.Complete ||
            !string.IsNullOrEmpty(semantic.IncompleteReason) ||
            semantic.TokenizationVersion != "rune-l-n-mn-mc-han-nfc-simple-lower-invariant-v1" ||
            !string.Equals(
                semantic.DigestKeySha256,
                HashSha256(Convert.FromHexString(semanticDigestKey)),
                StringComparison.OrdinalIgnoreCase) ||
            !IsFinitePositive(semantic.Width) || semantic.Width > viewportWidth + 2 ||
            !IsFinitePositive(semantic.Height) || semantic.Height > renderedHeight + 2 ||
            semantic.VisibleTextTokenDigests is null || semantic.VisibleTextTokenDigests.Count > 20_000 ||
            semantic.VisibleMermaidSourceDigests is null || semantic.VisibleMermaidSourceDigests.Count > 20_000 ||
            semantic.VisibleTextTokenCount is < 0 or > 1_000_000 ||
            semantic.HeadingCount is < 0 or > 200_000 ||
            semantic.DistinctLinkCount is < 0 or > 200_000 ||
            semantic.ImageCount != expectedImageCount ||
            semantic.ImageCount is < 0 or > 15_000 ||
            semantic.DistinctImageCount is < 0 || semantic.DistinctImageCount > semantic.ImageCount ||
            semantic.MediaCount != 0 || semantic.DistinctMediaCount != 0 ||
            semantic.TableCount is < 0 or > 200_000 ||
            semantic.CodeBlockCount is < 0 or > 200_000 ||
            semantic.TaskCheckboxCount is < 0 or > 200_000 ||
            semantic.DetailsCount is < 0 or > 200_000 ||
            semantic.VisibleMermaidSourceDigests.Any(digest => !IsSha256(digest)))
        {
            return false;
        }

        long totalTokens = 0;
        foreach ((string digest, int count) in semantic.VisibleTextTokenDigests)
        {
            if (!IsSha256(digest) || count <= 0)
                return false;
            totalTokens += count;
            if (totalTokens > semantic.VisibleTextTokenCount)
                return false;
        }

        return totalTokens == semantic.VisibleTextTokenCount;
    }

    private static bool TryGetBidirectionalSourceArticleTextCoverage(
        BrowserSameByteReplaySemanticEvidence sourceSemantic,
        string capturedGitHubArticleVisibleText,
        string semanticDigestKey,
        out double sourceToGitHubCoverage,
        out double gitHubToSourceCoverage)
    {
        sourceToGitHubCoverage = 0;
        gitHubToSourceCoverage = 0;
        if (!IsSha256(semanticDigestKey) || sourceSemantic.VisibleTextTokenDigests is null)
            return false;

        var capturedArticleDigests = new Dictionary<string, int>(StringComparer.Ordinal);
        long capturedArticleTokenCount = 0;
        foreach ((string token, int count) in CountTokens(capturedGitHubArticleVisibleText))
        {
            string digest = HashSemanticText(semanticDigestKey, token);
            capturedArticleDigests[digest] = capturedArticleDigests.GetValueOrDefault(digest) + count;
            capturedArticleTokenCount += count;
        }

        long sourceTokenCount = sourceSemantic.VisibleTextTokenCount;
        if (sourceTokenCount == 0 && capturedArticleTokenCount == 0)
        {
            sourceToGitHubCoverage = 1;
            gitHubToSourceCoverage = 1;
            return true;
        }
        if (sourceTokenCount == 0 || capturedArticleTokenCount == 0)
            return true;

        long matchedTokenCount = 0;
        foreach ((string digest, int sourceCount) in sourceSemantic.VisibleTextTokenDigests)
        {
            matchedTokenCount += Math.Min(
                sourceCount,
                capturedArticleDigests.GetValueOrDefault(digest));
        }

        sourceToGitHubCoverage = (double)matchedTokenCount / sourceTokenCount;
        gitHubToSourceCoverage = (double)matchedTokenCount / capturedArticleTokenCount;
        return double.IsFinite(sourceToGitHubCoverage) && double.IsFinite(gitHubToSourceCoverage);
    }

    private static double ComputeCapturedGitHubSourceStructureScore(
        BrowserSemantic capturedGitHubArticleSemantic,
        BrowserSameByteReplaySemanticEvidence sourceSemantic)
    {
        int capturedHeadingCount = capturedGitHubArticleSemantic.Headings?.Count ?? -1;
        if (capturedHeadingCount is < 0 or > 200_000 ||
            capturedGitHubArticleSemantic.Tables is < 0 or > 200_000 ||
            capturedGitHubArticleSemantic.TaskCheckboxes is < 0 or > 200_000 ||
            capturedGitHubArticleSemantic.Details is < 0 or > 200_000 ||
            sourceSemantic.HeadingCount is < 0 or > 200_000 ||
            sourceSemantic.TableCount is < 0 or > 200_000 ||
            sourceSemantic.TaskCheckboxCount is < 0 or > 200_000 ||
            sourceSemantic.DetailsCount is < 0 or > 200_000)
        {
            return 0;
        }

        double[] domainWeights = [1, 1, 1, 1];
        double totalWeight = domainWeights.Sum();
        if (!double.IsFinite(totalWeight) || totalWeight <= 0)
            return 0;

        double[] domainFidelities =
        [
            CountFidelity(capturedHeadingCount, sourceSemantic.HeadingCount),
            CountFidelity(capturedGitHubArticleSemantic.Tables, sourceSemantic.TableCount),
            CountFidelity(capturedGitHubArticleSemantic.TaskCheckboxes, sourceSemantic.TaskCheckboxCount),
            CountFidelity(capturedGitHubArticleSemantic.Details, sourceSemantic.DetailsCount),
        ];
        double score = 0;
        for (int index = 0; index < domainFidelities.Length; index++)
        {
            double normalizedWeight = domainWeights[index] / totalWeight;
            score += domainFidelities[index] * normalizedWeight;
        }

        return double.IsFinite(score) ? score : 0;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;

    private static string HashSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashSourceIdentity(string? value) =>
        "sha256:" + HashSha256(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string RedactUrls(string value) => Regex.Replace(
        value,
        @"https?://[^\s""'<>]+",
        match => HashSourceIdentity(match.Value),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static BrowserAuditResult? SanitizeBrowserAuditResult(BrowserAuditResult? report)
    {
        if (report is null) return null;
        BrowserSemantic semantic = report.Semantic;
        return new BrowserAuditResult
        {
            SchemaVersion = report.SchemaVersion,
            RepositoryUrl = HashSourceIdentity(report.RepositoryUrl),
            ReadmeSha = report.ReadmeSha,
            ReadmeRendered = report.ReadmeRendered,
            Timing = report.Timing,
            SameByteCorpus = report.SameByteCorpus,
            SameByteReplay = SanitizeSameByteReplayEvidence(report.SameByteReplay),
            SameByteHtmlReplay = report.SameByteHtmlReplay,
            Semantic = new BrowserSemantic
            {
                // Persist aggregate comparison evidence, not raw README prose.
                Text = string.Empty,
                VisibleText = string.Empty,
                Width = semantic.Width,
                Height = semantic.Height,
                Headings = semantic.Headings.Select(heading => new BrowserHeading
                {
                    Level = heading.Level,
                    Text = string.Empty,
                }).ToList(),
                Links = semantic.Links.Select(link => new BrowserLink
                {
                    Text = string.Empty,
                    Href = HashSourceIdentity(link.Href),
                }).ToList(),
                Images = semantic.Images.Select(image => new BrowserImage
                {
                    Alt = string.Empty,
                    Source = HashSourceIdentity(image.Source),
                    CurrentSource = HashSourceIdentity(image.CurrentSource),
                    Complete = image.Complete,
                    NaturalWidth = image.NaturalWidth,
                    NaturalHeight = image.NaturalHeight,
                    RenderedWidth = image.RenderedWidth,
                    RenderedHeight = image.RenderedHeight,
                }).ToList(),
                Media = semantic.Media.Select(media => new BrowserMedia
                {
                    Kind = media.Kind,
                    Source = HashSourceIdentity(media.Source),
                    CurrentSource = HashSourceIdentity(media.CurrentSource),
                    RenderedWidth = media.RenderedWidth,
                    RenderedHeight = media.RenderedHeight,
                    AccessibleName = string.Empty,
                }).ToList(),
                VisibleMermaidSources = [],
                UnavailableImages = semantic.UnavailableImages,
                Tables = semantic.Tables,
                CodeBlocks = semantic.CodeBlocks,
                TaskCheckboxes = semantic.TaskCheckboxes,
                Details = semantic.Details,
            },
            Tiles = report.Tiles,
        };
    }

    private static BrowserSameByteReplayEvidence? SanitizeSameByteReplayEvidence(
        BrowserSameByteReplayEvidence? replay)
    {
        if (replay is null) return null;
        BrowserSameByteReplaySemanticEvidence semantic = replay.Semantic;
        return new BrowserSameByteReplayEvidence
        {
            SchemaVersion = replay.SchemaVersion,
            Status = replay.Status,
            Reason = replay.Reason,
            Source = replay.Source,
            Parser = replay.Parser,
            Viewport = replay.Viewport,
            RenderedExtent = replay.RenderedExtent,
            Assets = replay.Assets,
            Timing = replay.Timing,
            Tiles = replay.Tiles,
            Semantic = new BrowserSameByteReplaySemanticEvidence
            {
                Complete = semantic.Complete,
                IncompleteReason = semantic.IncompleteReason,
                TokenizationVersion = semantic.TokenizationVersion,
                DigestKeySha256 = semantic.DigestKeySha256,
                VisibleTextTokenCount = semantic.VisibleTextTokenCount,
                VisibleTextTokenDigestCount = semantic.VisibleTextTokenDigests?.Count ?? 0,
                SourceReplayToCapturedGitHubVisibleTextTokenCoverage =
                    semantic.SourceReplayToCapturedGitHubVisibleTextTokenCoverage,
                CapturedGitHubToSourceReplayVisibleTextTokenCoverage =
                    semantic.CapturedGitHubToSourceReplayVisibleTextTokenCoverage,
                CapturedGitHubSourceStructureScore = semantic.CapturedGitHubSourceStructureScore,
                Width = semantic.Width,
                Height = semantic.Height,
                HeadingCount = semantic.HeadingCount,
                DistinctLinkCount = semantic.DistinctLinkCount,
                ImageCount = semantic.ImageCount,
                DistinctImageCount = semantic.DistinctImageCount,
                MediaCount = semantic.MediaCount,
                DistinctMediaCount = semantic.DistinctMediaCount,
                TableCount = semantic.TableCount,
                CodeBlockCount = semantic.CodeBlockCount,
                TaskCheckboxCount = semantic.TaskCheckboxCount,
                DetailsCount = semantic.DetailsCount,
                VisibleMermaidSourceCount = semantic.VisibleMermaidSourceDigests?.Count ?? 0,
            },
        };
    }

    private static NativeAuditResult? SanitizeNativeAuditResult(NativeAuditResult? result) => result is null
        ? null
        : new NativeAuditResult
        {
            FirstRenderMs = result.FirstRenderMs,
            FirstViewportImagesReadyMs = result.FirstViewportImagesReadyMs,
            FirstViewportImageWait = result.FirstViewportImageWait is { } firstViewportImageWait
                ? new ReadmeAuditFirstViewportImageWait
                {
                    ElapsedMilliseconds = firstViewportImageWait.ElapsedMilliseconds,
                    ProbeOverheadMs = firstViewportImageWait.ProbeOverheadMs,
                    PollCount = firstViewportImageWait.PollCount,
                    InitialHasLoadingVisibleImages = firstViewportImageWait.InitialHasLoadingVisibleImages,
                    ApplicationSignalGeneration = firstViewportImageWait.ApplicationSignalGeneration,
                    ApplicationSignalViewportPaintGeneration = firstViewportImageWait.ApplicationSignalViewportPaintGeneration,
                    ApplicationSignalPollCount = firstViewportImageWait.ApplicationSignalPollCount,
                    ApplicationSignalProbeWorkMilliseconds = firstViewportImageWait.ApplicationSignalProbeWorkMilliseconds,
                    ApplicationSignalViewportTop = firstViewportImageWait.ApplicationSignalViewportTop,
                    ApplicationSignalViewportHeight = firstViewportImageWait.ApplicationSignalViewportHeight,
                    ApplicationSignalViewportMeasured = firstViewportImageWait.ApplicationSignalViewportMeasured,
                    ApplicationSignalAfterRenderCompleteMs = firstViewportImageWait.ApplicationSignalAfterRenderCompleteMs,
                    LoadingStateTransitions = firstViewportImageWait.LoadingStateTransitions
                        .Select(transition => new ReadmeAuditVisibleImageLoadingStateTransition
                        {
                            ElapsedMilliseconds = transition.ElapsedMilliseconds,
                            HasLoadingVisibleImages = transition.HasLoadingVisibleImages,
                        })
                        .ToArray(),
                }
                : null,
            ExperienceFirstRenderMs = result.ExperienceFirstRenderMs,
            ColdStartToFirstRenderMs = result.ColdStartToFirstRenderMs,
            FullTraversalMs = result.FullTraversalMs,
            FullTraversalViewportStepRatio = result.FullTraversalViewportStepRatio,
            FullTraversalViewportCount = result.FullTraversalViewportCount,
            AuditOverheadMs = result.AuditOverheadMs,
            FirstRenderCpuMs = result.FirstRenderCpuMs,
            CpuMs = result.CpuMs,
            FirstPerformance = result.FirstPerformance,
            FullPerformance = result.FullPerformance,
            PeakWorkingSetBytes = result.PeakWorkingSetBytes,
            Text = string.Empty,
            MermaidSources = [],
            Width = result.Width,
            ContentViewportWidth = result.ContentViewportWidth,
            ContentViewportHeight = result.ContentViewportHeight,
            RasterizationScale = result.RasterizationScale,
            EstimatedContentHeight = result.EstimatedContentHeight,
            Tiles = result.Tiles,
            VisibleImageWaits = result.VisibleImageWaits,
            HeadingObservations = result.HeadingObservations,
            LinkObservations = result.LinkObservations,
            ImageObservations = result.ImageObservations,
            TableObservations = result.TableObservations,
            CodeBlockObservations = result.CodeBlockObservations,
            TaskCheckboxObservations = result.TaskCheckboxObservations,
            DisclosureObservations = result.DisclosureObservations,
            ImageSourceCount = result.ImageSourceCount,
            LoadingImagesAfterTraversal = result.LoadingImagesAfterTraversal,
            UnavailableImages = result.UnavailableImages,
            RawUnavailableImages = result.RawUnavailableImages,
            RenderFailure = result.RenderFailure is null ? null : RedactUrls(result.RenderFailure),
            CleanExit = result.CleanExit,
            CloseFailure = result.CloseFailure is null ? null : RedactUrls(result.CloseFailure),
            ReadmeSourceSha256 = result.ReadmeSourceSha256,
        };

    private static NativeAuditResult RunNativeAudit(
        CaptureOptions options,
        ReadmeAuditRepository repository,
        string caseDirectory,
        string accessToken,
        IReadOnlyList<BrowserImage>? renderedBrowserImages,
        string? sameByteCorpusPath,
        string? expectedReadmeSha256,
        long? expectedReadmeBytes,
        bool expectRenderedReadme)
    {
        string output = Path.Combine(caseDirectory, "native");
        string runtime = Path.Combine(caseDirectory, ".runtime");
        string preservedFirstViewportImagesReady = Path.Combine(output, "first-viewport-images-ready.json");
        string preservedFirstViewportImagesReadyProgress = Path.Combine(output, "first-viewport-images-ready-progress.ndjson");
        // Keep diagnostics from retries isolated. Reusing a case directory used
        // to append a previous run's failures/resolutions to the new evidence.
        string dataRoot = Path.Combine(runtime, $"data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(dataRoot);
        string appReady = Path.Combine(runtime, "app-ready.json");
        string hostReady = Path.Combine(runtime, "host-ready.json");
        string renderComplete = Path.Combine(runtime, "render-complete.json");
        string firstViewportImagesReady = Path.Combine(runtime, "first-viewport-images-ready.json");
        string firstViewportImagesReadyProgress = Path.Combine(runtime, "first-viewport-images-ready-progress.ndjson");
        string renderFailure = Path.Combine(runtime, "render-failure.txt");
        string imageEvidence = Path.Combine(runtime, "image-unavailable.ndjson");
        string imageResolutionEvidence = Path.Combine(runtime, "image-resolution.ndjson");
        string rasterPreparationEvidence = Path.Combine(runtime, "raster-preparation.ndjson");
        string svgWorkerEvidence = Path.Combine(runtime, "svg-worker-timeouts.ndjson");
        string svgPreflightEvidence = Path.Combine(runtime, "svg-preflight-rejections.ndjson");
        string readmeSourceEvidence = Path.Combine(runtime, "readme-source.json");
        string shutdownStageEvidence = Path.Combine(runtime, "shutdown-stage.json");
        string captureRequest = Path.Combine(runtime, "capture-request.json");
        string captureResponse = Path.Combine(runtime, "capture-response.json");
        foreach (string stale in new[]
        {
            appReady, hostReady, renderComplete, firstViewportImagesReady, firstViewportImagesReadyProgress, renderFailure, imageEvidence,
            imageResolutionEvidence, rasterPreparationEvidence,
            svgWorkerEvidence, svgPreflightEvidence,
            readmeSourceEvidence,
            shutdownStageEvidence,
            captureRequest, captureResponse, preservedFirstViewportImagesReady,
        })
        {
            if (File.Exists(stale)) File.Delete(stale);
        }

        string[] arguments =
        [
            "--page=repo-code",
            "--theme=light",
            $"--repo={repository.FullName}",
            $"--branch={repository.CommitSha}",
            "--readme-production-audit",
            $"--markdown-lifecycle-host={HostAutomationId}",
        ];
        var startInfo = new ProcessStartInfo(options.AppPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["JITHUB_PREVIEW_PAGE"] = "repo-code";
        startInfo.Environment["JITHUB_PREVIEW_THEME"] = "light";
        startInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = repository.FullName;
        startInfo.Environment["JITHUB_PREVIEW_BRANCH"] = repository.CommitSha;
        startInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot;
        startInfo.Environment["JITHUB_README_AUDIT_GITHUB_TOKEN"] = accessToken;
        startInfo.Environment["JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID"] =
            Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID")!;
        startInfo.Environment["JITHUB_MARKDOWN_APP_READY_PATH"] = appReady;
        startInfo.Environment["JITHUB_MARKDOWN_HOST_READY_PATH"] = hostReady;
        startInfo.Environment["JITHUB_MARKDOWN_RENDER_COMPLETE_EVIDENCE_PATH"] = renderComplete;
        startInfo.Environment["JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_EVIDENCE_PATH"] =
            firstViewportImagesReady;
        startInfo.Environment["JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_PROGRESS_PATH"] =
            firstViewportImagesReadyProgress;
        startInfo.Environment["JITHUB_MARKDOWN_RENDER_FAILURE_EVIDENCE_PATH"] = renderFailure;
        startInfo.Environment["JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH"] = imageEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH"] = imageResolutionEvidence;
        if (Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_RASTER_DIAGNOSTICS") == "1")
            startInfo.Environment["JITHUB_MARKDOWN_RASTER_PREPARATION_EVIDENCE_PATH"] =
                rasterPreparationEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_SVG_WORKER_EVIDENCE_PATH"] = svgWorkerEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_SVG_PREFLIGHT_EVIDENCE_PATH"] = svgPreflightEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH"] = shutdownStageEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_CAPTURE_REQUEST_PATH"] = captureRequest;
        startInfo.Environment["JITHUB_MARKDOWN_CAPTURE_RESPONSE_PATH"] = captureResponse;
        startInfo.Environment["JITHUB_README_AUDIT_SAME_BYTE_CORPUS"] = null;
        startInfo.Environment["JITHUB_README_AUDIT_SAME_BYTE_README_SHA"] = null;
        startInfo.Environment["JITHUB_README_AUDIT_NATIVE_SOURCE_EVIDENCE_PATH"] = null;
        if (sameByteCorpusPath is not null)
        {
            if (string.IsNullOrWhiteSpace(sameByteCorpusPath) ||
                !Directory.Exists(sameByteCorpusPath) ||
                !File.Exists(Path.Combine(sameByteCorpusPath, "manifest.json")))
            {
                throw new InvalidDataException(
                    "The freshly captured same-byte corpus is missing or incomplete.");
            }
            startInfo.Environment["JITHUB_README_AUDIT_SAME_BYTE_CORPUS"] = sameByteCorpusPath;
            startInfo.Environment["JITHUB_README_AUDIT_SAME_BYTE_README_SHA"] = repository.Readme.Sha;
            startInfo.Environment["JITHUB_README_AUDIT_NATIVE_SOURCE_EVIDENCE_PATH"] =
                readmeSourceEvidence;
        }

        Stopwatch wall = Stopwatch.StartNew();
        using Process launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start JitHub for README audit.");
        Process? appProcess = null;
        Application? application = null;
        Window? window = null;
        try
        {
            WriteNativeAuditStage("waiting for app-ready signal");
            int processId = WaitForReadySignal(appReady, launcher, TimeSpan.FromSeconds(30));
            WriteNativeAuditStage("app-ready signal observed");
            double appReadyElapsedMs = wall.Elapsed.TotalMilliseconds;
            appProcess = Process.GetProcessById(processId);
            appProcess.Refresh();
            double cpuAtReadyMs = appProcess.TotalProcessorTime.TotalMilliseconds;
            WriteNativeAuditStage("attaching UI Automation application");
            application = Application.Attach(processId);
            WriteNativeAuditStage("UI Automation application attached");
            WriteNativeAuditStage("creating UI Automation client");
            using var automation = new UIA3Automation();
            automation.ConnectionTimeout = UiaProviderConnectionTimeout;
            automation.TransactionTimeout = UiaProviderTransactionTimeout;
            WriteNativeAuditStage("UI Automation client created");
            WriteNativeAuditStage("waiting for app window");
            window = WaitForWindow(application, automation, TimeSpan.FromSeconds(30));
            WriteNativeAuditStage("app window observed");
            IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
            WriteNativeAuditStage("resizing and activating app window");
            NativeMethods.ResizeWindow(windowHandle, ViewportWidth, ViewportHeight);
            NativeMethods.ActivateForKeyboard(windowHandle);
            Thread.Sleep(300);
            WriteNativeAuditStage("app window activated");

            if (!repository.Readme.Available)
            {
                AutomationElement tree = WaitForAutomationElement(
                    window,
                    "RepoCodeFileTree",
                    TimeSpan.FromSeconds(60),
                    Path.Combine(output, "missing-readme-timeout.png"));
                Stopwatch treeProbe = Stopwatch.StartNew();
                string treeText = WaitForRepositoryTreeReady(tree, TimeSpan.FromSeconds(45));
                treeProbe.Stop();
                if (window.FindFirstDescendant(cf => cf.ByAutomationId(HostAutomationId)) is not null)
                {
                    throw new InvalidOperationException(
                        "JitHub exposed a rendered README for a repository without one.");
                }

                double absentColdStartMs = wall.Elapsed.TotalMilliseconds;
                double absentReadyMs = absentColdStartMs - appReadyElapsedMs;
                Stopwatch capture = Stopwatch.StartNew();
                (int width, int height) = CaptureHost(
                    window,
                    tree,
                    Path.Combine(output, "missing-readme-view.png"),
                    captureRequest,
                    captureResponse,
                    out _,
                    useRendererCapture: false);
                capture.Stop();
                appProcess.Refresh();
                double absentCpuMs = Math.Max(
                    0,
                    appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
                int absentRawUnavailable = CountNonEmptyLines(imageEvidence);
                string? absentFailure = File.Exists(renderFailure) ? File.ReadAllText(renderFailure) : null;
                long absentPeakWorkingSetBytes = appProcess.PeakWorkingSet64;
                ReadmeAuditCloseResult absentClose = CloseAndWait(appProcess, launcher);
                if (!absentClose.CleanExit)
                    PreserveShutdownExceptionDiagnostics(dataRoot, output);
                PreserveEvidenceFile(shutdownStageEvidence, Path.Combine(output, "shutdown-stage.json"));
                PreserveEvidenceFile(svgPreflightEvidence, Path.Combine(output, "svg-preflight-rejections.ndjson"));
                window = null;
                Console.WriteLine("README native audit complete.");
                return new NativeAuditResult
                {
                    FirstRenderMs = absentReadyMs,
                    ExperienceFirstRenderMs = absentReadyMs,
                    ColdStartToFirstRenderMs = absentColdStartMs,
                    FullTraversalMs = absentReadyMs,
                    AuditOverheadMs = treeProbe.Elapsed.TotalMilliseconds + capture.Elapsed.TotalMilliseconds,
                    FirstRenderCpuMs = absentCpuMs,
                    CpuMs = absentCpuMs,
                    PeakWorkingSetBytes = absentPeakWorkingSetBytes,
                    Text = treeText,
                    Width = width,
                    EstimatedContentHeight = height,
                    UnavailableImages = absentRawUnavailable,
                    RawUnavailableImages = absentRawUnavailable,
                    RenderFailure = absentFailure,
                    CleanExit = absentClose.CleanExit,
                    CloseFailure = absentClose.Failure,
                };
            }

            WriteNativeAuditStage("opening pinned README");
            TryOpenReadme(window, repository.Readme.Path, TimeSpan.FromSeconds(45));
            WriteNativeAuditStage("pinned README open request completed");
            if (!expectRenderedReadme)
            {
                AutomationElement? sourceEditor = WaitForSourceEditorOrRenderedHost(
                    window,
                    TimeSpan.FromSeconds(60),
                    Path.Combine(output, "source-or-rendered-timeout.png"));
                if (sourceEditor is not null)
                {
                    Stopwatch sourceTextProbe = Stopwatch.StartNew();
                    string sourceText = WaitForStableText(sourceEditor, TimeSpan.FromSeconds(30));
                    sourceTextProbe.Stop();
                    double sourceColdStartMs = wall.Elapsed.TotalMilliseconds;
                    double sourceFirstRenderMs = sourceColdStartMs - appReadyElapsedMs;
                    Stopwatch capture = Stopwatch.StartNew();
                    (int width, int height) = CaptureHost(
                        window,
                        sourceEditor,
                        Path.Combine(output, "source-view.png"),
                        captureRequest,
                        captureResponse,
                        out _,
                        useRendererCapture: false);
                    capture.Stop();
                    appProcess.Refresh();
                    double sourceCpuMs = Math.Max(
                        0,
                        appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
                    string? sourceFailure = File.Exists(renderFailure) ? File.ReadAllText(renderFailure) : null;
                    long sourcePeakWorkingSetBytes = appProcess.PeakWorkingSet64;
                    ReadmeAuditCloseResult sourceClose = CloseAndWait(appProcess, launcher);
                    if (!sourceClose.CleanExit)
                        PreserveShutdownExceptionDiagnostics(dataRoot, output);
                    PreserveEvidenceFile(shutdownStageEvidence, Path.Combine(output, "shutdown-stage.json"));
                    PreserveEvidenceFile(svgPreflightEvidence, Path.Combine(output, "svg-preflight-rejections.ndjson"));
                    string? sourceReadmeSha256 = sameByteCorpusPath is null
                        ? null
                        : ReadSameByteSourceEvidence(
                            readmeSourceEvidence, output, expectedReadmeSha256, expectedReadmeBytes);
                    window = null;
                    Console.WriteLine("README native audit complete.");
                    return new NativeAuditResult
                    {
                        FirstRenderMs = sourceFirstRenderMs,
                        ExperienceFirstRenderMs = sourceFirstRenderMs,
                        ColdStartToFirstRenderMs = sourceColdStartMs,
                        FullTraversalMs = sourceFirstRenderMs,
                        AuditOverheadMs = sourceTextProbe.Elapsed.TotalMilliseconds + capture.Elapsed.TotalMilliseconds,
                        FirstRenderCpuMs = sourceCpuMs,
                        CpuMs = sourceCpuMs,
                        PeakWorkingSetBytes = sourcePeakWorkingSetBytes,
                        Text = NormalizeText(sourceText),
                        Width = width,
                        EstimatedContentHeight = height,
                        UnavailableImages = 0,
                        RawUnavailableImages = 0,
                        RenderFailure = sourceFailure,
                        CleanExit = sourceClose.CleanExit,
                        CloseFailure = sourceClose.Failure,
                        ReadmeSourceSha256 = sourceReadmeSha256,
                    };
                }
            }

            WriteNativeAuditStage("waiting for Markdown host");
            AutomationElement host = WaitForHost(
                window,
                TimeSpan.FromSeconds(60),
                Path.Combine(output, "host-timeout.png"));
            WriteNativeAuditStage("Markdown host observed");
            // The outer app is 1000px wide, but its actual Markdown reading
            // pane can be much narrower. Measure that clipped, visible pane
            // before comparing a client-side Edge render; audit tiles may be
            // up to 8192px tall and are not the interaction viewport.
            Rectangle visibleHostPixels = Rectangle.Intersect(
                host.BoundingRectangle,
                NativeMethods.GetPhysicalWindowBounds(windowHandle));
            double rasterizationScale = NativeMethods.GetWindowDpi(windowHandle) / 96.0;
            int contentViewportWidth = (int)Math.Floor(visibleHostPixels.Width / rasterizationScale);
            int contentViewportHeight = (int)Math.Floor(visibleHostPixels.Height / rasterizationScale);
            if (contentViewportWidth <= 0 || contentViewportHeight <= 0 ||
                contentViewportWidth > 8192 || contentViewportHeight > 8192)
            {
                throw new InvalidOperationException(
                    "The visible native Markdown content viewport could not be measured.");
            }
            WriteNativeAuditStage("waiting for render-complete signal");
            WaitForRenderSignal(
                renderComplete,
                renderFailure,
                TimeSpan.FromSeconds(45));
            WriteNativeAuditStage("render-complete observed");
            double coldStartToFirstRenderMs = wall.Elapsed.TotalMilliseconds;
            double experienceFirstRenderMs = coldStartToFirstRenderMs - appReadyElapsedMs;
            NativeLifecycleReadySignal hostReadySignal =
                NativeFirstViewportImagesReadyContract.ReadLifecycleReadySignal(hostReady);
            NativeRenderCompleteSignal renderCompleteSignal =
                NativeFirstViewportImagesReadyContract.ReadRenderCompleteSignal(renderComplete);
            NativeFirstViewportImagesReadyContract.ValidateRenderIdentity(
                processId,
                HostAutomationId,
                hostReadySignal,
                renderCompleteSignal);
            double firstRenderMs = (renderCompleteSignal.Timestamp - hostReadySignal.Timestamp).TotalMilliseconds;
            if (!double.IsFinite(firstRenderMs) || firstRenderMs < 0)
            {
                throw new InvalidDataException(
                    "The native render-complete timestamp preceded the verified host-ready timestamp.");
            }
            ReadmeAuditPerformanceSnapshot firstPerformance = ReadPerformanceSnapshot(renderComplete);
            WriteNativeAuditStage("first-viewport image wait starting");
            VisibleImageWaitResult firstImageWait = WaitForVisibleImages(
                host,
                TimeSpan.FromSeconds(20),
                captureLoadingStateTransitions: true);
            WriteNativeAuditStage("first-viewport UIA image wait complete");
            if (firstImageWait.TimedOut)
                throw new TimeoutException("The first native README viewport did not finish loading images.");
            WriteNativeAuditStage("first-viewport app readiness signal waiting");
            string? expectedReadmeGitBlobSha1 = sameByteCorpusPath is null
                ? null
                : repository.Readme.Sha;
            NativeFirstViewportImagesReadySignal firstImagesReadySignal =
                WaitForNativeFirstViewportImagesReadySignal(
                    firstViewportImagesReady,
                    processId,
                    HostAutomationId,
                    renderCompleteSignal,
                    expectedReadmeGitBlobSha1,
                    TimeSpan.FromSeconds(2));
            PreserveEvidenceFile(
                firstViewportImagesReady,
                Path.Combine(output, "first-viewport-images-ready.json"));
            PreserveEvidenceFile(
                firstViewportImagesReadyProgress,
                Path.Combine(output, "first-viewport-images-ready-progress.ndjson"));
            NativeFirstViewportImagesReadyContract.ValidateImagesReadySignal(
                processId,
                HostAutomationId,
                expectedReadmeGitBlobSha1,
                hostReadySignal,
                renderCompleteSignal,
                firstImagesReadySignal);
            double firstViewportImagesReadyMs =
                (firstImagesReadySignal.Timestamp - hostReadySignal.Timestamp).TotalMilliseconds;
            double readyDetectionAfterRenderCompleteMs =
                (firstImagesReadySignal.Timestamp - renderCompleteSignal.Timestamp).TotalMilliseconds;
            if (!double.IsFinite(firstViewportImagesReadyMs) ||
                firstViewportImagesReadyMs < firstRenderMs ||
                !double.IsFinite(readyDetectionAfterRenderCompleteMs) ||
                readyDetectionAfterRenderCompleteMs < 0)
            {
                throw new InvalidDataException(
                    "The native first-viewport readiness timestamp was not monotonic with render completion.");
            }
            WriteNativeAuditStage("first-viewport app readiness signal observed");
            appProcess.Refresh();
            double firstRenderCpuMs = Math.Max(
                0,
                appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
            Stopwatch textProbe = Stopwatch.StartNew();
            WriteNativeAuditStage("stable README text probe starting");
            string text = WaitForStableText(host, TimeSpan.FromSeconds(30));
            WriteNativeAuditStage("stable README text probe complete");
            textProbe.Stop();

            NativeTraversalResult traversal = CaptureNativeTiles(
                window,
                host,
                firstImagesReadySignal,
                output,
                renderFailure,
                appProcess,
                captureRequest,
                captureResponse);
            ReadmeAuditPerformanceSnapshot fullPerformance = ReadPerformanceSnapshot(captureResponse);
            // Keep repository navigation, API loading, UIA stability checks, and
            // screenshot capture out of the renderer comparison. The lifecycle
            // signals measure initial Markdown host publication, while elapsed
            // work after RenderCompleted covers lazy realization during the
            // full-document traversal.
            double postInitialTraversalMs = Math.Max(
                0,
                wall.Elapsed.TotalMilliseconds - coldStartToFirstRenderMs -
                textProbe.Elapsed.TotalMilliseconds - traversal.AuditOverheadMs);
            double fullTraversalMs = firstRenderMs + postInitialTraversalMs;
            appProcess.Refresh();
            double cpuMs = Math.Max(
                firstRenderCpuMs,
                appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
            long peakWorkingSetBytes = appProcess.PeakWorkingSet64;
            string? failure = File.Exists(renderFailure) ? File.ReadAllText(renderFailure) : null;
            int rawUnavailable = CountNonEmptyLines(imageEvidence);
            int unavailable = CountRenderedUnavailableImages(
                imageEvidence,
                renderedBrowserImages);
            // Resolver evidence covers remote assets; self-contained data SVGs
            // (including inert HTML-media placeholders) intentionally bypass
            // the resolver. UIA observes both paths after full traversal.
            int imageSourceCount = Math.Max(
                CountDistinctImageSources(imageResolutionEvidence),
                traversal.Images);
            if (options.AuditCaptureSameByteCorpus)
            {
                PreserveSanitizedEvidenceFile(imageEvidence, Path.Combine(output, "image-unavailable.ndjson"));
                PreserveSanitizedEvidenceFile(imageResolutionEvidence, Path.Combine(output, "image-resolution.ndjson"));
            }
            else
            {
                PreserveEvidenceFile(imageEvidence, Path.Combine(output, "image-unavailable.ndjson"));
                PreserveEvidenceFile(imageResolutionEvidence, Path.Combine(output, "image-resolution.ndjson"));
            }
            PreserveEvidenceFile(rasterPreparationEvidence, Path.Combine(output, "raster-preparation.ndjson"));
            PreserveEvidenceFile(svgWorkerEvidence, Path.Combine(output, "svg-worker-timeouts.ndjson"));
            PreserveEvidenceFile(svgPreflightEvidence, Path.Combine(output, "svg-preflight-rejections.ndjson"));

            ReadmeAuditCloseResult close = CloseAndWait(appProcess, launcher);
            if (!close.CleanExit)
                PreserveShutdownExceptionDiagnostics(dataRoot, output);
            PreserveEvidenceFile(shutdownStageEvidence, Path.Combine(output, "shutdown-stage.json"));
            string? nativeReadmeSha256 = sameByteCorpusPath is null
                ? null
                : ReadSameByteSourceEvidence(
                    readmeSourceEvidence, output, expectedReadmeSha256, expectedReadmeBytes);
            window = null;
            Console.WriteLine("README native audit complete.");
            return new NativeAuditResult
            {
                FirstRenderMs = firstRenderMs,
                FirstViewportImagesReadyMs = firstViewportImagesReadyMs,
                FirstViewportImageWait = new ReadmeAuditFirstViewportImageWait
                {
                    ElapsedMilliseconds = firstImageWait.ElapsedMilliseconds,
                    ProbeOverheadMs = firstImageWait.ProbeOverheadMs,
                    PollCount = firstImageWait.PollCount,
                    InitialHasLoadingVisibleImages = firstImageWait.InitialHasLoadingVisibleImages,
                    ApplicationSignalGeneration = firstImagesReadySignal.Generation,
                    ApplicationSignalViewportPaintGeneration = firstImagesReadySignal.ViewportPaintGeneration,
                    ApplicationSignalPollCount = firstImagesReadySignal.PollCount,
                    ApplicationSignalProbeWorkMilliseconds = firstImagesReadySignal.ProbeWorkMilliseconds,
                    ApplicationSignalViewportTop = firstImagesReadySignal.ViewportTop,
                    ApplicationSignalViewportHeight = firstImagesReadySignal.ViewportHeight,
                    ApplicationSignalViewportMeasured = firstImagesReadySignal.ViewportMeasured,
                    ApplicationSignalAfterRenderCompleteMs = readyDetectionAfterRenderCompleteMs,
                    LoadingStateTransitions = firstImageWait.LoadingStateTransitions,
                },
                ExperienceFirstRenderMs = experienceFirstRenderMs,
                ColdStartToFirstRenderMs = coldStartToFirstRenderMs,
                FullTraversalMs = fullTraversalMs,
                FullTraversalViewportStepRatio = traversal.ViewportStepRatio,
                FullTraversalViewportCount = traversal.Tiles.Count,
                AuditOverheadMs = textProbe.Elapsed.TotalMilliseconds + traversal.AuditOverheadMs,
                FirstRenderCpuMs = firstRenderCpuMs,
                CpuMs = cpuMs,
                FirstPerformance = firstPerformance,
                FullPerformance = fullPerformance,
                PeakWorkingSetBytes = peakWorkingSetBytes,
                Text = NormalizeText(text),
                MermaidSources = traversal.MermaidSources,
                Width = traversal.Width,
                ContentViewportWidth = contentViewportWidth,
                ContentViewportHeight = contentViewportHeight,
                RasterizationScale = rasterizationScale,
                EstimatedContentHeight = traversal.EstimatedContentHeight,
                Tiles = traversal.Tiles,
                VisibleImageWaits = traversal.VisibleImageWaits,
                HeadingObservations = traversal.Headings,
                LinkObservations = traversal.Links,
                ImageObservations = traversal.Images,
                TableObservations = traversal.Tables,
                CodeBlockObservations = traversal.CodeBlocks,
                TaskCheckboxObservations = traversal.TaskCheckboxes,
                DisclosureObservations = traversal.Disclosures,
                ImageSourceCount = imageSourceCount,
                LoadingImagesAfterTraversal = traversal.LoadingImages,
                UnavailableImages = unavailable,
                RawUnavailableImages = rawUnavailable,
                RenderFailure = failure,
                CleanExit = close.CleanExit,
                CloseFailure = close.Failure,
                ReadmeSourceSha256 = nativeReadmeSha256,
            };
        }
        catch
        {
            PreserveEvidenceFile(
                firstViewportImagesReady,
                Path.Combine(output, "first-viewport-images-ready.json"));
            PreserveEvidenceFile(
                firstViewportImagesReadyProgress,
                Path.Combine(output, "first-viewport-images-ready-progress.ndjson"));
            PreserveEvidenceFile(
                rasterPreparationEvidence,
                Path.Combine(output, "raster-preparation.ndjson"));
            PreserveStartupDiagnostics(dataRoot, output, launcher);
            throw;
        }
        finally
        {
            try { application?.Dispose(); } catch { }
            if (appProcess is not null)
            {
                try
                {
                    if (!appProcess.HasExited) appProcess.Kill(entireProcessTree: true);
                }
                catch { }
                appProcess.Dispose();
            }
            try
            {
                if (!launcher.HasExited) launcher.Kill(entireProcessTree: true);
            }
            catch { }
            if (options.AuditCaptureSameByteCorpus)
            {
                SanitizeSameByteRuntimeEvidence(runtime);
            }
        }
    }

    private static string ReadSameByteSourceEvidence(
        string evidencePath,
        string outputDirectory,
        string? expectedSha256,
        long? expectedBytes)
    {
        if (!IsSha256(expectedSha256) || expectedBytes is null or < 0 or > 16_777_216)
            throw new InvalidDataException("Same-byte README capture identity is missing or over budget.");

        FileInfo info = new(evidencePath);
        if (!info.Exists || info.Length <= 0 || info.Length > 1_024 ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("JitHub did not record the README source it rendered.");
        }

        using JsonDocument sourceEvidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        JsonElement root = sourceEvidence.RootElement;
        string? actualSha256 = root.GetProperty("Sha256").GetString();
        if (root.GetProperty("ByteSize").GetInt64() != expectedBytes ||
            !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("JitHub rendered README bytes outside the captured corpus.");
        }

        PreserveEvidenceFile(evidencePath, Path.Combine(outputDirectory, "readme-source.json"));
        return actualSha256!;
    }

    private static NativeTraversalResult CaptureNativeTiles(
        Window window,
        AutomationElement host,
        NativeFirstViewportImagesReadySignal firstImagesReadySignal,
        string output,
        string renderFailurePath,
        Process appProcess,
        string captureRequestPath,
        string captureResponsePath)
    {
        WriteNativeAuditStage("checking host ScrollPattern support");
        if (!host.Patterns.Scroll.IsSupported)
            throw new InvalidOperationException("Repository README host does not expose read-only ScrollPattern measurements.");
        WriteNativeAuditStage("acquiring host ScrollPattern");
        var scroll = host.Patterns.Scroll.Pattern;
        WriteNativeAuditStage("host ScrollPattern acquired");
        var tiles = new List<AuditTile>();
        var visibleImageWaits = new List<ReadmeAuditVisibleImageWait>();
        int headingObservations = 0;
        int linkObservations = 0;
        int imageObservations = 0;
        int tableObservations = 0;
        int codeBlockObservations = 0;
        int taskCheckboxObservations = 0;
        int disclosureObservations = 0;
        int loadingAfterTraversal = 0;
        int width = 0;
        int viewportHeight = 0;
        double auditOverheadMs = 0;
        bool reachedBottom = false;
        double terminalCandidatePercent = double.NaN;
        double terminalCandidateViewSize = double.NaN;
        var pendingViewportMovementOffsets = new List<double>();
        bool pendingNativeMovement = false;
        int positionCorrectionCount = 0;
        int traversalPositionCount = 0;
        Stopwatch traversalWall = Stopwatch.StartNew();
        WriteNativeTraversalProgress("starting");
        while (tiles.Count < MaximumTraversalViewports)
        {
            int index = tiles.Count;
            if (++traversalPositionCount > MaximumNativeViewportPositions)
            {
                throw new InvalidOperationException(
                    $"README traversal exceeded the {MaximumNativeViewportPositions}-position safety ceiling before reaching the document end.");
            }
            if (traversalWall.Elapsed >= NativeTraversalTimeout)
            {
                throw new TimeoutException(
                    $"README traversal exceeded the {NativeTraversalTimeout.TotalSeconds:F0}-second native deadline.");
            }
            if (appProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "JitHub exited during README traversal.");
            }
            if (File.Exists(renderFailurePath))
            {
                throw new InvalidOperationException("JitHub reported a renderer exception during traversal.");
            }
            // Dynamic image realization can change both percent and view size
            // while visible images finish. Require both to remain stable across
            // image readiness before capturing or choosing the next viewport.
            double actual = 0;
            double currentViewSize = 100;
            double visibleImageWaitElapsedMs = 0;
            bool viewportStable = false;
            Stopwatch automationProbe = Stopwatch.StartNew();
            for (int stabilizationPass = 0;
                 stabilizationPass <= MaximumViewportPositionCorrections;
                 stabilizationPass++)
            {
                scroll = host.Patterns.Scroll.Pattern;
                auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
                automationProbe.Restart();
                double beforeImagesPercent = scroll.VerticalScrollPercent.ValueOrDefault;
                double beforeImagesViewSize = scroll.VerticalViewSize.ValueOrDefault;
                automationProbe.Stop();
                auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;

                VisibleImageWaitResult imageWait = WaitForVisibleImages(host, TimeSpan.FromSeconds(20));
                auditOverheadMs += imageWait.ProbeOverheadMs;
                visibleImageWaitElapsedMs += imageWait.ElapsedMilliseconds;
                if (imageWait.TimedOut)
                {
                    throw new TimeoutException(
                        $"README viewport {index} did not finish loading its visible images.");
                }

                scroll = host.Patterns.Scroll.Pattern;
                auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
                scroll = host.Patterns.Scroll.Pattern;
                automationProbe.Restart();
                double afterImagesPercent = scroll.VerticalScrollPercent.ValueOrDefault;
                double afterImagesViewSize = scroll.VerticalViewSize.ValueOrDefault;
                automationProbe.Stop();
                auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
                if (double.IsFinite(afterImagesPercent) && double.IsFinite(afterImagesViewSize) &&
                    Math.Abs(afterImagesPercent - beforeImagesPercent) <= 0.01 &&
                    Math.Abs(afterImagesViewSize - beforeImagesViewSize) <= 0.01)
                {
                    actual = Math.Clamp(afterImagesPercent, 0, 100);
                    currentViewSize = afterImagesViewSize;
                    viewportStable = true;
                    break;
                }

                // Extent-changing relayout can replace the ScrollPresenter's
                // UIA provider, so retry against the host's current pattern.
                scroll = host.Patterns.Scroll.Pattern;
            }
            if (!viewportStable)
            {
                throw new TimeoutException(
                    $"README viewport {index} did not stabilize after visible image readiness.");
            }

            if (double.IsFinite(terminalCandidatePercent))
            {
                scroll = host.Patterns.Scroll.Pattern;
                bool hasTerminalEvidence = actual >= 100 - 0.000001 ||
                    !scroll.VerticallyScrollable.ValueOrDefault;
                if (Math.Abs(actual - terminalCandidatePercent) <= 0.01 &&
                    Math.Abs(currentViewSize - terminalCandidateViewSize) <= 0.01 &&
                    hasTerminalEvidence)
                {
                    // A no-op in-app audit scroll is terminal only with independent
                    // 100% or non-scrollable evidence; this ready re-check
                    // confirms that the extent did not grow.
                    reachedBottom = true;
                    break;
                }
                terminalCandidatePercent = double.NaN;
                terminalCandidateViewSize = double.NaN;
            }

            double scrollTopViewportUnits = GetScrollTopViewportUnits(actual, currentViewSize);
            if (pendingNativeMovement)
            {
                pendingViewportMovementOffsets.Add(scrollTopViewportUnits);
                pendingNativeMovement = false;
                WriteNativeTraversalProgress(
                    $"movement {index:D4}; settled at {scrollTopViewportUnits:F4} viewport heights");
            }
            if (tiles.Count > 0)
            {
                double previousViewportTop = tiles[^1].ScrollTopViewportUnits;
                double movementSinceCapture = scrollTopViewportUnits - previousViewportTop;
                if (!double.IsFinite(movementSinceCapture))
                {
                    throw new InvalidOperationException("README native scroll position was not finite.");
                }
                if (movementSinceCapture <= 0.000001 ||
                    movementSinceCapture > SameByteTraversalViewportStepRatio + 0.000001)
                {
                    if (positionCorrectionCount >= MaximumViewportPositionCorrections)
                    {
                        throw new InvalidOperationException(
                            $"README native viewport position {scrollTopViewportUnits:F4} could not be corrected to a monotonic overlapping step.");
                    }

                    double correctionViewportFraction = movementSinceCapture > SameByteTraversalViewportStepRatio
                        ? -Math.Clamp(
                            movementSinceCapture - SameByteTraversalViewportStepRatio,
                            0.05,
                            MaximumNativeScrollViewportFraction)
                        : Math.Clamp(
                            SameByteTraversalViewportStepRatio - Math.Max(0, movementSinceCapture),
                            0.05,
                            MaximumNativeScrollViewportFraction);
                    automationProbe.Restart();
                    RendererCaptureResponse correctionScrollResponse = RequestRendererScroll(
                        captureRequestPath,
                        captureResponsePath,
                        correctionViewportFraction);
                    automationProbe.Stop();
                    auditOverheadMs += Math.Max(
                        0,
                        automationProbe.Elapsed.TotalMilliseconds -
                            correctionScrollResponse.ScrollOperationMilliseconds);
                    scroll = host.Patterns.Scroll.Pattern;
                    ScrollWaitResult correctionChange = WaitForScrollChange(
                        scroll,
                        actual,
                        TimeSpan.FromSeconds(2));
                    auditOverheadMs += correctionChange.ProbeOverheadMs;
                    if (!correctionChange.Succeeded)
                    {
                        throw new InvalidOperationException(
                            "README in-app scroll correction was a no-op away from the terminal viewport.");
                    }

                    pendingNativeMovement = true;
                    positionCorrectionCount++;
                    WriteNativeTraversalProgress(
                        $"movement correction {positionCorrectionCount:D2}; in-app step {correctionViewportFraction:+0.000;-0.000} viewport heights from {scrollTopViewportUnits:F4}");
                    continue;
                }
            }
            if (visibleImageWaitElapsedMs >= SlowVisibleImageWaitMilliseconds)
            {
                visibleImageWaits.Add(new ReadmeAuditVisibleImageWait
                {
                    TileIndex = index,
                    ElapsedMilliseconds = visibleImageWaitElapsedMs,
                    TimedOut = false,
                    LoadingImageCount = 0,
                });
            }
            string file = $"tile-{index:D4}.png";
            string path = Path.Combine(output, file);
            Stopwatch capture = Stopwatch.StartNew();
            (int tileWidth, int tileHeight) = CaptureHost(
                window,
                host,
                path,
                captureRequestPath,
                captureResponsePath,
                out double capturedDocumentTop);
            capture.Stop();
            auditOverheadMs += capture.Elapsed.TotalMilliseconds;
            if (index == 0)
            {
                NativeFirstViewportImagesReadyContract.ValidateInitialViewportIdentity(
                    firstImagesReadySignal,
                    capturedDocumentTop);
            }
            width = Math.Max(width, tileWidth);
            viewportHeight = Math.Max(viewportHeight, tileHeight);
            double elapsedAtCaptureMs = traversalWall.Elapsed.TotalMilliseconds;
            tiles.Add(new AuditTile
            {
                Index = index,
                ScrollPercent = actual,
                ScrollTopViewportUnits = scrollTopViewportUnits,
                VerticalViewSize = currentViewSize,
                NativeViewportMovementOffsets = pendingViewportMovementOffsets.ToArray(),
                Width = tileWidth,
                Height = tileHeight,
                File = file,
                NativeTraversalElapsedAtCaptureMs = elapsedAtCaptureMs,
                NativeChargedAtCaptureMs = Math.Max(0, elapsedAtCaptureMs - auditOverheadMs),
            });
            positionCorrectionCount = 0;

            WriteNativeTraversalProgress(
                $"viewport {index:D4}/{MaximumTraversalViewports}; captured at {actual:F2}% (top {scrollTopViewportUnits:F4} viewports)");
            pendingViewportMovementOffsets.Clear();
            scroll = host.Patterns.Scroll.Pattern;
            automationProbe.Restart();
            bool verticallyScrollable = scroll.VerticallyScrollable.ValueOrDefault;
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            if (!verticallyScrollable || actual >= 100 - 0.000001)
            {
                reachedBottom = true;
                break;
            }

            if (!double.IsFinite(currentViewSize) || currentViewSize <= 0 || currentViewSize >= 100)
            {
                throw new InvalidOperationException("README viewport reported an invalid UIA view size.");
            }
            bool movementObserved = false;
            bool terminalCandidateFound = false;
            for (int attempt = 0; attempt < MaximumNativeScrollAttempts; attempt++)
            {
                // A request on the existing app-side audit IPC is applied by the
                // MarkdownViewer on its UI thread. UIA remains read-only here and
                // supplies the settled position and viewport-size evidence. Keep
                // the app-reported ChangeView duration in native traversal time;
                // only subtract request-file and response-poll transport overhead.
                automationProbe.Restart();
                RendererCaptureResponse scrollResponse = RequestRendererScroll(
                    captureRequestPath,
                    captureResponsePath,
                    SameByteTraversalViewportStepRatio);
                automationProbe.Stop();
                auditOverheadMs += Math.Max(
                    0,
                    automationProbe.Elapsed.TotalMilliseconds - scrollResponse.ScrollOperationMilliseconds);
                scroll = host.Patterns.Scroll.Pattern;
                ScrollWaitResult scrollChange = WaitForScrollChange(
                    scroll,
                    actual,
                    TimeSpan.FromSeconds(2));
                auditOverheadMs += scrollChange.ProbeOverheadMs;
                if (scrollChange.Succeeded)
                {
                    movementObserved = true;
                    break;
                }

                // Wait for an in-flight layout/scroll to settle, then read from
                // a fresh pattern before deciding whether this was a provider
                // no-op. The non-moving wait is harness overhead, not renderer
                // work; if the fresh read sees movement, retain it in the exact
                // native movement profile instead.
                scroll = host.Patterns.Scroll.Pattern;
                auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
                scroll = host.Patterns.Scroll.Pattern;
                automationProbe.Restart();
                double settled = scroll.VerticalScrollPercent.ValueOrDefault;
                double settledViewSize = scroll.VerticalViewSize.ValueOrDefault;
                bool settledScrollable = scroll.VerticallyScrollable.ValueOrDefault;
                automationProbe.Stop();
                auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;

                if (double.IsFinite(settled) && double.IsFinite(settledViewSize) &&
                    Math.Abs(settled - actual) > MinimumScrollPercentChange)
                {
                    movementObserved = true;
                    break;
                }

                auditOverheadMs += Math.Max(
                    0,
                    scrollChange.ElapsedMs - scrollChange.ProbeOverheadMs);
                bool hasTerminalEvidence = settled >= 100 - 0.000001 || !settledScrollable;
                if (double.IsFinite(settled) && double.IsFinite(settledViewSize) &&
                    Math.Abs(settled - actual) <= 0.01 &&
                    Math.Abs(settledViewSize - currentViewSize) <= 0.01 &&
                    hasTerminalEvidence)
                {
                    // A stable in-app-scroll no-op is terminal only when UIA
                    // independently reports 100% or says the content is not
                    // scrollable. Re-enter the image-ready loop to confirm the
                    // extent after visible lazy images have settled.
                    terminalCandidatePercent = Math.Clamp(settled, 0, 100);
                    terminalCandidateViewSize = settledViewSize;
                    terminalCandidateFound = true;
                    break;
                }

                if (attempt + 1 < MaximumNativeScrollAttempts)
                {
                    WriteNativeTraversalProgress(
                        $"in-app scroll no-op; retrying {attempt + 2}/{MaximumNativeScrollAttempts} after viewport {index:D4}");
                    continue;
                }

                throw new InvalidOperationException(
                    $"README scrolling stopped at {actual:F2}% before the document end after {MaximumNativeScrollAttempts} in-app scroll attempts.");
            }

            if (terminalCandidateFound)
            {
                continue;
            }

            if (movementObserved)
            {
                pendingNativeMovement = true;
                WriteNativeTraversalProgress($"in-app scroll movement observed after viewport {index:D4}");
            }
        }

        if (!reachedBottom)
        {
            throw new InvalidOperationException(
                $"README traversal exceeded the {MaximumTraversalViewports}-view safety ceiling before reaching the document end.");
        }
        EnsureNativeViewportCoverage(tiles);

        Stopwatch semanticProbe = Stopwatch.StartNew();
        AutomationElement[] descendants = host.FindAllDescendants();
        loadingAfterTraversal = descendants.Count(IsLoadingImage);
        semanticProbe.Stop();
        auditOverheadMs += semanticProbe.Elapsed.TotalMilliseconds;
        if (loadingAfterTraversal > 0)
        {
            throw new InvalidOperationException(
                $"README native full traversal left {loadingAfterTraversal} deferred image placeholder(s); the captured movement profile is incomplete.");
        }
        semanticProbe.Restart();
        Dictionary<string, int> automationSemanticHistogram = descendants
            .GroupBy(
                element =>
                    $"{element.ControlType}|{element.ClassName ?? string.Empty}",
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        WriteJson(Path.Combine(output, "automation-semantics.json"), automationSemanticHistogram);
        var nativeLinks = descendants
            .Where(element => element.ControlType == ControlType.Hyperlink)
            .Select(element => new
            {
                Name = ReadAutomationString(() => element.Name),
                ClassName = ReadAutomationString(() => element.ClassName),
                HelpText = ReadAutomationString(
                    () => element.Properties.HelpText.ValueOrDefault),
            })
            .ToArray();
        WriteJson(Path.Combine(output, "automation-links.json"), nativeLinks);
        string[] nativeMermaidSources = descendants
            .Where(element => string.Equals(
                ReadAutomationString(() => element.ClassName),
                "MarkdownDiagram",
                StringComparison.Ordinal))
            .Select(element => ReadAutomationString(
                () => element.Properties.HelpText.ValueOrDefault))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToArray();
        WriteJson(Path.Combine(output, "automation-mermaid-sources.json"), nativeMermaidSources);
        headingObservations = descendants.Count(element => element.ControlType == ControlType.Header);
        // Image-only anchors are links too. The source-bound Edge oracle counts
        // them, and JitHub exposes each as an operable MarkdownLinkedImage UIA
        // hyperlink. Compare distinct destinations so fragmented text anchors
        // do not inflate the native count; image payloads remain gated apart.
        linkObservations = nativeLinks
            .Select(link => link.HelpText)
            .Where(destination => !string.IsNullOrWhiteSpace(destination))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        AutomationElement[] nativeImages = descendants.Where(element =>
            string.Equals(element.ClassName, "MarkdownImage", StringComparison.Ordinal) ||
            string.Equals(element.ClassName, "MarkdownLinkedImage", StringComparison.Ordinal) ||
            string.Equals(element.ClassName, "MarkdownHostedImage", StringComparison.Ordinal)).ToArray();
        imageObservations = nativeImages.Length;
        tableObservations = descendants.Count(element => element.ControlType == ControlType.Table);
        codeBlockObservations = descendants.Count(element =>
            string.Equals(element.ClassName, "MarkdownCodeBlock", StringComparison.Ordinal));
        taskCheckboxObservations = descendants.Count(element =>
            string.Equals(element.ClassName, "MarkdownTaskCheckBox", StringComparison.Ordinal) ||
            string.Equals(element.ClassName, "MarkdownTaskState", StringComparison.Ordinal));
        disclosureObservations = descendants.Count(element =>
            string.Equals(element.ClassName, "MarkdownDisclosure", StringComparison.Ordinal));
        loadingAfterTraversal = descendants.Count(IsLoadingImage);
        semanticProbe.Stop();
        auditOverheadMs += semanticProbe.Elapsed.TotalMilliseconds;
        double viewSize = Math.Clamp(scroll.VerticalViewSize.ValueOrDefault, 0.1, 100);
        int estimatedHeight = viewSize <= 0 ? viewportHeight : (int)Math.Ceiling(viewportHeight * 100 / viewSize);
        double viewportStepRatio = tiles.Count <= 1
            ? SameByteTraversalViewportStepRatio
            : tiles.Zip(tiles.Skip(1), (previous, current) =>
                current.ScrollTopViewportUnits - previous.ScrollTopViewportUnits).Average();
        if (!double.IsFinite(viewportStepRatio) || viewportStepRatio <= 0 || viewportStepRatio >= 1)
        {
            throw new InvalidOperationException(
                $"README native viewport step ratio {viewportStepRatio:F3} did not preserve positive overlap.");
        }
        WriteNativeTraversalProgress(
            $"complete: {tiles.Count} viewports; measured step {viewportStepRatio:F3} viewport heights");
        return new NativeTraversalResult(
            width,
            estimatedHeight,
            tiles,
            headingObservations,
            linkObservations,
            imageObservations,
            tableObservations,
            codeBlockObservations,
            taskCheckboxObservations,
            disclosureObservations,
            nativeMermaidSources,
            loadingAfterTraversal,
            auditOverheadMs,
            visibleImageWaits,
            viewportStepRatio);
    }

    private static double RevisitPendingImages(
        AutomationElement host,
        ref IScrollPattern scroll,
        Process appProcess,
        string renderFailurePath,
        Stopwatch traversalWall,
        string captureRequestPath,
        string captureResponsePath)
    {
        double auditOverheadMs = 0;
        int visited = 0;
        for (int index = 0; index < MaximumTraversalViewports; index++)
        {
            if (traversalWall.Elapsed >= NativeTraversalTimeout)
            {
                throw new TimeoutException(
                    $"README image completion exceeded the {NativeTraversalTimeout.TotalSeconds:F0}-second native deadline.");
            }
            if (appProcess.HasExited)
                throw new InvalidOperationException("JitHub exited while completing deferred README images.");
            if (File.Exists(renderFailurePath))
                throw new InvalidOperationException("JitHub reported a renderer exception while completing deferred images.");

            scroll = host.Patterns.Scroll.Pattern;
            auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
            // Only UIA probing is audit overhead. Time spent waiting for an
            // actual visible image remains part of native full-page latency.
            VisibleImageWaitResult imageWait = WaitForVisibleImages(host, TimeSpan.FromSeconds(20));
            auditOverheadMs += imageWait.ProbeOverheadMs;
            if (imageWait.TimedOut)
            {
                throw new TimeoutException("README image revisit did not finish its visible images.");
            }

            scroll = host.Patterns.Scroll.Pattern;
            Stopwatch probe = Stopwatch.StartNew();
            double actual = scroll.VerticalScrollPercent.ValueOrDefault;
            bool verticallyScrollable = scroll.VerticallyScrollable.ValueOrDefault;
            probe.Stop();
            auditOverheadMs += probe.Elapsed.TotalMilliseconds;
            if (!verticallyScrollable || actual <= 0.000001)
            {
                WriteNativeTraversalProgress($"revisit {visited:D4}; reached top at {actual:F2}%");
                return auditOverheadMs;
            }

            WriteNativeTraversalProgress($"revisit {visited:D4}; visited at {actual:F2}%");
            visited++;
            Stopwatch scrollRequest = Stopwatch.StartNew();
            RendererCaptureResponse scrollResponse = RequestRendererScroll(
                captureRequestPath,
                captureResponsePath,
                -SameByteTraversalViewportStepRatio);
            scrollRequest.Stop();
            auditOverheadMs += Math.Max(
                0,
                scrollRequest.Elapsed.TotalMilliseconds - scrollResponse.ScrollOperationMilliseconds);
            ScrollWaitResult scrollChange = WaitForScrollChange(scroll, actual, TimeSpan.FromSeconds(2));
            auditOverheadMs += scrollChange.ProbeOverheadMs;
            if (scrollChange.Succeeded)
            {
                continue;
            }

            auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
            probe.Restart();
            double settled = scroll.VerticalScrollPercent.ValueOrDefault;
            bool settledScrollable = scroll.VerticallyScrollable.ValueOrDefault;
            probe.Stop();
            auditOverheadMs += probe.Elapsed.TotalMilliseconds;
            if (settled <= 0.000001 || !settledScrollable)
            {
                WriteNativeTraversalProgress($"revisit {visited:D4}; confirmed top at {settled:F2}%");
                return auditOverheadMs;
            }

            throw new InvalidOperationException(
                $"README image revisit stopped at {settled:F2}% before returning to the document start.");
        }

        throw new InvalidOperationException(
            $"README image revisit exceeded its {MaximumTraversalViewports}-viewport safety ceiling.");
    }

    private static void WriteNativeTraversalProgress(string progress)
    {
        Console.WriteLine($"README native traversal {progress}.");
        Console.Out.Flush();
    }

    private static void WriteNativeAuditStage(string stage)
    {
        Console.WriteLine($"README native audit stage: {stage}.");
        Console.Out.Flush();
    }

    private static double GetScrollTopViewportUnits(double verticalPercent, double verticalViewSize)
    {
        if (!double.IsFinite(verticalPercent) || verticalPercent < 0 || verticalPercent > 100 ||
            !double.IsFinite(verticalViewSize) || verticalViewSize <= 0 || verticalViewSize > 100)
        {
            throw new InvalidOperationException("README host exposed an invalid normalized scroll position.");
        }

        return verticalPercent / 100 * ((100 - verticalViewSize) / verticalViewSize);
    }

    private static void EnsureNativeViewportCoverage(IReadOnlyList<AuditTile> tiles)
    {
        if (tiles.Count is < 1 or > MaximumTraversalViewports ||
            !double.IsFinite(tiles[0].ScrollTopViewportUnits) ||
            Math.Abs(tiles[0].ScrollTopViewportUnits) > 1.0 / Math.Max(1, tiles[0].Height) ||
            tiles[0].NativeViewportMovementOffsets.Count != 0)
        {
            throw new InvalidOperationException("README native viewport profile did not begin at the document start.");
        }

        for (int index = 0; index < tiles.Count; index++)
        {
            AuditTile tile = tiles[index];
            if (!double.IsFinite(tile.ScrollTopViewportUnits) || tile.ScrollTopViewportUnits < 0 ||
                !double.IsFinite(tile.VerticalViewSize) || tile.VerticalViewSize <= 0 || tile.VerticalViewSize > 100)
            {
                throw new InvalidOperationException($"README native viewport {index} had invalid coverage coordinates.");
            }
            if (index == 0)
                continue;

            AuditTile previous = tiles[index - 1];
            double step = tile.ScrollTopViewportUnits - previous.ScrollTopViewportUnits;
            if (!double.IsFinite(step) || step <= 0 ||
                step > SameByteTraversalViewportStepRatio + 0.000001 ||
                tile.NativeViewportMovementOffsets.Count is < 1 or > MaximumViewportPositionCorrections + 1 ||
                Math.Abs(tile.NativeViewportMovementOffsets[^1] - tile.ScrollTopViewportUnits) > 0.000001)
            {
                throw new InvalidOperationException(
                    $"README native viewport {index} did not preserve a monotonic, positively overlapping coverage step.");
            }
            if (tile.NativeViewportMovementOffsets.Any(offset => !double.IsFinite(offset) || offset < 0))
            {
                throw new InvalidOperationException($"README native viewport {index} had an invalid movement profile.");
            }
        }

        AuditTile last = tiles[^1];
        double coveredEnd = last.ScrollTopViewportUnits + 1;
        double documentEnd = 100 / last.VerticalViewSize;
        if (coveredEnd + 1.0 / Math.Max(1, last.Height) < documentEnd)
        {
            throw new InvalidOperationException("README native viewport tiles did not cover the confirmed document end.");
        }
    }

    private static ReadmeAuditComparison Compare(
        BrowserAuditResult browser,
        NativeAuditResult native,
        string caseDirectory)
    {
        BrowserSameByteReplaySemanticEvidence? sourceSemantic =
            browser.SameByteReplay is { SchemaVersion: 3, Status: "passed" } sourceReplay &&
            sourceReplay.Semantic is { Complete: true }
                ? sourceReplay.Semantic
                : null;
        string browserVisibleText = string.IsNullOrWhiteSpace(browser.Semantic.VisibleText)
            ? browser.Semantic.Text
            : browser.Semantic.VisibleText;
        IReadOnlyList<string> matchedVisibleMermaidSources = sourceSemantic is null
            ? MatchEquivalentMermaidSources(browser.Semantic.VisibleMermaidSources, native.MermaidSources)
            : MatchEquivalentMermaidSources(
                sourceSemantic.VisibleMermaidSourceDigests,
                native.MermaidSources,
                sourceSemantic.SemanticDigestKeyHex);
        string comparableNativeText = matchedVisibleMermaidSources.Count == 0
            ? native.Text
            : string.Concat(native.Text, " ", string.Join(' ', matchedVisibleMermaidSources));
        // Coverage answers the page-fidelity question: every piece of text
        // visibly rendered by Edge must exist in JitHub's document. Native UIA
        // intentionally also includes names for atomic images. Since TextPattern
        // cannot separate those names from visible prose, accessible-document
        // precision is retained as diagnostic evidence while the structural
        // score uses the comparable visible-text coverage. Image-name and image
        // source correctness are gated independently below.
        double textCoverage = sourceSemantic is null
            ? TokenCoverage(browserVisibleText, comparableNativeText)
            : TokenCoverage(sourceSemantic, comparableNativeText);
        double textPrecision = TokenCoverage(native.Text, browser.Semantic.Text);
        double textFidelity = textCoverage;
        var similarities = new List<double>();
        string browserDirectory = Path.Combine(caseDirectory, "browser");
        string nativeDirectory = Path.Combine(caseDirectory, "native");
        Bitmap? cachedBrowserTile = null;
        string? cachedBrowserTilePath = null;
        try
        {
            foreach (AuditTile tile in native.Tiles)
            {
                using var nativeBitmap = new Bitmap(Path.Combine(nativeDirectory, tile.File));
                double widthScale = browser.Semantic.Width / Math.Max(1, nativeBitmap.Width);
                int browserViewportHeight = Math.Clamp(
                    (int)Math.Round(nativeBitmap.Height * widthScale),
                    1,
                    Math.Max(1, (int)Math.Ceiling(browser.Semantic.Height)));
                double fraction = Math.Clamp(tile.ScrollPercent / 100, 0, 1);
                double browserScrollableHeight = Math.Max(
                    0,
                    browser.Semantic.Height - browserViewportHeight);
                double browserY = fraction * browserScrollableHeight;
                AuditTile browserTile = browser.Tiles
                    .Where(candidate =>
                        candidate.RelativeY <= browserY + 0.5 &&
                        candidate.RelativeY + candidate.Height >= browserY + browserViewportHeight - 0.5)
                    .OrderByDescending(candidate => candidate.RelativeY)
                    .FirstOrDefault()
                    ?? browser.Tiles
                        .Where(candidate =>
                            candidate.RelativeY <= browserY + 0.5 &&
                            candidate.RelativeY + candidate.Height > browserY)
                        .OrderByDescending(candidate => candidate.RelativeY)
                        .FirstOrDefault()
                    ?? browser.Tiles
                        .OrderBy(candidate => Math.Abs(candidate.RelativeY - browserY))
                        .First();

                string browserTilePath = Path.Combine(browserDirectory, browserTile.File);
                if (!string.Equals(cachedBrowserTilePath, browserTilePath, StringComparison.Ordinal))
                {
                    cachedBrowserTile?.Dispose();
                    cachedBrowserTile = new Bitmap(browserTilePath);
                    cachedBrowserTilePath = browserTilePath;
                }

                Bitmap browserTileBitmap = cachedBrowserTile
                    ?? throw new InvalidOperationException("The browser comparison tile was not loaded.");
                int cropHeight = Math.Min(browserViewportHeight, browserTileBitmap.Height);
                int cropY = Math.Clamp(
                    (int)Math.Round(browserY - browserTile.RelativeY),
                    0,
                    browserTileBitmap.Height - cropHeight);
                using Bitmap browserViewport = browserTileBitmap.Clone(
                    new Rectangle(0, cropY, browserTileBitmap.Width, cropHeight),
                    PixelFormat.Format32bppArgb);
                similarities.Add(CalculateSsim(nativeBitmap, browserViewport));
            }
        }
        finally
        {
            cachedBrowserTile?.Dispose();
        }

        double nativeToBrowserFirst = browser.Timing.FirstReadmeMs <= 0
            ? double.PositiveInfinity
            : native.FirstRenderMs / browser.Timing.FirstReadmeMs;
        double nativeToBrowserFull = browser.Timing.SettledReadmeMs <= 0
            ? double.PositiveInfinity
            : native.FullTraversalMs / browser.Timing.SettledReadmeMs;
        // Live GitHub timings include CDN delivery and server work. They remain
        // diagnostics, never the qualified same-byte performance denominator.
        // The offline replay starts with the captured, hash-bound article and
        // image bytes already local on the same machine.
        double? nativeToSameByteFirst = browser.SameByteReplay is { SchemaVersion: 3, Status: "passed" } replay &&
            IsFinitePositive(replay.Timing.FirstViewportImagesReadyMs) &&
            IsFinitePositive(native.FirstViewportImagesReadyMs)
                ? native.FirstViewportImagesReadyMs / replay.Timing.FirstViewportImagesReadyMs
                : null;
        double? nativeToSameByteFull = browser.SameByteReplay is { SchemaVersion: 3, Status: "passed" } fullReplay &&
            IsFinitePositive(fullReplay.Timing.ChargedTraversalMs)
                ? native.FullTraversalMs / fullReplay.Timing.ChargedTraversalMs
                : null;
        double? sourceBoundLayoutExtentRatio = browser.SameByteReplay is { SchemaVersion: 3, Status: "passed" } extentReplay &&
            extentReplay.RenderedExtent.Height > 0 && native.EstimatedContentHeight > 0
                ? native.EstimatedContentHeight / (double)extentReplay.RenderedExtent.Height
                : null;
        int browserImageCount = sourceSemantic?.ImageCount ?? browser.Semantic.Images.Count;
        int browserDistinctImages = sourceSemantic?.DistinctImageCount ?? browser.Semantic.Images
            .Where(IsVisibleRenderedBrowserImage)
            .Select(image => string.IsNullOrWhiteSpace(image.CurrentSource)
                ? image.Source
                : image.CurrentSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .Count();
        int browserMediaCount = sourceSemantic?.MediaCount ?? browser.Semantic.Media.Count;
        int browserDistinctMedia = sourceSemantic?.DistinctMediaCount ?? browser.Semantic.Media
            .Select(media => string.IsNullOrWhiteSpace(media.CurrentSource)
                ? media.Source
                : media.CurrentSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .Count();
        int browserDistinctAtomicMedia = browserDistinctImages + browserDistinctMedia;
        int browserDistinctLinks = sourceSemantic?.DistinctLinkCount ?? browser.Semantic.Links
            .Select(link => link.Href)
            .Where(destination => !string.IsNullOrWhiteSpace(destination))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        double headingFidelity = CountFidelity(
            sourceSemantic?.HeadingCount ?? browser.Semantic.Headings.Count,
            native.HeadingObservations);
        double linkFidelity = CountFidelity(
            browserDistinctLinks,
            native.LinkObservations);
        // Browser image failures remain reference evidence, but a transient Edge
        // download failure must not penalize JitHub for successfully rendering the
        // same authored source. Missing native coverage is still a hard failure below.
        double imageFidelity = CoverageFidelity(browserDistinctAtomicMedia, native.ImageSourceCount);
        double tableFidelity = CountFidelity(
            sourceSemantic?.TableCount ?? browser.Semantic.Tables,
            native.TableObservations);
        int comparableBrowserCodeBlocks = Math.Max(
            0,
            (sourceSemantic?.CodeBlockCount ?? browser.Semantic.CodeBlocks) - matchedVisibleMermaidSources.Count);
        double codeBlockFidelity = CountFidelity(comparableBrowserCodeBlocks, native.CodeBlockObservations);
        double taskCheckboxFidelity = CountFidelity(
            sourceSemantic?.TaskCheckboxCount ?? browser.Semantic.TaskCheckboxes,
            native.TaskCheckboxObservations);
        double detailsFidelity = CountFidelity(
            sourceSemantic?.DetailsCount ?? browser.Semantic.Details,
            native.DisclosureObservations);
        double semanticWidth = sourceSemantic?.Width ?? browser.Semantic.Width;
        double semanticHeight = sourceSemantic?.Height ?? browser.Semantic.Height;
        double expectedNativeHeight = semanticWidth <= 0
            ? semanticHeight
            : semanticHeight * semanticWidth / Math.Max(1, native.Width);
        double layoutExtentRatio = expectedNativeHeight <= 0
            ? 1
            : native.EstimatedContentHeight / expectedNativeHeight;
        double layoutFidelity = RatioFidelity(layoutExtentRatio);
        double visualStructureScore =
            (textFidelity * 0.30) +
            (headingFidelity * 0.10) +
            (linkFidelity * 0.10) +
            (imageFidelity * 0.15) +
            (tableFidelity * 0.10) +
            (codeBlockFidelity * 0.10) +
            (taskCheckboxFidelity * 0.05) +
            (detailsFidelity * 0.05) +
            (layoutFidelity * 0.05);
        return new ReadmeAuditComparison
        {
            FidelityReference = sourceSemantic is null ? "live-github-diagnostic" : "same-byte-source",
            TileSsimReference = "live-github-cross-style-diagnostic",
            TextTokenCoverage = textCoverage,
            TextTokenPrecision = textPrecision,
            TextTokenFidelity = textFidelity,
            MeanTileSsim = similarities.Count == 0 ? 0 : similarities.Average(),
            MinimumTileSsim = similarities.Count == 0 ? 0 : similarities.Min(),
            VisualStructureScore = visualStructureScore,
            LayoutExtentRatio = layoutExtentRatio,
            SourceBoundLayoutExtentRatio = sourceBoundLayoutExtentRatio,
            HeadingCountFidelity = headingFidelity,
            LinkCountFidelity = linkFidelity,
            ImageCountFidelity = imageFidelity,
            TableCountFidelity = tableFidelity,
            CodeBlockCountFidelity = codeBlockFidelity,
            TaskCheckboxCountFidelity = taskCheckboxFidelity,
            DetailsCountFidelity = detailsFidelity,
            NativeToBrowserFirstRenderRatio = nativeToBrowserFirst,
            NativeToBrowserFullPageRatio = nativeToBrowserFull,
            NativeToSameByteFirstRenderRatio = nativeToSameByteFirst,
            NativeToSameByteFullPageRatio = nativeToSameByteFull,
            BrowserImageCount = browserImageCount,
            NativeImageObservations = native.ImageObservations,
            BrowserDistinctImageCount = browserDistinctImages,
            BrowserMediaCount = browserMediaCount,
            BrowserDistinctAtomicMediaCount = browserDistinctAtomicMedia,
            NativeImageSourceCount = native.ImageSourceCount,
            BrowserHeadingCount = sourceSemantic?.HeadingCount ?? browser.Semantic.Headings.Count,
            NativeHeadingObservations = native.HeadingObservations,
            BrowserLinkCount = browserDistinctLinks,
            NativeLinkObservations = native.LinkObservations,
            BrowserTableCount = sourceSemantic?.TableCount ?? browser.Semantic.Tables,
            NativeTableObservations = native.TableObservations,
            BrowserCodeBlockCount = sourceSemantic?.CodeBlockCount ?? browser.Semantic.CodeBlocks,
            NativeCodeBlockObservations = native.CodeBlockObservations,
            BrowserTaskCheckboxCount = sourceSemantic?.TaskCheckboxCount ?? browser.Semantic.TaskCheckboxes,
            NativeTaskCheckboxObservations = native.TaskCheckboxObservations,
            BrowserDetailsCount = sourceSemantic?.DetailsCount ?? browser.Semantic.Details,
            NativeDisclosureObservations = native.DisclosureObservations,
            BrowserVisibleMermaidSources = sourceSemantic?.VisibleMermaidSourceDigests.Count ??
                browser.Semantic.VisibleMermaidSources.Count,
            NativeMermaidDiagrams = native.MermaidSources.Count,
            MatchedMermaidTransformations = matchedVisibleMermaidSources.Count,
        };
    }

    private static IReadOnlyList<string> MatchEquivalentMermaidSources(
        IReadOnlyList<string> browserSources,
        IReadOnlyList<string> nativeSources)
    {
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nativeSources.Count; i++)
        {
            string key = NormalizeMermaidSource(nativeSources[i]);
            if (key.Length > 0)
                available[key] = available.GetValueOrDefault(key) + 1;
        }

        var matched = new List<string>();
        for (int i = 0; i < browserSources.Count; i++)
        {
            string key = NormalizeMermaidSource(browserSources[i]);
            if (key.Length == 0 || available.GetValueOrDefault(key) <= 0)
                continue;
            available[key]--;
            matched.Add(browserSources[i]);
        }
        return matched;
    }

    private static IReadOnlyList<string> MatchEquivalentMermaidSources(
        IReadOnlyList<string> browserSourceDigests,
        IReadOnlyList<string> nativeSources,
        string semanticDigestKey)
    {
        var available = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (string source in nativeSources)
        {
            string normalized = NormalizeMermaidSource(source);
            if (normalized.Length == 0) continue;
            string digest = HashSemanticText(semanticDigestKey, normalized);
            if (!available.TryGetValue(digest, out Queue<string>? matches))
            {
                matches = new Queue<string>();
                available.Add(digest, matches);
            }
            matches.Enqueue(source);
        }

        var matched = new List<string>();
        foreach (string digest in browserSourceDigests)
        {
            if (available.TryGetValue(digest, out Queue<string>? matches) && matches.Count > 0)
                matched.Add(matches.Dequeue());
        }
        return matched;
    }

    private static string NormalizeMermaidSource(string source) =>
        (source ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static ReadmeAuditSummary BuildSummary(
        ReadmeAuditManifest manifest,
        IReadOnlyList<ReadmeAuditRepository> selected,
        IReadOnlyList<ReadmeAuditCaseResult> results,
        bool auditCaptureSameByteCorpus)
    {
        ReadmeAuditCaseResult[] comparisonResults = results
            .Where(result => result.Comparison is not null)
            .ToArray();
        double[] liveFirstRatios = comparisonResults
            .Where(result => double.IsFinite(result.Comparison!.NativeToBrowserFirstRenderRatio))
            .Select(result => result.Comparison!.NativeToBrowserFirstRenderRatio)
            .OrderBy(value => value)
            .ToArray();
        double[] liveFullRatios = comparisonResults
            .Where(result => double.IsFinite(result.Comparison!.NativeToBrowserFullPageRatio))
            .Select(result => result.Comparison!.NativeToBrowserFullPageRatio)
            .OrderBy(value => value)
            .ToArray();
        double[] sameByteFirstRatios = comparisonResults
            .Select(result => result.Comparison!.NativeToSameByteFirstRenderRatio)
            .Where(value => value.HasValue && IsFinitePositive(value.Value))
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        double[] sameByteFullRatios = comparisonResults
            .Select(result => result.Comparison!.NativeToSameByteFullPageRatio)
            .Where(value => value.HasValue && IsFinitePositive(value.Value))
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        bool hasSameByteEvidence = results.Any(result =>
            result.Browser?.SameByteCorpus is not null ||
            result.Browser?.SameByteReplay is not null ||
            result.Comparison?.NativeToSameByteFirstRenderRatio.HasValue == true ||
            result.Comparison?.NativeToSameByteFullPageRatio.HasValue == true);
        bool sameByteRatiosRequired = auditCaptureSameByteCorpus || hasSameByteEvidence;
        bool sameByteRatiosComplete =
            sameByteFirstRatios.Length == comparisonResults.Length &&
            sameByteFullRatios.Length == comparisonResults.Length;
        bool useSameByteRatios = sameByteRatiosRequired && sameByteRatiosComplete;
        bool sourceBoundRatiosIncomplete = sameByteRatiosRequired && !sameByteRatiosComplete;
        double[] firstRatios = sourceBoundRatiosIncomplete
            ? []
            : useSameByteRatios ? sameByteFirstRatios : liveFirstRatios;
        double[] fullRatios = sourceBoundRatiosIncomplete
            ? []
            : useSameByteRatios ? sameByteFullRatios : liveFullRatios;
        string performanceRatioReference = sameByteRatiosRequired
            ? "same-byte Edge"
            : "live GitHub Edge";
        var aggregateFailures = new List<string>();
        double firstP95 = Percentile(firstRatios, 0.95);
        double fullP95 = Percentile(fullRatios, 0.95);
        // A percentile gate must describe the requested corpus, not an
        // arbitrary CI shard. Matrix jobs still fail every individual fidelity,
        // exception, unavailable-content, and clean-exit violation; the merger
        // recomputes these performance gates across exactly all 500 results.
        bool enforceAggregateGates =
            selected.Count == manifest.Repositories.Count &&
            results.Count == manifest.Repositories.Count;
        if (sourceBoundRatiosIncomplete)
        {
            aggregateFailures.Add(
                $"Same-byte aggregate timing ratios were incomplete: first-render {sameByteFirstRatios.Length}/{comparisonResults.Length}, " +
                $"full-page {sameByteFullRatios.Length}/{comparisonResults.Length}; live GitHub ratios were not substituted.");
        }
        if (enforceAggregateGates && !sourceBoundRatiosIncomplete && firstP95 > 1.10)
        {
            aggregateFailures.Add(
                $"Native first-render p95 against {performanceRatioReference} was {firstP95:P1}, above 110%.");
        }
        if (enforceAggregateGates && !sourceBoundRatiosIncomplete && fullP95 > 1.10)
        {
            aggregateFailures.Add(
                $"Native full-page p95 against {performanceRatioReference} was {fullP95:P1}, above 110%.");
        }

        int failed = results.Count(result => !string.Equals(result.Status, "passed", StringComparison.Ordinal));
        return new ReadmeAuditSummary
        {
            SchemaVersion = 1,
            CorpusGeneratedAtUtc = manifest.GeneratedAtUtc,
            Query = manifest.Query,
            StartRank = selected.Min(repository => repository.Rank),
            EndRank = selected.Max(repository => repository.Rank),
            TotalCases = results.Count,
            PassedCases = results.Count - failed,
            FailedCases = failed,
            NativeFirstRenderRatioP50 = Percentile(firstRatios, 0.50),
            NativeFirstRenderRatioP95 = firstP95,
            NativeFullPageRatioP50 = Percentile(fullRatios, 0.50),
            NativeFullPageRatioP95 = fullP95,
            PerformanceRatioReference = performanceRatioReference,
            SourceBoundPerformanceRatiosRequired = sameByteRatiosRequired,
            SourceBoundPerformanceRatiosComplete = sameByteRatiosComplete,
            FirstPerformanceRatioCaseCount = useSameByteRatios
                ? sameByteFirstRatios.Length
                : sourceBoundRatiosIncomplete ? sameByteFirstRatios.Length : liveFirstRatios.Length,
            FullPerformanceRatioCaseCount = useSameByteRatios
                ? sameByteFullRatios.Length
                : sourceBoundRatiosIncomplete ? sameByteFullRatios.Length : liveFullRatios.Length,
            ExpectedPerformanceRatioCaseCount = comparisonResults.Length,
            AggregateGateEnforced = enforceAggregateGates,
            AggregateFailures = aggregateFailures,
            Passed = failed == 0 && aggregateFailures.Count == 0,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static void WriteSummaryMarkdown(
        string path,
        ReadmeAuditSummary summary,
        IReadOnlyList<ReadmeAuditCaseResult> results)
    {
        double[] liveFirstRatios = results
            .Where(result => result.Comparison is not null && double.IsFinite(result.Comparison.NativeToBrowserFirstRenderRatio))
            .Select(result => result.Comparison!.NativeToBrowserFirstRenderRatio)
            .OrderBy(value => value)
            .ToArray();
        double[] liveFullRatios = results
            .Where(result => result.Comparison is not null && double.IsFinite(result.Comparison.NativeToBrowserFullPageRatio))
            .Select(result => result.Comparison!.NativeToBrowserFullPageRatio)
            .OrderBy(value => value)
            .ToArray();
        double[] sameByteFirstRatios = results
            .Select(result => result.Comparison?.NativeToSameByteFirstRenderRatio)
            .Where(value => value.HasValue && double.IsFinite(value.Value) && value.Value > 0)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        double[] sameByteFullRatios = results
            .Select(result => result.Comparison?.NativeToSameByteFullPageRatio)
            .Where(value => value.HasValue && double.IsFinite(value.Value) && value.Value > 0)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        string FormatPercentiles(double[] ratios) => ratios.Length == 0
            ? "n/a"
            : $"p50 {Percentile(ratios, 0.50).ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"p95 {Percentile(ratios, 0.95).ToString("F3", CultureInfo.InvariantCulture)} " +
                $"({ratios.Length} cases)";
        string FormatSelectedPercentiles(double p50, double p95, int caseCount) =>
            $"p50 {p50.ToString("F3", CultureInfo.InvariantCulture)}, " +
            $"p95 {p95.ToString("F3", CultureInfo.InvariantCulture)} " +
            $"({caseCount} cases)";
        string FormatSourceBoundIncomplete() =>
            $"incomplete (first-render {sameByteFirstRatios.Length}/{summary.ExpectedPerformanceRatioCaseCount}, " +
            $"full-page {sameByteFullRatios.Length}/{summary.ExpectedPerformanceRatioCaseCount} rendered cases)";
        string FormatSelectedRatio(double p50, double p95, int caseCount) =>
            summary.SourceBoundPerformanceRatiosRequired && !summary.SourceBoundPerformanceRatiosComplete
                ? FormatSourceBoundIncomplete()
                : FormatSelectedPercentiles(p50, p95, caseCount);
        string aggregateGateLabel = summary.AggregateGateEnforced
            ? " (aggregate gate metric)"
            : " (partial selection; gate not evaluated)";

        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("# Top README rendering audit");
        writer.WriteLine();
        writer.WriteLine($"- Result: **{(summary.Passed ? "PASS" : "FAIL")}**");
        writer.WriteLine($"- Corpus: ranks {summary.StartRank}–{summary.EndRank}, generated {summary.CorpusGeneratedAtUtc:O}");
        writer.WriteLine($"- Cases: {summary.PassedCases} passed, {summary.FailedCases} failed");
        writer.WriteLine(
            $"- Native/{summary.PerformanceRatioReference} first-render ratio{aggregateGateLabel}: " +
            FormatSelectedRatio(summary.NativeFirstRenderRatioP50, summary.NativeFirstRenderRatioP95,
                summary.FirstPerformanceRatioCaseCount));
        writer.WriteLine(
            $"- Native/{summary.PerformanceRatioReference} full-page ratio{aggregateGateLabel}: " +
            FormatSelectedRatio(summary.NativeFullPageRatioP50, summary.NativeFullPageRatioP95,
                summary.FullPerformanceRatioCaseCount));
        if (summary.SourceBoundPerformanceRatiosRequired)
        {
            writer.WriteLine($"- Native/live GitHub Edge first-render ratio (diagnostic): {FormatPercentiles(liveFirstRatios)}");
            writer.WriteLine($"- Native/live GitHub Edge full-page ratio (diagnostic): {FormatPercentiles(liveFullRatios)}");
        }
        writer.WriteLine();
        writer.WriteLine("| Rank | Repository | Result | Text | Structure | Styled viewport SSIM | Native unavailable | Same-byte first ratio | Same-byte full ratio | Live GitHub first ratio (diagnostic) | Live GitHub full ratio (diagnostic) | Parse/extension ms | Setup ms | Initial layout ms | Layout CPU ms | Layout queue ms | Layout worker wall ms | Layout continuation ms | UI publication ms | Commit ms | Overlay reset ms | Plan construction ms | Visible realization ms | Embed realization ms | Highlight scheduling ms | Highlight retirement ms | Highlight band ms | Adornment/focus ms | Final notification ms |");
        writer.WriteLine("| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (ReadmeAuditCaseResult result in results)
        {
            writer.WriteLine(
                $"| {result.Rank} | {result.FullName} | {result.Status} | " +
                $"{result.Comparison?.TextTokenCoverage.ToString("P2", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.VisualStructureScore.ToString("P2", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.MeanTileSsim.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.UnavailableImages.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.NativeToSameByteFirstRenderRatio?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.NativeToSameByteFullPageRatio?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.NativeToBrowserFirstRenderRatio.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.NativeToBrowserFullPageRatio.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.ParseMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.SetupMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.LayoutMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.LayoutCpuMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.LayoutQueueMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.LayoutWorkerWallMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.LayoutContinuationMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.PublicationMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.CommitMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.OverlayResetMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.PlanConstructionMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.VisibleRealizationMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.EmbedRealizationMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.HighlightSchedulingMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.HighlightRetirementMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.HighlightBandSchedulingMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.AdornmentFocusMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.FirstPerformance?.Pipeline.FinalNotificationMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} |");
        }
        if (summary.AggregateFailures.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("## Aggregate failures");
            foreach (string failure in summary.AggregateFailures) writer.WriteLine($"- {failure}");
        }
    }

    private static void TryOpenReadme(Window window, string readmePath, TimeSpan timeout)
    {
        string expectedStatus = "path:" + readmePath;
        Stopwatch stopwatch = Stopwatch.StartNew();
        COMException? lastAutomationTimeout = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                AutomationElement? host = window.FindFirstDescendant(cf => cf.ByAutomationId(HostAutomationId));
                if (host is not null) return;
                AutomationElement? item = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element => string.Equals(
                        element.Properties.ItemStatus.ValueOrDefault,
                        expectedStatus,
                        StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                {
                    // Tree rows can be realized outside the clipped viewport, in
                    // which case pointer synthesis has no clickable point. File
                    // selection is the control's native activation contract and
                    // works for keyboard, UIA, compact drawers, and virtualized
                    // rows without depending on screen coordinates.
                    if (item.Patterns.SelectionItem.IsSupported)
                    {
                        item.Patterns.SelectionItem.Pattern.Select();
                    }
                    else if (item.Patterns.Invoke.IsSupported)
                    {
                        item.Patterns.Invoke.Pattern.Invoke();
                    }
                    else
                    {
                        item.DoubleClick();
                    }
                    return;
                }
            }
            catch (COMException exception) when (exception.HResult == UiaOperationTimeoutHResult)
            {
                // UIA can time out a single cross-process tree query while the
                // WinUI page is still starting. Keep the original 45-second
                // deadline and count this delay in the native timing; a hung
                // app still fails instead of being waived as infrastructure.
                lastAutomationTimeout = exception;
                Console.Error.WriteLine($"README UIA query timed out after {stopwatch.Elapsed.TotalSeconds:F1}s; retrying within the existing deadline.");
            }
            Thread.Sleep(150);
        }

        throw new TimeoutException(
            $"README host or tree item '{readmePath}' did not become available within {timeout.TotalSeconds:F0}s.",
            lastAutomationTimeout);
    }

    private static AutomationElement? WaitForSourceEditorOrRenderedHost(
        Window window,
        TimeSpan timeout,
        string timeoutScreenshotPath)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            AutomationElement? sourceEditor = window.FindFirstDescendant(
                cf => cf.ByAutomationId("RepoCodeEditor"));
            if (sourceEditor is not null &&
                sourceEditor.BoundingRectangle.Width > 0 &&
                sourceEditor.BoundingRectangle.Height > 0)
            {
                return sourceEditor;
            }

            AutomationElement? renderedHost = window.FindFirstDescendant(
                cf => cf.ByAutomationId(HostAutomationId));
            if (renderedHost is not null &&
                renderedHost.BoundingRectangle.Width > 0 &&
                renderedHost.BoundingRectangle.Height > 0)
            {
                return null;
            }

            Thread.Sleep(100);
        }

        try
        {
            IntPtr handle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
            using Bitmap screenshot = NativeMethods.CaptureWindowSurface(handle);
            screenshot.Save(timeoutScreenshotPath, ImageFormat.Png);
        }
        catch
        {
        }

        throw new TimeoutException(
            "JitHub did not expose a source editor or rendered README within the deadline.");
    }

    private static AutomationElement WaitForHost(
        Window window,
        TimeSpan timeout,
        string timeoutScreenshotPath)
        => WaitForAutomationElement(window, HostAutomationId, timeout, timeoutScreenshotPath);

    private static AutomationElement WaitForAutomationElement(
        Window window,
        string automationId,
        TimeSpan timeout,
        string timeoutScreenshotPath)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            AutomationElement? host = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (host is not null && host.BoundingRectangle.Width > 0 && host.BoundingRectangle.Height > 0)
            {
                return host;
            }
            Thread.Sleep(100);
        }
        try
        {
            IntPtr handle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
            using Bitmap screenshot = NativeMethods.CaptureWindowSurface(handle);
            screenshot.Save(timeoutScreenshotPath, ImageFormat.Png);
        }
        catch
        {
            // Preserve the original timeout; diagnostics are best-effort.
        }
        throw new TimeoutException(
            $"JitHub did not expose '{automationId}' within {timeout.TotalSeconds:0} seconds. " +
            $"The last window surface was saved to '{timeoutScreenshotPath}'.");
    }

    private static string WaitForRepositoryTreeReady(
        AutomationElement tree,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string previous = string.Empty;
        int stable = 0;
        while (stopwatch.Elapsed < timeout)
        {
            AutomationElement[] items = tree.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.TreeItem));
            string current = NormalizeText(string.Join(
                '\n',
                items.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name))));
            if (current.Length > 0 && string.Equals(current, previous, StringComparison.Ordinal))
            {
                if (++stable >= 3)
                {
                    return current;
                }
            }
            else
            {
                previous = current;
                stable = 0;
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException("JitHub did not expose a stable repository file tree.");
    }

    private static string WaitForStableText(AutomationElement host, TimeSpan timeout)
    {
        WriteNativeAuditStage("acquiring TextPattern");
        var textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("README host does not expose UIA TextPattern.");
        WriteNativeAuditStage("TextPattern acquired");
        Stopwatch stopwatch = Stopwatch.StartNew();
        string previous = string.Empty;
        int stable = 0;
        int readAttempt = 0;
        while (stopwatch.Elapsed < timeout)
        {
            readAttempt++;
            WriteNativeAuditStage($"TextPattern read attempt {readAttempt}: acquiring DocumentRange");
            ITextRange documentRange = textPattern.DocumentRange;
            WriteNativeAuditStage($"TextPattern read attempt {readAttempt}: reading bounded text range");
            string current = NormalizeText(ReadDocumentTextInBoundedChunks(documentRange));
            if (current.Length > 0 && string.Equals(current, previous, StringComparison.Ordinal))
            {
                if (++stable >= 3) return current;
            }
            else
            {
                stable = 0;
                previous = current;
            }
            Thread.Sleep(150);
        }
        if (previous.Length > 0) return previous;
        throw new TimeoutException("README UIA text remained empty after rendering.");
    }

    private static string ReadDocumentTextInBoundedChunks(ITextRange documentRange)
    {
        const int chunkCharacters = 32 * 1024;
        const int maximumDocumentCharacters = 16 * 1024 * 1024;
        WriteNativeAuditStage("TextPatternRange.Clone document range");
        ITextRange cursor = documentRange.Clone();
        WriteNativeAuditStage("TextPatternRange.MoveEndpointByRange to document start");
        cursor.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            cursor,
            TextPatternRangeEndpoint.Start);
        var result = new StringBuilder(Math.Min(chunkCharacters, maximumDocumentCharacters));
        while (result.Length < maximumDocumentCharacters &&
               CompareTextRangeEndpoints(cursor, documentRange,
                   result.Length == 0 ? "TextPatternRange.CompareEndpoints first chunk" : null) < 0)
        {
            bool firstChunk = result.Length == 0;
            if (firstChunk) WriteNativeAuditStage("TextPatternRange.Clone first chunk");
            ITextRange chunk = cursor.Clone();
            if (firstChunk) WriteNativeAuditStage("TextPatternRange.MoveEndpointByUnit first chunk");
            int moved = chunk.MoveEndpointByUnit(
                TextPatternRangeEndpoint.End,
                TextUnit.Character,
                Math.Min(chunkCharacters, maximumDocumentCharacters - result.Length));
            if (moved <= 0)
                break;

            if (firstChunk) WriteNativeAuditStage("TextPatternRange.GetText first chunk");
            string value = chunk.GetText(-1);
            if (value.Length == 0)
                break;
            result.Append(value);
            if (firstChunk) WriteNativeAuditStage("TextPatternRange.MoveEndpointByRange to next chunk");
            cursor.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                chunk,
                TextPatternRangeEndpoint.End);
            cursor.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                chunk,
                TextPatternRangeEndpoint.End);
        }

        return result.ToString();
    }

    private static int CompareTextRangeEndpoints(
        ITextRange cursor,
        ITextRange documentRange,
        string? diagnosticStage)
    {
        if (diagnosticStage is not null)
            WriteNativeAuditStage(diagnosticStage);
        return cursor.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    documentRange,
                    TextPatternRangeEndpoint.End);
    }

    private static VisibleImageWaitResult WaitForVisibleImages(
        AutomationElement host,
        TimeSpan timeout,
        bool captureLoadingStateTransitions = false)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        double probeOverheadMs = 0;
        int pollCount = 0;
        bool? initialHasLoadingVisibleImages = null;
        bool? previousHasLoadingVisibleImages = null;
        var loadingStateTransitions = captureLoadingStateTransitions
            ? new List<ReadmeAuditVisibleImageLoadingStateTransition>(capacity: 1)
            : null;
        while (stopwatch.Elapsed < timeout)
        {
            Stopwatch probe = Stopwatch.StartNew();
            // MarkdownRenderer exposes the aggregate visible-image state on
            // the document peer. Polling one property keeps this O(images)
            // inside the control instead of materializing the complete UIA
            // tree on every viewport. The old traversal was quadratic in long
            // documents and also retained thousands of transient COM wrappers.
            if (captureLoadingStateTransitions && pollCount == 0)
                WriteNativeAuditStage("UIA ItemStatus read starting");
            bool isLoading = !string.IsNullOrWhiteSpace(
                host.Properties.ItemStatus.ValueOrDefault);
            if (captureLoadingStateTransitions && pollCount == 0)
                WriteNativeAuditStage("UIA ItemStatus read complete");
            probe.Stop();
            probeOverheadMs += probe.Elapsed.TotalMilliseconds;
            if (captureLoadingStateTransitions)
            {
                pollCount++;
                double elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                initialHasLoadingVisibleImages ??= isLoading;
                if (previousHasLoadingVisibleImages is bool previous && previous != isLoading)
                {
                    loadingStateTransitions!.Add(new ReadmeAuditVisibleImageLoadingStateTransition
                    {
                        ElapsedMilliseconds = elapsedMilliseconds,
                        HasLoadingVisibleImages = isLoading,
                    });
                }
                previousHasLoadingVisibleImages = isLoading;
            }
            if (!isLoading)
            {
                return new VisibleImageWaitResult(
                    probeOverheadMs,
                    false,
                    0,
                    stopwatch.Elapsed.TotalMilliseconds,
                    pollCount,
                    initialHasLoadingVisibleImages,
                    loadingStateTransitions ?? []);
            }
            Thread.Sleep(10);
        }

        // Only on a deadline, inspect the full UIA tree once to distinguish a
        // genuinely pending image from a stale aggregate ItemStatus. This
        // diagnostic walk is harness overhead, not document render time.
        Stopwatch diagnosticProbe = Stopwatch.StartNew();
        int loadingImageCount;
        try
        {
            loadingImageCount = host.FindAllDescendants().Count(IsLoadingImage);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or
                System.Runtime.InteropServices.COMException)
        {
            loadingImageCount = -1;
        }
        diagnosticProbe.Stop();
        return new VisibleImageWaitResult(
            probeOverheadMs + diagnosticProbe.Elapsed.TotalMilliseconds,
            true,
            loadingImageCount,
            stopwatch.Elapsed.TotalMilliseconds - diagnosticProbe.Elapsed.TotalMilliseconds,
            pollCount,
            initialHasLoadingVisibleImages,
            loadingStateTransitions ?? []);
    }

    private static bool IsLoadingImage(AutomationElement element) =>
        (element.ControlType == ControlType.Image ||
         string.Equals(element.ClassName, "MarkdownLinkedImage", StringComparison.Ordinal)) &&
        !string.IsNullOrWhiteSpace(element.Properties.ItemStatus.ValueOrDefault);

    private static ScrollWaitResult WaitForScrollChange(
        IScrollPattern scroll,
        double previous,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        double probeOverheadMs = 0;
        while (stopwatch.Elapsed < timeout)
        {
            Stopwatch probe = Stopwatch.StartNew();
            double actual = scroll.VerticalScrollPercent.ValueOrDefault;
            probe.Stop();
            probeOverheadMs += probe.Elapsed.TotalMilliseconds;
            if (double.IsFinite(actual) && actual >= 0 &&
                Math.Abs(actual - previous) > MinimumScrollPercentChange)
            {
                return new ScrollWaitResult(
                    true,
                    probeOverheadMs,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            Thread.Sleep(5);
        }
        return new ScrollWaitResult(
            false,
            probeOverheadMs,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static double ReadSignalElapsedMilliseconds(string startPath, string endPath)
    {
        DateTimeOffset start = ReadSignalTimestamp(startPath);
        DateTimeOffset end = ReadSignalTimestamp(endPath);
        double elapsed = (end - start).TotalMilliseconds;
        if (!double.IsFinite(elapsed) || elapsed < 0)
        {
            throw new InvalidDataException(
                $"Markdown lifecycle timestamps were not monotonic: '{startPath}' -> '{endPath}'.");
        }

        return elapsed;
    }

    private static DateTimeOffset ReadSignalTimestamp(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("Timestamp", out JsonElement timestamp) ||
            timestamp.ValueKind != JsonValueKind.String ||
            !timestamp.TryGetDateTimeOffset(out DateTimeOffset value))
        {
            throw new InvalidDataException(
                $"Markdown lifecycle signal '{path}' does not contain a valid Timestamp.");
        }

        return value;
    }

    private static NativeFirstViewportImagesReadySignal WaitForNativeFirstViewportImagesReadySignal(
        string path,
        int processId,
        string expectedHost,
        NativeRenderCompleteSignal renderComplete,
        string? expectedReadmeGitBlobSha1,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                return NativeFirstViewportImagesReadyContract.ReadImagesReadySignal(path);
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException(
            $"The native renderer did not publish first-viewport readiness evidence within {timeout.TotalSeconds:0.###} seconds " +
            $"for process {processId}, host '{expectedHost}', generation {renderComplete.Generation}, " +
            $"README git blob SHA '{expectedReadmeGitBlobSha1 ?? "not-applicable"}'.");
    }

    private static ReadmeAuditPerformanceSnapshot ReadPerformanceSnapshot(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        try
        {
            return ReadmeAuditPerformanceSnapshot.Parse(document.RootElement);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"Markdown audit signal '{path}' contained invalid performance counters.",
                exception);
        }
    }

    private static double WaitForScrollSettled(IScrollPattern scroll, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        double probeOverheadMs = 0;
        double previousPercent = double.NaN;
        double previousViewSize = double.NaN;
        int stableSamples = 0;
        double demonstratedStableDelayMs = 0;
        double precedingDelayMs = 0;
        while (stopwatch.Elapsed < timeout)
        {
            Stopwatch probe = Stopwatch.StartNew();
            double percent = scroll.VerticalScrollPercent.ValueOrDefault;
            double viewSize = scroll.VerticalViewSize.ValueOrDefault;
            probe.Stop();
            probeOverheadMs += probe.Elapsed.TotalMilliseconds;
            if (double.IsFinite(percent) && double.IsFinite(viewSize) &&
                Math.Abs(percent - previousPercent) <= 0.01 &&
                Math.Abs(viewSize - previousViewSize) <= 0.01)
            {
                // The delay before a matching sample exists only to prove that
                // an already-settled viewport remained stable. Treat that
                // confirmation window as harness overhead, while retaining any
                // delay that preceded a real extent/position change as renderer
                // latency. This prevents long documents from being penalized by
                // a fixed 75 ms audit tax for every viewport.
                demonstratedStableDelayMs += precedingDelayMs;
                if (++stableSamples >= 3)
                {
                    return probeOverheadMs + demonstratedStableDelayMs;
                }
            }
            else
            {
                stableSamples = 0;
                previousPercent = percent;
                previousViewSize = viewSize;
            }
            // Three samples at 25 ms closely match the browser oracle's
            // 70 ms lazy-content pacing without adding 150 ms of harness
            // latency to every native viewport.
            Stopwatch delay = Stopwatch.StartNew();
            Thread.Sleep(25);
            delay.Stop();
            precedingDelayMs = delay.Elapsed.TotalMilliseconds;
        }
        return probeOverheadMs + demonstratedStableDelayMs;
    }

    private static (int Width, int Height) CaptureHost(
        Window window,
        AutomationElement host,
        string path,
        string captureRequestPath,
        string captureResponsePath,
        out double documentTop,
        bool useRendererCapture = true)
    {
        documentTop = double.NaN;
        if (useRendererCapture)
        {
            // The warm paint triggers any viewport-tiled SVG work that a
            // compositor-owned CanvasVirtualControl would normally request.
            // Wait for those resources, then save the authoritative repaint.
            _ = RequestRendererCapture(
                captureRequestPath,
                captureResponsePath,
                outputPath: null,
                save: false);
            _ = WaitForVisibleImages(host, TimeSpan.FromSeconds(20));
            RendererCaptureResponse capture = RequestRendererCapture(
                captureRequestPath,
                captureResponsePath,
                path,
                save: true);
            if (!File.Exists(path))
                throw new InvalidOperationException("The Markdown renderer did not publish its requested audit tile.");
            documentTop = capture.DocumentTop;
            return (capture.Width, capture.Height);
        }

        IntPtr handle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        NativeMethods.ActivateForKeyboard(handle);
        Thread.Sleep(70);
        Rectangle windowBounds = NativeMethods.GetPhysicalWindowBounds(handle);
        Rectangle hostBounds = host.BoundingRectangle;
        using Bitmap windowBitmap = NativeMethods.CaptureWindowSurface(handle);
        Rectangle crop = Rectangle.Intersect(
            new Rectangle(
                hostBounds.Left - windowBounds.Left,
                hostBounds.Top - windowBounds.Top,
                hostBounds.Width,
                hostBounds.Height),
            new Rectangle(Point.Empty, windowBitmap.Size));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("README host bounds do not intersect the JitHub window surface.");
        }
        using Bitmap hostBitmap = windowBitmap.Clone(crop, PixelFormat.Format32bppArgb);
        hostBitmap.Save(path, ImageFormat.Png);
        return (hostBitmap.Width, hostBitmap.Height);
    }

    private static RendererCaptureResponse RequestRendererCapture(
        string requestPath,
        string responsePath,
        string? outputPath,
        bool save,
        double? scrollViewportFraction = null)
    {
        if (scrollViewportFraction is double requestedFraction &&
            (!double.IsFinite(requestedFraction) || requestedFraction == 0 ||
             Math.Abs(requestedFraction) > MaximumNativeScrollViewportFraction))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scrollViewportFraction),
                $"The in-app audit scroll step must be nonzero and no larger than {MaximumNativeScrollViewportFraction:P0} of one viewport.");
        }

        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string temporaryPath = requestPath + $".{requestId}.tmp";
            bool sendAttempted = false;
            try
            {
                if (File.Exists(responsePath))
                    File.Delete(responsePath);
                var request = new RendererCaptureRequest(
                    requestId,
                    outputPath,
                    save,
                    scrollViewportFraction);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(request, JsonOptions));
                sendAttempted = true;
                File.Move(temporaryPath, requestPath, overwrite: true);
            }
            catch (IOException exception)
            {
                if (scrollViewportFraction is not null && sendAttempted)
                {
                    throw new IOException(
                        "Publishing the one-shot audit scroll request failed after its atomic send was attempted.",
                        exception);
                }

                lastFailure = exception;
                TryDeleteAuditFile(temporaryPath);
                Thread.Sleep(50 * (attempt + 1));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                if (scrollViewportFraction is not null && sendAttempted)
                {
                    throw new UnauthorizedAccessException(
                        "Publishing the one-shot audit scroll request failed after its atomic send was attempted.",
                        exception);
                }

                lastFailure = exception;
                TryDeleteAuditFile(temporaryPath);
                Thread.Sleep(50 * (attempt + 1));
                continue;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                try
                {
                    if (File.Exists(responsePath))
                    {
                        RendererCaptureResponse? response = JsonSerializer.Deserialize<RendererCaptureResponse>(
                            File.ReadAllText(responsePath),
                            JsonOptions);
                        if (response is not null &&
                            string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                        {
                            if (response.Succeeded && response.Width > 0 && response.Height > 0)
                                return response;

                            lastFailure = new InvalidOperationException(
                                response.Error ?? "The Markdown renderer rejected the audit capture request.");
                            if (scrollViewportFraction is not null)
                            {
                                throw new InvalidOperationException(
                                    "The Markdown renderer rejected the one-shot audit scroll request.",
                                    lastFailure);
                            }
                            break;
                        }
                    }
                }
                catch (IOException exception)
                {
                    lastFailure = exception;
                }
                catch (JsonException exception)
                {
                    lastFailure = exception;
                }
                catch (UnauthorizedAccessException exception)
                {
                    lastFailure = exception;
                }
                Thread.Sleep(25);
            }

            if (scrollViewportFraction is not null)
            {
                throw new TimeoutException(
                    "The Markdown renderer did not acknowledge the one-shot audit scroll request.",
                    lastFailure);
            }

            Thread.Sleep(50 * (attempt + 1));
        }

        throw new TimeoutException(
            "The Markdown renderer did not complete its internal audit capture request.",
            lastFailure);
    }

    private static RendererCaptureResponse RequestRendererScroll(
        string requestPath,
        string responsePath,
        double viewportFraction)
    {
        RendererCaptureResponse response = RequestRendererCapture(
            requestPath,
            responsePath,
            outputPath: null,
            save: false,
            scrollViewportFraction: viewportFraction);
        if (!double.IsFinite(response.DocumentTop) || response.DocumentTop < 0 ||
            !double.IsFinite(response.ScrollOperationMilliseconds) ||
            response.ScrollOperationMilliseconds <= 0)
        {
            throw new InvalidDataException(
                "The Markdown viewer returned invalid in-app audit scroll or timing evidence.");
        }

        return response;
    }

    private static void TryDeleteAuditFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Window WaitForWindow(Application application, UIA3Automation automation, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? lastLookupFailure = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                Window? window = application.GetMainWindow(automation);
                if (window is not null) return window;
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException exception)
            {
                // UIA's out-of-process ElementFromHandle lookup can time out
                // once during WinUI startup while the process already has a
                // responsive HWND. Retry only within the original deadline;
                // repeated timeouts remain a recorded infrastructure failure.
                lastLookupFailure = exception;
            }
            Thread.Sleep(100);
        }
        throw new NativeAuditInfrastructureException(
            "JitHub main window did not become available to UI Automation" +
            (lastLookupFailure is null
                ? "."
                : $" (last lookup: {lastLookupFailure.GetType().Name}, " +
                  $"0x{unchecked((uint)lastLookupFailure.HResult):X8})."));
    }

    private static int WaitForReadySignal(string path, Process launcher, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (TryReadProcessId(path, out int processId)) return processId;
            if (launcher.HasExited)
            {
                throw new NativeAuditInfrastructureException(
                    $"JitHub exited before readiness with code 0x{unchecked((uint)launcher.ExitCode):X8}.");
            }
            Thread.Sleep(50);
        }
        throw new NativeAuditInfrastructureException(
            $"JitHub process {launcher.Id} did not publish app readiness in time " +
            $"(session {TryGetProcessSessionId(launcher)}, responding {TryGetProcessResponding(launcher)}, " +
            $"main window 0x{TryGetMainWindowHandle(launcher).ToInt64():X}).");
    }

    private static void PreserveStartupDiagnostics(
        string dataRoot,
        string output,
        Process launcher)
    {
        try
        {
            string source = Path.Combine(dataRoot, "Local", "logs");
            if (Directory.Exists(source))
            {
                foreach (string path in Directory.EnumerateFiles(source))
                {
                    File.Copy(path, Path.Combine(output, Path.GetFileName(path)), overwrite: true);
                }
            }

            File.WriteAllLines(
                Path.Combine(output, "startup-process.txt"),
                [
                    $"ProcessId={launcher.Id}",
                    $"HasExited={launcher.HasExited}",
                    $"ExitCode={(launcher.HasExited ? $"0x{unchecked((uint)launcher.ExitCode):X8}" : "running")}",
                    $"SessionId={TryGetProcessSessionId(launcher)}",
                    $"Responding={TryGetProcessResponding(launcher)}",
                    $"MainWindowHandle=0x{TryGetMainWindowHandle(launcher).ToInt64():X}",
                ]);
        }
        catch
        {
            // Diagnostics must never hide the launch failure they describe.
        }
    }

    private static void PreserveShutdownExceptionDiagnostics(string dataRoot, string output)
    {
        // Audit artifacts may be public. Preserve only exception categories
        // relevant to a nonzero exit, not every account/session log.
        string logs = Path.Combine(dataRoot, "Local", "logs");
        foreach (string name in new[]
        {
            "xaml-unhandled.log",
            "appdomain-unhandled.log",
            "task-unobserved.log",
            "markdown-runtime-shutdown.log",
        })
        {
            try
            {
                string source = Path.Combine(logs, name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(output, name), overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static int TryGetProcessSessionId(Process process)
    {
        try { return process.SessionId; }
        catch { return -1; }
    }

    private static bool TryGetProcessResponding(Process process)
    {
        try { return process.Responding; }
        catch { return false; }
    }

    private static IntPtr TryGetMainWindowHandle(Process process)
    {
        try
        {
            process.Refresh();
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void WaitForSignal(string path, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path)) return;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"Timed out waiting for '{Path.GetFileName(path)}'.");
    }

    private static void WaitForRenderSignal(
        string completionPath,
        string failurePath,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(completionPath))
                return;
            if (File.Exists(failurePath))
            {
                string details;
                try { details = File.ReadAllText(failurePath); }
                catch (IOException) { details = "The renderer reported a failure."; }
                throw new InvalidOperationException(
                    $"Native Markdown rendering failed before completion.{Environment.NewLine}{details}");
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException(
            $"Timed out waiting for '{Path.GetFileName(completionPath)}'.");
    }

    private static bool TryReadProcessId(string path, out int processId)
    {
        processId = 0;
        try
        {
            if (!File.Exists(path)) return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            processId = document.RootElement.GetProperty("ProcessId").GetInt32();
            return processId > 0;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private static ReadmeAuditCloseResult CloseAndWait(
        Process appProcess, Process launcher)
    {
        // The packaged app may not be the process returned by Process.Start.
        // Process.ExitCode throws for Process.GetProcessById attachments, so
        // retain a native handle before closing the window and read that code.
        IntPtr appExitHandle = NativeMethods.OpenProcessExitHandle(appProcess.Id);
        try
        {
            // FlaUI's synchronous UIA Window.Close can block indefinitely when
            // an unattended desktop's provider is unresponsive. Post WM_CLOSE
            // only to this audit-owned process's visible unowned top-level
            // window, then keep the existing bounded process-exit check.
            bool closeRequestFailed = !appProcess.HasExited &&
                !NativeMethods.TryRequestGracefulClose(appProcess.Id, out _);

            if (!appProcess.WaitForExit(12_000))
            {
                return new ReadmeAuditCloseResult(
                    false,
                    closeRequestFailed
                        ? "window-close-request-failed; app-exit-timeout-12s"
                        : "app-exit-timeout-12s");
            }

            uint appExitCode = NativeMethods.GetProcessExitCode(appExitHandle);
            if (appExitCode != 0)
            {
                return new ReadmeAuditCloseResult(
                    false,
                    $"app-exit-code-0x{appExitCode:X8}");
            }

            if (!launcher.HasExited) launcher.WaitForExit(2_000);
            return launcher.HasExited && launcher.ExitCode != 0
                ? new ReadmeAuditCloseResult(false, $"launcher-exit-code-0x{launcher.ExitCode:X8}")
                : new ReadmeAuditCloseResult(true, null);
        }
        finally
        {
            NativeMethods.CloseProcessExitHandle(appExitHandle);
        }
    }

    private static double TokenCoverage(string expected, string actual)
    {
        Dictionary<string, int> expectedCounts = CountTokens(expected);
        Dictionary<string, int> actualCounts = CountTokens(actual);
        int total = expectedCounts.Values.Sum();
        if (total == 0) return actualCounts.Count == 0 ? 1 : 0;
        int matched = expectedCounts.Sum(pair => Math.Min(pair.Value, actualCounts.GetValueOrDefault(pair.Key)));
        return (double)matched / total;
    }

    private static double TokenCoverage(
        BrowserSameByteReplaySemanticEvidence expected,
        string actual)
    {
        Dictionary<string, int> actualCounts = CountTokens(actual);
        long total = expected.VisibleTextTokenCount;
        if (total == 0) return actualCounts.Count == 0 ? 1 : 0;
        if (!IsSha256(expected.SemanticDigestKeyHex)) return 0;

        long matched = 0;
        foreach ((string token, int count) in actualCounts)
        {
            string digest = HashSemanticText(expected.SemanticDigestKeyHex, token);
            matched += Math.Min(
                expected.VisibleTextTokenDigests.GetValueOrDefault(digest),
                count);
        }

        return (double)matched / total;
    }

    private static string HashSemanticText(string keyHex, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Convert.FromHexString(keyHex),
            Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Dictionary<string, int> CountTokens(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var buffered = new System.Text.StringBuilder();
        foreach (System.Text.Rune rune in (text ?? string.Empty).EnumerateRunes())
        {
            UnicodeCategory category = System.Text.Rune.GetUnicodeCategory(rune);
            bool wordCharacter = category is
                UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or
                UnicodeCategory.OtherNumber or
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark;
            if (!wordCharacter)
            {
                FlushBufferedToken(buffered, counts);
                continue;
            }

            if (IsHanIdeograph(rune.Value))
            {
                FlushBufferedToken(buffered, counts);
                AddToken(rune.ToString(), counts);
                continue;
            }

            buffered.Append(rune.ToString());
        }

        FlushBufferedToken(buffered, counts);
        return counts;
    }

    private static bool IsHanIdeograph(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or
        >= 0x20000 and <= 0x2FA1F;

    private static void FlushBufferedToken(
        System.Text.StringBuilder buffered,
        Dictionary<string, int> counts)
    {
        if (buffered.Length == 0)
            return;

        AddToken(buffered.ToString(), counts);
        buffered.Clear();
    }

    private static void AddToken(string value, Dictionary<string, int> counts)
    {
        value = value.Normalize().ToLowerInvariant();
        counts[value] = counts.GetValueOrDefault(value) + 1;
    }

    private static double HarmonicMean(double left, double right) =>
        left <= 0 || right <= 0 ? 0 : 2 * left * right / (left + right);

    private static double CountFidelity(int expected, int actual) => expected == 0
        ? actual == 0 ? 1 : 0
        : RatioFidelity((double)actual / expected);

    private static double CoverageFidelity(int expected, int actual) => expected <= 0
        ? 1
        : Math.Clamp((double)actual / expected, 0, 1);

    private static double RatioFidelity(double ratio) =>
        !double.IsFinite(ratio) || ratio <= 0 ? 0 : Math.Min(ratio, 1 / ratio);

    private static double CalculateSsim(Bitmap left, Bitmap right)
    {
        const int size = 128;
        using Bitmap a = Resize(left, size, size);
        using Bitmap b = Resize(right, size, size);
        double sumA = 0;
        double sumB = 0;
        var valuesA = new double[size * size];
        var valuesB = new double[size * size];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++, index++)
            {
                Color ca = a.GetPixel(x, y);
                Color cb = b.GetPixel(x, y);
                valuesA[index] = 0.2126 * ca.R + 0.7152 * ca.G + 0.0722 * ca.B;
                valuesB[index] = 0.2126 * cb.R + 0.7152 * cb.G + 0.0722 * cb.B;
                sumA += valuesA[index];
                sumB += valuesB[index];
            }
        }
        double meanA = sumA / valuesA.Length;
        double meanB = sumB / valuesB.Length;
        double varianceA = 0;
        double varianceB = 0;
        double covariance = 0;
        for (int i = 0; i < valuesA.Length; i++)
        {
            double da = valuesA[i] - meanA;
            double db = valuesB[i] - meanB;
            varianceA += da * da;
            varianceB += db * db;
            covariance += da * db;
        }
        varianceA /= valuesA.Length - 1;
        varianceB /= valuesB.Length - 1;
        covariance /= valuesA.Length - 1;
        const double c1 = 6.5025;
        const double c2 = 58.5225;
        return ((2 * meanA * meanB + c1) * (2 * covariance + c2)) /
            ((meanA * meanA + meanB * meanB + c1) * (varianceA + varianceB + c2));
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    private static int CountNonEmptyLines(string path) => File.Exists(path)
        ? File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line))
        : 0;

    private static void PreserveEvidenceFile(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static void PreserveSanitizedEvidenceFile(string source, string destination)
    {
        if (File.Exists(source))
        {
            SanitizeJsonEvidenceFile(source, destination);
        }
    }

    private static void SanitizeSameByteRuntimeEvidence(string runtimeDirectory)
    {
        foreach (string name in new[] { "image-unavailable.ndjson", "image-resolution.ndjson" })
        {
            string path = Path.Combine(runtimeDirectory, name);
            if (File.Exists(path)) SanitizeJsonEvidenceFile(path, path);
        }

        string renderFailure = Path.Combine(runtimeDirectory, "render-failure.txt");
        if (File.Exists(renderFailure))
            File.WriteAllText(renderFailure, RedactUrls(File.ReadAllText(renderFailure)));
    }

    private static void SanitizeJsonEvidenceFile(string source, string destination)
    {
        string temporaryPath = destination + ".sanitized.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var writer = new StreamWriter(temporaryPath, append: false, new UTF8Encoding(false)))
        {
            foreach (string line in File.ReadLines(source))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    writer.WriteLine();
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    using var buffer = new MemoryStream();
                    using (var jsonWriter = new Utf8JsonWriter(buffer))
                    {
                        WriteSanitizedJsonValue(jsonWriter, document.RootElement, null);
                    }
                    writer.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
                }
                catch (JsonException)
                {
                    writer.WriteLine(RedactUrls(line));
                }
            }
        }
        File.Move(temporaryPath, destination, overwrite: true);
    }

    private static void WriteSanitizedJsonValue(
        Utf8JsonWriter writer,
        JsonElement element,
        string? propertyName)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitizedJsonValue(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteSanitizedJsonValue(writer, item, propertyName);
                writer.WriteEndArray();
                break;
            case System.Text.Json.JsonValueKind.String:
                string? value = element.GetString();
                if (propertyName is "Source" or "source" or "CurrentSource" or "currentSource" or
                    "Href" or "href" or "Uri" or "uri" or "ResolvedUri" or "resolvedUri" or
                    "Url" or "url" or "RepositoryUrl" or "repositoryUrl" or "DownloadUrl" or "downloadUrl")
                {
                    writer.WriteStringValue(HashSourceIdentity(value));
                }
                else
                {
                    writer.WriteStringValue(RedactUrls(value ?? string.Empty));
                }
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static int CountRenderedUnavailableImages(
        string path,
        IReadOnlyList<BrowserImage>? renderedBrowserImages)
    {
        if (!File.Exists(path) || renderedBrowserImages is null)
        {
            return CountNonEmptyLines(path);
        }

        string[] visibleSources = renderedBrowserImages
            .Where(IsVisibleRenderedBrowserImage)
            .SelectMany(image => new[] { image.Source, image.CurrentSource })
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int count = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("Source", out JsonElement sourceProperty))
                {
                    count++;
                    continue;
                }

                string source = sourceProperty.GetString() ?? string.Empty;
                if (visibleSources.Any(candidate => ImageSourcesReferToSameAsset(source, candidate)))
                {
                    count++;
                }
            }
            catch (JsonException)
            {
                // Malformed evidence is a failure signal, never something the
                // audit silently discounts.
                count++;
            }
        }

        return count;
    }

    private static bool IsVisibleRenderedBrowserImage(BrowserImage image) =>
        image.Complete &&
        image.NaturalWidth > 0 &&
        image.NaturalHeight > 0 &&
        image.RenderedWidth > 0 &&
        image.RenderedHeight > 0;

    private static string ReadAutomationString(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            InvalidOperationException or
            FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            // UIA providers may expose a hyperlink while omitting one optional
            // string property. Evidence collection must retain the element and
            // leave that field empty instead of aborting the full-page audit.
            return string.Empty;
        }
    }

    private static bool ImageSourcesReferToSameAsset(string left, string right)
    {
        string normalizedLeft = Uri.UnescapeDataString(left.Trim()).Replace('\\', '/');
        string normalizedRight = Uri.UnescapeDataString(right.Trim()).Replace('\\', '/');
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(normalizedLeft, UriKind.Absolute, out Uri? leftUri) &&
            Uri.TryCreate(normalizedRight, UriKind.Absolute, out Uri? rightUri))
        {
            return string.Equals(
                leftUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.Unescaped),
                rightUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.Unescaped),
                StringComparison.OrdinalIgnoreCase);
        }

        string relative = normalizedLeft.TrimStart('.', '/');
        string candidatePath = Uri.TryCreate(normalizedRight, UriKind.Absolute, out Uri? absoluteCandidate)
            ? Uri.UnescapeDataString(absoluteCandidate.AbsolutePath).TrimStart('/')
            : normalizedRight.TrimStart('.', '/');
        return relative.Length > 0 &&
            candidatePath.EndsWith('/' + relative, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDistinctImageSources(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            bool hasAsset = root.TryGetProperty("HasAsset", out JsonElement hasAssetProperty) &&
                hasAssetProperty.ValueKind == JsonValueKind.True;
            if (hasAsset && root.TryGetProperty("Source", out JsonElement source))
            {
                sources.Add(source.GetString() ?? string.Empty);
            }
        }
        return sources.Count;
    }

    private static string NormalizeText(string? text) => string.Join(
        ' ',
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Sanitize(string value) => string.Concat(
        value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private static bool TryReadCompletedCase(
        string path,
        ReadmeAuditManifest manifest,
        ReadmeAuditRepository repository,
        out ReadmeAuditCaseResult? result)
    {
        result = null;
        try
        {
            if (!File.Exists(path)) return false;
            result = JsonSerializer.Deserialize<ReadmeAuditCaseResult>(File.ReadAllText(path), JsonOptions);
            return result is not null &&
                !result.InfrastructureFailure &&
                result.SchemaVersion == 4 &&
                result.CorpusGeneratedAtUtc == manifest.GeneratedAtUtc &&
                result.Rank == repository.Rank &&
                string.Equals(result.ReadmeSha, repository.Readme.Sha, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            result = null;
            return false;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record NativeTraversalResult(
        int Width,
        int EstimatedContentHeight,
        IReadOnlyList<AuditTile> Tiles,
        int Headings,
        int Links,
        int Images,
        int Tables,
        int CodeBlocks,
        int TaskCheckboxes,
        int Disclosures,
        IReadOnlyList<string> MermaidSources,
        int LoadingImages,
        double AuditOverheadMs,
        IReadOnlyList<ReadmeAuditVisibleImageWait> VisibleImageWaits,
        double ViewportStepRatio);

    private readonly record struct VisibleImageWaitResult(
        double ProbeOverheadMs,
        bool TimedOut,
        int LoadingImageCount,
        double ElapsedMilliseconds,
        int PollCount,
        bool? InitialHasLoadingVisibleImages,
        IReadOnlyList<ReadmeAuditVisibleImageLoadingStateTransition> LoadingStateTransitions);

    private readonly record struct ScrollWaitResult(
        bool Succeeded,
        double ProbeOverheadMs,
        double ElapsedMs);

    private sealed record RendererCaptureRequest(
        string RequestId,
        string? OutputPath,
        bool Save,
        double? ScrollViewportFraction = null);

    private sealed record RendererCaptureResponse(
        string RequestId,
        bool Succeeded,
        int Width,
        int Height,
        double DocumentTop,
        string? Error,
        ReadmeAuditPerformanceSnapshot? Performance,
        double ScrollOperationMilliseconds);
}

internal sealed class ReadmeAuditManifest
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public string Query { get; init; } = string.Empty;
    public List<ReadmeAuditRepository> Repositories { get; init; } = [];
}

internal sealed class ReadmeAuditRepository
{
    public int Rank { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string DefaultBranch { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public long Stars { get; init; }
    public string Url { get; init; } = string.Empty;
    public ReadmeAuditReadme Readme { get; init; } = new();
}

internal sealed class ReadmeAuditReadme
{
    public bool Available { get; init; }
    public long ByteSize { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Sha { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
}

internal sealed class BrowserAuditResult
{
    public int SchemaVersion { get; init; }
    public string RepositoryUrl { get; init; } = string.Empty;
    public string ReadmeSha { get; init; } = string.Empty;
    public bool? ReadmeRendered { get; init; }
    public BrowserTiming Timing { get; init; } = new();
    public BrowserSameByteCorpusEvidence? SameByteCorpus { get; init; }
    public BrowserSameByteReplayEvidence? SameByteReplay { get; set; }
    public BrowserSameByteHtmlReplayEvidence? SameByteHtmlReplay { get; init; }
    public BrowserSemantic Semantic { get; init; } = new();
    public List<AuditTile> Tiles { get; init; } = [];
}

internal sealed class BrowserSameByteCorpusEvidence
{
    public string Manifest { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public string ReadmeSha256 { get; init; } = string.Empty;
    public long ReadmeBytes { get; init; }
    public int AssetCount { get; init; }
    public long AssetBytes { get; init; }
}

internal sealed class BrowserSameByteHtmlReplayEvidence
{
    public int SchemaVersion { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string ReadmeGitBlobSha1 { get; init; } = string.Empty;
    public string ReadmeSha256 { get; init; } = string.Empty;
    public string RenderedHtmlSha256 { get; init; } = string.Empty;
    public string AssetUrlMapSha256 { get; init; } = string.Empty;
    public BrowserSameByteReplayViewport Viewport { get; init; } = new();
    public BrowserSameByteReplayAssets Assets { get; init; } = new();
    public BrowserSameByteHtmlReplayTiming Timing { get; init; } = new();
    public List<AuditTile> Tiles { get; init; } = [];
}

internal sealed class BrowserSameByteReplayEvidence
{
    public int SchemaVersion { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public BrowserSameByteReplaySource Source { get; init; } = new();
    public BrowserSameByteReplayParser Parser { get; init; } = new();
    public BrowserSameByteSourceViewport Viewport { get; init; } = new();
    public BrowserSameByteRenderedExtent RenderedExtent { get; init; } = new();
    public BrowserSameByteSourceAssets Assets { get; init; } = new();
    public BrowserSameByteReplayTiming Timing { get; init; } = new();
    public List<AuditTile> Tiles { get; init; } = [];
    public BrowserSameByteReplaySemanticEvidence Semantic { get; init; } = new();
}

internal sealed class BrowserSameByteReplaySemanticEvidence
{
    public bool Complete { get; init; }
    public string IncompleteReason { get; init; } = string.Empty;
    public string TokenizationVersion { get; init; } = string.Empty;
    public string DigestKeySha256 { get; init; } = string.Empty;
    public int VisibleTextTokenCount { get; init; }
    public Dictionary<string, int> VisibleTextTokenDigests { get; init; } = new(StringComparer.Ordinal);
    public int VisibleTextTokenDigestCount { get; init; }
    public double? SourceReplayToCapturedGitHubVisibleTextTokenCoverage { get; set; }
    public double? CapturedGitHubToSourceReplayVisibleTextTokenCoverage { get; set; }
    public double? CapturedGitHubSourceStructureScore { get; set; }
    public double Width { get; init; }
    public double Height { get; init; }
    public int HeadingCount { get; init; }
    public int DistinctLinkCount { get; init; }
    public int ImageCount { get; init; }
    public int DistinctImageCount { get; init; }
    public int MediaCount { get; init; }
    public int DistinctMediaCount { get; init; }
    public int TableCount { get; init; }
    public int CodeBlockCount { get; init; }
    public int TaskCheckboxCount { get; init; }
    public int DetailsCount { get; init; }
    public List<string> VisibleMermaidSourceDigests { get; init; } = [];
    public int VisibleMermaidSourceCount { get; init; }

    // Populated from the per-run Automation key after report validation. The
    // field is intentionally non-serialized and is removed from result copies.
    internal string SemanticDigestKeyHex = string.Empty;
}

internal sealed class BrowserSameByteRenderedExtent
{
    public int Width { get; init; }
    public int Height { get; init; }
}

internal sealed class BrowserSameByteReplaySource
{
    public string ReadmeGitBlobSha1 { get; init; } = string.Empty;
    public string ReadmeSha256 { get; init; } = string.Empty;
    public long ByteSize { get; init; }
}

internal sealed class BrowserSameByteReplayParser
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class BrowserSameByteSourceViewport
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double DeviceScaleFactor { get; init; }
    public string ColorScheme { get; init; } = string.Empty;
    public int EdgeInnerWidth { get; init; }
    public int EdgeInnerHeight { get; init; }
    public double EdgeDeviceScaleFactor { get; init; }
}

internal sealed class BrowserSameByteSourceAssets
{
    public string AssetUrlMapSha256 { get; init; } = string.Empty;
    public int ExpectedImageCount { get; init; }
    public int VerifiedImageCount { get; init; }
    public int DistinctExpectedUrlHashes { get; init; }
    public int DistinctServedUrlHashes { get; init; }
    public int ReplayMissCount { get; init; }
    public int BlockedExternalRequestCount { get; init; }
}

internal sealed class BrowserSameByteReplayViewport
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double DeviceScaleFactor { get; init; }
}

internal sealed class BrowserSameByteReplayAssets
{
    public int ExpectedVisibleImageCount { get; init; }
    public int DistinctExpectedUrlHashes { get; init; }
    public int DistinctServedUrlHashes { get; init; }
    public int ReplayMissCount { get; init; }
    public int BlockedExternalRequestCount { get; init; }
    public int DataImageCount { get; init; }
}

internal sealed class BrowserSameByteReplayTiming
{
    public double FirstViewportPaintMs { get; init; }
    public double FirstViewportImagesReadyMs { get; init; }
    public double FullTraversalMs { get; init; }
    public double ChargedTraversalMs { get; init; }
    public double AuditOnlyFrameWaitMs { get; init; }
    public int AuditOnlyFrameWaitCount { get; init; }
    public double ViewportStepRatio { get; init; }
    public int TraversalViewportCount { get; init; }
    public int MovementCount { get; init; }
    public int SourceTailViewportCount { get; init; }
    public int SourceTailMovementCount { get; init; }
    public string ViewportProfileSha256 { get; init; } = string.Empty;
    public IReadOnlyList<double> CaptureOffsetsViewportUnits { get; init; } = [];
    public IReadOnlyList<double>? SourceTailCaptureOffsetsViewportUnits { get; init; }
    public string FirstViewportBoundary { get; init; } = string.Empty;
    public string FirstViewportImagesReadyBoundary { get; init; } = string.Empty;
    public string FullTraversalBoundary { get; init; } = string.Empty;
    public string ChargedTraversalBoundary { get; init; } = string.Empty;
    public string AuditOnlyFrameWaitBoundary { get; init; } = string.Empty;
}

// The rendered-HTML replay is a legacy artifact used only as a diagnostic.
// Its raw traversal clock is not the source-bound performance denominator.
internal sealed class BrowserSameByteHtmlReplayTiming
{
    public double FirstViewportPaintMs { get; init; }
    public double FirstViewportImagesReadyMs { get; init; }
    public double FullTraversalMs { get; init; }
    public double ViewportStepRatio { get; init; }
    public int TraversalViewportCount { get; init; }
    public string FirstViewportBoundary { get; init; } = string.Empty;
    public string FirstViewportImagesReadyBoundary { get; init; } = string.Empty;
    public string FullTraversalBoundary { get; init; } = string.Empty;
}

internal sealed class BrowserTiming
{
    public double FirstReadmeMs { get; init; }
    public double SettledReadmeMs { get; init; }
    public double FullCaptureMs { get; init; }
    public double WallMs { get; init; }
    public int NavigationRetries { get; init; }
    public double TaskDurationMs { get; init; }
    public double ScriptDurationMs { get; init; }
    public double LayoutDurationMs { get; init; }
    public double RecalcStyleDurationMs { get; init; }
    public double LayoutCount { get; init; }
    public double RecalcStyleCount { get; init; }
    public double DomNodes { get; init; }
    public double Documents { get; init; }
    public double JsHeapUsedBytes { get; init; }
    public double FullCaptureTaskDurationMs { get; init; }
}

internal sealed class BrowserSemantic
{
    public string Text { get; init; } = string.Empty;
    public string VisibleText { get; init; } = string.Empty;
    public double Width { get; init; }
    public double Height { get; init; }
    public List<BrowserHeading> Headings { get; init; } = [];
    public List<BrowserLink> Links { get; init; } = [];
    public List<BrowserImage> Images { get; init; } = [];
    public List<BrowserMedia> Media { get; init; } = [];
    public List<string> VisibleMermaidSources { get; init; } = [];
    public int UnavailableImages { get; init; }
    public int Tables { get; init; }
    public int CodeBlocks { get; init; }
    public int TaskCheckboxes { get; init; }
    public int Details { get; init; }
}

internal sealed class BrowserHeading
{
    public int Level { get; init; }
    public string Text { get; init; } = string.Empty;
}

internal sealed class BrowserLink
{
    public string Text { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
}

internal sealed class BrowserImage
{
    public string Alt { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string CurrentSource { get; init; } = string.Empty;
    public bool Complete { get; init; }
    public int NaturalWidth { get; init; }
    public int NaturalHeight { get; init; }
    public double RenderedWidth { get; init; }
    public double RenderedHeight { get; init; }
}

internal sealed class BrowserMedia
{
    public string Kind { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string CurrentSource { get; init; } = string.Empty;
    public double RenderedWidth { get; init; }
    public double RenderedHeight { get; init; }
    public string AccessibleName { get; init; } = string.Empty;
}

internal sealed class AuditTile
{
    public int Index { get; init; }
    public double RelativeY { get; init; }
    public double ScrollPercent { get; init; }
    public double ScrollTopViewportUnits { get; init; }
    public double VerticalViewSize { get; init; }
    public IReadOnlyList<double> NativeViewportMovementOffsets { get; init; } = [];
    public double Width { get; init; }
    public double Height { get; init; }
    public string File { get; init; } = string.Empty;
    // Cumulative clocks make each scroll step's renderer charge auditable
    // without timing screenshot or UIA evidence capture as rendering work.
    public double NativeTraversalElapsedAtCaptureMs { get; init; }
    public double NativeChargedAtCaptureMs { get; init; }
}

internal sealed class ReadmeAuditVisibleImageWait
{
    public int TileIndex { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public bool TimedOut { get; init; }
    public int LoadingImageCount { get; init; }
}

internal sealed class ReadmeAuditFirstViewportImageWait
{
    public double ElapsedMilliseconds { get; init; }
    public double ProbeOverheadMs { get; init; }
    public int PollCount { get; init; }
    public bool? InitialHasLoadingVisibleImages { get; init; }
    public long ApplicationSignalGeneration { get; init; }
    public long ApplicationSignalViewportPaintGeneration { get; init; }
    public int ApplicationSignalPollCount { get; init; }
    public double ApplicationSignalProbeWorkMilliseconds { get; init; }
    public double ApplicationSignalViewportTop { get; init; }
    public double ApplicationSignalViewportHeight { get; init; }
    public bool ApplicationSignalViewportMeasured { get; init; }
    public double ApplicationSignalAfterRenderCompleteMs { get; init; }
    public IReadOnlyList<ReadmeAuditVisibleImageLoadingStateTransition> LoadingStateTransitions { get; init; } = [];
}

internal sealed class ReadmeAuditVisibleImageLoadingStateTransition
{
    public double ElapsedMilliseconds { get; init; }
    public bool HasLoadingVisibleImages { get; init; }
}

internal sealed class NativeAuditResult
{
    public double FirstRenderMs { get; init; }
    public double FirstViewportImagesReadyMs { get; init; }
    public ReadmeAuditFirstViewportImageWait? FirstViewportImageWait { get; init; }
    public double ExperienceFirstRenderMs { get; init; }
    public double ColdStartToFirstRenderMs { get; init; }
    public double FullTraversalMs { get; init; }
    public double FullTraversalViewportStepRatio { get; init; }
    public int FullTraversalViewportCount { get; init; }
    public double AuditOverheadMs { get; init; }
    public double FirstRenderCpuMs { get; init; }
    public double CpuMs { get; init; }
    public ReadmeAuditPerformanceSnapshot? FirstPerformance { get; init; }
    public ReadmeAuditPerformanceSnapshot? FullPerformance { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<string> MermaidSources { get; init; } = [];
    public int Width { get; init; }
    public int ContentViewportWidth { get; init; }
    public int ContentViewportHeight { get; init; }
    public double RasterizationScale { get; init; }
    public int EstimatedContentHeight { get; init; }
    public IReadOnlyList<AuditTile> Tiles { get; init; } = [];
    public IReadOnlyList<ReadmeAuditVisibleImageWait> VisibleImageWaits { get; init; } = [];
    public int HeadingObservations { get; init; }
    public int LinkObservations { get; init; }
    public int ImageObservations { get; init; }
    public int TableObservations { get; init; }
    public int CodeBlockObservations { get; init; }
    public int TaskCheckboxObservations { get; init; }
    public int DisclosureObservations { get; init; }
    public int ImageSourceCount { get; init; }
    public int LoadingImagesAfterTraversal { get; init; }
    public int UnavailableImages { get; init; }
    public int RawUnavailableImages { get; init; }
    public string? RenderFailure { get; init; }
    public bool CleanExit { get; init; }
    public string? CloseFailure { get; init; }
    public string? ReadmeSourceSha256 { get; init; }
}

internal readonly record struct ReadmeAuditCloseResult(bool CleanExit, string? Failure);

internal sealed class ReadmeAuditComparison
{
    public string FidelityReference { get; init; } = string.Empty;
    public string TileSsimReference { get; init; } = string.Empty;
    public double TextTokenCoverage { get; init; }
    public double TextTokenPrecision { get; init; }
    public double TextTokenFidelity { get; init; }
    public double MeanTileSsim { get; init; }
    public double MinimumTileSsim { get; init; }
    public double VisualStructureScore { get; init; }
    public double LayoutExtentRatio { get; init; }
    public double? SourceBoundLayoutExtentRatio { get; init; }
    public double HeadingCountFidelity { get; init; }
    public double LinkCountFidelity { get; init; }
    public double ImageCountFidelity { get; init; }
    public double TableCountFidelity { get; init; }
    public double CodeBlockCountFidelity { get; init; }
    public double TaskCheckboxCountFidelity { get; init; }
    public double DetailsCountFidelity { get; init; }
    public double NativeToBrowserFirstRenderRatio { get; init; }
    public double NativeToBrowserFullPageRatio { get; init; }
    public double? NativeToSameByteFirstRenderRatio { get; init; }
    public double? NativeToSameByteFullPageRatio { get; init; }
    public int BrowserImageCount { get; init; }
    public int NativeImageObservations { get; init; }
    public int BrowserDistinctImageCount { get; init; }
    public int BrowserMediaCount { get; init; }
    public int BrowserDistinctAtomicMediaCount { get; init; }
    public int NativeImageSourceCount { get; init; }
    public int BrowserHeadingCount { get; init; }
    public int NativeHeadingObservations { get; init; }
    public int BrowserLinkCount { get; init; }
    public int NativeLinkObservations { get; init; }
    public int BrowserTableCount { get; init; }
    public int NativeTableObservations { get; init; }
    public int BrowserCodeBlockCount { get; init; }
    public int NativeCodeBlockObservations { get; init; }
    public int BrowserTaskCheckboxCount { get; init; }
    public int NativeTaskCheckboxObservations { get; init; }
    public int BrowserDetailsCount { get; init; }
    public int NativeDisclosureObservations { get; init; }
    public int BrowserVisibleMermaidSources { get; init; }
    public int NativeMermaidDiagrams { get; init; }
    public int MatchedMermaidTransformations { get; init; }
}

internal sealed class ReadmeAuditCaseResult
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset CorpusGeneratedAtUtc { get; init; }
    public int Rank { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string ReadmeSha { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<string> Failures { get; init; } = [];
    public bool InfrastructureFailure { get; init; }
    public BrowserAuditResult? Browser { get; init; }
    public NativeAuditResult? Native { get; init; }
    public ReadmeAuditComparison? Comparison { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}

internal sealed class NativeAuditInfrastructureException : Exception
{
    internal NativeAuditInfrastructureException(string message) : base(message) { }
    internal NativeAuditInfrastructureException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal sealed class ReadmeAuditSummary
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset CorpusGeneratedAtUtc { get; init; }
    public string Query { get; init; } = string.Empty;
    public int StartRank { get; init; }
    public int EndRank { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public double NativeFirstRenderRatioP50 { get; init; }
    public double NativeFirstRenderRatioP95 { get; init; }
    public double NativeFullPageRatioP50 { get; init; }
    public double NativeFullPageRatioP95 { get; init; }
    public string PerformanceRatioReference { get; init; } = "live GitHub Edge";
    public bool SourceBoundPerformanceRatiosRequired { get; init; }
    public bool SourceBoundPerformanceRatiosComplete { get; init; }
    public int FirstPerformanceRatioCaseCount { get; init; }
    public int FullPerformanceRatioCaseCount { get; init; }
    public int ExpectedPerformanceRatioCaseCount { get; init; }
    public bool AggregateGateEnforced { get; init; }
    public IReadOnlyList<string> AggregateFailures { get; init; } = [];
    public bool Passed { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}
