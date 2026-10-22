[CmdletBinding()]
param(
    # Phase 3 correctness audit of the frozen top-500 README corpus in the
    # existing Windows Sandbox. Inputs are browser-free schema-3
    # correctness-input-v1 folders; see
    # docs/markdown-renderer/phase3-correctness-input-design.md.
    [ValidateRange(1, 500)]
    [int]$StartRank = 1,

    [ValidateRange(1, 500)]
    [int]$Count = 500,

    [ValidateRange(1, 50)]
    [int]$ShardSize = 25,

    [Parameter(Mandatory = $true)]
    [string]$AppDirectory,

    [Parameter(Mandatory = $true)]
    [string]$AutomationRunnerDirectory,

    [Parameter(Mandatory = $true)]
    [string]$InputRoot,

    [Parameter(Mandatory = $true)]
    [string]$SourceAcquisitionRoot,

    [string]$ManifestPath = '',

    [string]$OutputRoot = '',

    [string]$WinAppPath = 'winapp',

    # The operator attests that this Sandbox was started with host clipboard
    # redirection disabled. Without it, plainTextCopy stays NotEvaluated.
    [switch]$ClipboardRedirectionDisabled,

    [switch]$Run
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$FrozenManifestSha256 = '615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72'
if ($StartRank + $Count - 1 -gt 500) { throw 'The correctness audit rank range must stay within 1..500.' }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'docs\markdown-renderer\evidence\2026-10-17\top-repositories.json'
}
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$AutomationRunnerDirectory = [IO.Path]::GetFullPath($AutomationRunnerDirectory)
$InputRoot = [IO.Path]::GetFullPath($InputRoot)
$SourceAcquisitionRoot = [IO.Path]::GetFullPath($SourceAcquisitionRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot ('artifacts\readme-correctness-sandbox\run-' +
        [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

# Bounds and exclusions the shared staging helpers read from script scope.
$script:MaximumSourceFileBytes = 256MB
$script:MaximumSourceSnapshotBytes = 4GB
$script:MaximumAppPayloadBytes = 2GB
$script:MaximumRunnerPayloadBytes = 2GB
$script:MaximumCorpusFileBytes = 64MB
$script:ExcludedDirectoryNames = @(
    '.git', '.vs', '.codex', '.idea', 'bin', 'obj', 'artifacts',
    'node_modules', 'target', 'TestResults', 'packages', '.nuget')
$script:SensitiveExtensions = @('.pfx', '.p12', '.pem', '.key', '.env', '.netrc')
$script:CredentialFileNames = @(
    '.npmrc', '.git-credentials', 'secrets.json', 'credentials.json',
    'id_rsa', 'id_ed25519', 'known_hosts')

# Reuse the offline replay's proven Sandbox staging, Windows App Runtime and
# guest-exec helpers by loading only their function definitions. The replay
# script's top-level code never runs here.
$sharedFunctionNames = @(
    'Invoke-NativeAotArtifactVerifier', 'Add-BoundedByteCount', 'Assert-OrdinaryPath',
    'Get-OrdinaryTreeFiles', 'Test-SafeCredentialFilename', 'Copy-VerifiedFile',
    'Get-PeMachine', 'Get-SourceCommitSha', 'Invoke-WinApp', 'Initialize-SandboxWorkRoot',
    # Assert-PlainGuestPath and Get-CurrentUserWindowsAppRuntimeInventory are
    # guest script text inside the preflight script, not host functions.
    'New-GuestWindowsAppRuntimePreflightScript', 'Invoke-SandboxWindowsAppRuntimePreflight',
    'Get-AndAssertSandboxSnapshot', 'New-GuestBatchExecArguments',
    'Test-SourceFileIncluded', 'Add-SourceTreeToSnapshot',
    'ConvertTo-GuestPowerShellSingleQuotedLiteral')
$templatePath = Join-Path $PSScriptRoot 'Invoke-TopReadmeSandboxOfflineSameByteReplay.ps1'
$tokens = $null
$parseErrors = $null
$templateAst = [System.Management.Automation.Language.Parser]::ParseFile($templatePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw "The shared Sandbox helper source does not parse: $templatePath" }
foreach ($name in $sharedFunctionNames) {
    $definition = @($templateAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false))
    if ($definition.Count -ne 1) { throw "Shared Sandbox helper '$name' is missing or ambiguous in $templatePath." }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}

function Get-CaseId {
    param([Parameter(Mandatory = $true)]$Repository)
    # Matches the probe's Sanitize: every non letter/digit becomes '-'.
    $safeName = -join ([string]$Repository.fullName).ToCharArray().ForEach({ if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' } })
    return ('{0:D3}-{1}' -f [int]$Repository.rank, $safeName)
}

function Get-FrozenRepositories {
    $path = Assert-OrdinaryPath -Path $ManifestPath -Description 'Frozen correctness manifest'
    $bytes = [IO.File]::ReadAllBytes($path)
    $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if ($sha256 -cne $FrozenManifestSha256) {
        throw "The correctness audit requires the frozen September 28 manifest ($FrozenManifestSha256); received $sha256."
    }
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 32
    $ordered = @($manifest.repositories | Sort-Object { [int]$_.rank })
    if ($ordered.Count -ne 500) { throw 'The frozen manifest must contain exactly 500 repositories.' }
    return [pscustomobject]@{ Bytes = $bytes; Sha256 = $sha256; Repositories = $ordered }
}

function Get-InputPlan {
    param([Parameter(Mandatory = $true)]$Frozen)
    $selected = @($Frozen.Repositories | Where-Object { [int]$_.rank -ge $StartRank -and [int]$_.rank -lt $StartRank + $Count })
    $plan = [Collections.Generic.List[object]]::new()
    foreach ($repository in $selected) {
        $caseId = Get-CaseId -Repository $repository
        $folder = Join-Path $InputRoot $caseId
        $manifestFile = Join-Path $folder 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
            throw "Correctness input for rank $($repository.rank) is missing: $folder"
        }
        $input = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json -Depth 32
        if ([int]$input.schemaVersion -ne 3 -or [string]$input.kind -cne 'correctness-input-v1' -or
            [string]$input.repository.fullName -cne [string]$repository.fullName -or
            [string]$input.repository.commitSha -ine [string]$repository.commitSha -or
            [string]$input.repository.corpusManifestSha256 -cne $FrozenManifestSha256 -or
            $null -eq $input.inventory) {
            throw "Correctness input for rank $($repository.rank) is not a bound schema-3 input with an inventory."
        }
        $bytes = (Get-ChildItem -LiteralPath $folder -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $plan.Add([pscustomobject]@{ Repository = $repository; CaseId = $caseId; Folder = $folder; Bytes = [long]$bytes })
    }
    return $plan
}

function New-Shards {
    param([Parameter(Mandatory = $true)]$Plan)
    $shards = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Plan.Count; $index += $ShardSize) {
        $items = @($Plan[$index..([Math]::Min($index + $ShardSize, $Plan.Count) - 1)])
        $shards.Add([pscustomobject]@{
            Items = $items
            First = [int]$items[0].Repository.rank
            Last = [int]$items[-1].Repository.rank
            Bytes = [long](($items | Measure-Object -Property Bytes -Sum).Sum)
        })
    }
    return $shards
}

function Add-TreeToSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][string]$RelativePrefix,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)]$FileManifest,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $total = 0L
    foreach ($file in (Get-OrdinaryTreeFiles -Root $Source)) {
        $file.Refresh()
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $record = Copy-VerifiedFile -Source $file.FullName -Destination (Join-Path $DestinationRoot $relative) -MaximumBytes $MaximumBytes -Description $Description
        $record.relativePath = $RelativePrefix + $relative.Replace('\', '/')
        $FileManifest.Add($record)
        $total = Add-BoundedByteCount -Current $total -Additional ([long]$record.byteSize) -Maximum $MaximumBytes -Description $Description
    }
    return $total
}

