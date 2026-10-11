[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageSizeGates.Common.ps1')
Add-Type -AssemblyName System.IO.Compression

function Assert-True {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][bool] $Condition,
        [Parameter(Mandatory = $true, Position = 1)][string] $Message
    )

    if (-not $Condition) { throw "Selected deployment size self-test failed: $Message" }
}

$extraAssertionArgumentRejected = $false
try { Assert-True $true 'assertion argument guard' 'unexpected extra argument' }
catch { $extraAssertionArgumentRejected = $_.Exception.Message -like '*positional parameter*' }
if (-not $extraAssertionArgumentRejected) {
    throw 'Selected deployment size self-test failed: Assert-True must reject surplus positional arguments.'
}

$capBytes = [long] (3.5MB)
$providerPath = 'lib/net10.0-windows10.0.26100/MarkdownRenderer.Svg.Resvg.dll'
$supportedRids = @('win-x86', 'win-x64', 'win-arm64')

foreach ($selectedRid in $supportedRids) {
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($rid in $supportedRids) {
        $workerBytes = if ($rid -ceq $selectedRid) { $capBytes - 1024 } else { $capBytes * 4 }
        $entries.Add([pscustomobject]@{
            Name = "runtimes/$rid/native/MarkdownRenderer.Svg.Resvg.Worker.exe"
            Length = [long] $workerBytes
        })
    }
    $entries.Add([pscustomobject]@{
        Name = $providerPath
        Length = [long] 1024
    })
    $entries.Add([pscustomobject]@{
        Name = 'lib/net8.0/MarkdownRenderer.Svg.Resvg.dll'
        Length = [long] ($capBytes * 5)
    })
    $entries.Add([pscustomobject]@{
        Name = 'lib/net10.0-windows10.0.26100/MarkdownRenderer.Svg.Resvg.xml'
        Length = [long] ($capBytes * 5)
    })
    $entries.Add([pscustomobject]@{
        Name = 'README.md'
        Length = [long] ($capBytes * 5)
    })

    $measurement = Get-ResvgSelectedDeploymentPayload `
        -Entries $entries.ToArray() `
        -Rid $selectedRid `
        -ProviderAssemblyPath $providerPath
    Assert-ResvgSelectedDeploymentWithinCap -Deployment $measurement -MaximumBytes $capBytes
    Assert-True ($measurement.totalBytes -eq $capBytes) `
        "$selectedRid must count exactly its worker plus one selected provider runtime DLL, excluding documentation, other RIDs, and other TFMs"
    Assert-True ($measurement.providerAssemblyBytes -eq 1024) `
        "$selectedRid must report the selected TFM provider DLL's uncompressed bytes"
}

$oneByteOver = Get-ResvgSelectedDeploymentPayload `
    -Entries @(
        [pscustomobject]@{
            Name = 'runtimes/win-x64/native/MarkdownRenderer.Svg.Resvg.Worker.exe'
            Length = [long] ($capBytes - 1023)
        },
        [pscustomobject]@{
            Name = $providerPath
            Length = [long] 1024
        }
    ) `
    -Rid 'win-x64' `
    -ProviderAssemblyPath $providerPath
$oneByteOverRejected = $false
try { Assert-ResvgSelectedDeploymentWithinCap -Deployment $oneByteOver -MaximumBytes $capBytes }
catch { $oneByteOverRejected = $_.Exception.Message -like '*3670017 bytes*3670016 bytes*' }
Assert-True ($oneByteOver.totalBytes -eq ($capBytes + 1) -and $oneByteOverRejected) `
    'the selected deployment aggregate must reject exactly one byte over 3.5 MiB'

$validEntries = @(
    [pscustomobject]@{
        Name = 'runtimes/win-x64/native/MarkdownRenderer.Svg.Resvg.Worker.exe'
        Length = [long] ($capBytes - 1024)
    },
    [pscustomobject]@{
        Name = $providerPath
        Length = [long] 1024
    }
)
$invalidCases = @(
    [pscustomobject]@{
        Name = 'missing provider DLL'
        Entries = @($validEntries | Where-Object { $_.Name -cne $providerPath })
        Fragment = 'exactly one provider runtime DLL'
    },
    [pscustomobject]@{
        Name = 'zero-byte provider DLL'
        Entries = @($validEntries | ForEach-Object {
            if ($_.Name -ceq $providerPath) { [pscustomobject]@{ Name = $_.Name; Length = [long] 0 } }
            else { $_ }
        })
        Fragment = 'has no payload bytes'
    },
    [pscustomobject]@{
        Name = 'zero-byte selected worker'
        Entries = @($validEntries | ForEach-Object {
            if ($_.Name -like 'runtimes/*/native/*') { [pscustomobject]@{ Name = $_.Name; Length = [long] 0 } }
            else { $_ }
        })
        Fragment = 'has no payload bytes'
    },
    [pscustomobject]@{
        Name = 'duplicate provider DLL'
        Entries = @($validEntries + $validEntries[1])
        Fragment = 'exactly one provider runtime DLL'
    },
    [pscustomobject]@{
        Name = 'missing selected worker'
        Entries = @($validEntries | Where-Object { $_.Name -notlike 'runtimes/*/native/*' })
        Fragment = 'exactly one worker'
    }
)

foreach ($case in $invalidCases) {
    $rejected = $false
    try {
        Get-ResvgSelectedDeploymentPayload `
            -Entries $case.Entries `
            -Rid 'win-x64' `
            -ProviderAssemblyPath $providerPath | Out-Null
    }
    catch { $rejected = $_.Exception.Message -like "*$($case.Fragment)*" }
    Assert-True $rejected "selected deployment accounting must reject $($case.Name) instead of counting a missing or zero-length asset"
}

