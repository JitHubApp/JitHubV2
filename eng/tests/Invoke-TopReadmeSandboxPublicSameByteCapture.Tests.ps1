$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'eng\Invoke-TopReadmeSandboxPublicSameByteCapture.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Public Sandbox capture script is missing: '$scriptPath'."
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Public Sandbox capture script did not parse: $($parseErrors[0].Message) at line $($parseErrors[0].Extent.StartLineNumber)."
}

foreach ($functionName in @('Get-PinnedManifest', 'Get-OrdinaryFiles', 'New-CaptureSnapshot', 'Get-AndAssertSandboxSnapshot', 'Initialize-SandboxWorkRoot', 'ConvertTo-WindowsProcessArgument', 'Invoke-CapturedProcess', 'New-GuestBatchScript', 'Test-CapturedShard')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $function) { throw "Required public capture function '$functionName' is missing." }
}

$scriptText = Get-Content -LiteralPath $scriptPath -Raw
foreach ($required in @(
    'target snapshot sandbox --json',
    "'target', 'push', 'sandbox'",
    "'target', 'exec', 'sandbox'",
    "'target', 'pull', 'sandbox'",
    "--probe=readme-public-same-byte-capture",
    "--capture-same-byte-corpus",
    "overallReleaseQualification = 'not-qualified'",
    "authenticatedLiveAuditEquivalent = `$false",
    "liveTop500RankingFreshness = 'not-checked'",
    "nativeJitHubRenderComparison = 'not-run'",
    "sameByteEdgeFullPageReplay = 'not-run'",
    'nodeVersion = $nodeVersion',
    'Staged Node failed the expected-version probe before any repository capture.',
    "No host browser or UI automation were performed",
    "no host fallback was attempted")) {
    if (-not $scriptText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Public Sandbox capture script is missing required isolation/scope contract '$required'."
    }
}

