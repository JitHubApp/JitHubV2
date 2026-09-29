param(
    [Parameter(Mandatory)][string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
$requiredArchitectures = @('x86', 'x64', 'arm64')
$requiredDpiScales = @(1.0, 1.5, 2.0)
$requiredScreenshots = @(
    'long-press-selection.png',
    'selection-dark.png',
    'selection-high-contrast-200-text.png',
    'selection-rtl.png'
)

$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$runs = [System.Collections.Generic.List[object]]::new()
$coverageErrors = [System.Collections.Generic.List[string]]::new()
foreach ($environmentFile in Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'environment.json') {
    $directory = $environmentFile.Directory.FullName
    $resultsPath = Join-Path $directory 'results.json'
    if (-not (Test-Path -LiteralPath $resultsPath)) {
        throw "Touch-selection evidence at '$directory' has no results.json."
    }

    $environment = Get-Content -Raw -LiteralPath $environmentFile.FullName | ConvertFrom-Json
    $results = @(Get-Content -Raw -LiteralPath $resultsPath | ConvertFrom-Json)
    $failures = @($results | Where-Object status -ne 'PASS')
    if ($failures.Count -gt 0) {
        $names = ($failures | ForEach-Object name) -join ', '
        throw "Touch-selection evidence at '$directory' contains failures: $names"
    }

    foreach ($screenshot in $requiredScreenshots) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $screenshot))) {
            throw "Touch-selection evidence at '$directory' is missing '$screenshot'."
        }
    }

    if ($environment.systemHighContrast) {
        $systemContrastScreenshot = @(Get-ChildItem -LiteralPath $directory -File -Filter 'selection-system-high-contrast-*.png')
        if ($systemContrastScreenshot.Count -eq 0) {
            throw "System High Contrast run '$directory' has no system-contrast screenshot."
        }
    }

    $runs.Add([pscustomobject]@{
        directory = $directory
        architecture = [string]$environment.architecture
        dpiScale = [double]$environment.dpiScale
        systemHighContrast = [bool]$environment.systemHighContrast
    })
}

if ($runs.Count -eq 0) {
    throw "No touch-selection evidence was found under '$root'."
}

foreach ($architecture in $requiredArchitectures) {
    if (-not @($runs | Where-Object architecture -eq $architecture).Count) {
        $coverageErrors.Add("Missing touch-selection evidence for process architecture '$architecture'.")
    }
}

foreach ($scale in $requiredDpiScales) {
    $atScale = @($runs | Where-Object { [Math]::Abs($_.dpiScale - $scale) -le 0.01 })
    if ($atScale.Count -eq 0) {
        $coverageErrors.Add("Missing touch-selection evidence at $scale display scale.")
        continue
    }
    if (-not @($atScale | Where-Object systemHighContrast).Count) {
        $coverageErrors.Add("Missing Windows system High Contrast touch evidence at $scale display scale.")
    }
    if (-not @($atScale | Where-Object { -not $_.systemHighContrast }).Count) {
        $coverageErrors.Add("Missing normal-contrast touch evidence at $scale display scale.")
    }
}

if ($coverageErrors.Count -gt 0) {
    throw "Touch-selection release evidence is incomplete:`n - $($coverageErrors -join "`n - ")"
}

$runs | Sort-Object dpiScale, architecture, systemHighContrast | Format-Table -AutoSize
Write-Host "Touch-selection release matrix passed: $($runs.Count) evidence runs."
