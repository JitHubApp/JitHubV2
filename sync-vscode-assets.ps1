[CmdletBinding()]
param(
    [string]$VsCodeRepoPath,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'eng\Sync-JitHubVsCodeAssets.ps1') `
    -VsCodeRepoPath $VsCodeRepoPath `
    -SkipInstall:$SkipInstall
