using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
            if (options.AuditResume && TryReadCompletedCase(caseResultPath, manifest, repository, out ReadmeAuditCaseResult? resumed))
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
                ReadmeAuditSummary partial = BuildSummary(manifest, selected, results);
                WriteJson(Path.Combine(options.OutputDirectory, "summary.json"), partial);
                WriteSummaryMarkdown(Path.Combine(options.OutputDirectory, "summary.md"), partial, results);
                throw new InvalidOperationException(
                    $"Native README audit infrastructure failed at rank {repository.Rank} " +
                    $"({repository.FullName}); the remaining repositories cannot produce valid comparisons.");
            }
        }

        ReadmeAuditSummary summary = BuildSummary(manifest, selected, results);
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
            if (comparison.NativeImageSourceCount < comparison.BrowserDistinctAtomicMediaCount)
            {
                failures.Add(
                    $"JitHub audited {comparison.NativeImageSourceCount} distinct image/media sources, " +
                    $"but Edge rendered {comparison.BrowserDistinctAtomicMediaCount}.");
            }
        }

        return new ReadmeAuditCaseResult
        {
            SchemaVersion = 2,
            CorpusGeneratedAtUtc = manifest.GeneratedAtUtc,
            Rank = repository.Rank,
            FullName = repository.FullName,
            ReadmeSha = repository.Readme.Sha,
            Status = failures.Count == 0 ? "passed" : "failed",
            Failures = failures,
            InfrastructureFailure = infrastructureFailure,
            Browser = browser,
            Native = native,
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
        startInfo.ArgumentList.Add(browserScript);
        startInfo.ArgumentList.Add($"--url={GetSnapshotUrl(repository)}");
        startInfo.ArgumentList.Add($"--readme-sha={GetReadmeEvidenceIdentity(repository)}");
        startInfo.ArgumentList.Add($"--out={output}");
        startInfo.ArgumentList.Add($"--width={ViewportWidth}");
        startInfo.ArgumentList.Add("--height=700");
        startInfo.ArgumentList.Add("--max-tiles=512");
        if (!string.IsNullOrWhiteSpace(options.AuditEdgePath))
        {
            startInfo.ArgumentList.Add($"--edge={options.AuditEdgePath}");
        }

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

        return JsonSerializer.Deserialize<BrowserAuditResult>(File.ReadAllText(reportPath), JsonOptions)
            ?? throw new InvalidDataException("Edge README oracle produced an empty report.");
    }

    private static string GetSnapshotUrl(ReadmeAuditRepository repository) =>
        $"{repository.Url.TrimEnd('/')}/tree/{repository.CommitSha}";

    private static string GetReadmeEvidenceIdentity(ReadmeAuditRepository repository) =>
        repository.Readme.Available ? repository.Readme.Sha : "absent";

    private static NativeAuditResult RunNativeAudit(
        CaptureOptions options,
        ReadmeAuditRepository repository,
        string caseDirectory,
        string accessToken,
        IReadOnlyList<BrowserImage>? renderedBrowserImages,
        bool expectRenderedReadme)
    {
        string output = Path.Combine(caseDirectory, "native");
        string runtime = Path.Combine(caseDirectory, ".runtime");
        // Keep diagnostics from retries isolated. Reusing a case directory used
        // to append a previous run's failures/resolutions to the new evidence.
        string dataRoot = Path.Combine(runtime, $"data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(dataRoot);
        string appReady = Path.Combine(runtime, "app-ready.json");
        string hostReady = Path.Combine(runtime, "host-ready.json");
        string renderComplete = Path.Combine(runtime, "render-complete.json");
        string renderFailure = Path.Combine(runtime, "render-failure.txt");
        string imageEvidence = Path.Combine(runtime, "image-unavailable.ndjson");
        string imageResolutionEvidence = Path.Combine(runtime, "image-resolution.ndjson");
        string svgWorkerEvidence = Path.Combine(runtime, "svg-worker-timeouts.ndjson");
        string svgPreflightEvidence = Path.Combine(runtime, "svg-preflight-rejections.ndjson");
        string shutdownStageEvidence = Path.Combine(runtime, "shutdown-stage.json");
        string captureRequest = Path.Combine(runtime, "capture-request.json");
        string captureResponse = Path.Combine(runtime, "capture-response.json");
        foreach (string stale in new[]
        {
            appReady, hostReady, renderComplete, renderFailure, imageEvidence,
            imageResolutionEvidence, svgWorkerEvidence, svgPreflightEvidence,
            shutdownStageEvidence,
            captureRequest, captureResponse,
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
        startInfo.Environment["JITHUB_MARKDOWN_RENDER_FAILURE_EVIDENCE_PATH"] = renderFailure;
        startInfo.Environment["JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH"] = imageEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH"] = imageResolutionEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_SVG_WORKER_EVIDENCE_PATH"] = svgWorkerEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_SVG_PREFLIGHT_EVIDENCE_PATH"] = svgPreflightEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH"] = shutdownStageEvidence;
        startInfo.Environment["JITHUB_MARKDOWN_CAPTURE_REQUEST_PATH"] = captureRequest;
        startInfo.Environment["JITHUB_MARKDOWN_CAPTURE_RESPONSE_PATH"] = captureResponse;

        Stopwatch wall = Stopwatch.StartNew();
        using Process launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start JitHub for README audit.");
        Process? appProcess = null;
        Application? application = null;
        Window? window = null;
        try
        {
            int processId = WaitForReadySignal(appReady, launcher, TimeSpan.FromSeconds(30));
            double appReadyElapsedMs = wall.Elapsed.TotalMilliseconds;
            appProcess = Process.GetProcessById(processId);
            appProcess.Refresh();
            double cpuAtReadyMs = appProcess.TotalProcessorTime.TotalMilliseconds;
            application = Application.Attach(processId);
            using var automation = new UIA3Automation();
            window = WaitForWindow(application, automation, TimeSpan.FromSeconds(30));
            IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
            NativeMethods.ResizeWindow(windowHandle, ViewportWidth, ViewportHeight);
            NativeMethods.ActivateForKeyboard(windowHandle);
            Thread.Sleep(300);

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
                    useRendererCapture: false);
                capture.Stop();
                appProcess.Refresh();
                double absentCpuMs = Math.Max(
                    0,
                    appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
                int absentRawUnavailable = CountNonEmptyLines(imageEvidence);
                string? absentFailure = File.Exists(renderFailure) ? File.ReadAllText(renderFailure) : null;
                long absentPeakWorkingSetBytes = appProcess.PeakWorkingSet64;
                ReadmeAuditCloseResult absentClose = CloseAndWait(window, appProcess, launcher);
                PreserveEvidenceFile(shutdownStageEvidence, Path.Combine(output, "shutdown-stage.json"));
                PreserveEvidenceFile(svgPreflightEvidence, Path.Combine(output, "svg-preflight-rejections.ndjson"));
                window = null;
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

            TryOpenReadme(window, repository.Readme.Path, TimeSpan.FromSeconds(45));
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
                        useRendererCapture: false);
                    capture.Stop();
                    appProcess.Refresh();
                    double sourceCpuMs = Math.Max(
                        0,
                        appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
                    string? sourceFailure = File.Exists(renderFailure) ? File.ReadAllText(renderFailure) : null;
                    long sourcePeakWorkingSetBytes = appProcess.PeakWorkingSet64;
                    ReadmeAuditCloseResult sourceClose = CloseAndWait(window, appProcess, launcher);
                    PreserveEvidenceFile(shutdownStageEvidence, Path.Combine(output, "shutdown-stage.json"));
                    PreserveEvidenceFile(svgPreflightEvidence, Path.Combine(output, "svg-preflight-rejections.ndjson"));
                    window = null;
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
                    };
                }
            }

            AutomationElement host = WaitForHost(
                window,
                TimeSpan.FromSeconds(60),
                Path.Combine(output, "host-timeout.png"));
            WaitForRenderSignal(
                renderComplete,
                renderFailure,
                TimeSpan.FromSeconds(45));
            double coldStartToFirstRenderMs = wall.Elapsed.TotalMilliseconds;
            double experienceFirstRenderMs = coldStartToFirstRenderMs - appReadyElapsedMs;
            double firstRenderMs = ReadSignalElapsedMilliseconds(hostReady, renderComplete);
            ReadmeAuditPerformanceSnapshot firstPerformance = ReadPerformanceSnapshot(renderComplete);
            appProcess.Refresh();
            double firstRenderCpuMs = Math.Max(
                0,
                appProcess.TotalProcessorTime.TotalMilliseconds - cpuAtReadyMs);
            Stopwatch textProbe = Stopwatch.StartNew();
            string text = WaitForStableText(host, TimeSpan.FromSeconds(30));
            textProbe.Stop();

            NativeTraversalResult traversal = CaptureNativeTiles(
                window,
                host,
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
            PreserveEvidenceFile(imageEvidence, Path.Combine(output, "image-unavailable.ndjson"));
            PreserveEvidenceFile(imageResolutionEvidence, Path.Combine(output, "image-resolution.ndjson"));
            PreserveEvidenceFile(svgWorkerEvidence, Path.Combine(output, "svg-worker-timeouts.ndjson"));
            PreserveEvidenceFile(svgPreflightEvidence, Path.Combine(output, "svg-preflight-rejections.ndjson"));

            ReadmeAuditCloseResult close = CloseAndWait(window, appProcess, launcher);
            PreserveEvidenceFile(shutdownStageEvidence, Path.Combine(output, "shutdown-stage.json"));
            window = null;
            return new NativeAuditResult
            {
                FirstRenderMs = firstRenderMs,
                ExperienceFirstRenderMs = experienceFirstRenderMs,
                ColdStartToFirstRenderMs = coldStartToFirstRenderMs,
                FullTraversalMs = fullTraversalMs,
                AuditOverheadMs = textProbe.Elapsed.TotalMilliseconds + traversal.AuditOverheadMs,
                FirstRenderCpuMs = firstRenderCpuMs,
                CpuMs = cpuMs,
                FirstPerformance = firstPerformance,
                FullPerformance = fullPerformance,
                PeakWorkingSetBytes = peakWorkingSetBytes,
                Text = NormalizeText(text),
                MermaidSources = traversal.MermaidSources,
                Width = traversal.Width,
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
            };
        }
        catch
        {
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
        }
    }

    private static NativeTraversalResult CaptureNativeTiles(
        Window window,
        AutomationElement host,
        string output,
        string renderFailurePath,
        Process appProcess,
        string captureRequestPath,
        string captureResponsePath)
    {
        if (!host.Patterns.Scroll.IsSupported)
        {
            throw new InvalidOperationException("Repository README host does not expose ScrollPattern.");
        }
        var scroll = host.Patterns.Scroll.Pattern;
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
        Stopwatch traversalWall = Stopwatch.StartNew();
        for (int index = 0; index < 512; index++)
        {
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
            // Lazy realization can change the document extent after the scroll
            // operation itself has completed. Wait for both percentage and view
            // size to stabilize before capturing or advancing again, without
            // walking the expensive full UIA subtree per viewport.
            auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
            VisibleImageWaitResult imageWait = WaitForVisibleImages(host, TimeSpan.FromSeconds(20));
            auditOverheadMs += imageWait.ProbeOverheadMs;
            if (imageWait.ElapsedMilliseconds >= SlowVisibleImageWaitMilliseconds)
            {
                visibleImageWaits.Add(new ReadmeAuditVisibleImageWait
                {
                    TileIndex = index,
                    ElapsedMilliseconds = imageWait.ElapsedMilliseconds,
                    TimedOut = imageWait.TimedOut,
                    LoadingImageCount = imageWait.LoadingImageCount,
                });
            }

            Stopwatch automationProbe = Stopwatch.StartNew();
            double actual = scroll.VerticalScrollPercent.ValueOrDefault;
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            if (double.IsNaN(actual) || actual < 0)
            {
                actual = 0;
            }
            string file = $"tile-{index:D4}.png";
            string path = Path.Combine(output, file);
            Stopwatch capture = Stopwatch.StartNew();
            (int tileWidth, int tileHeight) = CaptureHost(
                window,
                host,
                path,
                captureRequestPath,
                captureResponsePath);
            capture.Stop();
            auditOverheadMs += capture.Elapsed.TotalMilliseconds;
            width = Math.Max(width, tileWidth);
            viewportHeight = Math.Max(viewportHeight, tileHeight);
            double elapsedAtCaptureMs = traversalWall.Elapsed.TotalMilliseconds;
            tiles.Add(new AuditTile
            {
                Index = index,
                ScrollPercent = actual,
                Width = tileWidth,
                Height = tileHeight,
                File = file,
                NativeTraversalElapsedAtCaptureMs = elapsedAtCaptureMs,
                NativeChargedAtCaptureMs = Math.Max(0, elapsedAtCaptureMs - auditOverheadMs),
            });

            automationProbe.Restart();
            bool verticallyScrollable = scroll.VerticallyScrollable.ValueOrDefault;
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            if (!verticallyScrollable)
            {
                reachedBottom = true;
                break;
            }

            automationProbe.Restart();
            scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            // WinUI's UIA LargeIncrement can be a no-op on the first request
            // even though SetScrollPercent works. Probe it briefly, then use
            // the explicit-percent fallback below. The fallback still waits
            // up to five seconds for actual movement and fails if none occurs;
            // a no-op command must not add a fixed five-second renderer charge.
            TimeSpan scrollChangeTimeout = TimeSpan.FromMilliseconds(250);
            ScrollWaitResult scrollChange = WaitForScrollChange(
                scroll,
                actual,
                scrollChangeTimeout);
            auditOverheadMs += scrollChange.ProbeOverheadMs;
            if (scrollChange.Succeeded)
            {
                continue;
            }

            // A no-op large increment at 100% is the only reliable end signal
            // when lazy realization can expand the extent and move the current
            // percentage backwards while the audit is traversing.
            automationProbe.Restart();
            double settled = scroll.VerticalScrollPercent.ValueOrDefault;
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            if (actual >= 99.5 && (settled < 0 || settled >= 99.5))
            {
                // The document was already stable at 100%; the elapsed wait
                // only confirmed that the requested increment was a no-op.
                auditOverheadMs += Math.Max(
                    0,
                    scrollChange.ElapsedMs - scrollChange.ProbeOverheadMs);
                reachedBottom = true;
                break;
            }

            automationProbe.Restart();
            double currentViewSize = Math.Clamp(scroll.VerticalViewSize.ValueOrDefault, 0.1, 100);
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            double requested = Math.Min(100, Math.Max(actual + 0.5, actual + (currentViewSize * 0.9)));
            automationProbe.Restart();
            SetScrollPercentWithRetry(host, ref scroll, requested, required: true);
            automationProbe.Stop();
            auditOverheadMs += automationProbe.Elapsed.TotalMilliseconds;
            scrollChange = WaitForScrollChange(scroll, actual, TimeSpan.FromSeconds(5));
            auditOverheadMs += scrollChange.ProbeOverheadMs;
            if (!scrollChange.Succeeded)
            {
                throw new InvalidOperationException(
                    $"README scrolling stopped at {actual:F2}% before the document end.");
            }
        }

        if (!reachedBottom)
        {
            throw new InvalidOperationException(
                "README traversal exceeded the 512-view safety ceiling before reaching the document end.");
        }

        Stopwatch semanticProbe = Stopwatch.StartNew();
        AutomationElement[] descendants = host.FindAllDescendants();
        loadingAfterTraversal = descendants.Count(IsLoadingImage);
        semanticProbe.Stop();
        auditOverheadMs += semanticProbe.Elapsed.TotalMilliseconds;
        if (loadingAfterTraversal > 0)
        {
            // A large image wall can expand rows that have already been
            // visited, shifting a few still-deferred images between the first
            // pass's viewport stops. Revisit the exact captured percentages
            // only when that happened. This keeps the normal path single-pass
            // while proving that a full-page audit leaves no lazy placeholder.
            auditOverheadMs += RevisitPendingImages(
                host,
                ref scroll,
                tiles,
                appProcess,
                renderFailurePath,
                traversalWall);
            semanticProbe.Restart();
            descendants = host.FindAllDescendants();
        }
        else
        {
            semanticProbe.Restart();
        }
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
        // Compare textual link destinations independently from atomic linked
        // images. GitHub's live DOM removes or rewrites many generated
        // animated-image self links while the rendered-README API preserves
        // them, and JitHub intentionally exposes those images as operable UIA
        // hyperlinks. Images and their source coverage are gated separately.
        // A single authored text anchor can still expose multiple fragments, so
        // compare its logical destination rather than its raw peer count.
        linkObservations = nativeLinks
            .Where(link => !string.Equals(
                link.ClassName,
                "MarkdownLinkedImage",
                StringComparison.Ordinal))
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
        SetScrollPercentWithRetry(host, ref scroll, 0, required: false);
        double viewSize = Math.Clamp(scroll.VerticalViewSize.ValueOrDefault, 0.1, 100);
        int estimatedHeight = viewSize <= 0 ? viewportHeight : (int)Math.Ceiling(viewportHeight * 100 / viewSize);
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
            visibleImageWaits);
    }

    private static double RevisitPendingImages(
        AutomationElement host,
        ref IScrollPattern scroll,
        IReadOnlyList<AuditTile> tiles,
        Process appProcess,
        string renderFailurePath,
        Stopwatch traversalWall)
    {
        double auditOverheadMs = 0;
        double previous = double.NaN;
        foreach (AuditTile tile in tiles)
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

            double requested = Math.Clamp(tile.ScrollPercent, 0, 100);
            if (double.IsFinite(previous) && Math.Abs(requested - previous) < 0.01)
                continue;

            Stopwatch probe = Stopwatch.StartNew();
            SetScrollPercentWithRetry(host, ref scroll, requested, required: true);
            probe.Stop();
            auditOverheadMs += probe.Elapsed.TotalMilliseconds;
            auditOverheadMs += WaitForScrollSettled(scroll, TimeSpan.FromSeconds(2));
            // Only UIA probing is audit overhead. Time spent waiting for an
            // actual visible image remains part of native full-page latency.
            auditOverheadMs += WaitForVisibleImages(host, TimeSpan.FromSeconds(20)).ProbeOverheadMs;
            previous = requested;
        }

        return auditOverheadMs;
    }

    private static ReadmeAuditComparison Compare(
        BrowserAuditResult browser,
        NativeAuditResult native,
        string caseDirectory)
    {
        string browserVisibleText = string.IsNullOrWhiteSpace(browser.Semantic.VisibleText)
            ? browser.Semantic.Text
            : browser.Semantic.VisibleText;
        IReadOnlyList<string> matchedVisibleMermaidSources = MatchEquivalentMermaidSources(
            browser.Semantic.VisibleMermaidSources,
            native.MermaidSources);
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
        double textCoverage = TokenCoverage(browserVisibleText, comparableNativeText);
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
        int browserDistinctImages = browser.Semantic.Images
            .Where(IsVisibleRenderedBrowserImage)
            .Select(image => string.IsNullOrWhiteSpace(image.CurrentSource)
                ? image.Source
                : image.CurrentSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .Count();
        int browserDistinctMedia = browser.Semantic.Media
            .Select(media => string.IsNullOrWhiteSpace(media.CurrentSource)
                ? media.Source
                : media.CurrentSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .Count();
        int browserDistinctAtomicMedia = browserDistinctImages + browserDistinctMedia;
        int browserDistinctLinks = browser.Semantic.Links
            .Where(link => !string.IsNullOrWhiteSpace(link.Text))
            .Select(link => link.Href)
            .Where(destination => !string.IsNullOrWhiteSpace(destination))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        double headingFidelity = CountFidelity(
            browser.Semantic.Headings.Count,
            native.HeadingObservations);
        double linkFidelity = CountFidelity(
            browserDistinctLinks,
            native.LinkObservations);
        // Browser image failures remain reference evidence, but a transient Edge
        // download failure must not penalize JitHub for successfully rendering the
        // same authored source. Missing native coverage is still a hard failure below.
        double imageFidelity = CoverageFidelity(browserDistinctAtomicMedia, native.ImageSourceCount);
        double tableFidelity = CountFidelity(browser.Semantic.Tables, native.TableObservations);
        int comparableBrowserCodeBlocks = Math.Max(
            0,
            browser.Semantic.CodeBlocks - matchedVisibleMermaidSources.Count);
        double codeBlockFidelity = CountFidelity(comparableBrowserCodeBlocks, native.CodeBlockObservations);
        double taskCheckboxFidelity = CountFidelity(
            browser.Semantic.TaskCheckboxes,
            native.TaskCheckboxObservations);
        double detailsFidelity = CountFidelity(browser.Semantic.Details, native.DisclosureObservations);
        double expectedNativeHeight = browser.Semantic.Width <= 0
            ? browser.Semantic.Height
            : browser.Semantic.Height * browser.Semantic.Width / Math.Max(1, native.Width);
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
            TextTokenCoverage = textCoverage,
            TextTokenPrecision = textPrecision,
            TextTokenFidelity = textFidelity,
            MeanTileSsim = similarities.Count == 0 ? 0 : similarities.Average(),
            MinimumTileSsim = similarities.Count == 0 ? 0 : similarities.Min(),
            VisualStructureScore = visualStructureScore,
            LayoutExtentRatio = layoutExtentRatio,
            HeadingCountFidelity = headingFidelity,
            LinkCountFidelity = linkFidelity,
            ImageCountFidelity = imageFidelity,
            TableCountFidelity = tableFidelity,
            CodeBlockCountFidelity = codeBlockFidelity,
            TaskCheckboxCountFidelity = taskCheckboxFidelity,
            DetailsCountFidelity = detailsFidelity,
            NativeToBrowserFirstRenderRatio = nativeToBrowserFirst,
            NativeToBrowserFullPageRatio = nativeToBrowserFull,
            BrowserImageCount = browser.Semantic.Images.Count,
            NativeImageObservations = native.ImageObservations,
            BrowserDistinctImageCount = browserDistinctImages,
            BrowserMediaCount = browser.Semantic.Media.Count,
            BrowserDistinctAtomicMediaCount = browserDistinctAtomicMedia,
            NativeImageSourceCount = native.ImageSourceCount,
            BrowserHeadingCount = browser.Semantic.Headings.Count,
            NativeHeadingObservations = native.HeadingObservations,
            BrowserLinkCount = browserDistinctLinks,
            NativeLinkObservations = native.LinkObservations,
            BrowserTableCount = browser.Semantic.Tables,
            NativeTableObservations = native.TableObservations,
            BrowserCodeBlockCount = browser.Semantic.CodeBlocks,
            NativeCodeBlockObservations = native.CodeBlockObservations,
            BrowserTaskCheckboxCount = browser.Semantic.TaskCheckboxes,
            NativeTaskCheckboxObservations = native.TaskCheckboxObservations,
            BrowserDetailsCount = browser.Semantic.Details,
            NativeDisclosureObservations = native.DisclosureObservations,
            BrowserVisibleMermaidSources = browser.Semantic.VisibleMermaidSources.Count,
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

    private static string NormalizeMermaidSource(string source) =>
        (source ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static ReadmeAuditSummary BuildSummary(
        ReadmeAuditManifest manifest,
        IReadOnlyList<ReadmeAuditRepository> selected,
        IReadOnlyList<ReadmeAuditCaseResult> results)
    {
        double[] firstRatios = results
            .Where(result => result.Comparison is not null && double.IsFinite(result.Comparison.NativeToBrowserFirstRenderRatio))
            .Select(result => result.Comparison!.NativeToBrowserFirstRenderRatio)
            .OrderBy(value => value)
            .ToArray();
        double[] fullRatios = results
            .Where(result => result.Comparison is not null && double.IsFinite(result.Comparison.NativeToBrowserFullPageRatio))
            .Select(result => result.Comparison!.NativeToBrowserFullPageRatio)
            .OrderBy(value => value)
            .ToArray();
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
        if (enforceAggregateGates && firstP95 > 1.10)
        {
            aggregateFailures.Add($"Native first-render p95 was {firstP95:P1} of Edge, above 110%.");
        }
        if (enforceAggregateGates && fullP95 > 1.10)
        {
            aggregateFailures.Add($"Native full-page p95 was {fullP95:P1} of Edge, above 110%.");
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
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("# Top README rendering audit");
        writer.WriteLine();
        writer.WriteLine($"- Result: **{(summary.Passed ? "PASS" : "FAIL")}**");
        writer.WriteLine($"- Corpus: ranks {summary.StartRank}–{summary.EndRank}, generated {summary.CorpusGeneratedAtUtc:O}");
        writer.WriteLine($"- Cases: {summary.PassedCases} passed, {summary.FailedCases} failed");
        writer.WriteLine($"- Native/Edge first-render ratio: p50 {summary.NativeFirstRenderRatioP50:F3}, p95 {summary.NativeFirstRenderRatioP95:F3}");
        writer.WriteLine($"- Native/Edge full-page ratio: p50 {summary.NativeFullPageRatioP50:F3}, p95 {summary.NativeFullPageRatioP95:F3}");
        writer.WriteLine();
        writer.WriteLine("| Rank | Repository | Result | Text | Structure | Styled viewport SSIM | Native unavailable | First ratio | Full ratio |");
        writer.WriteLine("| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (ReadmeAuditCaseResult result in results)
        {
            writer.WriteLine(
                $"| {result.Rank} | {result.FullName} | {result.Status} | " +
                $"{result.Comparison?.TextTokenCoverage.ToString("P2", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.VisualStructureScore.ToString("P2", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.MeanTileSsim.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Native?.UnavailableImages.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.NativeToBrowserFirstRenderRatio.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} | " +
                $"{result.Comparison?.NativeToBrowserFullPageRatio.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} |");
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
        while (stopwatch.Elapsed < timeout)
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
            Thread.Sleep(150);
        }
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
        var textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("README host does not expose UIA TextPattern.");
        Stopwatch stopwatch = Stopwatch.StartNew();
        string previous = string.Empty;
        int stable = 0;
        while (stopwatch.Elapsed < timeout)
        {
            string current = NormalizeText(ReadDocumentTextInBoundedChunks(textPattern.DocumentRange));
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
        ITextRange cursor = documentRange.Clone();
        cursor.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            cursor,
            TextPatternRangeEndpoint.Start);
        var result = new StringBuilder(Math.Min(chunkCharacters, maximumDocumentCharacters));
        while (result.Length < maximumDocumentCharacters &&
               cursor.CompareEndpoints(
                   TextPatternRangeEndpoint.Start,
                   documentRange,
                   TextPatternRangeEndpoint.End) < 0)
        {
            ITextRange chunk = cursor.Clone();
            int moved = chunk.MoveEndpointByUnit(
                TextPatternRangeEndpoint.End,
                TextUnit.Character,
                Math.Min(chunkCharacters, maximumDocumentCharacters - result.Length));
            if (moved <= 0)
                break;

            string value = chunk.GetText(-1);
            if (value.Length == 0)
                break;
            result.Append(value);
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

    private static VisibleImageWaitResult WaitForVisibleImages(
        AutomationElement host,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        double probeOverheadMs = 0;
        while (stopwatch.Elapsed < timeout)
        {
            Stopwatch probe = Stopwatch.StartNew();
            // MarkdownRenderer exposes the aggregate visible-image state on
            // the document peer. Polling one property keeps this O(images)
            // inside the control instead of materializing the complete UIA
            // tree on every viewport. The old traversal was quadratic in long
            // documents and also retained thousands of transient COM wrappers.
            bool isLoading = !string.IsNullOrWhiteSpace(
                host.Properties.ItemStatus.ValueOrDefault);
            probe.Stop();
            probeOverheadMs += probe.Elapsed.TotalMilliseconds;
            if (!isLoading)
                return new VisibleImageWaitResult(probeOverheadMs, false, 0, stopwatch.Elapsed.TotalMilliseconds);
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
            stopwatch.Elapsed.TotalMilliseconds - diagnosticProbe.Elapsed.TotalMilliseconds);
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
            if (actual >= 0 && Math.Abs(actual - previous) > 0.01)
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

    private static ReadmeAuditPerformanceSnapshot ReadPerformanceSnapshot(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("Performance", out JsonElement performance) ||
            performance.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Markdown audit signal '{path}' did not include performance counters.");
        }

        ReadmeAuditPerformanceSnapshot? snapshot = performance.Deserialize<ReadmeAuditPerformanceSnapshot>(JsonOptions);
        if (snapshot is null || snapshot.SourceCacheBytes < 0 || snapshot.SourceCacheHits < 0 ||
            snapshot.ImageFetches < 0 || snapshot.ImageFetchMilliseconds < 0 ||
            snapshot.ImageFetchFailures < 0 || snapshot.ImageFetchCancellations < 0 ||
            snapshot.SourceCacheEvictions < 0 || snapshot.PendingImageFetches < 0 ||
            snapshot.ActiveImageFetches < 0 || snapshot.CpuPreparations < 0 ||
            snapshot.CpuPreparationMilliseconds < 0 || snapshot.ScenePreparations < 0 ||
            snapshot.ScenePreparationMilliseconds < 0)
        {
            throw new InvalidDataException(
                $"Markdown audit signal '{path}' contained invalid performance counters.");
        }

        return snapshot;
    }

    private static void SetScrollPercentWithRetry(
        AutomationElement host,
        ref IScrollPattern scroll,
        double verticalPercent,
        bool required)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!host.Patterns.Scroll.IsSupported)
                {
                    throw new InvalidOperationException(
                        "Repository README host stopped exposing ScrollPattern.");
                }

                // WinUI replaces its ScrollPresenter automation provider during
                // extent-changing relayout. Reacquiring the pattern avoids using
                // a stale COM provider after lazy images change document height.
                scroll = host.Patterns.Scroll.Pattern;
                scroll.SetScrollPercent(ScrollPatternConstants.NoScroll, verticalPercent);
                return;
            }
            catch (InvalidOperationException exception)
            {
                lastFailure = exception;
                Thread.Sleep(25 * (attempt + 1));
            }
        }

        if (required)
        {
            throw new InvalidOperationException(
                $"README could not scroll to {verticalPercent:F2}% after provider relayout.",
                lastFailure);
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
        bool useRendererCapture = true)
    {
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
        bool save)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string temporaryPath = requestPath + $".{requestId}.tmp";
            try
            {
                if (File.Exists(responsePath))
                    File.Delete(responsePath);
                var request = new RendererCaptureRequest(requestId, outputPath, save);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(request, JsonOptions));
                File.Move(temporaryPath, requestPath, overwrite: true);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                TryDeleteAuditFile(temporaryPath);
                Thread.Sleep(50 * (attempt + 1));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
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

            Thread.Sleep(50 * (attempt + 1));
        }

        throw new TimeoutException(
            "The Markdown renderer did not complete its internal audit capture request.",
            lastFailure);
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
        Window window, Process appProcess, Process launcher)
    {
        // The packaged app may not be the process returned by Process.Start.
        // Process.ExitCode throws for Process.GetProcessById attachments, so
        // retain a native handle before closing the window and read that code.
        IntPtr appExitHandle = NativeMethods.OpenProcessExitHandle(appProcess.Id);
        try
        {
            bool closeRequestFailed = false;
            try { window.Close(); }
            catch { closeRequestFailed = true; }

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
                result.SchemaVersion == 2 &&
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
        IReadOnlyList<ReadmeAuditVisibleImageWait> VisibleImageWaits);

    private readonly record struct VisibleImageWaitResult(
        double ProbeOverheadMs,
        bool TimedOut,
        int LoadingImageCount,
        double ElapsedMilliseconds);

    private readonly record struct ScrollWaitResult(
        bool Succeeded,
        double ProbeOverheadMs,
        double ElapsedMs);

    private sealed record RendererCaptureRequest(
        string RequestId,
        string? OutputPath,
        bool Save);

    private sealed record RendererCaptureResponse(
        string RequestId,
        bool Succeeded,
        int Width,
        int Height,
        double DocumentTop,
        string? Error,
        ReadmeAuditPerformanceSnapshot? Performance);
}

internal sealed class ReadmeAuditPerformanceSnapshot
{
    public required long SourceCacheBytes { get; init; }
    public required long SourceCacheHits { get; init; }
    public required long ImageFetches { get; init; }
    public required long ImageFetchMilliseconds { get; init; }
    public required long ImageFetchFailures { get; init; }
    public required long ImageFetchCancellations { get; init; }
    public required long SourceCacheEvictions { get; init; }
    public required int PendingImageFetches { get; init; }
    public required int ActiveImageFetches { get; init; }
    public required long CpuPreparations { get; init; }
    public required long CpuPreparationMilliseconds { get; init; }
    public required long ScenePreparations { get; init; }
    public required long ScenePreparationMilliseconds { get; init; }
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
    public string Path { get; init; } = string.Empty;
    public string Sha { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
}

internal sealed class BrowserAuditResult
{
    public int SchemaVersion { get; init; }
    public string RepositoryUrl { get; init; } = string.Empty;
    public string ReadmeSha { get; init; } = string.Empty;
    public bool? ReadmeRendered { get; init; }
    public BrowserTiming Timing { get; init; } = new();
    public BrowserSemantic Semantic { get; init; } = new();
    public List<AuditTile> Tiles { get; init; } = [];
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

internal sealed class NativeAuditResult
{
    public double FirstRenderMs { get; init; }
    public double ExperienceFirstRenderMs { get; init; }
    public double ColdStartToFirstRenderMs { get; init; }
    public double FullTraversalMs { get; init; }
    public double AuditOverheadMs { get; init; }
    public double FirstRenderCpuMs { get; init; }
    public double CpuMs { get; init; }
    public ReadmeAuditPerformanceSnapshot? FirstPerformance { get; init; }
    public ReadmeAuditPerformanceSnapshot? FullPerformance { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<string> MermaidSources { get; init; } = [];
    public int Width { get; init; }
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
}

internal readonly record struct ReadmeAuditCloseResult(bool CleanExit, string? Failure);

internal sealed class ReadmeAuditComparison
{
    public double TextTokenCoverage { get; init; }
    public double TextTokenPrecision { get; init; }
    public double TextTokenFidelity { get; init; }
    public double MeanTileSsim { get; init; }
    public double MinimumTileSsim { get; init; }
    public double VisualStructureScore { get; init; }
    public double LayoutExtentRatio { get; init; }
    public double HeadingCountFidelity { get; init; }
    public double LinkCountFidelity { get; init; }
    public double ImageCountFidelity { get; init; }
    public double TableCountFidelity { get; init; }
    public double CodeBlockCountFidelity { get; init; }
    public double TaskCheckboxCountFidelity { get; init; }
    public double DetailsCountFidelity { get; init; }
    public double NativeToBrowserFirstRenderRatio { get; init; }
    public double NativeToBrowserFullPageRatio { get; init; }
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
    public IReadOnlyList<string> AggregateFailures { get; init; } = [];
    public bool Passed { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}
