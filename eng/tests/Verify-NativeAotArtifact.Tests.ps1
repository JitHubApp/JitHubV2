$ErrorActionPreference = 'Stop'
$verifierPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Verify-NativeAotArtifact.ps1'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$tempRoot = Join-Path $tempParent "jithub-native-aot-verifier-tests-$([Guid]::NewGuid().ToString('N'))"
$resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
if (-not $resolvedTempRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create NativeAOT verifier fixtures outside the temporary directory.'
}
New-Item -ItemType Directory -Path $resolvedTempRoot | Out-Null

$junctionPaths = [Collections.Generic.List[string]]::new()
$requiredNames = @(
    'JitHub.WinUI.exe',
    'e_sqlite3.dll',
    'Microsoft.Graphics.Canvas.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'MarkdownRenderer.Svg.Resvg.Worker.exe',
    'WebView2Loader.dll',
    'WinUIEditor.dll'
)

function New-NativeLayout([string]$Name) {
    $layout = Join-Path $resolvedTempRoot $Name
    New-Item -ItemType Directory -Path $layout | Out-Null
    foreach ($requiredName in $requiredNames) {
        Copy-Item -LiteralPath $script:fixturePePath -Destination (Join-Path $layout $requiredName)
    }
    return $layout
}

function Set-Hidden([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    $item.Attributes = $item.Attributes -bor [IO.FileAttributes]::Hidden
}

function Assert-ArtifactRejected([string]$Layout, [string]$ExpectedMessage) {
    $rejected = $false
    try {
        & $verifierPath -InputPath $Layout -Architecture x64
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedMessage) {
            throw "NativeAOT verifier rejected a fixture for the wrong reason. Expected '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
        $rejected = $true
    }
    if (-not $rejected) {
        throw "NativeAOT verifier accepted a fixture that should fail '$ExpectedMessage'."
    }
}

try {
    $script:fixturePePath = Join-Path $env:WINDIR 'System32\kernel32.dll'
    if (-not (Test-Path -LiteralPath $script:fixturePePath -PathType Leaf)) {
        throw 'The verifier mutation test requires the Windows x64 kernel32 PE fixture.'
    }

    $baseline = New-NativeLayout 'baseline'
    & $verifierPath -InputPath $baseline -Architecture x64

    $hiddenCoreClrLayout = New-NativeLayout 'hidden-coreclr'
    $hiddenCoreClr = Join-Path $hiddenCoreClrLayout 'coreclr.dll'
    Copy-Item -LiteralPath $script:fixturePePath -Destination $hiddenCoreClr
    Set-Hidden $hiddenCoreClr
    Assert-ArtifactRejected $hiddenCoreClrLayout 'CoreCLR/JIT host payload is forbidden'

    $hiddenMetadataLayout = New-NativeLayout 'hidden-managed-metadata'
    $hiddenMetadata = Join-Path $hiddenMetadataLayout 'JitHub.WinUI.runtimeconfig.json'
    [IO.File]::WriteAllText($hiddenMetadata, '{}')
    Set-Hidden $hiddenMetadata
    Assert-ArtifactRejected $hiddenMetadataLayout 'Managed host metadata is forbidden'

    $junctionLayout = New-NativeLayout 'junction-layout'
    $junctionTarget = Join-Path $resolvedTempRoot 'external-target'
    New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    $junction = Join-Path $junctionLayout 'hidden-runtime'
    New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
    $junctionPaths.Add($junction)
    Assert-ArtifactRejected $junctionLayout 'Reparse-point entries are forbidden'

    $rootTarget = New-NativeLayout 'root-target'
    $rootJunction = Join-Path $resolvedTempRoot 'root-junction'
    New-Item -ItemType Junction -Path $rootJunction -Target $rootTarget | Out-Null
    $junctionPaths.Add($rootJunction)
    Assert-ArtifactRejected $rootJunction 'layout root must be a regular directory'

    Write-Host 'NativeAOT verifier tests passed: clean native layout accepted; hidden CoreCLR, hidden runtime metadata, nested junction, and reparse-point layout root rejected.'
}
finally {
    foreach ($junctionPath in $junctionPaths) {
        $resolvedJunction = [IO.Path]::GetFullPath($junctionPath)
        if (-not $resolvedJunction.StartsWith($resolvedTempRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a test junction outside its fixture root: $resolvedJunction"
        }
        $junctionItem = Get-Item -LiteralPath $resolvedJunction -Force -ErrorAction SilentlyContinue
        if ($null -ne $junctionItem -and
            ($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            [IO.Directory]::Delete($resolvedJunction, $false)
        }
    }

    if (Test-Path -LiteralPath $resolvedTempRoot) {
        $actual = [IO.Path]::GetFullPath($resolvedTempRoot)
        if (-not $actual.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($actual) -notmatch '^jithub-native-aot-verifier-tests-[0-9a-f]{32}$') {
            throw 'Temporary NativeAOT verifier fixture path changed unexpectedly; preserving it.'
        }
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
