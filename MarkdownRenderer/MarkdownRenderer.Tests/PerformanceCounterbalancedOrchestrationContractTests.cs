using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceCounterbalancedOrchestrationContractTests
{
    [Fact]
    public void OrchestratorHasValidPowerShellSyntax()
    {
        string scriptPath = FindOrchestrator();
        string escapedPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        string command =
            "$tokens=$null;$errors=$null;" +
            $"[Management.Automation.Language.Parser]::ParseFile('{escapedPath}',[ref]$tokens,[ref]$errors)|Out-Null;" +
            "if($errors.Count){$errors|ForEach-Object{$_.Message}|Write-Error;exit 1};exit 0";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void OrchestratorRejectsTheSameExecutableBeforeCreatingEvidence()
    {
        string scriptPath = FindOrchestrator();
        string executable = GetCurrentExecutable();
        string outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-same-{Guid.NewGuid():N}");

        ProcessResult result = RunPowerShell(
            "-File", scriptPath,
            "-ReferenceHarnessPath", executable,
            "-CandidateHarnessPath", executable,
            "-OutputRoot", outputRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "must be different files",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public void OrchestratorRejectsOutputRootInsideEitherDeploymentBeforeCreatingEvidence()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-output-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "reference-runtime");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "candidate-runtime");
            foreach (string deploymentRoot in new[]
            {
                Path.GetDirectoryName(reference)!,
                Path.GetDirectoryName(candidate)!,
            })
            {
                string outputRoot = Path.Combine(deploymentRoot, "must-not-create-evidence");

                ProcessResult result = RunPowerShell(
                    "-File", FindOrchestrator(),
                    "-ReferenceHarnessPath", reference,
                    "-CandidateHarnessPath", candidate,
                    "-OutputRoot", outputRoot);

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "Output root must be outside both reference and candidate runtime-output directories",
                    result.Output,
                    StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(outputRoot));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OrchestratorRejectsOutputRootLinkedIntoADeploymentBeforeCreatingEvidence()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-linked-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string link = Path.Combine(directory, "output-link");
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "reference-runtime");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "candidate-runtime");
            string forbiddenOutput = Path.Combine(
                Path.GetDirectoryName(reference)!,
                "must-not-create-evidence");
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(reference)!);

            ProcessResult result = RunPowerShell(
                "-File", FindOrchestrator(),
                "-ReferenceHarnessPath", reference,
                "-CandidateHarnessPath", candidate,
                "-OutputRoot", Path.Combine(link, "must-not-create-evidence"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "Output root must be outside both reference and candidate runtime-output directories",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(forbiddenOutput));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OrchestratorRejectsAChildRuntimeReparsePointBeforeCreatingEvidence()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-runtime-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string link = Path.Combine(directory, "reference", "linked-runtime");
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "reference-runtime");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "candidate-runtime");
            string externalRuntime = Path.Combine(directory, "external-runtime");
            Directory.CreateDirectory(externalRuntime);
            File.WriteAllText(
                Path.Combine(externalRuntime, "dependency.dll"),
                "dependency",
                Encoding.UTF8);
            Directory.CreateSymbolicLink(link, externalRuntime);
            string outputRoot = Path.Combine(directory, "evidence");

            ProcessResult result = RunPowerShell(
                "-File", FindOrchestrator(),
                "-ReferenceHarnessPath", reference,
                "-CandidateHarnessPath", candidate,
                "-OutputRoot", outputRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("unsupported reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(outputRoot));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OrchestratorRejectsDifferentPathsWithTheSameCompleteArtifactSet()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "same-managed-artifacts");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "same-managed-artifacts");
            string outputRoot = Path.Combine(directory, "evidence");

            ProcessResult result = RunPowerShell(
                "-File", FindOrchestrator(),
                "-ReferenceHarnessPath", reference,
                "-CandidateHarnessPath", candidate,
                "-OutputRoot", outputRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "different runtime-output manifest identities",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(outputRoot));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreflightAllowsByteIdenticalApphostsWhenAManagedArtifactDiffers()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-apphost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "reference-managed-artifacts");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "candidate-managed-artifacts");
            string escapedScript = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
            string escapedReference = reference.Replace("'", "''", StringComparison.Ordinal);
            string escapedCandidate = candidate.Replace("'", "''", StringComparison.Ordinal);
            string command = $$"""
                $tokens=$null;$errors=$null;
                $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedScript}}',[ref]$tokens,[ref]$errors);
                if($errors.Count){exit 20};
                $needed=@('Resolve-RequiredFile','Get-Sha256','Get-ExpectedBuildArtifactSet','Test-ArtifactHashSetEqual','Get-RuntimeOutputManifest');
                foreach($name in $needed){
                    $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                    if($node.Count -ne 1){exit 21};
                    Invoke-Expression $node[0].Extent.Text;
                }
                $reference=Get-ExpectedBuildArtifactSet -ExecutablePath '{{escapedReference}}' -Label 'Reference';
                $candidate=Get-ExpectedBuildArtifactSet -ExecutablePath '{{escapedCandidate}}' -Label 'Candidate';
                $script:Utf8NoBom=[Text.UTF8Encoding]::new($false,$true);
                $script:RuntimeOutputManifestPolicy='runtime-output-manifest-v1';
                if($reference.executable.sha256 -cne $candidate.executable.sha256){exit 22};
                if(Test-ArtifactHashSetEqual -Left $reference -Right $candidate){exit 23};
                $referenceManifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedReference}}' -Label 'Reference';
                $candidateManifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedCandidate}}' -Label 'Candidate';
                if($referenceManifest.identity -ceq $candidateManifest.identity){exit 24};
                exit 0
                """;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreflightTreatsRuntimeConfigurationAsBuildIdentity()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "same-managed-artifacts",
                "reference-runtime");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "same-managed-artifacts",
                "candidate-runtime");
            string escapedScript = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
            string escapedReference = reference.Replace("'", "''", StringComparison.Ordinal);
            string escapedCandidate = candidate.Replace("'", "''", StringComparison.Ordinal);
            string command = $$"""
                $tokens=$null;$errors=$null;
                $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedScript}}',[ref]$tokens,[ref]$errors);
                if($errors.Count){exit 30};
                $needed=@('Resolve-RequiredFile','Get-Sha256','Get-ExpectedBuildArtifactSet','Test-ArtifactHashSetEqual','Get-RuntimeOutputManifest');
                foreach($name in $needed){
                    $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                    if($node.Count -ne 1){exit 31};
                    Invoke-Expression $node[0].Extent.Text;
                }
                $reference=Get-ExpectedBuildArtifactSet -ExecutablePath '{{escapedReference}}' -Label 'Reference';
                $candidate=Get-ExpectedBuildArtifactSet -ExecutablePath '{{escapedCandidate}}' -Label 'Candidate';
                $script:Utf8NoBom=[Text.UTF8Encoding]::new($false,$true);
                $script:RuntimeOutputManifestPolicy='runtime-output-manifest-v1';
                if($reference.Count -ne 5 -or $candidate.Count -ne 5){exit 32};
                if($reference.executable.sha256 -cne $candidate.executable.sha256){exit 33};
                if($reference.performanceHarnessDll.sha256 -cne $candidate.performanceHarnessDll.sha256 -or
                   $reference.markdownRendererDll.sha256 -cne $candidate.markdownRendererDll.sha256 -or
                   $reference.markdownRendererCoreDll.sha256 -cne $candidate.markdownRendererCoreDll.sha256){exit 34};
                if($reference.runtimeConfig.sha256 -ceq $candidate.runtimeConfig.sha256){exit 35};
                if(Test-ArtifactHashSetEqual -Left $reference -Right $candidate){exit 36};
                $referenceManifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedReference}}' -Label 'Reference';
                $candidateManifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedCandidate}}' -Label 'Candidate';
                if($referenceManifest.identity -ceq $candidateManifest.identity){exit 37};
                exit 0
                """;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RuntimeOutputManifestHasCanonicalRecursiveContentIdentity()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string executable = CreateSyntheticBuild(directory, "managed-artifacts");
            string nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "Z.runtime"), [0, 1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(nested, "a.runtime"), [9, 8, 7]);
            string ignoredPdb = Path.Combine(nested, "symbols.PdB");
            string ignoredXml = Path.Combine(directory, "documentation.XML");
            File.WriteAllText(ignoredPdb, "first symbols", Encoding.UTF8);
            File.WriteAllText(ignoredXml, "first documentation", Encoding.UTF8);

            string expected = ComputeExpectedRuntimeOutputIdentity(executable);
            string first = GetRuntimeOutputIdentityFromPowerShell(executable);

            Assert.Equal(expected, first);
            Assert.Equal(
                PerformanceDeploymentIdentity.CaptureForExecutable(executable),
                first);
            Assert.Matches(
                "^runtime-output-manifest-v1:[0-9A-F]{64}$",
                first);

            File.WriteAllText(ignoredPdb, "changed symbols", Encoding.UTF8);
            File.WriteAllText(ignoredXml, "changed documentation", Encoding.UTF8);
            string afterIgnoredChanges = GetRuntimeOutputIdentityFromPowerShell(executable);

            Assert.Equal(first, afterIgnoredChanges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreflightDistinguishesNestedRuntimeContentBeyondTheFiveExplicitArtifacts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-dependency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string reference = CreateSyntheticBuild(
                Path.Combine(directory, "reference"),
                "same-explicit-artifacts");
            string candidate = CreateSyntheticBuild(
                Path.Combine(directory, "candidate"),
                "same-explicit-artifacts");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reference)!, "runtimes"));
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(candidate)!, "runtimes"));
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(reference)!, "runtimes", "dependency.dll"),
                "reference-dependency",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(candidate)!, "runtimes", "dependency.dll"),
                "candidate-dependency",
                Encoding.UTF8);

            string escapedScript = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
            string escapedReference = reference.Replace("'", "''", StringComparison.Ordinal);
            string escapedCandidate = candidate.Replace("'", "''", StringComparison.Ordinal);
            string command = $$"""
                $ErrorActionPreference='Stop';Set-StrictMode -Version Latest;
                $tokens=$null;$errors=$null;
                $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedScript}}',[ref]$tokens,[ref]$errors);
                if($errors.Count){exit 40};
                $needed=@('Resolve-RequiredFile','Get-Sha256','Get-ExpectedBuildArtifactSet','Test-ArtifactHashSetEqual','Get-RuntimeOutputManifest');
                foreach($name in $needed){
                    $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                    if($node.Count -ne 1){exit 41};
                    Invoke-Expression $node[0].Extent.Text;
                }
                $script:Utf8NoBom=[Text.UTF8Encoding]::new($false,$true);
                $script:RuntimeOutputManifestPolicy='runtime-output-manifest-v1';
                $referenceArtifacts=Get-ExpectedBuildArtifactSet -ExecutablePath '{{escapedReference}}' -Label 'Reference';
                $candidateArtifacts=Get-ExpectedBuildArtifactSet -ExecutablePath '{{escapedCandidate}}' -Label 'Candidate';
                if(-not (Test-ArtifactHashSetEqual -Left $referenceArtifacts -Right $candidateArtifacts)){exit 42};
                $referenceManifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedReference}}' -Label 'Reference';
                $candidateManifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedCandidate}}' -Label 'Candidate';
                if($referenceManifest.identity -ceq $candidateManifest.identity){exit 43};
                if($referenceManifest.fileCount -ne 6 -or $candidateManifest.fileCount -ne 6){exit 44};
                exit 0
                """;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RuntimeOutputManifestGuardRejectsAChangedNestedRuntimeFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"markdown-counterbalanced-manifest-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string executable = CreateSyntheticBuild(directory, "managed-artifacts");
            string nestedRuntime = Path.Combine(directory, "nested-runtime.dll");
            string ignoredPdb = Path.Combine(directory, "symbols.PDB");
            File.WriteAllText(nestedRuntime, "original runtime", Encoding.UTF8);
            File.WriteAllText(ignoredPdb, "original symbols", Encoding.UTF8);
            string escapedScript = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
            string escapedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
            string escapedRuntime = nestedRuntime.Replace("'", "''", StringComparison.Ordinal);
            string escapedPdb = ignoredPdb.Replace("'", "''", StringComparison.Ordinal);
            string command = $$"""
                $ErrorActionPreference='Stop';Set-StrictMode -Version Latest;
                $tokens=$null;$errors=$null;
                $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedScript}}',[ref]$tokens,[ref]$errors);
                if($errors.Count){exit 50};
                foreach($name in @('Get-Sha256','Get-RuntimeOutputManifest','Assert-RuntimeOutputManifestUnchanged')){
                    $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                    if($node.Count -ne 1){exit 51};
                    Invoke-Expression $node[0].Extent.Text;
                }
                $script:Utf8NoBom=[Text.UTF8Encoding]::new($false,$true);
                $script:RuntimeOutputManifestPolicy='runtime-output-manifest-v1';
                $expected=Get-RuntimeOutputManifest -ExecutablePath '{{escapedExecutable}}' -Label 'Synthetic build';
                [IO.File]::WriteAllText('{{escapedPdb}}','changed ignored symbols');
                Assert-RuntimeOutputManifestUnchanged -ExpectedManifest $expected -ExecutablePath '{{escapedExecutable}}' -Label 'Synthetic build';
                [IO.File]::WriteAllText('{{escapedRuntime}}','changed runtime');
                try {
                    Assert-RuntimeOutputManifestUnchanged -ExpectedManifest $expected -ExecutablePath '{{escapedExecutable}}' -Label 'Synthetic build';
                }
                catch {
                    if($_.Exception.Message -notlike '*runtime output changed*'){exit 52};
                    exit 0;
                }
                exit 53
                """;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScriptFreezesSinglePassOrderAndHashBoundCreateNewEvidence()
    {
        string text = File.ReadAllText(FindOrchestrator());
        int r1 = text.IndexOf("Ordinal = 1; Role = 'R1'", StringComparison.Ordinal);
        int c1 = text.IndexOf("Ordinal = 2; Role = 'C1'", StringComparison.Ordinal);
        int c2 = text.IndexOf("Ordinal = 3; Role = 'C2'", StringComparison.Ordinal);
        int r2 = text.IndexOf("Ordinal = 4; Role = 'R2'", StringComparison.Ordinal);
        int outputContainment = text.IndexOf(
            "Output root must be outside both reference and candidate runtime-output directories",
            StringComparison.Ordinal);
        int outputCreation = text.IndexOf(
            "[IO.Directory]::CreateDirectory($fullOutputRoot)",
            StringComparison.Ordinal);

        Assert.True(r1 >= 0 && r1 < c1 && c1 < c2 && c2 < r2);
        Assert.True(outputContainment >= 0 && outputContainment < outputCreation);
        Assert.Contains("[IO.FileMode]::CreateNew", text, StringComparison.Ordinal);
        Assert.Contains("Get-EvidenceSetSha256", text, StringComparison.Ordinal);
        Assert.Contains("Assert-BuildArtifactSetUnchanged", text, StringComparison.Ordinal);
        Assert.Contains("Get-RuntimeOutputManifest", text, StringComparison.Ordinal);
        Assert.Contains("Assert-RuntimeOutputManifestUnchanged", text, StringComparison.Ordinal);
        Assert.Contains("$first = & $captureSnapshot", text, StringComparison.Ordinal);
        Assert.Contains("$second = & $captureSnapshot", text, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::Read", text, StringComparison.Ordinal);
        Assert.Contains("[IO.FileAttributes]::ReparsePoint", text, StringComparison.Ordinal);
        Assert.Contains(
            "$script:RuntimeOutputManifestPolicy = 'runtime-output-manifest-v1'",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ([string]$referenceRuntimeOutputManifest.identity -ceq",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (Test-ArtifactHashSetEqual -Left $referenceArtifactSet -Right $candidateArtifactSet)",
            text,
            StringComparison.Ordinal);
        Assert.Contains("-ExpectedBuildIdentity $run.ExpectedBuildIdentity", text, StringComparison.Ordinal);
        Assert.Contains("reference-runtime-output-manifest:", text, StringComparison.Ordinal);
        Assert.Contains("candidate-runtime-output-manifest:", text, StringComparison.Ordinal);
        Assert.Contains("'runtimeConfig'", text, StringComparison.Ordinal);
        Assert.Contains(
            "MarkdownRenderer.PerformanceHarness.runtimeconfig.json",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Counterbalanced orchestrator", text, StringComparison.Ordinal);
        Assert.Contains("foreach ($run in $plan)", text, StringComparison.Ordinal);
        Assert.Contains("-RequiredRegressionMode", text, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(text, "GateMode = 'Baseline'"));
        Assert.DoesNotContain("GateMode = 'Candidate'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'--reference', $r1Path", text, StringComparison.Ordinal);
        Assert.Contains("$script:AggregateSchemaVersion = 2", text, StringComparison.Ordinal);
        Assert.Contains(
            "schema10-r1-c1-c2-r2-abba-contrast-v2",
            text,
            StringComparison.Ordinal);
        Assert.Contains("[int]$Report.schemaVersion -ne 10", text, StringComparison.Ordinal);
        Assert.Contains("if ([int]$gateResult.ExitCode -ne 0)", text, StringComparison.Ordinal);
        Assert.Contains("Assert-ReportRoleBinding", text, StringComparison.Ordinal);
        Assert.Contains("diagnosticsAreNonGating = $true", text, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeOutputManifest =", text, StringComparison.Ordinal);
        Assert.DoesNotContain("withinIndividualAllowance", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pairedDeltaSpreadAllowance", text, StringComparison.Ordinal);
        Assert.DoesNotContain("roleSpreadBudgetPercent", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregatePolicyUsesAllFourRolesAndRejectsAReplicatedRegression()
    {
        string escapedPath = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            $tokens=$null;$errors=$null;
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedPath}}',[ref]$tokens,[ref]$errors);
            if($errors.Count){exit 10};
            $needed=@('Get-HodgesLehmann','Get-RoleSpreadPercent','Get-DeltaPercent','Test-LessThanOrNearlyEqual','Get-MetricValue','Get-MetricDefinitions','Get-AggregateMetricVerdicts');
            foreach($name in $needed){
                $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                if($node.Count -ne 1){exit 11};
                Invoke-Expression $node[0].Extent.Text;
            }
            function New-Report([double]$value){
                $first=@();
                foreach($mode in @('cache-disabled','cache-hit')){
                    foreach($size in @(102400,1048576,10485760)){
                        $first += [pscustomobject]@{mode=$mode;sourceUtf16Bytes=$size;regressionP95Milliseconds=$value};
                    }
                }
                return [pscustomobject]@{
                    firstUsableViewport=$first;
                    scroll=[pscustomobject]@{
                        regressionUiThreadWorkP95Milliseconds=$value;
                        regressionUiThreadWorkP99Milliseconds=$value;
                        regressionFrameTimeP95Milliseconds=$value;
                        regressionRendererOwnedAllocatedBytesPerFrameP95=$value
                    };
                    sourceLookup=[pscustomobject]@{regressionP95Nanoseconds=$value};
                    cancellation=[pscustomobject]@{regressionP95Milliseconds=$value}
                };
            }
            $reports=[ordered]@{R1=(New-Report 100);C1=(New-Report 100);C2=(New-Report 100);R2=(New-Report 100)};
            $passing=@(Get-AggregateMetricVerdicts -ReportsByRole $reports);
            if($passing.Count -ne 12 -or @($passing|Where-Object{-not $_.passed}).Count -ne 0){exit 12};

            # ABBA must cancel an exact six-unit linear drift per run, including
            # pair disagreement that is larger than the product allowance.
            $reports=[ordered]@{R1=(New-Report 100);C1=(New-Report 106);C2=(New-Report 112);R2=(New-Report 118)};
            $drift=@(Get-AggregateMetricVerdicts -ReportsByRole $reports);
            if(@($drift|Where-Object{-not $_.passed}).Count -ne 0){exit 13};
            $driftMetric=@($drift|Where-Object{$_.metric -ceq 'cancellation.p95Ms.hodgesLehmann'});
            if($driftMetric.Count -ne 1 -or $driftMetric[0].abbaContrastEffect -ne 0 -or
               $driftMetric[0].pairedDeltaSpread -ne 12 -or
               $driftMetric[0].linearDriftEstimatePerRun -ne 6 -or
               $driftMetric[0].referenceRoleSpan -ne 18 -or
               $driftMetric[0].candidateRoleSpan -ne 6){exit 14};

            # A replicated +6% candidate effect has no temporal drift to cancel
            # and must remain a regression for latency metrics with a 5% floor.
            $reports=[ordered]@{R1=(New-Report 100);C1=(New-Report 106);C2=(New-Report 106);R2=(New-Report 100)};
            $failing=@(Get-AggregateMetricVerdicts -ReportsByRole $reports);
            $cancellation=@($failing|Where-Object{$_.metric -ceq 'cancellation.p95Ms.hodgesLehmann'});
            if($cancellation.Count -ne 1 -or $cancellation[0].decision -cne 'regression' -or $cancellation[0].passed){exit 15};

            # Even extreme pure linear drift is diagnostic-only. Binding any
            # pair delta or role span here would defeat the ABBA contrast.
            $reports=[ordered]@{R1=(New-Report 100);C1=(New-Report 200);C2=(New-Report 300);R2=(New-Report 400)};
            $diagnostic=@(Get-AggregateMetricVerdicts -ReportsByRole $reports);
            $diagnosticCancellation=@($diagnostic|Where-Object{$_.metric -ceq 'cancellation.p95Ms.hodgesLehmann'});
            if($diagnosticCancellation.Count -ne 1 -or -not $diagnosticCancellation[0].passed -or
               $diagnosticCancellation[0].decision -cne 'pass' -or
               -not $diagnosticCancellation[0].diagnosticsAreNonGating -or
               $diagnosticCancellation[0].pairedDeltaSpread -ne 200 -or
               $diagnosticCancellation[0].linearDriftEstimatePerRun -ne 100 -or
               $diagnosticCancellation[0].referenceRoleSpan -ne 300 -or
               $diagnosticCancellation[0].candidateRoleSpan -ne 100){exit 16};
            exit 0
            """;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void ComponentBindingRejectsMalformedAndNonpassingBaselineReports()
    {
        string escapedPath = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest;
            $tokens=$null;$errors=$null;
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedPath}}',[ref]$tokens,[ref]$errors);
            if($errors.Count){exit 20};
            foreach($name in @('Test-PathEqual','Assert-ReportRoleBinding')){
                $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                if($node.Count -ne 1){exit 21};
                Invoke-Expression $node[0].Extent.Text;
            }
            function New-Artifact([string]$fileName){
                return [ordered]@{fileName=$fileName;path=('C:\synthetic\'+$fileName);sha256='ABCDEF'};
            }
            $expected=[ordered]@{
                executable=(New-Artifact 'MarkdownRenderer.PerformanceHarness.exe');
                runtimeConfig=(New-Artifact 'MarkdownRenderer.PerformanceHarness.runtimeconfig.json');
                performanceHarnessDll=(New-Artifact 'MarkdownRenderer.PerformanceHarness.dll');
                markdownRendererDll=(New-Artifact 'MarkdownRenderer.dll');
                markdownRendererCoreDll=(New-Artifact 'MarkdownRenderer.Core.dll')
            };
            $expectedIdentity='runtime-output-manifest-v1:' + ('A' * 64);
            function New-Report([int]$schema,[bool]$passed,[string]$identity){
                return [pscustomobject]@{
                    schemaVersion=$schema;providerName='MarkdownRenderer-Performance';
                    isReleaseEvidence=$true;passed=$passed;buildIdentity=$identity;
                    regression=[pscustomobject]@{mode='baseline';referencePath='';referenceReportSha256=''};
                    buildArtifacts=[pscustomobject]@{
                        executable=(New-Artifact 'MarkdownRenderer.PerformanceHarness.exe');
                        runtimeConfig=(New-Artifact 'MarkdownRenderer.PerformanceHarness.runtimeconfig.json');
                        performanceHarnessDll=(New-Artifact 'MarkdownRenderer.PerformanceHarness.dll');
                        markdownRendererDll=(New-Artifact 'MarkdownRenderer.dll');
                        markdownRendererCoreDll=(New-Artifact 'MarkdownRenderer.Core.dll')
                    }
                };
            }
            Assert-ReportRoleBinding -Report (New-Report 10 $true $expectedIdentity) -Mode Baseline -ExpectedArtifactSet $expected -ExpectedBuildIdentity $expectedIdentity;
            $rejected=0;
            foreach($invalid in @(
                (New-Report 9 $true $expectedIdentity),
                (New-Report 10 $false $expectedIdentity),
                (New-Report 10 $true ('runtime-output-manifest-v1:' + ('B' * 64))),
                [pscustomobject]@{
                    schemaVersion=10;providerName='MarkdownRenderer-Performance';
                    isReleaseEvidence=$true;passed=$true;buildIdentity=$expectedIdentity;
                    regression=[pscustomobject]@{mode='baseline';referencePath='';referenceReportSha256=''};
                    buildArtifacts=[pscustomobject]@{
                    }
                }
            )){
                try {
                    Assert-ReportRoleBinding -Report $invalid -Mode Baseline -ExpectedArtifactSet $expected -ExpectedBuildIdentity $expectedIdentity;
                }
                catch {
                    $rejected++;
                }
            }
            if($rejected -ne 4){exit 22};
            exit 0
            """;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static string FindOrchestrator()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "eng",
                "Invoke-MarkdownWinUICounterbalancedPerformance.ps1");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "eng",
                "Invoke-MarkdownWinUICounterbalancedPerformance.ps1");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException(
            "Could not locate Invoke-MarkdownWinUICounterbalancedPerformance.ps1 from the test output.");
    }

    private static string GetCurrentExecutable()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Windows test host executable could not be resolved.");
        }

        return Path.GetFullPath(path);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static string CreateSyntheticBuild(
        string directory,
        string managedMarker,
        string? runtimeConfigMarker = null)
    {
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "MarkdownRenderer.PerformanceHarness.exe");
        File.Copy(GetCurrentExecutable(), executable);
        File.WriteAllText(
            Path.Combine(directory, "MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
            runtimeConfigMarker ?? managedMarker,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directory, "MarkdownRenderer.PerformanceHarness.dll"),
            managedMarker,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directory, "MarkdownRenderer.dll"),
            managedMarker,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directory, "MarkdownRenderer.Core.dll"),
            managedMarker,
            Encoding.UTF8);
        return executable;
    }

    private static string ComputeExpectedRuntimeOutputIdentity(string executablePath)
    {
        const string Policy = "runtime-output-manifest-v1";
        string directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new InvalidOperationException("Synthetic executable has no directory.");
        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(static path =>
                !string.Equals(Path.GetExtension(path), ".pdb", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                FullPath = Path.GetFullPath(path),
                RelativePath = Path.GetRelativePath(directory, path).Replace('\\', '/'),
            })
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var lines = new List<string>(files.Length + 1) { Policy };
        foreach (var file in files)
        {
            using FileStream stream = File.OpenRead(file.FullPath);
            string sha256 = Convert.ToHexString(SHA256.HashData(stream));
            lines.Add(string.Concat(
                file.RelativePath,
                "|",
                stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "|",
                sha256));
        }

        byte[] payload = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        return string.Concat(Policy, ":", Convert.ToHexString(SHA256.HashData(payload)));
    }

    private static string GetRuntimeOutputIdentityFromPowerShell(string executablePath)
    {
        string escapedScript = FindOrchestrator().Replace("'", "''", StringComparison.Ordinal);
        string escapedExecutable = executablePath.Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest;
            $tokens=$null;$errors=$null;
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{escapedScript}}',[ref]$tokens,[ref]$errors);
            if($errors.Count){exit 60};
            foreach($name in @('Get-Sha256','Get-RuntimeOutputManifest')){
                $node=@($ast.FindAll({param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $name},$true));
                if($node.Count -ne 1){exit 61};
                Invoke-Expression $node[0].Extent.Text;
            }
            $script:Utf8NoBom=[Text.UTF8Encoding]::new($false,$true);
            $script:RuntimeOutputManifestPolicy='runtime-output-manifest-v1';
            $manifest=Get-RuntimeOutputManifest -ExecutablePath '{{escapedExecutable}}' -Label 'Synthetic build';
            [Console]::Out.Write($manifest.identity);
            exit 0
            """;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        ProcessResult result = RunPowerShell("-EncodedCommand", encoded);

        Assert.True(result.ExitCode == 0, result.Output);
        return result.Output.Trim();
    }

    private static ProcessResult RunPowerShell(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("PowerShell contract check exceeded 30 seconds.");
        }

        string output = standardOutput.GetAwaiter().GetResult() +
            standardError.GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, output);
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}
