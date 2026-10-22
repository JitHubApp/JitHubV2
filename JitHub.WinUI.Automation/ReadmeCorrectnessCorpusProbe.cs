using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.WindowsAPI;

internal static partial class ReadmeAuditProbe
{
    internal const string CorrectnessCorpusProbeName = "readme-correctness-corpus";
    internal const string CorrectnessAccountingSelfTestProbeName = "readme-correctness-selftest";
    internal const string CorrectnessInputPreflightProbeName = "readme-correctness-preflight";

    private const string FrozenCorrectnessManifestSha256 =
        "615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72";
    private const string CorrectnessSandboxMarker = "JITHUB_README_CORRECTNESS_SANDBOX";
    private const string CorrectnessClipboardIsolationMarker = "JITHUB_README_CORRECTNESS_CLIPBOARD_ISOLATED";
    private const int SourceAcquisitionMaximumBytes = 4 * 1024 * 1024;
    private const long MaximumCorrectnessReadmeBytes = 16L * 1024 * 1024;
    private const long MaximumScannedCorrectnessManifestBytes = 64L * 1024 * 1024;
    private const int MaximumCorrectnessCorpusEntries = 500_000;
    private const int MaximumCorrectnessCorpusManifests = 100_000;

    private static readonly string[] CorrectnessRequiredChecks =
    [
        "sourceIdentity",
        "verifiedCorpusInput",
        "renderLifecycle",
        "layoutTraversal",
        "visibleTextReadable",
        "contentCompleteness",
        "resourceCoverage",
        "unsupportedSyntaxClassification",
        "selection",
        "plainTextCopy",
        "routeAwayAndBack",
    ];

    internal static void RunCorrectnessAccountingSelfTests(CaptureOptions options)
    {
        CorrectnessAccountingSelfTestResult result = EvaluateCorrectnessAccountingSelfTests();
        string json = JsonSerializer.Serialize(result, JsonOptions);
        Console.WriteLine(json);
        Directory.CreateDirectory(options.OutputDirectory);
        WriteJson(Path.Combine(options.OutputDirectory, "readme-correctness-accounting-selftest.json"), result);
        if (!result.Passed)
            throw new InvalidOperationException("Correctness result-accounting self-tests failed.");
    }

