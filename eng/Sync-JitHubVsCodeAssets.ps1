[CmdletBinding()]
param(
    [string]$VsCodeRepoPath,
    [string]$DestinationPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\EditorAssets\dist'),
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoRoot {
    return Split-Path -Parent $PSScriptRoot
}

function Resolve-VsCodeRepoPath {
    param(
        [string]$ExplicitPath
    )

    $repoRoot = Resolve-RepoRoot
    $candidates = @(
        $ExplicitPath,
        $env:JITHUB_VSCODE_PATH,
        (Join-Path $repoRoot '..\jithub-vs-code'),
        'E:\jithub-vs-code'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        $fullPath = [System.IO.Path]::GetFullPath($candidate)
        if (
            (Test-Path -LiteralPath $fullPath) -and
            (Test-Path -LiteralPath (Join-Path $fullPath 'package.json')) -and
            (Test-Path -LiteralPath (Join-Path $fullPath 'webpack.config.js'))
        ) {
            return $fullPath
        }
    }

    throw 'Unable to locate the jithub-vs-code repository. Pass -VsCodeRepoPath or set JITHUB_VSCODE_PATH.'
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    Write-Host "> $FilePath $($ArgumentList -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Clear-DestinationDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $attempt = 0
    while ($attempt -lt 3) {
        $attempt++
        try {
            Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
                Remove-Item -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -ge 3) {
                throw "Unable to clear $Path. Close JitHub or any process using files under Assets\\dist, then try again. $($_.Exception.Message)"
            }

            Start-Sleep -Seconds 1
        }
    }
}

$repoPath = Resolve-VsCodeRepoPath -ExplicitPath $VsCodeRepoPath
$destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required to build jithub-vs-code assets.'
}

if (-not (Get-Command yarn -ErrorAction SilentlyContinue)) {
    throw 'Yarn is required to build jithub-vs-code assets.'
}

Write-Host "Using jithub-vs-code repository at: $repoPath"

if (-not $SkipInstall) {
    Invoke-CheckedCommand -FilePath 'yarn' -ArgumentList @('--frozen-lockfile') -WorkingDirectory $repoPath
}

Invoke-CheckedCommand -FilePath 'yarn' -ArgumentList @('build') -WorkingDirectory $repoPath

$sourceDistPath = Join-Path $repoPath 'dist'
if (
    -not (Test-Path -LiteralPath $sourceDistPath) -or
    -not (Test-Path -LiteralPath (Join-Path $sourceDistPath 'index.html'))
) {
    throw "The jithub-vs-code build did not produce the expected dist output at $sourceDistPath"
}

New-Item -ItemType Directory -Path $destinationFullPath -Force | Out-Null

Clear-DestinationDirectory -Path $destinationFullPath

Copy-Item -Path (Join-Path $sourceDistPath '*') -Destination $destinationFullPath -Recurse -Force

Write-Host "Synced editor assets to: $destinationFullPath"
