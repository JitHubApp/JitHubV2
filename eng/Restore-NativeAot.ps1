param(
    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Architecture = 'x64',

    [switch]$UpdateLocks
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProject = Join-Path $repositoryRoot 'JitHub.WinUI\JitHub.WinUI.csproj'

$platform = switch ($Architecture) {
    'x86' { 'x86' }
    'x64' { 'x64' }
    'arm64' { 'ARM64' }
}
function Invoke-DotNetRestore {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,

        [switch]$ForceEvaluate
    )

    $arguments = @(
        'restore'
        $Project
        "-p:Platform=$platform"
        '-p:Configuration=Release'
        '-p:SkipReleaseSecurityGate=true'
    )

    if ($ForceEvaluate) {
        $arguments += '-p:RestoreLockedMode=false'
        $arguments += '--force-evaluate'
    }
    else {
        $arguments += '-p:RestoreLockedMode=true'
        $arguments += '--locked-mode'
        $arguments += '--force'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT restore failed for $Project ($Architecture)."
    }
}

if ($UpdateLocks) {
    # NativeAot.props enables AOT at the app root. Do not pass PublishAot as a
    # command-line global property: NuGet would inject ILCompiler into every
    # project-reference lock and make ordinary Release restores invalid.
    Invoke-DotNetRestore -Project $appProject -ForceEvaluate
}

Invoke-DotNetRestore -Project $appProject
Write-Host "Verified locked Native AOT restore for $Architecture."