$guestBatch = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuestBatchScript' }, $true)
$guestBatchText = $guestBatch.Extent.Text
$functionDefinitions = foreach ($functionName in @('ConvertTo-WindowsProcessArgument', 'Invoke-CapturedProcess')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    $function.Extent.Text
}
Invoke-Expression ($functionDefinitions -join "`n")
$nodeProbeIndex = $guestBatchText.IndexOf('nodeVersionExitCode', [StringComparison]::Ordinal)
$runnerStartIndex = $guestBatchText.IndexOf('Invoke-CapturedProcess -FilePath `$runner', [StringComparison]::Ordinal)
if ($nodeProbeIndex -lt 0 -or $runnerStartIndex -lt 0 -or $nodeProbeIndex -ge $runnerStartIndex) {
    throw 'The staged Node executable must pass its pinned version check before the first browser-capture runner invocation.'
}
$sandbox = [pscustomobject]@{ workRoot = 'C:\WinApp\work' }
$EdgeExecutablePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$script:GitHubCredentialEnvironmentNames = @('GITHUB_TOKEN')
Invoke-Expression $guestBatchText
$generatedGuestBatch = New-GuestBatchScript -GuestRepo 'readme-public-same-byte\test\repo' -GuestOutput 'artifacts\out' -FirstRank 1 -Amount 1 -ExpectedNodeVersion 'v22.22.0'
$generatedProbeIndex = $generatedGuestBatch.IndexOf("`$nodeVersionOutput", [StringComparison]::Ordinal)
$generatedRunnerIndex = $generatedGuestBatch.IndexOf('Invoke-CapturedProcess -FilePath $runner', [StringComparison]::Ordinal)
if ($generatedProbeIndex -lt 0 -or $generatedRunnerIndex -lt 0 -or $generatedProbeIndex -ge $generatedRunnerIndex -or
    -not $generatedGuestBatch.Contains("-cne 'v22.22.0'", [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains("'runner-output.log'", [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains("'runner-error.log'", [StringComparison]::Ordinal) -or
    $generatedGuestBatch.Contains('& $runner @runnerArgs', [StringComparison]::Ordinal)) {
    throw 'Generated guest capture script must validate staged Node, keep runner stdout/stderr separate, and avoid PowerShell native stderr redirection.'
}
$guestTokens = $null
$guestParseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($generatedGuestBatch, [ref]$guestTokens, [ref]$guestParseErrors)
if ($guestParseErrors.Count -gt 0) {
    throw "Generated guest process launcher did not parse: $($guestParseErrors[0].Message) at line $($guestParseErrors[0].Extent.StartLineNumber)."
}

# Exercise the exact ProcessStartInfo launcher from Windows PowerShell 5.1 with a child that writes stderr,
# continues writing stdout, persists its report, and exits nonzero. This pins the old abort-after-stderr failure.
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for the guest stderr regression.' }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('readme-capture-stderr-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
try {
    $fakeRunner = Join-Path $tempRoot 'fake runner.ps1'
    $reportPath = Join-Path $tempRoot 'capture report.json'
    $stdoutPath = Join-Path $tempRoot 'runner-output.log'
    $stderrPath = Join-Path $tempRoot 'runner-error.log'
    $fakeRunnerSource = @'
param([string]$ReportPath, [string]$Marker)
$ErrorActionPreference = 'Stop'
[Console]::Out.WriteLine('stdout-before')
[Console]::Error.WriteLine('stderr-record')
[IO.File]::WriteAllText($ReportPath, '{"completed":true}')
[Console]::Out.WriteLine("stdout-after:$Marker")
exit 19
'@
    [IO.File]::WriteAllText($fakeRunner, $fakeRunnerSource, (New-Object System.Text.UTF8Encoding($false)))
    $exitCode = Invoke-CapturedProcess -FilePath $windowsPowerShell -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $fakeRunner, '-ReportPath', $reportPath, '-Marker', 'marker with spaces') -WorkingDirectory $tempRoot -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath
    $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    $stderr = Get-Content -LiteralPath $stderrPath -Raw
    if ($exitCode -ne 19 -or
        -not $stdout.Contains('stdout-before', [StringComparison]::Ordinal) -or
        -not $stdout.Contains('stdout-after:marker with spaces', [StringComparison]::Ordinal) -or
        -not $stderr.Contains('stderr-record', [StringComparison]::Ordinal) -or
        -not (Test-Path -LiteralPath $reportPath -PathType Leaf) -or
        (Get-Content -LiteralPath $reportPath -Raw) -cne '{"completed":true}') {
        throw "Captured PS5 runner did not preserve complete output/report/exit status (exit=$exitCode)."
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
foreach ($forbidden in @('target create', 'target connect', 'FlaUI', '--app=')) {
    if ($scriptText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Public browser-only capture must not use '$forbidden'."
    }
}

$initializer = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-SandboxWorkRoot' }, $true)
$initializerText = $initializer.Extent.Text
foreach ($required in @('capabilities.managedRoot', "'work'", '[IO.Directory]::CreateDirectory', '[IO.FileAttributes]::ReparsePoint')) {
    if (-not $initializerText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Sandbox work-root initializer is missing the bounded directory invariant '$required'."
    }
}
$initializerExec = [regex]::Match($initializerText, "Invoke-WinApp -Arguments @\('target', 'exec', 'sandbox', '--', 'powershell\.exe'[^\r\n]*\) -Description 'initialize expected Sandbox work root'")
if (-not $initializerExec.Success -or $initializerExec.Value.Contains('--cwd', [StringComparison]::Ordinal)) {
    throw 'Fresh Sandbox work-root initialization must run without --cwd, because that directory may not exist yet.'
}
$initializeCall = $scriptText.IndexOf('Initialize-SandboxWorkRoot -Snapshot $snapshotNow', [StringComparison]::Ordinal)
$uniquePreflightCall = $scriptText.IndexOf("-Description 'unique Sandbox staging path preflight'", [StringComparison]::Ordinal)
if ($initializeCall -lt 0 -or $uniquePreflightCall -lt 0 -or $initializeCall -ge $uniquePreflightCall) {
    throw 'The expected Sandbox work root must be initialized before the first --cwd-based staging preflight.'
}

# Invalid rank bounds must fail before reading/building inputs or contacting the Sandbox.
$invalidRangeRejected = $false
try {
    & $scriptPath -AutomationRunnerDirectory 'C:\missing-runner' -NodeExecutablePath 'C:\missing-node.exe' -StartRank 500 -Count 2
}
catch {
    $invalidRangeRejected = $_.Exception.Message.Contains('within 1..500', [StringComparison]::Ordinal)
    if (-not $invalidRangeRejected) { throw }
}
if (-not $invalidRangeRejected) { throw 'Public capture accepted a rank range beyond the pinned top 500.' }

Write-Host 'PASS: public same-byte capture stays inside the existing Sandbox, strips credentials, validates pinned identities, and remains explicitly unqualified.'