# Exercise the same accounting against real ZIP central-directory entries,
# including decoys that are intentionally outside the selected deployment.
$archiveRoot = Join-Path ([IO.Path]::GetTempPath()) ("resvg-selected-deployment-{0}" -f [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $archiveRoot 'SelectedDeploymentFixture.nupkg'
New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
try {
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $fixtureEntries = @(
                [pscustomobject]@{
                    Name = 'runtimes/win-x64/native/MarkdownRenderer.Svg.Resvg.Worker.exe'
                    Length = [long]($capBytes - 1024)
                },
                [pscustomobject]@{ Name = $providerPath; Length = [long]1024 },
                [pscustomobject]@{
                    Name = 'runtimes/win-arm64/native/MarkdownRenderer.Svg.Resvg.Worker.exe'
                    Length = [long]($capBytes * 4)
                },
                [pscustomobject]@{ Name = 'lib/net8.0/MarkdownRenderer.Svg.Resvg.dll'; Length = [long]($capBytes * 4) },
                [pscustomobject]@{ Name = 'README.md'; Length = [long]($capBytes * 4) }
            )
            foreach ($entry in $fixtureEntries) {
                $zipEntry = $archive.CreateEntry([string]$entry.Name, [IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $zipEntry.Open()
                try {
                    $remaining = [long]$entry.Length
                    $buffer = [byte[]]::new([Math]::Min(65536, [int]$entry.Length))
                    while ($remaining -gt 0) {
                        $writeCount = [int][Math]::Min($remaining, $buffer.Length)
                        $entryStream.Write($buffer, 0, $writeCount)
                        $remaining -= $writeCount
                    }
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }

    $readArchive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $archiveEntries = @($readArchive.Entries | ForEach-Object {
            [pscustomobject]@{ Name = $_.FullName; Length = [long]$_.Length; CompressedLength = [long]$_.CompressedLength }
        })
        $archiveMeasurement = Get-ResvgSelectedDeploymentPayload `
            -Entries $archiveEntries `
            -Rid 'win-x64' `
            -ProviderAssemblyPath $providerPath
        Assert-ResvgSelectedDeploymentWithinCap -Deployment $archiveMeasurement -MaximumBytes $capBytes
        Assert-True ($archiveMeasurement.totalBytes -eq $capBytes -and
            $archiveMeasurement.totalBytes -eq ($archiveMeasurement.workerBytes + $archiveMeasurement.providerAssemblyBytes)) `
            'actual NuGet ZIP entries must count selected uncompressed worker plus provider DLL bytes and ignore other RIDs, TFMs, and documentation'
        $readmeEntry = @($archiveEntries | Where-Object { $_.Name -ceq 'README.md' })[0]
        $selectedZipEntries = @($archiveEntries | Where-Object {
            $_.Name -ceq "runtimes/win-x64/native/MarkdownRenderer.Svg.Resvg.Worker.exe" -or
            $_.Name -ceq $providerPath
        })
        $selectedCompressedBytes = [long](($selectedZipEntries | Measure-Object -Property CompressedLength -Sum).Sum)
        Assert-True ($null -ne $readmeEntry -and
            $readmeEntry.Length -gt $capBytes -and
            $readmeEntry.CompressedLength -lt $capBytes -and
            $selectedCompressedBytes -lt $capBytes -and
            $archiveMeasurement.totalBytes -eq $capBytes) `
            'the ZIP fixture must prove the gate uses uncompressed entry lengths rather than archive/compressed size'
    }
    finally { $readArchive.Dispose() }
}
finally {
    $resolvedArchiveRoot = [IO.Path]::GetFullPath($archiveRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedArchiveRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected ZIP fixture path '$resolvedArchiveRoot'."
    }
    if (Test-Path -LiteralPath $resolvedArchiveRoot) { Remove-Item -LiteralPath $resolvedArchiveRoot -Recurse -Force }
}

Write-Host 'Selected Resvg deployment size self-tests passed.'
