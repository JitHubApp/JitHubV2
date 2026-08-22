param(
    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Architecture = 'x64',

    [switch]$UpdateLocks
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProject = Join-Path $repositoryRoot 'JitHub.WinUI\JitHub.WinUI.csproj'
$rendererProject = Join-Path $repositoryRoot 'MarkdownRenderer\MarkdownRenderer\MarkdownRenderer.csproj'
$gfmProject = Join-Path $repositoryRoot 'MarkdownRenderer\MarkdownRenderer.Gfm\MarkdownRenderer.Gfm.csproj'

$platform = switch ($Architecture) {
    'x86' { 'x86' }
    'x64' { 'x64' }
    'arm64' { 'ARM64' }
}
$runtimeIdentifier = "win-$Architecture"

function Invoke-DotNetRestore {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,

        [string]$ExplicitRuntimeIdentifier,

        [switch]$ForceEvaluate
    )

    $arguments = @(
        'restore'
        $Project
        "-p:Platform=$platform"
        '-p:Configuration=Release'
        '-p:PublishAot=true'
        '-p:SkipReleaseSecurityGate=true'
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRuntimeIdentifier)) {
        $arguments += "-p:RuntimeIdentifier=$ExplicitRuntimeIdentifier"
    }

    if ($ForceEvaluate) {
        $arguments += '-p:RestoreLockedMode=false'
        $arguments += '--force-evaluate'
    }
    else {
        $arguments += '-p:RestoreLockedMode=true'
        $arguments += '--locked-mode'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT restore failed for $Project ($Architecture)."
    }
}

if ($UpdateLocks) {
    # Restore the app graph first, then close the two class-library graphs explicitly so
    # their lock files include the architecture-specific ILC packages for every RID.
    Invoke-DotNetRestore -Project $appProject -ForceEvaluate
    Invoke-DotNetRestore -Project $rendererProject -ExplicitRuntimeIdentifier $runtimeIdentifier -ForceEvaluate
    Invoke-DotNetRestore -Project $gfmProject -ExplicitRuntimeIdentifier $runtimeIdentifier -ForceEvaluate
}

Invoke-DotNetRestore -Project $appProject
Write-Host "Verified locked Native AOT restore for $Architecture."
