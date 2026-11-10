using System.Security.Cryptography;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Evidence;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.Tests.TestSupport;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates;

/// <summary>
/// Builds the release-shaped baseline and candidate reports, and the fake
/// deployment they hash, that <see cref="PerformanceGateEvidence"/> hands to the
/// external performance-gate script. Every value sits on a gate edge taken
/// from <see cref="PerformanceGateValues"/>.
/// </summary>
internal static class PerformanceGateReportFactory
{
    internal static BuildArtifactHashes CreateArtifacts(
        string directory,
        out string executablePath)
    {
        BuildArtifactHash Capture(string fileName)
        {
            string path = Path.Combine(directory, fileName);
            string contents = fileName.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)
                ? """
                  {
                    "runtimeOptions": {
                      "configProperties": {
                        "System.Runtime.TieredCompilation": false,
                        "System.Runtime.TieredPGO": false,
                        "System.GC.Concurrent": false,
                        "System.Runtime.ReadyToRun": false
                      }
                    }
                  }
                  """
                : $"schema-12-fixture:{fileName}";
            File.WriteAllText(path, contents);
            return new BuildArtifactHash
            {
                FileName = fileName,
                Path = path,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            };
        }

        BuildArtifactHash executable = Capture("MarkdownRenderer.PerformanceHarness.exe");
        executablePath = executable.Path;
        return new BuildArtifactHashes
        {
            Executable = executable,
            PerformanceHarnessDll = Capture("MarkdownRenderer.PerformanceHarness.dll"),
            MarkdownRendererDll = Capture("MarkdownRenderer.dll"),
            RuntimeConfig = Capture("MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
        };
    }

    internal static PerformanceReport CreateReport(
        PerformanceGateValues values,
        BuildArtifactHashes artifacts,
        string buildIdentity,
        bool candidate)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        ScrollResult scroll = PerformanceGateMeasurementFactory.CreateScroll(values, candidate);
        var report = new PerformanceReport
        {
            ProviderName = PerformanceMeasurementContract.ProviderName,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc.AddSeconds(1),
            IsReleaseEvidence = true,
            BuildIdentity = buildIdentity,
            BuildArtifacts = artifacts,
            RuntimeConfiguration = CreateRuntimeConfiguration(),
            Machine = CreateMachineMetadata(scroll, completion: false),
            CompletionMachine = CreateMachineMetadata(scroll, completion: true),
            MeasurementVisibility = PerformanceVisibilityFixture.CreatePassing(
                startedUtc,
                intervalOffset: TimeSpan.FromMilliseconds(50),
                duration: TimeSpan.FromMilliseconds(900)),
            Scroll = scroll,
            LifecyclePlateau = CreateLifecyclePlateau(),
            SourceLookup = PerformanceGateMeasurementFactory.CreateLookup(values, candidate),
            Cancellation = PerformanceGateMeasurementFactory.CreateCancellation(values, candidate),
            Regression = new RegressionResult
            {
                Mode = "baseline",
                DecisionPolicy = PerformanceMeasurementContract.RegressionDecisionPolicy,
                ReferenceEligible = true,
                MachineComparable = true,
                Passed = true,
            },
            Passed = true,
        };
        AddScenarioResults(report, values, startedUtc, candidate);
        return report;
    }

    private static void AddScenarioResults(
        PerformanceReport report,
        PerformanceGateValues values,
        DateTimeOffset startedUtc,
        bool candidate)
    {
        int scenario = 0;
        foreach (int sourceBytes in new[] { 100 * 1024, 1024 * 1024, 10 * 1024 * 1024 })
        {
            foreach (string mode in new[]
                     {
                         PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
                         PerformanceMeasurementContract.FirstViewportCacheHitMode,
                     })
            {
                double[] trialMeasurements = candidate
                    ? values.FirstViewportCandidateTrialP95Milliseconds[scenario]
                    : values.FirstViewportReferenceTrialP95Milliseconds[scenario];
                report.FirstUsableViewport.Add(PerformanceGateFirstViewportFactory.CreateResult(
                    startedUtc,
                    scenario,
                    sourceBytes,
                    mode,
                    trialMeasurements));
                scenario++;
            }

            report.RetainedMemory.Add(CreateRetainedMemory(sourceBytes));
        }
    }

    private static RuntimeConfigurationEvidence CreateRuntimeConfiguration()
        => new()
        {
            Policy = PerformanceMeasurementContract.RuntimeConfigurationPolicy,
            TieredCompilationEnabled = false,
            TieredPgoEnabled = false,
            ConcurrentGcEnabled = false,
            ReadyToRunEnabled = false,
            OverrideEnvironmentVariables = [],
            Passed = true,
        };

    private static LifecyclePlateauResult CreateLifecyclePlateau()
        => new()
        {
            RequiredCycles = 100,
            CompletedCycles = 100,
            SyntheticDeviceResets = 4,
            Checkpoint80ManagedBytes = 1,
            Checkpoint100ManagedBytes = 1,
            Checkpoint80PrivateBytes = 1,
            Checkpoint100PrivateBytes = 1,
            ManagedGrowthPercent = 0,
            PrivateGrowthPercent = 0,
            GrowthPercent = 0,
            RenderFailures = 0,
            Passed = true,
        };

    private static RetainedMemoryResult CreateRetainedMemory(int sourceBytes)
    {
        long memoryBudget = sourceBytes * 8L + 32L * 1024 * 1024;
        return new RetainedMemoryResult
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
            SourceUtf16Bytes = sourceBytes,
            ManagedDeltaBytes = memoryBudget,
            PrivateDeltaBytes = memoryBudget,
            BudgetBytes = memoryBudget,
            Passed = true,
        };
    }

    private static MachineMetadata CreateMachineMetadata(
        ScrollResult scroll,
        bool completion)
        => new()
        {
            MachineInstanceSha256 = new string('A', 64),
            Cpu = "fixture-cpu",
            LogicalProcessorCount = 8,
            OsDescription = "fixture-os",
            OsVersion = "10.0.26100",
            OsArchitecture = "X64",
            ProcessArchitecture = "X64",
            FrameworkDescription = ".NET fixture",
            DpiScale = 1,
            ViewportWidthDips = 1100,
            ViewportHeightDips = 760,
            DisplayDeviceName = @"\\.\DISPLAY1",
            ConfiguredRefreshRateHz = 120,
            ObservedRefreshRateHz = completion
                ? 0
                : MachineEvidenceValidator.CalculateObservedRefreshRateHz(scroll),
            IsAtLeast120Hz = !completion,
            GpuAdapterLuid = "00000001:00000002",
            GpuAdapterIdentitySha256 = new string('C', 64),
            GpuDriverVersion = "31.0.15.1234",
            DisplayAdapterIdentitySha256 = new string('D', 64),
            DisplayAdapterLuid = "00000005:00000006",
            DisplayAdapterDriverVersion = "31.0.15.1234",
            ActivePowerSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            UserConfiguredAcPowerModeGuid = "ded574b5-45a0-4f42-8737-46345c09c238",
            UserConfiguredDcPowerModeGuid = "961cc777-2547-4f9d-8174-7d86181b8a7a",
            PowerSource = "AC",
            EnergySaverState = "Off",
            EffectivePowerMode = "MaxPerformance",
            ProcessPriorityClass = "High",
            ProcessPowerThrottlingMode =
                PerformanceMeasurementContract.ProcessPowerThrottlingMode,
            GcServer = false,
            GcLatencyMode = "Interactive",
        };
}