function New-StagingSnapshot {
    param([Parameter(Mandatory = $true)][string]$RunId, [Parameter(Mandatory = $true)]$Frozen)
    $root = Join-Path $OutputRoot ('staging\' + $RunId + '\repo')
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $files = [Collections.Generic.List[object]]::new()
    # The guest Automation runner proves the app is fresh against its
    # repository: app content and the committed resvg worker are compared
    # with the source tree. Stage the same filtered source snapshot as the
    # proven offline replay (eng\readme-audit includes the runtime installer).
    $repoSourceBytes = 0L
    foreach ($rootName in @('JitHub.WinUI', 'MarkdownRenderer', 'JitHub.WinUI.Automation', 'eng\readme-audit')) {
        $sourceRoot = Join-Path $repoRoot $rootName
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "Required source root is missing: '$sourceRoot'." }
        Add-SourceTreeToSnapshot -RootName $rootName -SourceRoot $sourceRoot -SnapshotRoot $root -FileManifest $files -TotalBytes ([ref]$repoSourceBytes)
    }
    foreach ($rootFile in @('JitHub.slnx', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json')) {
        $path = Join-Path $repoRoot $rootFile
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $record = Copy-VerifiedFile -Source $path -Destination (Join-Path $root $rootFile) -MaximumBytes 16MB -Description 'Repository build identity file'
            $record.relativePath = $rootFile
            $files.Add($record)
        }
    }
    $manifestDestination = Join-Path $root 'input\top-repositories.json'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestDestination)) | Out-Null
    [IO.File]::WriteAllBytes($manifestDestination, $Frozen.Bytes)

    $appBytes = Add-TreeToSnapshot -Source $AppDirectory -DestinationRoot (Join-Path $root 'app') -RelativePrefix 'app/' `
        -MaximumBytes $script:MaximumAppPayloadBytes -FileManifest $files -Description 'JitHub app payload'
    $runnerBytes = Add-TreeToSnapshot -Source $AutomationRunnerDirectory -DestinationRoot (Join-Path $root 'tools\automation') -RelativePrefix 'tools/automation/' `
        -MaximumBytes $script:MaximumRunnerPayloadBytes -FileManifest $files -Description 'Self-contained Automation runner'
    $sourceBytes = Add-TreeToSnapshot -Source $SourceAcquisitionRoot -DestinationRoot (Join-Path $root 'input\sources') -RelativePrefix 'input/sources/' `
        -MaximumBytes 1GB -FileManifest $files -Description 'Verified frozen README source store'

    $runnerExe = Join-Path $root 'tools\automation\JitHub.WinUI.Automation.exe'
    foreach ($required in @($runnerExe, (Join-Path $root 'tools\automation\coreclr.dll'), (Join-Path $root 'app\JitHub.WinUI.exe'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Staging is incomplete; missing '$required'. Publish the Automation runner self-contained for win-x64 and the app for x64."
        }
    }
    if ((Get-PeMachine -Path $runnerExe) -ne 0x8664) { throw 'The Automation runner apphost is not x64.' }

    $manifest = [ordered]@{
        schemaVersion = 1
        scope = 'readme-correctness-sandbox-staging'
        sourceCommitSha = Get-SourceCommitSha
        appSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'app\JitHub.WinUI.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        automationRunnerSha256 = (Get-FileHash -LiteralPath $runnerExe -Algorithm SHA256).Hash.ToLowerInvariant()
        frozenManifestSha256 = $Frozen.Sha256
        appBytes = $appBytes
        runnerBytes = $runnerBytes
        sourceStoreBytes = $sourceBytes
        repositorySourceBytes = $repoSourceBytes
        files = @($files | Sort-Object relativePath)
    }
    $manifestPath = Join-Path $OutputRoot ('staging-' + $RunId + '.json')
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ Root = $root; Manifest = $manifest; ManifestPath = $manifestPath }
}

function Copy-ShardInputs {
    param([Parameter(Mandatory = $true)]$Shard, [Parameter(Mandatory = $true)][string]$RunId)
    $root = Join-Path $OutputRoot ('staging\' + $RunId + ('\inputs-{0:D3}-{1:D3}' -f $Shard.First, $Shard.Last))
    foreach ($item in $Shard.Items) {
        $destination = Join-Path $root $item.CaseId
        foreach ($file in (Get-OrdinaryTreeFiles -Root $item.Folder)) {
            $relative = [IO.Path]::GetRelativePath($item.Folder, $file.FullName)
            $null = Copy-VerifiedFile -Source $file.FullName -Destination (Join-Path $destination $relative) -MaximumBytes $script:MaximumCorpusFileBytes -Description 'Correctness input file'
        }
    }
    return $root
}

function New-GuestCorrectnessScript {
    param(
        [Parameter(Mandatory = $true)][string]$GuestRepo,
        [Parameter(Mandatory = $true)][string]$InputRelative,
        [Parameter(Mandatory = $true)][string]$OutputRelative,
        [Parameter(Mandatory = $true)][int]$First,
        [Parameter(Mandatory = $true)][int]$Amount
    )
    $input = Join-Path $GuestRepo $InputRelative
    $output = Join-Path $GuestRepo $OutputRelative
    $runner = Join-Path $GuestRepo 'tools\automation\JitHub.WinUI.Automation.exe'
    $app = Join-Path $GuestRepo 'app\JitHub.WinUI.exe'
    $manifest = Join-Path $GuestRepo 'input\top-repositories.json'
    $sources = Join-Path $GuestRepo 'input\sources'
    $arguments = @(
        '--probe=readme-correctness-corpus',
        "--app=$app",
        "--out=$output",
        "--manifest=$manifest",
        "--source-acquisition-root=$sources",
        "--offline-same-byte-corpus-root=$input",
        "--start-rank=$First",
        "--count=$Amount")
    $argumentLiteral = ($arguments | ForEach-Object { ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $_ }) -join ', '
    $clipboard = if ($ClipboardRedirectionDisabled) { '1' } else { '' }
    return @"
`$ErrorActionPreference = 'Stop'
`$runner = $(ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $runner)
foreach (`$path in @(`$runner, $(ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $app), $(ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $manifest))) {
    if (-not (Test-Path -LiteralPath `$path -PathType Leaf)) { throw "Required staged file is missing: `$path" }
}
`$env:JITHUB_README_CORRECTNESS_SANDBOX = '1'
`$env:JITHUB_README_CORRECTNESS_CLIPBOARD_ISOLATED = '$clipboard'
[IO.Directory]::CreateDirectory($(ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $output)) | Out-Null
& `$runner @($argumentLiteral) *> $(ConvertTo-GuestPowerShellSingleQuotedLiteral -Value (Join-Path $output 'runner.log'))
exit `$LASTEXITCODE
"@
}

