using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarkdownRenderer.PerformanceHarness;

namespace MarkdownRenderer.Tests;

/// <summary>
/// A temporary baseline/candidate report pair and fake deployment that the
/// repository's external performance-gate script
/// (eng/Test-MarkdownWinUIPerformanceGates.ps1) is run against. Tests change the
/// evidence through <see cref="ApplyMutation"/> or the named scenario methods
/// and then run the script with <see cref="RunGate"/>.
/// </summary>
internal sealed class PerformanceGateEvidence : IDisposable
{
    // This watchdog bounds a test-only PowerShell process, not a product
    // performance budget. Hosted Windows runners can occasionally spend
    // more than 30 seconds in process launch/script scanning while several
    // architecture and browser jobs share the host. The gate's measured
    // latency limits remain unchanged inside the script.
    private const int ExternalGateProcessTimeoutMilliseconds = 60_000;

    private readonly string _directory;
    private readonly string _scriptPath;

    private PerformanceGateEvidence(
        string directory,
        string scriptPath,
        string executableArtifactPath,
        string baselinePath,
        string candidatePath)
    {
        _directory = directory;
        _scriptPath = scriptPath;
        ExecutableArtifactPath = executableArtifactPath;
        BaselinePath = baselinePath;
        CandidatePath = candidatePath;
    }

    internal string BaselinePath { get; }
    internal string CandidatePath { get; }
    internal string ExecutableArtifactPath { get; }

    internal string DeploymentDirectory
        => Path.GetDirectoryName(ExecutableArtifactPath)
            ?? throw new InvalidOperationException("The fixture executable has no parent directory.");