    internal static void RunCorrectnessInputPreflight(CaptureOptions options)
    {
        ClearOfflineGitHubCredentialEnvironment();
        CorrectnessAccountingSelfTestResult accounting = EvaluateCorrectnessAccountingSelfTests();
        if (!accounting.Passed)
            throw new InvalidOperationException("Correctness result-accounting self-tests failed before input preflight.");

        ValidateCorrectnessRankRange(options);
        string manifestPath = options.AuditManifestPath
            ?? throw new ArgumentException("Correctness preflight requires --manifest=<frozen-top500.json>.");
        string sourceRootPath = options.AuditSourceAcquisitionRoot
            ?? throw new ArgumentException("Correctness preflight requires --source-acquisition-root=<verified-source-root>.");
        string corpusRootPath = options.AuditOfflineSameByteCorpusRoot
            ?? throw new ArgumentException("Correctness preflight requires --offline-same-byte-corpus-root=<existing-corpus-root>.");

        ReadmeAuditManifest manifest = ReadOfflinePinnedManifest(manifestPath, out string manifestSha256);
        if (!string.Equals(manifestSha256, FrozenCorrectnessManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Correctness preflight accepts only the frozen September 28 top-500 manifest.");

        string sourceRoot = RequireOrdinaryDirectory(sourceRootPath, "frozen README source-acquisition root");
        SourceAcquisitionIndex sourceIndex = ReadSourceAcquisitionIndex(sourceRoot, manifestPath, manifestSha256, manifest);
        string corpusRoot = GetOptionalOrdinaryDirectory(corpusRootPath, "existing same-byte corpus root", out bool corpusRootExists);
        IReadOnlyList<CorpusManifestCandidate> candidates = corpusRootExists
            ? ScanCorrectnessCorpusManifests(corpusRoot)
            : [];
        ReadmeAuditRepository[] selected = GetSelectedCorrectnessRepositories(manifest, options);
        var rows = new List<CorrectnessInputPreflightRow>(selected.Length);

        foreach (ReadmeAuditRepository repository in selected)
        {
            string? sourceSha256 = null;
            CorpusInputVerification input;
            if (!repository.Readme.Available)
            {
                input = CorpusInputVerification.NotApplicable("The frozen top-500 manifest has no README for this repository.");
            }
            else
            {
                try
                {
                    SourceAcquisitionRecord record = sourceIndex.Records[repository.Rank];
                    byte[] bytes = ReadVerifiedSourceBody(sourceRoot, repository, record);
                    sourceSha256 = HashSha256(bytes);
                    input = FindVerifiedCorrectnessCorpus(candidates, corpusRoot, repository, bytes, sourceSha256);
                }
                catch (Exception exception)
                {
                    input = CorpusInputVerification.Failed(RedactUrls(exception.Message));
                }
            }

            rows.Add(new CorrectnessInputPreflightRow
            {
                Rank = repository.Rank,
                Repository = repository.FullName,
                CommitSha = repository.CommitSha,
                ReadmeGitBlobSha1 = repository.Readme.Available ? repository.Readme.Sha : "absent",
                ReadmeSourceSha256 = sourceSha256,
                Status = GetCorrectnessInputStateName(input.State),
                Detail = input.Detail,
                CorpusManifestSha256 = input.ManifestSha256,
                VerifiedAssetCount = input.AssetCount,
                VerifiedAssetBytes = input.AssetBytes,
            });
        }

        string outputPath = Path.Combine(
            options.OutputDirectory,
            $"readme-correctness-input-preflight-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var report = new CorrectnessInputPreflightReport
        {
            SchemaVersion = 1,
            Scope = "offline-input-integrity-only-no-app-or-ui",
            Status = rows.All(row => row.Status is "passed" or "not-applicable")
                ? "inputs-ready-for-requested-scope"
                : rows.Any(row => row.Status == "failed") ? "input-integrity-failed" : "audit-input-missing",
            PhaseStatus = "incomplete-no-render-or-interaction-evaluated",
            ReleaseQualification = "not-qualified",
            FrozenManifestSha256 = manifestSha256,
            SourceAcquisitionManifestSha256 = sourceIndex.ManifestSha256,
            RequestedStartRank = options.AuditStartRank,
            RequestedCount = options.AuditCount,
            InputReadyCount = rows.Count(row => row.Status == "passed"),
            AuditInputMissingCount = rows.Count(row => row.Status == "audit-input-missing"),
            FailedCount = rows.Count(row => row.Status == "failed"),
            Rows = rows,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        WriteJson(outputPath, report);
        Console.WriteLine($"Correctness input preflight: {outputPath}");
        Console.WriteLine($"No app/UI was launched. Status={report.Status}; ready={report.InputReadyCount}, missing={report.AuditInputMissingCount}, failed={report.FailedCount}.");
    }

    internal static void RunCorrectnessCorpus(CaptureOptions options)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(CorrectnessSandboxMarker),
                "1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The correctness corpus probe is Sandbox-only. The selected-target guest runner must set its explicit Sandbox marker.");
        }

        ClearOfflineGitHubCredentialEnvironment();
        CorrectnessAccountingSelfTestResult accounting = EvaluateCorrectnessAccountingSelfTests();
        if (!accounting.Passed)
            throw new InvalidOperationException("Correctness result-accounting self-tests failed before corpus execution.");

        string manifestPath = options.AuditManifestPath
            ?? throw new ArgumentException("Correctness corpus audit requires --manifest=<frozen-top500.json>.");
        string sourceRootPath = options.AuditSourceAcquisitionRoot
            ?? throw new ArgumentException("Correctness corpus audit requires --source-acquisition-root=<verified-source-root>.");
        string corpusRootPath = options.AuditOfflineSameByteCorpusRoot
            ?? throw new ArgumentException("Correctness corpus audit requires --offline-same-byte-corpus-root=<verified-corpus-root>.");
        if (options.AuditStartRank <= 0 || options.AuditCount <= 0 ||
            options.AuditStartRank > 500 || options.AuditCount > 500 ||
            checked(options.AuditStartRank + options.AuditCount - 1) > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Correctness rank range must stay within 1..500.");
        }

        ReadmeAuditManifest manifest = ReadOfflinePinnedManifest(manifestPath, out string manifestSha256);
        if (!string.Equals(manifestSha256, FrozenCorrectnessManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Correctness audit accepts only the frozen September 28 top-500 manifest.");
        }

        string sourceRoot = RequireOrdinaryDirectory(sourceRootPath, "frozen README source-acquisition root");
        SourceAcquisitionIndex sourceIndex = ReadSourceAcquisitionIndex(sourceRoot, manifestPath, manifestSha256, manifest);
        string corpusRoot = GetOptionalOrdinaryDirectory(corpusRootPath, "verified same-byte corpus root", out bool corpusRootExists);
        IReadOnlyList<CorpusManifestCandidate> corpusCandidates = corpusRootExists
            ? ScanCorrectnessCorpusManifests(corpusRoot)
            : [];
        ReadmeAuditRepository[] selected = GetSelectedCorrectnessRepositories(manifest, options);

        string runDirectory = Path.Combine(
            options.OutputDirectory,
            "correctness-only",
            $"manifest-{manifestSha256[..12]}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        var cases = new List<CorrectnessCorpusCaseResult>(selected.Length);

        foreach (ReadmeAuditRepository repository in selected)
        {
            string caseId = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
            string caseDirectory = Path.Combine(runDirectory, "cases", caseId);
            Directory.CreateDirectory(caseDirectory);
            var checks = CorrectnessRequiredChecks
                .Select(name => CorrectnessCheck.NotEvaluated(name, "Not attempted."))
                .ToDictionary(check => check.Name, StringComparer.Ordinal);
            string? sourceSha256 = null;
            string? appFailure = null;
            CorpusInputVerification? corpusInput = null;
            NativeAuditResult? native = null;

            try
            {
                if (!repository.Readme.Available)
                {
                    checks["sourceIdentity"] = CorrectnessCheck.NotApplicable(
                        "sourceIdentity", "Frozen manifest has no README for this repository.");
                    checks["verifiedCorpusInput"] = CorrectnessCheck.NotApplicable(
                        "verifiedCorpusInput", "No README exists to bind to a local same-byte corpus.");
                    appFailure = "No README is available in the frozen manifest.";
                }
                else
                {
                    SourceAcquisitionRecord record = sourceIndex.Records[repository.Rank];
                    byte[] rawReadme = ReadVerifiedSourceBody(sourceRoot, repository, record);
                    sourceSha256 = HashSha256(rawReadme);
                    checks["sourceIdentity"] = CorrectnessCheck.Passed(
                        "sourceIdentity",
                        $"Frozen Git blob {repository.Readme.Sha}; {rawReadme.Length} source bytes; SHA-256 {sourceSha256}.");

                    corpusInput = FindVerifiedCorrectnessCorpus(
                        corpusCandidates,
                        corpusRoot,
                        repository,
                        rawReadme,
                        sourceSha256);
                    checks["verifiedCorpusInput"] = corpusInput.State switch
                    {
                        CorpusInputState.Passed => CorrectnessCheck.Passed("verifiedCorpusInput", corpusInput.Detail),
                        CorpusInputState.Failed => CorrectnessCheck.Failed("verifiedCorpusInput", corpusInput.Detail),
                        _ => CorrectnessCheck.AuditInputMissing("verifiedCorpusInput", corpusInput.Detail),
                    };

                    if (corpusInput.State == CorpusInputState.Passed)
                    {
                        ReadmeCorrectnessInteractionResult? interactions = null;
                        DateTimeOffset sourceEvidenceStartedAtUtc = DateTimeOffset.UtcNow;
                        native = RunNativeAudit(
                            options,
                            repository,
                            caseDirectory,
                            accessToken: string.Empty,
                            renderedBrowserImages: null,
                            sameByteCorpusPath: corpusInput.DirectoryPath,
                            expectedReadmeSha256: sourceSha256,
                            expectedReadmeBytes: rawReadme.LongLength,
                            expectRenderedReadme: true,
                            removeGitHubCredentials: true,
                            offlineSameBytePublicAudit: true,
                            pinnedManifestPath: manifestPath,
                            pinnedManifestRank: repository.Rank,
                            pinnedManifestSha256: manifestSha256,
                            correctnessInteraction: context => interactions = RunCorrectnessInteractions(
                                context with { SourceEvidenceStartedAtUtc = sourceEvidenceStartedAtUtc },
                                repository,
                                sourceSha256,
                                rawReadme.LongLength));

                        bool exactSource = string.Equals(
                            native.ReadmeSourceSha256,
                            sourceSha256,
                            StringComparison.OrdinalIgnoreCase);
                        checks["sourceIdentity"] = exactSource
                            ? CorrectnessCheck.Passed(
                                "sourceIdentity",
                                $"Actual app source matched frozen Git blob {repository.Readme.Sha}; {rawReadme.Length} bytes; SHA-256 {sourceSha256}.")
                            : CorrectnessCheck.Failed(
                                "sourceIdentity",
                                "Actual app source evidence did not match the verified frozen README bytes.");

                        bool renderLifecyclePassed = native.RenderFailure is null && native.CleanExit &&
                            string.IsNullOrWhiteSpace(native.CloseFailure);
                        checks["renderLifecycle"] = renderLifecyclePassed
                            ? CorrectnessCheck.Passed("renderLifecycle", "Renderer completed and app shut down cleanly.")
                            : CorrectnessCheck.Failed(
                                "renderLifecycle",
                                native.RenderFailure ?? native.CloseFailure ?? "App did not exit cleanly.");

                        bool traversalPassed = native.FullTraversalViewportCount > 0 &&
                            native.Tiles.Count > 0 && native.ContentViewportWidth > 0 &&
                            native.ContentViewportHeight > 0 && native.EstimatedContentHeight > 0 &&
                            native.LoadingImagesAfterTraversal == 0;
                        checks["layoutTraversal"] = traversalPassed
                            ? CorrectnessCheck.Passed(
                                "layoutTraversal",
                                $"Full native scroll traversal captured {native.FullTraversalViewportCount} viewports; geometry and terminal image state were available.")
                            : CorrectnessCheck.Failed(
                                "layoutTraversal",
                                "Native full-page traversal did not prove bounded viewport geometry and terminal coverage.");

                        checks["visibleTextReadable"] = string.IsNullOrWhiteSpace(native.Text)
                            ? CorrectnessCheck.Failed("visibleTextReadable", "The actual app exposed no stable document text.")
                            : CorrectnessCheck.Passed(
                                "visibleTextReadable",
                                $"The actual app exposed {native.Text.Length} normalized UIA text characters.");

                        if (corpusInput.CorrectnessInput && corpusInput.ExpectedContentPath is { } expectedPath)
                        {
                            // Schema-3 inputs carry a verified expected-content
                            // inventory and image demand, so all three oracles run.
                            ReadmeCorrectnessOracles.ExpectedContent expected = ReadmeCorrectnessOracles.LoadExpectedContent(expectedPath);
                            var observed = new ReadmeCorrectnessOracles.ObservedContent(
                                native.Text,
                                native.HeadingObservations,
                                native.TableObservations,
                                native.TaskCheckboxObservations,
                                native.DisclosureObservations,
                                native.UnavailableImages,
                                native.LoadingImagesAfterTraversal);
                            checks["contentCompleteness"] = ReadmeCorrectnessOracles.EvaluateContentCompleteness(expected, observed);
                            checks["unsupportedSyntaxClassification"] = ReadmeCorrectnessOracles.EvaluateUnsupportedSyntax(expected);
                            checks["resourceCoverage"] = ReadmeCorrectnessOracles.EvaluateResourceCoverage(
                                ReadmeCorrectnessOracles.LoadImageDemand(Path.Combine(corpusInput.DirectoryPath!, "manifest.json")),
                                native.UnavailableImages,
                                native.LoadingImagesAfterTraversal);
                        }
                        else
                        {
                            checks["contentCompleteness"] = CorrectnessCheck.NotEvaluated(
                                "contentCompleteness",
                                "This probe has no independent Markdown-to-visible-content oracle; UIA readability alone does not prove every source feature/content item rendered.");
                            checks["resourceCoverage"] = EvaluateObservedResourceCoverage(
                                native.UnavailableImages,
                                native.LoadingImagesAfterTraversal);
                            checks["unsupportedSyntaxClassification"] = CorrectnessCheck.NotEvaluated(
                                "unsupportedSyntaxClassification",
                                "The app audit does not yet expose a complete per-document unsupported-syntax diagnostic inventory.");
                        }

                        if (interactions is not null)
                        {
                            foreach (CorrectnessCheck check in interactions.Checks)
                                checks[check.Name] = check;
                        }
                    }
                    else
                    {
                        appFailure = corpusInput.Detail;
                        checks["renderLifecycle"] = CorrectnessCheck.NotEvaluated(
                            "renderLifecycle", "JitHub was not launched because no matching complete local corpus input passed preflight.");
                        checks["layoutTraversal"] = CorrectnessCheck.NotEvaluated(
                            "layoutTraversal", "JitHub was not launched because the required local corpus input was missing or invalid.");
                        checks["visibleTextReadable"] = CorrectnessCheck.NotEvaluated(
                            "visibleTextReadable", "JitHub was not launched because the required local corpus input was missing or invalid.");
                        checks["contentCompleteness"] = CorrectnessCheck.NotEvaluated(
                            "contentCompleteness", "Not evaluated because there was no verified local corpus to render.");
                        checks["resourceCoverage"] = corpusInput.State == CorpusInputState.Failed
                            ? CorrectnessCheck.Failed("resourceCoverage", corpusInput.Detail)
                            : CorrectnessCheck.AuditInputMissing("resourceCoverage", corpusInput.Detail);
                        checks["unsupportedSyntaxClassification"] = CorrectnessCheck.NotEvaluated(
                            "unsupportedSyntaxClassification", "Not evaluated because JitHub was not launched.");
                        checks["selection"] = CorrectnessCheck.NotEvaluated("selection", "Not evaluated because JitHub was not launched.");
                        checks["plainTextCopy"] = CorrectnessCheck.NotEvaluated("plainTextCopy", "Not evaluated because JitHub was not launched.");
                        checks["routeAwayAndBack"] = CorrectnessCheck.NotEvaluated("routeAwayAndBack", "Not evaluated because JitHub was not launched.");
                    }
                }
            }
            catch (Exception exception)
            {
                appFailure = RedactUrls(exception.Message);
                if (checks["sourceIdentity"].State == CorrectnessCheckState.NotEvaluated && repository.Readme.Available)
                    checks["sourceIdentity"] = CorrectnessCheck.Failed("sourceIdentity", appFailure);
                if (checks["renderLifecycle"].State == CorrectnessCheckState.NotEvaluated)
                    checks["renderLifecycle"] = CorrectnessCheck.Failed("renderLifecycle", appFailure);
            }

            string caseStatus = EvaluateCorrectnessCase(checks.Values);
            var result = new CorrectnessCorpusCaseResult
            {
                Rank = repository.Rank,
                Repository = repository.FullName,
                CommitSha = repository.CommitSha,
                ReadmeGitBlobSha1 = repository.Readme.Available ? repository.Readme.Sha : "absent",
                ReadmeSourceSha256 = sourceSha256,
                SameByteCorpusManifestSha256 = corpusInput?.ManifestSha256,
                VerifiedAssetCount = corpusInput?.AssetCount ?? 0,
                VerifiedAssetBytes = corpusInput?.AssetBytes ?? 0,
                Status = caseStatus,
                Failure = appFailure,
                Checks = checks.Values.OrderBy(check => check.Name, StringComparer.Ordinal).ToArray(),
                NativeEvidenceDirectory = native is null ? null : Path.Combine(caseDirectory, "native"),
                CompletedAtUtc = DateTimeOffset.UtcNow,
            };
            WriteJson(Path.Combine(caseDirectory, "correctness-case-result.json"), result);
            cases.Add(result);
            Console.WriteLine($"Correctness README {repository.Rank}/500: {repository.FullName} ({caseStatus}).");
        }

        bool fullTop500 = options.AuditStartRank == 1 && options.AuditCount == 500;
        int passed = cases.Count(result => result.Status == "passed");
        int classified = cases.Count(result => result.Status == "classified");
        int failed = cases.Count(result => result.Status == "failed");
        int incomplete = cases.Count(result => result.Status is not ("passed" or "classified" or "failed"));
        var report = new CorrectnessCorpusReport
        {
            SchemaVersion = 1,
            Scope = "frozen-top500-source-bound-app-correctness-only",
            ExecutionStatus = cases.Count == selected.Length ? "completed-within-requested-scope" : "incomplete",
            // Phase 3 completes when all 500 are evaluated with no failure:
            // classified outcomes are reported separately, never as passes.
            PhaseStatus = fullTop500 && passed + classified == 500 && failed == 0 && incomplete == 0
                ? "complete"
                : "incomplete",
            ReleaseQualification = "not-qualified",
            BrowserPixelComparison = "not-run-and-not-an-acceptance-gate",
            ComparativePerformance = "not-run-and-not-an-acceptance-gate",
            FrozenManifestSha256 = manifestSha256,
            SourceAcquisitionManifestSha256 = sourceIndex.ManifestSha256,
            RequestedStartRank = options.AuditStartRank,
            RequestedCount = options.AuditCount,
            CompletedCaseCount = cases.Count,
            PassedCaseCount = passed,
            ClassifiedCaseCount = classified,
            FailedCaseCount = failed,
            IncompleteCaseCount = incomplete,
            AccountingSelfTests = accounting,
            Cases = cases,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        string reportPath = Path.Combine(runDirectory, "correctness-only-summary.json");
        WriteJson(reportPath, report);
        Console.WriteLine($"Correctness-only report: {reportPath}");
        Console.WriteLine($"Release qualification: not-qualified; phase status: {report.PhaseStatus}; " +
            $"passed={passed}, classified={classified}, failed={failed}, incomplete={incomplete}.");

        if (failed != 0 || incomplete != 0 || !fullTop500)
        {
            throw new InvalidOperationException(
                $"Correctness-only audit did not meet all requested gates ({failed} failed, {incomplete} incomplete, full500={fullTop500}). " +
                "The report is diagnostic evidence only; no unattempted or unevaluated dimension is counted as a pass.");
        }
    }

    private static ReadmeCorrectnessInteractionResult RunCorrectnessInteractions(
        ReadmeCorrectnessLiveContext context,
        ReadmeAuditRepository repository,
        string expectedRawSha256,
        long expectedRawBytes)
    {
        var checks = new List<CorrectnessCheck>(3);
        string? selectedPhrase = null;
        string? selectedSha256 = null;
        string? fullDocumentSelectionSha256 = null;
        string? copiedSha256 = null;
        string navigationDetail = string.Empty;

        try
        {
            AutomationElement host = context.Host;
            var textPattern = host.Patterns.Text.PatternOrDefault
                ?? throw new InvalidOperationException("The Markdown host did not expose UIA TextPattern for selection.");
            ITextRange documentRange = textPattern.DocumentRange;
            documentRange.Select();
            string selectedDocument = ReadSelectionText(host);
            if (!string.Equals(selectedDocument, context.ExpectedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("TextPattern could not select and return the exact complete cross-block document text.");
            }
            fullDocumentSelectionSha256 = HashSha256(Encoding.UTF8.GetBytes(selectedDocument));

            // An atomic object (an image's alt text, a formula, a diagram)
            // selects as a whole, so the bounded round trip uses the first
            // candidate phrase whose first occurrence is ordinary text.
            string? phrase = null;
            ITextRange? range = null;
            string? enclosingAutomationId = null;
            int atomicCandidatesSkipped = 0;
            foreach (string candidate in EnumerateBoundedSelectionPhrases(context.ExpectedText).Take(64))
            {
                ITextRange? found = textPattern.DocumentRange.FindText(candidate, backward: false, ignoreCase: false);
                if (found is null)
                    continue;
                string? enclosing = found.GetEnclosingElement()?.Properties.AutomationId.ValueOrDefault;
                if (ReadmeCorrectnessOracles.IsAtomicSelectionObject(enclosing))
                {
                    atomicCandidatesSkipped++;
                    continue;
                }
                phrase = candidate;
                range = found;
                enclosingAutomationId = enclosing;
                break;
            }
            if (phrase is null || range is null)
                throw new InvalidOperationException(
                    $"No deterministic ordinary-text selection phrase could be located ({atomicCandidatesSkipped} atomic-object candidates skipped).");
            range.Select();
            string selected = ReadSelectionText(host);
            if (!string.Equals(selected, phrase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "TextPattern.GetSelection did not return the explicitly selected UIA range " +
                    $"(phrase {phrase.Length} chars '{Truncate(phrase, 64)}', selected {selected.Length} chars '{Truncate(selected, 96)}', " +
                    $"enclosing '{enclosingAutomationId ?? "<none>"}', {atomicCandidatesSkipped} atomic candidates skipped).");
            }

            selectedPhrase = phrase;
            selectedSha256 = HashSha256(Encoding.UTF8.GetBytes(selected));
            checks.Add(CorrectnessCheck.Passed(
                "selection",
                $"UIA TextPattern selected the complete {selectedDocument.Length}-character document across blocks exactly (SHA-256 {fullDocumentSelectionSha256}), then selected and returned the same bounded {selected.Length}-character range exactly (SHA-256 {selectedSha256})."));
        }
        catch (Exception exception)
        {
            checks.Add(CorrectnessCheck.Failed("selection", RedactUrls(exception.Message)));
        }

        bool clipboardIsolated = string.Equals(
            Environment.GetEnvironmentVariable(CorrectnessClipboardIsolationMarker),
            "1",
            StringComparison.Ordinal);
        if (!clipboardIsolated)
        {
            checks.Add(CorrectnessCheck.NotEvaluated(
                "plainTextCopy",
                "Clipboard test was not attempted because the selected-target runner did not attest disabled host clipboard redirection."));
        }
        else if (selectedPhrase is null)
        {
            checks.Add(CorrectnessCheck.NotEvaluated(
                "plainTextCopy",
                "Clipboard test was not attempted because selection did not pass."));
        }
        else
        {
            try
            {
                string sentinel = $"__jithub_correctness_clipboard_pending_{Guid.NewGuid():N}__";
                NativeMethods.SetClipboardText(sentinel);
                context.Host.FocusNative();
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
                string copied = WaitForCorrectnessValue(
                    "selected Markdown clipboard text",
                    () => NativeMethods.GetClipboardText(),
                    value => string.Equals(value, selectedPhrase, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(4));
                copiedSha256 = HashSha256(Encoding.UTF8.GetBytes(copied));
                checks.Add(CorrectnessCheck.Passed(
                    "plainTextCopy",
                    $"Ctrl+C copied the selected {copied.Length}-character text range (SHA-256 {copiedSha256}) in an attested isolated clipboard."));
            }
            catch (Exception exception)
            {
                checks.Add(CorrectnessCheck.Failed("plainTextCopy", RedactUrls(exception.Message)));
            }
        }

        try
        {
            DateTime sourceEvidenceBeforeNavigationUtc = File.GetLastWriteTimeUtc(context.ReadmeSourceEvidencePath);
            if (sourceEvidenceBeforeNavigationUtc == DateTime.MinValue || context.ProcessId <= 0 ||
                sourceEvidenceBeforeNavigationUtc <= context.SourceEvidenceStartedAtUtc.UtcDateTime)
                throw new InvalidDataException("The current app process/source identity evidence was unavailable before navigation.");

            AutomationElement issues = context.Window.FindFirstDescendant(
                    cf => cf.ByAutomationId("RepoNavigation_Issues"))
                ?? throw new InvalidOperationException("The repository Issues navigation item was not present.");
            InvokeNavigationItem(issues);
            WaitForCorrectnessCondition(
                "navigation away from README",
                () => IsNavigationItemSelected(issues) &&
                    IsReadmeHostAbsentOrOffscreen(context.Window),
                TimeSpan.FromSeconds(15));

            AutomationElement code = context.Window.FindFirstDescendant(
                    cf => cf.ByAutomationId("RepoNavigation_Code"))
                ?? throw new InvalidOperationException("The repository Code navigation item was not present after leaving the README.");
            InvokeNavigationItem(code);
            WaitForCorrectnessCondition(
                "navigation back to repository Code",
                () => IsNavigationItemSelected(code),
                TimeSpan.FromSeconds(15));
            TryOpenReadme(
                context.Window,
                repository.Readme.Path,
                TimeSpan.FromSeconds(30),
                allowExistingHost: false);
            AutomationElement reopenedHost = WaitForHost(
                context.Window,
                TimeSpan.FromSeconds(30),
                Path.Combine(context.NativeOutputDirectory, "correctness-navigation-timeout.png"));
            string reopenedText = WaitForStableCorrectnessText(reopenedHost, TimeSpan.FromSeconds(20));
            if (!string.Equals(reopenedText, context.ExpectedText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Returning to the pinned README did not restore the same visible document text.");
            }

            DateTime reopenedEvidenceWriteUtc = File.GetLastWriteTimeUtc(context.ReadmeSourceEvidencePath);
            if (reopenedEvidenceWriteUtc <= sourceEvidenceBeforeNavigationUtc)
                throw new InvalidDataException("Source identity evidence was not refreshed after route-away/reopen in the current app process.");

            NativeSameByteReadmeSourceIdentity identity = ReadSameByteSourceEvidence(
                context.ReadmeSourceEvidencePath,
                context.NativeOutputDirectory,
                repository.Readme.Sha,
                expectedRawSha256,
                expectedRawBytes);
            if (!string.Equals(identity.RawSha256, expectedRawSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(identity.ReadmeGitBlobSha1, repository.Readme.Sha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Returning to Code did not restore the exact pinned README source identity.");
            }

            navigationDetail = $"App process {context.ProcessId} selected the real Issues section, observed the README host leave, returned to Code, reopened the exact frozen README, verified exact UIA text and source identity, and observed refreshed source evidence at {reopenedEvidenceWriteUtc:O}.";
            checks.Add(CorrectnessCheck.Passed("routeAwayAndBack", navigationDetail));
        }
        catch (Exception exception)
        {
            checks.Add(CorrectnessCheck.Failed("routeAwayAndBack", RedactUrls(exception.Message)));
        }

        return new ReadmeCorrectnessInteractionResult
        {
            Checks = checks,
            FullDocumentSelectionSha256 = fullDocumentSelectionSha256,
            SelectedPhraseSha256 = selectedSha256,
            CopiedPhraseSha256 = copiedSha256,
            NavigationEvidence = navigationDetail,
        };
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static IEnumerable<string> EnumerateBoundedSelectionPhrases(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}_-]{2,63}", RegexOptions.CultureInvariant))
        {
            string candidate = match.Value;
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }

    private static string WaitForStableCorrectnessText(AutomationElement host, TimeSpan timeout)
    {
        var textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("README host does not expose UIA TextPattern for exact text verification.");
        Stopwatch stopwatch = Stopwatch.StartNew();
        string? previous = null;
        int consecutiveMatches = 0;
        while (stopwatch.Elapsed < timeout)
        {
            string current = ReadDocumentTextInBoundedChunks(textPattern.DocumentRange, requireComplete: true);
            if (previous is not null && string.Equals(current, previous, StringComparison.Ordinal))
            {
                if (++consecutiveMatches >= 2)
                    return current;
            }
            else
            {
                previous = current;
                consecutiveMatches = 0;
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException("README UIA text did not produce three identical complete raw-text snapshots.");
    }

    private static string ReadSelectionText(AutomationElement host)
    {
        var textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("Markdown host no longer exposes TextPattern.");
        return string.Concat(textPattern.GetSelection().Select(range => range.GetText(-1)));
    }

    private static void InvokeNavigationItem(AutomationElement item)
    {
        if (item.Patterns.SelectionItem.IsSupported)
            item.Patterns.SelectionItem.Pattern.Select();
        else if (item.Patterns.Invoke.IsSupported)
            item.Patterns.Invoke.Pattern.Invoke();
        else
            throw new InvalidOperationException($"Navigation item '{item.AutomationId}' exposes no selection or invoke pattern.");
    }

    private static bool IsNavigationItemSelected(AutomationElement item) =>
        item.Patterns.SelectionItem.IsSupported && item.Patterns.SelectionItem.Pattern.IsSelected.Value;

    private static bool IsReadmeHostAbsentOrOffscreen(Window window)
    {
        AutomationElement? host = window.FindFirstDescendant(cf => cf.ByAutomationId(HostAutomationId));
        return host is null || host.Properties.IsOffscreen.ValueOrDefault;
    }

    private static void WaitForCorrectnessCondition(string description, Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (condition())
                return;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for {description} after {timeout.TotalSeconds:F0}s.");
    }

    private static T WaitForCorrectnessValue<T>(
        string description,
        Func<T> read,
        Func<T, bool> predicate,
        TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        T last = default!;
        while (timer.Elapsed < timeout)
        {
            last = read();
            if (predicate(last))
                return last;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for {description} after {timeout.TotalSeconds:F0}s.");
    }

    private static ReadmeAuditRepository[] GetSelectedCorrectnessRepositories(
        ReadmeAuditManifest manifest,
        CaptureOptions options)
    {
        ValidateCorrectnessRankRange(options);
        ReadmeAuditRepository[] selected = manifest.Repositories
            .Where(repository => repository.Rank >= options.AuditStartRank &&
                repository.Rank < options.AuditStartRank + options.AuditCount)
            .OrderBy(repository => repository.Rank)
            .ToArray();
        if (selected.Length != options.AuditCount ||
            selected.Where((repository, index) => repository.Rank != options.AuditStartRank + index).Any())
        {
            throw new InvalidDataException("Frozen manifest does not contain every requested correctness rank exactly once.");
        }
        return selected;
    }

    private static void ValidateCorrectnessRankRange(CaptureOptions options)
    {
        if (options.AuditStartRank <= 0 || options.AuditCount <= 0 ||
            options.AuditStartRank > 500 || options.AuditCount > 500 ||
            checked(options.AuditStartRank + options.AuditCount - 1) > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Correctness rank range must stay within 1..500.");
        }
    }

    private static string GetCorrectnessInputStateName(CorpusInputState state) => state switch
    {
        CorpusInputState.Passed => "passed",
        CorpusInputState.Failed => "failed",
        CorpusInputState.AuditInputMissing => "audit-input-missing",
        CorpusInputState.NotApplicable => "not-applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static SourceAcquisitionIndex ReadSourceAcquisitionIndex(
        string sourceRoot,
        string frozenManifestPath,
        string pinnedManifestSha256,
        ReadmeAuditManifest manifest)
    {
        string metadataPath = Path.Combine(sourceRoot, "source-acquisition.json");
        byte[] metadataBytes = ReadBoundedFile(metadataPath, sourceRoot, SourceAcquisitionMaximumBytes);
        using JsonDocument document = JsonDocument.Parse(metadataBytes);
        JsonElement root = document.RootElement;
        DateTimeOffset createdAtUtc = root.GetProperty("createdAtUtc").GetDateTimeOffset();
        string corpusManifestPath = root.GetProperty("corpusManifestPath").GetString() ?? string.Empty;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            !string.Equals(root.GetProperty("objectAddressing").GetString(), "git-blob-sha1", StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("corpusManifestSha256").GetString(), pinnedManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("requestedCount").GetInt32() != 500 || createdAtUtc == default ||
            createdAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            !IsNormalizedRelativeSourceManifestPath(corpusManifestPath))
        {
            throw new InvalidDataException("Source-acquisition metadata is not bound to the frozen 500-entry manifest.");
        }

        IReadOnlyDictionary<int, long> frozenRepositoryIds = ReadFrozenRepositoryIds(frozenManifestPath, pinnedManifestSha256);

        JsonElement recordsElement = root.GetProperty("records");
        if (recordsElement.ValueKind != JsonValueKind.Array || recordsElement.GetArrayLength() != 500 ||
            manifest.Repositories.Count != 500)
        {
            throw new InvalidDataException("Source acquisition does not contain exactly 500 ordered README records.");
        }

        var records = new Dictionary<int, SourceAcquisitionRecord>(500);
        int expectedRank = 1;
        foreach (JsonElement item in recordsElement.EnumerateArray())
        {
            int rank = item.GetProperty("rank").GetInt32();
            long repositoryId = item.GetProperty("repositoryId").GetInt64();
            string fullName = item.GetProperty("fullName").GetString() ?? string.Empty;
            string commitSha = item.GetProperty("commitSha").GetString() ?? string.Empty;
            string defaultBranch = item.GetProperty("defaultBranch").GetString() ?? string.Empty;
            string readmePath = item.GetProperty("readmePath").GetString() ?? string.Empty;
            long size = item.GetProperty("expectedByteSize").GetInt64();
            string gitBlobSha1 = item.GetProperty("expectedGitBlobSha1").GetString() ?? string.Empty;
            string objectPath = item.GetProperty("objectPath").GetString() ?? string.Empty;
            string htmlUrl = item.GetProperty("htmlUrl").GetString() ?? string.Empty;
            string downloadUrl = item.GetProperty("downloadUrl").GetString() ?? string.Empty;
            ReadmeAuditRepository repository = manifest.Repositories.SingleOrDefault(item => item.Rank == expectedRank)
                ?? throw new InvalidDataException($"Frozen manifest is missing source-acquisition rank {expectedRank}.");
            string expectedObjectPath = $"objects/git-blob-sha1/{gitBlobSha1[..Math.Min(2, gitBlobSha1.Length)]}/{gitBlobSha1}.md";
            if (rank != expectedRank || repository.Rank != rank ||
                !frozenRepositoryIds.TryGetValue(rank, out long expectedRepositoryId) || repositoryId != expectedRepositoryId ||
                !string.Equals(repository.FullName, fullName, StringComparison.Ordinal) ||
                !string.Equals(repository.CommitSha, commitSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(repository.DefaultBranch, defaultBranch, StringComparison.Ordinal) ||
                !repository.Readme.Available ||
                !string.Equals(repository.Readme.Path, readmePath, StringComparison.Ordinal) ||
                repository.Readme.ByteSize != size ||
                !string.Equals(repository.Readme.Sha, gitBlobSha1, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(repository.Readme.HtmlUrl, htmlUrl, StringComparison.Ordinal) ||
                !string.Equals(repository.Readme.DownloadUrl, downloadUrl, StringComparison.Ordinal) ||
                size is <= 0 or > MaximumCorrectnessReadmeBytes ||
                !IsGitSha1(gitBlobSha1) ||
                !string.Equals(objectPath, expectedObjectPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Source-acquisition rank {expectedRank} does not exactly match the frozen manifest.");
            }

            string fullObjectPath = ResolveOrdinaryObjectPath(sourceRoot, objectPath);
            if (!records.TryAdd(rank, new SourceAcquisitionRecord(rank, gitBlobSha1, size, objectPath, fullObjectPath)))
                throw new InvalidDataException($"Source-acquisition rank {rank} is duplicated.");
            expectedRank++;
        }

        return new SourceAcquisitionIndex(HashSha256(metadataBytes), records);
    }

    private static byte[] ReadVerifiedSourceBody(
        string sourceRoot,
        ReadmeAuditRepository repository,
        SourceAcquisitionRecord record)
    {
        byte[] bytes = ReadBoundedFile(record.FullPath, sourceRoot, MaximumCorrectnessReadmeBytes, record.ByteSize);
        if (bytes.LongLength != repository.Readme.ByteSize ||
            !string.Equals(HashGitBlobSha1(bytes), repository.Readme.Sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Frozen README body failed its Git blob identity at rank {repository.Rank}.");
        }
        return bytes;
    }

    private static IReadOnlyList<CorpusManifestCandidate> ScanCorrectnessCorpusManifests(string corpusRoot)
    {
        var candidates = new List<CorpusManifestCandidate>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(corpusRoot));
        int visitedEntries = 0;
        long scannedManifestBytes = 0;

        while (pending.Count != 0)
        {
            DirectoryInfo directory = pending.Pop();
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Correctness corpus contains a missing or redirected directory.");

            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                if (++visitedEntries > MaximumCorrectnessCorpusEntries)
                    throw new InvalidDataException("Correctness corpus exceeds the bounded entry limit.");
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Correctness corpus contains a redirected filesystem entry.");

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                    continue;
                }

                if (!string.Equals(entry.Name, "manifest.json", StringComparison.Ordinal))
                    continue;
                if (candidates.Count >= MaximumCorrectnessCorpusManifests)
                    throw new InvalidDataException("Correctness corpus exceeds the bounded manifest count.");

                string manifestPath = Path.GetFullPath(entry.FullName);
                string caseFolder = GetCaseFolderFromCorpusManifestPath(manifestPath);
                FileInfo manifestInfo = (FileInfo)entry;
                if (manifestInfo.Length is <= 0 or > OfflineManifestMaximumBytes ||
                    checked(scannedManifestBytes + manifestInfo.Length) > MaximumScannedCorrectnessManifestBytes)
                {
                    throw new InvalidDataException("Correctness corpus exceeds the bounded aggregate manifest-byte limit.");
                }
                scannedManifestBytes = checked(scannedManifestBytes + manifestInfo.Length);

                try
                {
                    byte[] bytes = ReadBoundedFile(manifestPath, corpusRoot, OfflineManifestMaximumBytes, manifestInfo.Length);
                    using JsonDocument document = JsonDocument.Parse(bytes);
                    JsonElement root = document.RootElement;
                    JsonElement repository = root.GetProperty("repository");
                    candidates.Add(new CorpusManifestCandidate(
                        manifestPath,
                        caseFolder,
                        HashSha256(bytes),
                        repository.GetProperty("fullName").GetString(),
                        repository.GetProperty("commitSha").GetString(),
                        repository.GetProperty("readmePath").GetString(),
                        repository.GetProperty("readmeGitBlobSha1").GetString(),
                        null));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {
                    // Keep malformed candidates associated with their case directory so a
                    // damaged capture is a failed input, not silently called missing.
                    candidates.Add(new CorpusManifestCandidate(
                        manifestPath, caseFolder, null, null, null, null, null, RedactUrls(exception.Message)));
                }
            }
        }

        return candidates;
    }

    private static string GetCaseFolderFromCorpusManifestPath(string manifestPath)
    {
        DirectoryInfo? corpusDirectory = new(Path.GetDirectoryName(manifestPath)!);
        // A schema-3 correctness input is the rank folder itself; schema-2
        // captures nest the corpus two levels below their case folder.
        if (IsRankFolderName(corpusDirectory.Name))
            return corpusDirectory.Name;
        DirectoryInfo? browserDirectory = corpusDirectory.Parent;
        DirectoryInfo? caseDirectory = browserDirectory?.Parent;
        return caseDirectory?.Name ?? string.Empty;
    }

    private const int CorrectnessInputSchemaVersion = 3;
    private const long MaximumExpectedContentBytes = 64L * 1024 * 1024;
    private const string CorrectnessInputKind = "correctness-input-v1";

    /// <summary>
    /// A schema-3 input must name this frozen rank and corpus manifest, must
    /// not carry browser evidence, and must record its image demand.
    /// </summary>
    private static bool IsCorrectnessInputBinding(JsonElement root, JsonElement pinned, ReadmeAuditRepository repository) =>
        !root.TryGetProperty("browserRender", out _) &&
        !root.TryGetProperty("nonRenderedDecorativeImageExclusions", out _) &&
        root.TryGetProperty("imageDemand", out JsonElement demand) && demand.ValueKind == JsonValueKind.Array &&
        pinned.TryGetProperty("rank", out JsonElement rank) && rank.TryGetInt32(out int rankValue) && rankValue == repository.Rank &&
        pinned.TryGetProperty("corpusManifestSha256", out JsonElement corpus) &&
        string.Equals(corpus.GetString(), FrozenCorrectnessManifestSha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsRankFolderName(string name) =>
        name.Length > 4 && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) &&
        char.IsAsciiDigit(name[2]) && name[3] == '-';

    private static string GetOptionalOrdinaryDirectory(string path, string description, out bool exists)
    {
        string fullPath = Path.GetFullPath(path);
        DirectoryInfo directory = new(fullPath);
        directory.Refresh();
        exists = directory.Exists;
        if (!exists)
        {
            FileInfo file = new(fullPath);
            file.Refresh();
            if (file.Exists)
                throw new InvalidDataException($"The {description} is not an ordinary directory.");
            try
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The {description} is redirected.");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            return fullPath;
        }

        return RequireOrdinaryDirectory(fullPath, description);
    }

    private static CorpusInputVerification FindVerifiedCorrectnessCorpus(
        IReadOnlyList<CorpusManifestCandidate> candidates,
        string corpusRoot,
        ReadmeAuditRepository repository,
        byte[] rawReadme,
        string readmeSha256)
    {
        string caseId = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
        CorpusManifestCandidate[] matching = candidates
            .Where(candidate => string.Equals(candidate.CaseFolder, caseId, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.ManifestPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matching.Length == 0)
            return CorpusInputVerification.Missing($"No schema-2 capture or schema-3 correctness input exists for frozen rank {repository.Rank} ({repository.FullName}).");

        CorpusManifestCandidate[] identityMatches = matching.Where(candidate => candidate.ParseFailure is null &&
            string.Equals(candidate.FullName, repository.FullName, StringComparison.Ordinal) &&
            string.Equals(candidate.CommitSha, repository.CommitSha, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ReadmePath, repository.Readme.Path, StringComparison.Ordinal) &&
            string.Equals(candidate.ReadmeGitBlobSha1, repository.Readme.Sha, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (identityMatches.Length == 0)
        {
            if (matching.All(candidate => candidate.ParseFailure is not null))
                return CorpusInputVerification.Failed("Captured corpus manifests for this rank are malformed and their pinned source identity cannot be read.");
            return CorpusInputVerification.Missing($"Captured corpora exist for rank {repository.Rank}, but none match the frozen repository/commit/README identity.");
        }

        if (identityMatches.Select(candidate => candidate.ManifestSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            return CorpusInputVerification.Failed("Multiple different corpus manifests claim the same frozen rank; input selection is ambiguous.");

        CorpusManifestCandidate candidate = identityMatches[0];

        try
        {
            CorpusInputVerification verified = VerifyCorrectnessCorpusCandidate(
                candidate, corpusRoot, repository, rawReadme, readmeSha256);
            return identityMatches.Length == 1
                ? verified
                : verified with { Detail = $"Verified one of {identityMatches.Length} identical captured manifests; {verified.Detail}" };
        }
        catch (Exception exception)
        {
            return CorpusInputVerification.Failed(RedactUrls(exception.Message));
        }
    }

    private static CorpusInputVerification VerifyCorrectnessCorpusCandidate(
        CorpusManifestCandidate candidate,
        string corpusRoot,
        ReadmeAuditRepository repository,
        byte[] rawReadme,
        string readmeSha256)
    {
        string manifestDirectory = Path.GetDirectoryName(candidate.ManifestPath)
            ?? throw new InvalidDataException("Captured corpus manifest has no containing directory.");
        byte[] manifestBytes = ReadBoundedFile(candidate.ManifestPath, corpusRoot, OfflineManifestMaximumBytes);
        string manifestSha256 = HashSha256(manifestBytes);
        if (!string.Equals(manifestSha256, candidate.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Captured corpus manifest changed during preflight.");

        using JsonDocument document = JsonDocument.Parse(manifestBytes);
        JsonElement root = document.RootElement;
        JsonElement pinned = root.GetProperty("repository");
        JsonElement readme = root.GetProperty("readme");
        JsonElement assets = root.GetProperty("assets");
        int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        bool correctnessInput = schemaVersion == CorrectnessInputSchemaVersion &&
            root.TryGetProperty("kind", out JsonElement kind) &&
            string.Equals(kind.GetString(), CorrectnessInputKind, StringComparison.Ordinal);
        if (correctnessInput && !IsCorrectnessInputBinding(root, pinned, repository))
            throw new InvalidDataException("Correctness input is not bound to this frozen rank and manifest, carries browser evidence, or lacks its image demand record.");
        if (!(schemaVersion == 2 || correctnessInput) || !root.GetProperty("complete").GetBoolean() ||
            !string.Equals(pinned.GetProperty("fullName").GetString(), repository.FullName, StringComparison.Ordinal) ||
            !string.Equals(pinned.GetProperty("commitSha").GetString(), repository.CommitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pinned.GetProperty("readmePath").GetString(), repository.Readme.Path, StringComparison.Ordinal) ||
            !string.Equals(pinned.GetProperty("readmeGitBlobSha1").GetString(), repository.Readme.Sha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(readme.GetProperty("file").GetString(), "readme.md", StringComparison.Ordinal) ||
            readme.GetProperty("byteSize").GetInt64() != rawReadme.LongLength ||
            !string.Equals(readme.GetProperty("sha256").GetString(), readmeSha256, StringComparison.OrdinalIgnoreCase) ||
            assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 100_000)
        {
            throw new InvalidDataException("Corpus input does not match the pinned README bytes and repository identity.");
        }

        string readmeFile = Path.Combine(manifestDirectory, "readme.md");
        byte[] corpusReadme = ReadBoundedFile(readmeFile, corpusRoot, MaximumCorrectnessReadmeBytes, rawReadme.LongLength);
        if (!corpusReadme.AsSpan().SequenceEqual(rawReadme))
            throw new InvalidDataException("Captured corpus README is not byte-identical to the pinned Git blob.");

        string? renderStatus = correctnessInput
            ? null
            : root.GetProperty("browserRender").GetProperty("status").GetString();
        string? expectedContentPath = null;
        if (correctnessInput)
        {
            // Browser-free input: README and asset integrity are verified below;
            // the expected-content inventory is bound by size and SHA-256.
            JsonElement inventory = root.GetProperty("inventory");
            string inventoryFile = inventory.GetProperty("file").GetString() ?? string.Empty;
            long inventoryBytes = inventory.GetProperty("byteSize").GetInt64();
            string inventorySha256 = inventory.GetProperty("sha256").GetString() ?? string.Empty;
            if (!string.Equals(inventoryFile, "inventory.json", StringComparison.Ordinal) ||
                inventoryBytes is <= 0 or > MaximumExpectedContentBytes || !IsSha256(inventorySha256))
                throw new InvalidDataException("Correctness input expected-content metadata is malformed.");
            expectedContentPath = Path.Combine(manifestDirectory, inventoryFile);
            string actualInventorySha = HashBoundedFile(expectedContentPath, corpusRoot, MaximumExpectedContentBytes, inventoryBytes);
            if (!string.Equals(actualInventorySha, inventorySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Correctness input expected-content inventory failed its recorded size or SHA-256.");
        }
        else if (renderStatus == "passed")
        {
            JsonElement browserRender = root.GetProperty("browserRender");
            string renderedFile = browserRender.GetProperty("file").GetString() ?? string.Empty;
            long renderedBytes = browserRender.GetProperty("byteSize").GetInt64();
            string renderedSha256 = browserRender.GetProperty("sha256").GetString() ?? string.Empty;
            if (!string.Equals(renderedFile, "rendered.html", StringComparison.Ordinal) ||
                renderedBytes is <= 0 or > 33_554_432 || !IsSha256(renderedSha256))
                throw new InvalidDataException("Captured schema-2 rendered HTML metadata is malformed.");
            string renderedPath = Path.Combine(manifestDirectory, renderedFile);
            string actualRenderedSha = HashBoundedFile(renderedPath, corpusRoot, 33_554_432, renderedBytes);
            if (!string.Equals(actualRenderedSha, renderedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Captured rendered HTML failed its recorded size or SHA-256.");
        }
        else if (renderStatus == "not-applicable")
            throw new InvalidDataException("A schema-2 not-applicable browserRender needs independent original-capture provenance, which this correctness preflight does not accept from the manifest alone.");
        else
        {
            throw new InvalidDataException("Captured schema-2 corpus has an unrecognized rendered-source status.");
        }

        long verifiedAssetBytes = 0;
        var assetsByHash = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var urls = new Dictionary<string, (string Sha256, long ByteSize, string MimeType)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string urlSha256 = asset.GetProperty("urlSha256").GetString() ?? string.Empty;
            string assetSha256 = asset.GetProperty("sha256").GetString() ?? string.Empty;
            long byteSize = asset.GetProperty("byteSize").GetInt64();
            string mimeType = asset.GetProperty("mimeType").GetString() ?? string.Empty;
            if (!IsSha256(urlSha256) || !IsSha256(assetSha256) || byteSize is <= 0 or > OfflineCorpusMaximumFileBytes ||
                mimeType.Length is < 3 or > 128 || mimeType.Any(char.IsControl))
                throw new InvalidDataException("Captured corpus contains malformed image-resource metadata.");

            var mapped = (assetSha256, byteSize, mimeType);
            if (urls.TryGetValue(urlSha256, out var priorUrl) && priorUrl != mapped)
                throw new InvalidDataException("Captured corpus maps one resource URL to conflicting bytes.");
            urls[urlSha256] = mapped;
            if (assetsByHash.TryGetValue(assetSha256, out long priorSize))
            {
                if (priorSize != byteSize)
                    throw new InvalidDataException("Captured corpus assigns conflicting sizes to identical resource hashes.");
                continue;
            }

            string assetPath = Path.Combine(manifestDirectory, "assets", assetSha256);
            string actualSha = HashBoundedFile(assetPath, corpusRoot, OfflineCorpusMaximumFileBytes, byteSize);
            if (!string.Equals(actualSha, assetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Captured resource {assetSha256} failed its recorded SHA-256.");
            assetsByHash.Add(assetSha256, byteSize);
            verifiedAssetBytes = checked(verifiedAssetBytes + byteSize);
            if (verifiedAssetBytes > (correctnessInput ? 1024L : 256L) * 1024 * 1024)
                throw new InvalidDataException("Captured corpus exceeds the aggregate unique-asset byte limit.");
        }

        return CorpusInputVerification.Passed(
            manifestDirectory,
            manifestSha256,
            assetsByHash.Count,
            verifiedAssetBytes,
            correctnessInput,
            expectedContentPath,
            correctnessInput
                ? $"Schema-3 correctness-input integrity: README and {assetsByHash.Count} unique assets passed identity, size, and SHA-256 verification; no browser capture is claimed."
                : $"Schema-2 content integrity (not capture authenticity): README, rendered-source payload, and {assetsByHash.Count} unique assets passed identity, size, and SHA-256 verification.");
    }

    private static byte[] ReadBoundedFile(string path, string root, long maximumBytes, long? expectedLength = null)
    {
        EnsureOrdinaryFileWithinRoot(path, root, maximumBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length < 0 || length > maximumBytes || expectedLength is long expected && length != expected || length > int.MaxValue)
            throw new InvalidDataException("Input file length is outside its recorded or configured bound.");
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new InvalidDataException("Input file changed while its bounded bytes were being read.");
        return bytes;
    }

    private static string HashBoundedFile(string path, string root, long maximumBytes, long expectedLength)
    {
        EnsureOrdinaryFileWithinRoot(path, root, maximumBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length != expectedLength || length > maximumBytes)
            throw new InvalidDataException("Payload length does not match its captured integrity metadata.");

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new InvalidDataException("Payload ended before its recorded length while being hashed.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (stream.Position != length || stream.ReadByte() != -1 || stream.Length != length)
            throw new InvalidDataException("Payload changed while its bounded bytes were being hashed.");
        return actualHash;
    }

    private static IReadOnlyDictionary<int, long> ReadFrozenRepositoryIds(string manifestPath, string expectedSha256)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidDataException("Frozen manifest has no containing directory.");
        byte[] bytes = ReadBoundedFile(manifestPath, root, OfflineManifestMaximumBytes);
        if (!string.Equals(HashSha256(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen top-500 manifest changed during source-acquisition validation.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement repositories = document.RootElement.GetProperty("repositories");
        if (repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() != 500)
            throw new InvalidDataException("Frozen manifest repository-id index must contain exactly 500 entries.");
        var ids = new Dictionary<int, long>(500);
        var uniqueIds = new HashSet<long>();
        foreach (JsonElement repository in repositories.EnumerateArray())
        {
            int rank = repository.GetProperty("rank").GetInt32();
            long id = repository.GetProperty("id").GetInt64();
            if (rank is < 1 or > 500 || id <= 0 || !ids.TryAdd(rank, id) || !uniqueIds.Add(id))
                throw new InvalidDataException("Frozen top-500 manifest contains an invalid or duplicate repository ID.");
        }
        return ids;
    }

    private static bool IsNormalizedRelativeSourceManifestPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static string ResolveOrdinaryObjectPath(string sourceRoot, string objectPath)
    {
        if (Path.IsPathRooted(objectPath) || objectPath.Contains('\\') ||
            objectPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Source-acquisition object path is not a normalized relative path.");
        }
        string candidate = Path.GetFullPath(Path.Combine(sourceRoot, objectPath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(sourceRoot, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Source-acquisition object path escaped its pinned root.");
        }
        return candidate;
    }

    private static void EnsureOrdinaryFileWithinRoot(string path, string root, long maximumBytes)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Source-acquisition file escaped its pinned root.");
        }

        DirectoryInfo? directory = new(Path.GetDirectoryName(fullPath)!);
        while (directory is not null && directory.FullName.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source-acquisition path contains a missing or redirected directory.");
            if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), fullRoot, StringComparison.OrdinalIgnoreCase))
                break;
            directory = directory.Parent;
        }

        FileInfo info = new(fullPath);
        info.Refresh();
        if (!info.Exists || info.Length is < 0 || info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Source-acquisition file is missing, redirected, or oversized.");
        }
    }

    private static string HashGitBlobSha1(ReadOnlySpan<byte> bytes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length.ToString(CultureInfo.InvariantCulture)}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static CorrectnessAccountingSelfTestResult EvaluateCorrectnessAccountingSelfTests()
    {
        var allPassed = CorrectnessRequiredChecks
            .Select(name => CorrectnessCheck.Passed(name, "self-test"))
            .ToArray();
        bool allPassedCase = EvaluateCorrectnessCase(allPassed) == "passed";
        bool notAttemptedIncomplete = EvaluateCorrectnessCase(allPassed
            .Where(check => check.Name != "contentCompleteness")
            .Append(CorrectnessCheck.NotEvaluated("contentCompleteness", "self-test"))) == "incomplete";
        bool unsupportedIncomplete = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "contentCompleteness"
                ? CorrectnessCheck.Unsupported(check.Name, "self-test")
                : check)) == "incomplete";
        bool upstreamResourceClassified = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "resourceCoverage"
                ? CorrectnessCheck.UpstreamUnavailable(check.Name, "self-test")
                : check)) == "classified";
        bool unsupportedSyntaxClassified = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "unsupportedSyntaxClassification"
                ? CorrectnessCheck.Unsupported(check.Name, "self-test")
                : check)) == "classified";
        bool upstreamOnContentIncomplete = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "contentCompleteness"
                ? CorrectnessCheck.UpstreamUnavailable(check.Name, "self-test")
                : check)) == "incomplete";
        bool auditInputMissingIncomplete = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "verifiedCorpusInput"
                ? CorrectnessCheck.AuditInputMissing(check.Name, "self-test")
                : check)) == "incomplete";
        bool auditInputMissingIsNotUpstream = CorrectnessCheck.AuditInputMissing("verifiedCorpusInput", "self-test").State !=
            CorrectnessCheckState.UpstreamUnavailable;
        bool rendererUnavailableRequiresActualAttempt = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "renderLifecycle"
                ? CorrectnessCheck.UpstreamUnavailable(check.Name, "self-test")
                : check)) == "incomplete";
        bool failureFails = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "selection"
                ? CorrectnessCheck.Failed(check.Name, "self-test")
                : check)) == "failed";
        bool unattemptedCannotPass = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "selection"
                ? check with { Attempted = false, Evaluated = false }
                : check)) == "incomplete";
        bool requiredNotApplicableCannotPass = EvaluateCorrectnessCase(allPassed
            .Select(check => check.Name == "plainTextCopy"
                ? CorrectnessCheck.NotApplicable(check.Name, "self-test")
                : check)) == "incomplete";
        const string exactWhitespaceSample = "alpha \t  beta\r\n\r\n  gamma";
        var exactWhitespaceBuilder = new StringBuilder();
        AppendExactDocumentTextChunk(exactWhitespaceBuilder, "alpha \t ", 128);
        AppendExactDocumentTextChunk(exactWhitespaceBuilder, " beta\r\n", 128);
        AppendExactDocumentTextChunk(exactWhitespaceBuilder, "\r\n  gamma", 128);
        string exactWhitespaceSnapshot = CompleteExactDocumentText(exactWhitespaceBuilder, 128, hasRemainingText: false);
        bool exactWhitespacePreserved = string.Equals(exactWhitespaceSnapshot, exactWhitespaceSample, StringComparison.Ordinal) &&
            !string.Equals(NormalizeText(exactWhitespaceSnapshot), exactWhitespaceSample, StringComparison.Ordinal);
        var boundedBuilder = new StringBuilder();
        AppendExactDocumentTextChunk(boundedBuilder, "abc", 5);
        bool oversizedSnapshotRejected = ThrowsInvalidData(
            () => AppendExactDocumentTextChunk(boundedBuilder, "def", 5)) &&
            string.Equals(boundedBuilder.ToString(), "abc", StringComparison.Ordinal);
        bool incompleteSnapshotRejected = ThrowsInvalidData(
            () => CompleteExactDocumentText(boundedBuilder, 3, hasRemainingText: true));
        bool observedUnavailableImageFailsCoverage = EvaluateObservedResourceCoverage(1, 0).State == CorrectnessCheckState.Failed;
        bool observedLoadingImageFailsCoverage = EvaluateObservedResourceCoverage(0, 1).State == CorrectnessCheckState.Failed;
        bool cleanResourcesWithoutDemandOracleRemainNotEvaluated = EvaluateObservedResourceCoverage(0, 0).State == CorrectnessCheckState.NotEvaluated;

        // Schema-3 oracles.
        var span = new ReadmeCorrectnessOracles.Span(0, 10);
        var hidden = new ReadmeCorrectnessOracles.Span(100, 50);
        var expectedContent = new ReadmeCorrectnessOracles.ExpectedContent(
            [new("heading", span, "Title"), new("paragraph", new(20, 10), "Hello, world"), new("paragraph", new(110, 5), "secret body")],
            TableCount: 1,
            TaskItems: [new(40, 5)],
            Disclosures: [hidden],
            UnrecognizedInlineTypes: [],
            Diagnostics: []);
        ReadmeCorrectnessOracles.ObservedContent Observed(string text, int tables = 1, int tasks = 1, int disclosures = 1) =>
            new(text, 1, tables, tasks, disclosures, 0, 0);
        bool orderedTextPasses = ReadmeCorrectnessOracles.EvaluateContentCompleteness(
            expectedContent, Observed("Title extra Hello, world Summary")).State == CorrectnessCheckState.Passed;
        bool missingTextFails = ReadmeCorrectnessOracles.EvaluateContentCompleteness(
            expectedContent, Observed("Title Hello,")).State == CorrectnessCheckState.Failed;
        bool reorderedTextFails = ReadmeCorrectnessOracles.EvaluateContentCompleteness(
            expectedContent, Observed("Hello, world Title")).State == CorrectnessCheckState.Failed;
        bool missingTableFails = ReadmeCorrectnessOracles.EvaluateContentCompleteness(
            expectedContent, Observed("Title Hello, world", tables: 0)).State == CorrectnessCheckState.Failed;
        bool unrecognizedInlineIsNotEvaluated = ReadmeCorrectnessOracles.EvaluateContentCompleteness(
            expectedContent with { UnrecognizedInlineTypes = ["Unknown.Inline"] },
            Observed("Title Hello, world")).State == CorrectnessCheckState.NotEvaluated;
        bool diagnosticsClassifyUnsupported = ReadmeCorrectnessOracles.EvaluateUnsupportedSyntax(
            expectedContent with { Diagnostics = [("MATH100", "Error")] }).State == CorrectnessCheckState.Unsupported &&
            ReadmeCorrectnessOracles.EvaluateUnsupportedSyntax(expectedContent).State == CorrectnessCheckState.Passed;
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var cleanDemand = new ReadmeCorrectnessOracles.ImageDemandSummary(3, 1, 0, 0, states);
        var upstreamDemand = new ReadmeCorrectnessOracles.ImageDemandSummary(3, 0, 2, 0, states);
        bool demandOracleClassifiesResources =
            ReadmeCorrectnessOracles.EvaluateResourceCoverage(cleanDemand, 0, 0).State == CorrectnessCheckState.Passed &&
            ReadmeCorrectnessOracles.EvaluateResourceCoverage(cleanDemand, 1, 0).State == CorrectnessCheckState.Failed &&
            ReadmeCorrectnessOracles.EvaluateResourceCoverage(cleanDemand, 0, 1).State == CorrectnessCheckState.Failed &&
            ReadmeCorrectnessOracles.EvaluateResourceCoverage(upstreamDemand, 2, 0).State == CorrectnessCheckState.UpstreamUnavailable &&
            ReadmeCorrectnessOracles.EvaluateResourceCoverage(upstreamDemand, 3, 0).State == CorrectnessCheckState.Failed &&
            ReadmeCorrectnessOracles.EvaluateResourceCoverage(
                new ReadmeCorrectnessOracles.ImageDemandSummary(3, 0, 0, 1, new Dictionary<string, int>(StringComparer.Ordinal) { ["resource-limit"] = 1 }),
                0,
                0).State == CorrectnessCheckState.NotEvaluated;
        var inlineObjectContent = expectedContent with
        {
            TextBlocks = [new("paragraph", new(0, 40), "Search the list at https://example.test/ .")],
            TableCount = 0,
            TaskItems = [],
            Disclosures = [],
        };
        bool punctuationBesideOmittedInlinePasses = ReadmeCorrectnessOracles.EvaluateContentCompleteness(
            inlineObjectContent, Observed("Search the list at https://example.test/ https://example.test/.", 0, 0, 0)).State == CorrectnessCheckState.Passed &&
            ReadmeCorrectnessOracles.EvaluateContentCompleteness(
                inlineObjectContent, Observed("Search the at https://example.test/ https://example.test/.", 0, 0, 0)).State == CorrectnessCheckState.Failed;
        bool atomicSelectionObjectsAreRecognized =
            ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownImage") &&
            ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownLinkedImage-00000001-00000000-00000001-00000061-FFFFFFFF-FFFFFFFF") &&
            ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownMath") &&
            ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownDiagramNode") &&
            ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownHostedElement") &&
            ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownEmbed") &&
            !ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownHeading") &&
            !ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownGroup") &&
            !ReadmeCorrectnessOracles.IsAtomicSelectionObject("MarkdownRenderer") &&
            !ReadmeCorrectnessOracles.IsAtomicSelectionObject(null);
        bool[] tests =
        [
            punctuationBesideOmittedInlinePasses,
            atomicSelectionObjectsAreRecognized,
            allPassedCase,
            notAttemptedIncomplete,
            unsupportedIncomplete,
            upstreamResourceClassified,
            unsupportedSyntaxClassified,
            upstreamOnContentIncomplete,
            auditInputMissingIncomplete,
            auditInputMissingIsNotUpstream,
            rendererUnavailableRequiresActualAttempt,
            failureFails,
            unattemptedCannotPass,
            requiredNotApplicableCannotPass,
            exactWhitespacePreserved,
            oversizedSnapshotRejected,
            incompleteSnapshotRejected,
            observedUnavailableImageFailsCoverage,
            observedLoadingImageFailsCoverage,
            cleanResourcesWithoutDemandOracleRemainNotEvaluated,
            orderedTextPasses,
            missingTextFails,
            reorderedTextFails,
            missingTableFails,
            unrecognizedInlineIsNotEvaluated,
            diagnosticsClassifyUnsupported,
            demandOracleClassifiesResources,
        ];
        return new CorrectnessAccountingSelfTestResult
        {
            Passed = tests.All(value => value),
            PassedCount = tests.Count(value => value),
            TotalCount = tests.Length,
            Cases = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["all-required-evaluated-passed"] = allPassedCase,
                ["not-evaluated-is-incomplete"] = notAttemptedIncomplete,
                ["unsupported-is-incomplete"] = unsupportedIncomplete,
                ["upstream-unavailable-resource-is-classified-not-passed"] = upstreamResourceClassified,
                ["unsupported-syntax-is-classified-only-on-its-own-dimension"] = unsupportedSyntaxClassified,
                ["upstream-unavailable-elsewhere-is-incomplete"] = upstreamOnContentIncomplete,
                ["audit-input-missing-is-incomplete"] = auditInputMissingIncomplete,
                ["audit-input-missing-is-not-upstream-unavailable"] = auditInputMissingIsNotUpstream,
                ["renderer-unavailable-is-not-inferred-from-missing-audit-input"] = rendererUnavailableRequiresActualAttempt,
                ["required-failure-is-failed"] = failureFails,
                ["unattempted-pass-claim-is-incomplete"] = unattemptedCannotPass,
                ["required-not-applicable-is-incomplete"] = requiredNotApplicableCannotPass,
                ["exact-text-preserves-crlf-tabs-and-repeated-spaces"] = exactWhitespacePreserved,
                ["exact-text-bound-rejects-oversized-snapshot-without-truncating"] = oversizedSnapshotRejected,
                ["exact-text-bound-rejects-unconsumed-document-tail"] = incompleteSnapshotRejected,
                ["observed-unavailable-image-fails-resource-coverage"] = observedUnavailableImageFailsCoverage,
                ["observed-loading-image-fails-resource-coverage"] = observedLoadingImageFailsCoverage,
                ["clean-resource-count-without-demand-oracle-remains-not-evaluated"] = cleanResourcesWithoutDemandOracleRemainNotEvaluated,
                ["content-oracle-accepts-ordered-visible-text-and-excludes-collapsed-disclosures"] = orderedTextPasses,
                ["content-oracle-fails-missing-text"] = missingTextFails,
                ["content-oracle-fails-reordered-text"] = reorderedTextFails,
                ["content-oracle-fails-missing-structure"] = missingTableFails,
                ["content-oracle-unrecognized-inline-is-not-evaluated"] = unrecognizedInlineIsNotEvaluated,
                ["syntax-oracle-classifies-diagnostics-as-unsupported"] = diagnosticsClassifyUnsupported,
                ["resource-oracle-classifies-demand-terminal-states"] = demandOracleClassifiesResources,
                ["content-oracle-accepts-punctuation-beside-an-omitted-inline-object"] = punctuationBesideOmittedInlinePasses,
                ["selection-phrase-skips-atomic-selection-objects"] = atomicSelectionObjectsAreRecognized,
            },
        };
    }

    private static bool ThrowsInvalidData(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static CorrectnessCheck EvaluateObservedResourceCoverage(int unavailableImages, int loadingImages)
    {
        if (unavailableImages < 0 || loadingImages < 0)
            return CorrectnessCheck.Failed("resourceCoverage", "Observed image lifecycle counts were invalid.");
        if (unavailableImages > 0 || loadingImages > 0)
            return CorrectnessCheck.Failed(
                "resourceCoverage",
                $"The app reported {unavailableImages} unavailable image(s) and {loadingImages} image(s) still loading after full traversal.");
        return CorrectnessCheck.NotEvaluated(
            "resourceCoverage",
            "No unavailable or still-loading images were observed, but no independent source-to-resource demand oracle proves every referenced resource was emitted and resolved.");
    }

    private static string EvaluateCorrectnessCase(IEnumerable<CorrectnessCheck> checks)
    {
        CorrectnessCheck[] required = checks.Where(check => CorrectnessRequiredChecks.Contains(check.Name, StringComparer.Ordinal)).ToArray();
        if (required.Length != CorrectnessRequiredChecks.Length ||
            required.Select(check => check.Name).Distinct(StringComparer.Ordinal).Count() != CorrectnessRequiredChecks.Length)
        {
            return "incomplete";
        }
        if (required.Any(check => check.State == CorrectnessCheckState.Failed))
            return "failed";
        if (!required.All(check => check.Attempted && check.Evaluated))
            return "incomplete";
        if (required.All(check => check.State == CorrectnessCheckState.Passed))
            return "passed";
        // Every dimension was evaluated and none failed, but a dimension whose
        // job is classification recorded one: unsupported syntax, or a
        // genuinely unavailable upstream resource. Such a case is evaluated,
        // never a pass. Elsewhere those states still leave the case incomplete.
        return required.All(IsPassedOrOwnClassification) ? "classified" : "incomplete";
    }

    private static bool IsPassedOrOwnClassification(CorrectnessCheck check) =>
        check.State == CorrectnessCheckState.Passed ||
        (check.State == CorrectnessCheckState.Unsupported && check.Name == "unsupportedSyntaxClassification") ||
        (check.State == CorrectnessCheckState.UpstreamUnavailable && check.Name == "resourceCoverage");

    private sealed record SourceAcquisitionRecord(
        int Rank,
        string GitBlobSha1,
        long ByteSize,
        string RelativePath,
        string FullPath);

    private sealed record SourceAcquisitionIndex(
        string ManifestSha256,
        IReadOnlyDictionary<int, SourceAcquisitionRecord> Records);
}

internal enum CorrectnessCheckState
{
    NotEvaluated,
    Passed,
    Failed,
    NotApplicable,
    Unsupported,
    UpstreamUnavailable,
    AuditInputMissing,
}

internal sealed record CorrectnessCheck
{
    public string Name { get; init; } = string.Empty;
    public CorrectnessCheckState State { get; init; }
    public bool Attempted { get; init; }
    public bool Evaluated { get; init; }
    public string Detail { get; init; } = string.Empty;

    public static CorrectnessCheck Passed(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.Passed, Attempted = true, Evaluated = true, Detail = detail };

    public static CorrectnessCheck Failed(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.Failed, Attempted = true, Evaluated = true, Detail = detail };

    public static CorrectnessCheck NotEvaluated(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.NotEvaluated, Attempted = false, Evaluated = false, Detail = detail };

    public static CorrectnessCheck NotApplicable(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.NotApplicable, Attempted = true, Evaluated = true, Detail = detail };

    public static CorrectnessCheck Unsupported(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.Unsupported, Attempted = true, Evaluated = true, Detail = detail };

    public static CorrectnessCheck UpstreamUnavailable(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.UpstreamUnavailable, Attempted = true, Evaluated = true, Detail = detail };

    public static CorrectnessCheck AuditInputMissing(string name, string detail) =>
        new() { Name = name, State = CorrectnessCheckState.AuditInputMissing, Attempted = false, Evaluated = false, Detail = detail };
}

internal sealed record ReadmeCorrectnessLiveContext
{
    public required Window Window { get; init; }
    public required AutomationElement Host { get; init; }
    public required string ExpectedText { get; init; }
    public required string ReadmeSourceEvidencePath { get; init; }
    public required string NativeOutputDirectory { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset SourceEvidenceStartedAtUtc { get; init; }
}

internal sealed class ReadmeCorrectnessInteractionResult
{
    public IReadOnlyList<CorrectnessCheck> Checks { get; init; } = [];
    public string? FullDocumentSelectionSha256 { get; init; }
    public string? SelectedPhraseSha256 { get; init; }
    public string? CopiedPhraseSha256 { get; init; }
    public string NavigationEvidence { get; init; } = string.Empty;
}

internal sealed class CorrectnessAccountingSelfTestResult
{
    public bool Passed { get; init; }
    public int PassedCount { get; init; }
    public int TotalCount { get; init; }
    public IReadOnlyDictionary<string, bool> Cases { get; init; } = new Dictionary<string, bool>();
}

internal sealed class CorrectnessCorpusCaseResult
{
    public int Rank { get; init; }
    public string Repository { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public string ReadmeGitBlobSha1 { get; init; } = string.Empty;
    public string? ReadmeSourceSha256 { get; init; }
    public string? SameByteCorpusManifestSha256 { get; init; }
    public int VerifiedAssetCount { get; init; }
    public long VerifiedAssetBytes { get; init; }
    public string Status { get; init; } = "incomplete";
    public string? Failure { get; init; }
    public IReadOnlyList<CorrectnessCheck> Checks { get; init; } = [];
    public string? NativeEvidenceDirectory { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}

internal sealed class CorrectnessCorpusReport
{
    public int SchemaVersion { get; init; }
    public string Scope { get; init; } = string.Empty;
    public string ExecutionStatus { get; init; } = string.Empty;
    public string PhaseStatus { get; init; } = string.Empty;
    public string ReleaseQualification { get; init; } = string.Empty;
    public string BrowserPixelComparison { get; init; } = string.Empty;
    public string ComparativePerformance { get; init; } = string.Empty;
    public string FrozenManifestSha256 { get; init; } = string.Empty;
    public string SourceAcquisitionManifestSha256 { get; init; } = string.Empty;
    public int RequestedStartRank { get; init; }
    public int RequestedCount { get; init; }
    public int CompletedCaseCount { get; init; }
    public int PassedCaseCount { get; init; }
    public int ClassifiedCaseCount { get; init; }
    public int FailedCaseCount { get; init; }
    public int IncompleteCaseCount { get; init; }
    public CorrectnessAccountingSelfTestResult AccountingSelfTests { get; init; } = new();
    public IReadOnlyList<CorrectnessCorpusCaseResult> Cases { get; init; } = [];
    public DateTimeOffset CompletedAtUtc { get; init; }
}

internal sealed class CorrectnessInputPreflightRow
{
    public int Rank { get; init; }
    public string Repository { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public string ReadmeGitBlobSha1 { get; init; } = string.Empty;
    public string? ReadmeSourceSha256 { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string? CorpusManifestSha256 { get; init; }
    public int VerifiedAssetCount { get; init; }
    public long VerifiedAssetBytes { get; init; }
}

internal sealed class CorrectnessInputPreflightReport
{
    public int SchemaVersion { get; init; }
    public string Scope { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string PhaseStatus { get; init; } = string.Empty;
    public string ReleaseQualification { get; init; } = string.Empty;
    public string FrozenManifestSha256 { get; init; } = string.Empty;
    public string SourceAcquisitionManifestSha256 { get; init; } = string.Empty;
    public int RequestedStartRank { get; init; }
    public int RequestedCount { get; init; }
    public int InputReadyCount { get; init; }
    public int AuditInputMissingCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<CorrectnessInputPreflightRow> Rows { get; init; } = [];
    public DateTimeOffset CompletedAtUtc { get; init; }
}

internal enum CorpusInputState
{
    Passed,
    Failed,
    AuditInputMissing,
    NotApplicable,
}

internal sealed record CorpusInputVerification(
    CorpusInputState State,
    string Detail,
    string? DirectoryPath = null,
    string? ManifestSha256 = null,
    int AssetCount = 0,
    long AssetBytes = 0,
    bool CorrectnessInput = false,
    string? ExpectedContentPath = null)
{
    public static CorpusInputVerification Passed(string directory, string manifestSha256, int assetCount, long assetBytes, string detail) =>
        new(CorpusInputState.Passed, detail, directory, manifestSha256, assetCount, assetBytes);

    public static CorpusInputVerification Passed(
        string directory,
        string manifestSha256,
        int assetCount,
        long assetBytes,
        bool correctnessInput,
        string? expectedContentPath,
        string detail) =>
        new(CorpusInputState.Passed, detail, directory, manifestSha256, assetCount, assetBytes, correctnessInput, expectedContentPath);

    public static CorpusInputVerification Failed(string detail) =>
        new(CorpusInputState.Failed, detail);

    public static CorpusInputVerification Missing(string detail) =>
        new(CorpusInputState.AuditInputMissing, detail);

    public static CorpusInputVerification NotApplicable(string detail) =>
        new(CorpusInputState.NotApplicable, detail);
}

internal sealed record CorpusManifestCandidate(
    string ManifestPath,
    string CaseFolder,
    string? ManifestSha256,
    string? FullName,
    string? CommitSha,
    string? ReadmePath,
    string? ReadmeGitBlobSha1,
    string? ParseFailure);