$frozen = Get-FrozenRepositories
$plan = Get-InputPlan -Frozen $frozen
$shards = @(New-Shards -Plan $plan)
$planSummary = [ordered]@{
    scope = 'frozen-top500-readme-correctness-in-existing-windows-sandbox'
    executionMode = if ($Run) { 'run-requested' } else { 'plan-only-no-sandbox-files-changed' }
    frozenManifestSha256 = $frozen.Sha256
    inputRoot = $InputRoot
    sourceAcquisitionRoot = $SourceAcquisitionRoot
    appDirectory = $AppDirectory
    automationRunnerDirectory = $AutomationRunnerDirectory
    clipboardRedirectionDisabledAttested = [bool]$ClipboardRedirectionDisabled
    startRank = $StartRank
    count = $Count
    shardCount = $shards.Count
    shards = @($shards | ForEach-Object { [ordered]@{ first = $_.First; last = $_.Last; inputBytes = $_.Bytes } })
}
Write-Host ($planSummary | ConvertTo-Json -Depth 6)
if (-not $Run) {
    Write-Host 'Plan only: no Sandbox push, exec or pull was performed. Re-run with -Run after reviewing the inputs.'
    return
}

$OutputRoot = Assert-OrdinaryPath -Path $OutputRoot -Description 'Correctness audit output root'
if (Test-Path -LiteralPath (Join-Path $OutputRoot 'readme-correctness-sandbox-summary.json')) {
    throw 'Refusing to overwrite prior correctness evidence; choose a fresh OutputRoot.'
}
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$snapshot = Get-AndAssertSandboxSnapshot
$guestEpoch = [string]$snapshot.executionTarget.epoch
$runId = [Guid]::NewGuid().ToString('N')
$staging = New-StagingSnapshot -RunId $runId -Frozen $frozen
$guestRepoRelative = 'readme-correctness/' + $runId
$guestRepoAbsolute = Join-Path ([string]$snapshot.workRoot) ($guestRepoRelative -replace '/', '\')
$hostEvidenceRoot = Join-Path $OutputRoot ('evidence-' + $runId)
[IO.Directory]::CreateDirectory($hostEvidenceRoot) | Out-Null
$shardResults = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()

Initialize-SandboxWorkRoot -Snapshot $snapshot
Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $staging.Root, $guestRepoRelative) -Description 'stage app, runner, frozen manifest and source store'
$runtime = Invoke-SandboxWindowsAppRuntimePreflight -Snapshot $snapshot -GuestRepoAbsolute $guestRepoAbsolute `
    -GuestRepoRelative $guestRepoRelative -SnapshotData $staging -HostEvidenceRoot $hostEvidenceRoot -GuestEpoch $guestEpoch
if (-not $runtime.Success) { throw "Windows App Runtime prerequisite did not pass: $($runtime.Failure)" }

foreach ($shard in $shards) {
    $current = Get-AndAssertSandboxSnapshot
    if ([string]$current.executionTarget.epoch -cne $guestEpoch) { throw 'The Sandbox epoch changed during the audit; refusing to continue in a different guest.' }
    $range = '{0:D3}-{1:D3}' -f $shard.First, $shard.Last
    $hostInputs = Copy-ShardInputs -Shard $shard -RunId $runId
    $inputRelative = "inputs\ranks-$range"
    $outputRelative = "output\ranks-$range"
    Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $hostInputs, ($guestRepoRelative + '/inputs/ranks-' + $range)) -Description "stage correctness inputs $range"
    $scriptName = "correctness-ranks-$range.ps1"
    $hostScript = Join-Path $hostEvidenceRoot $scriptName
    [IO.File]::WriteAllText($hostScript, (New-GuestCorrectnessScript -GuestRepo $guestRepoAbsolute -InputRelative $inputRelative `
        -OutputRelative $outputRelative -First $shard.First -Amount ($shard.Last - $shard.First + 1)), [Text.UTF8Encoding]::new($false))
    Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $hostScript, ($guestRepoRelative + '/' + $scriptName)) -Description "stage guest script $range"
    $execFailure = $null
    try {
        Invoke-WinApp -Arguments (New-GuestBatchExecArguments -GuestCwd $guestRepoAbsolute -GuestScriptPath (Join-Path $guestRepoAbsolute $scriptName)) `
            -Description "correctness audit ranks $range"
    }
    catch { $execFailure = $_.Exception.Message }
    $hostOutput = Join-Path $hostEvidenceRoot "ranks-$range"
    $pullFailure = $null
    try { Invoke-WinApp -Arguments @('target', 'pull', 'sandbox', ($guestRepoRelative + '/output/ranks-' + $range), $hostOutput) -Description "pull results $range" }
    catch { $pullFailure = $_.Exception.Message }
    $summaryFile = @(Get-ChildItem -LiteralPath $hostOutput -Recurse -Filter 'correctness-only-summary.json' -ErrorAction SilentlyContinue | Select-Object -First 1)
    $summary = if ($summaryFile.Count -eq 1) { Get-Content -LiteralPath $summaryFile[0].FullName -Raw | ConvertFrom-Json -Depth 32 } else { $null }
    if ($null -eq $summary) { $failures.Add("Ranks $range delivered no correctness summary. exec=$execFailure pull=$pullFailure") }
    # Measure-Object cannot read [ordered] dictionary keys as properties.
    $shardResults.Add([pscustomobject][ordered]@{
        first = $shard.First
        last = $shard.Last
        execFailure = $execFailure
        pullFailure = $pullFailure
        passed = if ($summary) { [int]$summary.passedCaseCount } else { 0 }
        classified = if ($summary) { [int]$summary.classifiedCaseCount } else { 0 }
        failed = if ($summary) { [int]$summary.failedCaseCount } else { 0 }
        incomplete = if ($summary) { [int]$summary.incompleteCaseCount } else { $shard.Items.Count }
        summaryPath = if ($summaryFile.Count -eq 1) { $summaryFile[0].FullName } else { $null }
    })
    Write-Host ("Ranks {0}: passed={1} classified={2} failed={3} incomplete={4}" -f $range, $shardResults[-1].passed, $shardResults[-1].classified, $shardResults[-1].failed, $shardResults[-1].incomplete)
}

$totals = [ordered]@{
    passed = ($shardResults | Measure-Object -Property passed -Sum).Sum
    classified = ($shardResults | Measure-Object -Property classified -Sum).Sum
    failed = ($shardResults | Measure-Object -Property failed -Sum).Sum
    incomplete = ($shardResults | Measure-Object -Property incomplete -Sum).Sum
}
$fullTop500 = $StartRank -eq 1 -and $Count -eq 500
$report = [ordered]@{
    schemaVersion = 1
    scope = 'frozen-top500-readme-correctness-in-existing-windows-sandbox'
    sandboxEpoch = $guestEpoch
    stagingManifest = $staging.ManifestPath
    frozenManifestSha256 = $frozen.Sha256
    clipboardRedirectionDisabledAttested = [bool]$ClipboardRedirectionDisabled
    requestedStartRank = $StartRank
    requestedCount = $Count
    totals = $totals
    phaseStatus = if ($fullTop500 -and $failures.Count -eq 0 -and $totals.failed -eq 0 -and $totals.incomplete -eq 0 -and
        $totals.passed + $totals.classified -eq 500) { 'complete' } else { 'incomplete' }
    failures = @($failures)
    shards = @($shardResults)
    completedAtUtc = [DateTimeOffset]::UtcNow
}
$reportPath = Join-Path $OutputRoot 'readme-correctness-sandbox-summary.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Correctness summary: $reportPath"
Write-Host ("Totals: passed={0} classified={1} failed={2} incomplete={3}; phase {4}" -f $totals.passed, $totals.classified, $totals.failed, $totals.incomplete, $report.phaseStatus)
if ($report.phaseStatus -ne 'complete') { exit 1 }