    internal static PerformanceGateEvidence Create()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "PerformanceFixtures");
        PerformanceGateValues values = JsonSerializer.Deserialize<PerformanceGateValues>(
            File.ReadAllText(Path.Combine(fixtureDirectory, "performance-gate-schema10-values.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The schema-12 external-gate fixture was empty.");
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-renderer-gate-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string deploymentDirectory = Path.Combine(directory, "deployment");
        Directory.CreateDirectory(deploymentDirectory);

        BuildArtifactHashes artifacts = PerformanceGateReportFactory.CreateArtifacts(
            deploymentDirectory,
            out string executablePath);
        string buildIdentity =
            PerformanceDeploymentIdentity.CaptureForExecutable(executablePath);
        PerformanceReport baseline = PerformanceGateReportFactory.CreateReport(
            values,
            artifacts,
            buildIdentity,
            candidate: false);
        string baselinePath = Path.Combine(directory, "baseline.json");
        string candidatePath = Path.Combine(directory, "candidate.json");
        File.WriteAllText(
            baselinePath,
            JsonSerializer.Serialize(baseline, PerformanceReport.JsonOptions));

        PerformanceReport candidate = PerformanceGateReportFactory.CreateReport(
            values,
            artifacts,
            buildIdentity,
            candidate: true);
        candidate.Regression = RegressionEvaluator.Evaluate(
            candidate,
            PerformanceOptions.Parse(
            [
                "--output", candidatePath,
                "--reference", baselinePath,
            ]));
        candidate.Passed = candidate.Regression.Passed;
        File.WriteAllText(
            candidatePath,
            JsonSerializer.Serialize(candidate, PerformanceReport.JsonOptions));

        return new PerformanceGateEvidence(
            directory,
            FindGateScript(),
            executablePath,
            baselinePath,
            candidatePath);
    }

    /// <summary>Applies one named mutation from <see cref="PerformanceGateMutationCatalog"/>.</summary>
    internal void ApplyMutation(string mutation)
        => PerformanceGateMutationCatalog.Apply(this, mutation);

    internal JsonObject ReadCandidateReport() => ReadReport(CandidatePath);

    /// <summary>Rewrites the candidate report after <paramref name="mutation"/> changes it.</summary>
    internal void MutateCandidate(Action<JsonObject> mutation)
    {
        JsonObject candidate = ReadReport(CandidatePath);
        mutation(candidate);
        WriteReport(CandidatePath, candidate);
    }

    /// <summary>
    /// Rewrites the baseline (reference) report after <paramref name="mutation"/>
    /// changes it and the candidate, then rebinds the candidate to the new
    /// reference bytes so only the intended change is under test.
    /// </summary>
    internal void MutateReference(Action<JsonObject, JsonObject> mutation)
    {
        JsonObject baseline = ReadReport(BaselinePath);
        JsonObject candidate = ReadReport(CandidatePath);
        mutation(baseline, candidate);
        WriteReport(BaselinePath, baseline);
        candidate["regression"]!["referenceReportSha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BaselinePath)));
        WriteReport(CandidatePath, candidate);
    }

    /// <summary>Rewrites the raw candidate JSON text.</summary>
    internal void MutateCandidateText(Func<string, string> mutation)
        => File.WriteAllText(CandidatePath, mutation(File.ReadAllText(CandidatePath)));

    /// <summary>
    /// Rewrites the raw baseline JSON text and rebinds the candidate to the new
    /// reference bytes.
    /// </summary>
    internal void MutateReferenceText(Func<string, string> mutation)
    {
        File.WriteAllText(BaselinePath, mutation(File.ReadAllText(BaselinePath)));
        JsonObject candidate = ReadReport(CandidatePath);
        candidate["regression"]!["referenceReportSha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BaselinePath)));
        WriteReport(CandidatePath, candidate);
    }

    internal bool ObservedRefreshRatesDiffer()
    {
        JsonObject baseline = ReadReport(BaselinePath);
        JsonObject candidate = ReadReport(CandidatePath);
        double baselineRefresh = baseline["machine"]!["observedRefreshRateHz"]!.GetValue<double>();
        double candidateRefresh = candidate["machine"]!["observedRefreshRateHz"]!.GetValue<double>();
        return baselineRefresh != candidateRefresh;
    }

    internal void SetCandidateGpuAdapterLuid(string gpuAdapterLuid)
        => MutateCandidate(report =>
        {
            report["machine"]!["gpuAdapterLuid"] = gpuAdapterLuid;
            report["completionMachine"]!["gpuAdapterLuid"] = gpuAdapterLuid;
        });

    internal void SetCandidateDisplayAdapterLuid(string displayAdapterLuid)
        => MutateCandidate(report =>
        {
            report["machine"]!["displayAdapterLuid"] = displayAdapterLuid;
            report["completionMachine"]!["displayAdapterLuid"] = displayAdapterLuid;
        });

    internal void AddPrivateRuntimeDependencyAndRefreshCandidateIdentity()
    {
        string nestedDirectory = Path.Combine(DeploymentDirectory, "plugins", "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(
            Path.Combine(nestedDirectory, "Private.Runtime.Dependency.dll"),
            "private-runtime-dependency");

        MutateCandidate(candidate => candidate["buildIdentity"] =
            PerformanceDeploymentIdentity.CaptureForExecutable(ExecutableArtifactPath));
    }

    internal void AddExcludedDeploymentFiles()
    {
        string nestedDirectory = Path.Combine(DeploymentDirectory, "symbols");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "fixture.PDB"), "symbols");
        File.WriteAllText(Path.Combine(nestedDirectory, "fixture.Xml"), "documentation");
    }

    internal PerformanceGateResult RunCandidateWithEmptyDirectoryReparsePoint()
    {
        string externalDirectory = Path.Combine(_directory, "external-runtime");
        Directory.CreateDirectory(externalDirectory);
        string link = Path.Combine(DeploymentDirectory, "linked-runtime");
        Directory.CreateSymbolicLink(link, externalDirectory);
        try
        {
            return RunGate(CandidatePath, "Candidate");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    internal PerformanceGateResult RunCandidateWithExcludedFileReparsePoint()
    {
        string target = Path.Combine(_directory, "external-symbols.txt");
        File.WriteAllText(target, "external-symbols");
        string link = Path.Combine(DeploymentDirectory, "linked-symbols.PDB");
        File.CreateSymbolicLink(link, target);
        try
        {
            return RunGate(CandidatePath, "Candidate");
        }
        finally
        {
            File.Delete(link);
        }
    }

    internal PerformanceGateResult RunCandidateWithReferenceThroughNestedLinks()
    {
        string reportsDirectory = Path.Combine(DeploymentDirectory, "reports");
        Directory.CreateDirectory(reportsDirectory);
        string insideReferencePath = Path.Combine(reportsDirectory, "reference-report.json");
        File.Copy(BaselinePath, insideReferencePath);

        string deploymentLink = Path.Combine(_directory, "deployment-link");
        string nestedLink = Path.Combine(_directory, "nested-deployment-link");
        Directory.CreateSymbolicLink(deploymentLink, DeploymentDirectory);
        Directory.CreateSymbolicLink(
            nestedLink,
            Path.Combine(deploymentLink, "reports"));
        try
        {
            BindCandidateToReference(
                Path.Combine(nestedLink, "reference-report.json"),
                insideReferencePath);
            return RunGate(CandidatePath, "Candidate");
        }
        finally
        {
            Directory.Delete(nestedLink);
            Directory.Delete(deploymentLink);
        }
    }

    internal PerformanceGateResult RunCandidateFromDeploymentRoot()
    {
        string insidePath = Path.Combine(DeploymentDirectory, "candidate-report.json");
        File.Copy(CandidatePath, insidePath);
        return RunGate(insidePath, "Candidate");
    }

    internal PerformanceGateResult RunCandidateWithReferenceInsideDeploymentRoot()
    {
        string insideReferencePath = Path.Combine(
            DeploymentDirectory,
            "reference-report.json");
        File.Copy(BaselinePath, insideReferencePath);

        BindCandidateToReference(insideReferencePath, insideReferencePath);
        return RunGate(CandidatePath, "Candidate");
    }

    internal PerformanceGateResult RunGate(string reportPath, string mode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(_scriptPath);
        startInfo.ArgumentList.Add("-ReportPath");
        startInfo.ArgumentList.Add(reportPath);
        startInfo.ArgumentList.Add("-RequiredRegressionMode");
        startInfo.ArgumentList.Add(mode);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell for the external gate fixture.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ExternalGateProcessTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The external performance gate fixture timed out.");
        }
        Task.WaitAll(standardOutput, standardError);
        string output = standardOutput.Result + Environment.NewLine + standardError.Result;
        return new PerformanceGateResult(process.ExitCode == 0, output);
    }

    public void Dispose()
        => Directory.Delete(_directory, recursive: true);

    // Run the repository's gate script itself (the same file CI primes)
    // rather than a copy staged into the test output directory.
    private static string FindGateScript()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(directory.FullName, "eng", "Test-MarkdownWinUIPerformanceGates.ps1");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "eng",
                "Test-MarkdownWinUIPerformanceGates.ps1");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException(
            "The MarkdownRenderer external performance-gate script was not found above the test output directory.");
    }

    private void BindCandidateToReference(string referencePath, string referenceBytesPath)
        => MutateCandidate(candidate =>
        {
            candidate["regression"]!["referencePath"] = referencePath;
            candidate["regression"]!["referenceReportSha256"] = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(referenceBytesPath)));
            candidate["buildIdentity"] =
                PerformanceDeploymentIdentity.CaptureForExecutable(ExecutableArtifactPath);
        });

    private static JsonObject ReadReport(string path)
        => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void WriteReport(string path, JsonObject report)
        => File.WriteAllText(path, report.ToJsonString(PerformanceReport.JsonOptions));
}
