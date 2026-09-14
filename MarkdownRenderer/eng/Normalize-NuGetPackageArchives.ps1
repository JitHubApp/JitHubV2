[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Package directory '$PackageDirectory' does not exist."
}

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object Name)
if ($packages.Count -eq 0) {
    throw "No nupkg files were found in '$PackageDirectory'."
}

# NuGet.Pack writes the current DOS timestamp into every ZIP header even when all
# package entries are byte-for-byte deterministic. A fixed, ZIP-safe instant makes
# the complete nupkg reproducible without changing entry content or compression.
$fixedTimestamp = [DateTimeOffset]::ParseExact(
    '2000-01-01T00:00:00+00:00',
    'yyyy-MM-ddTHH:mm:sszzz',
    [Globalization.CultureInfo]::InvariantCulture)

Add-Type -AssemblyName System.IO.Compression
foreach ($package in $packages) {
    $stream = [IO.File]::Open(
        $package.FullName,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Update,
            $false)
        try {
            foreach ($entry in $archive.Entries) {
                $entry.LastWriteTime = $fixedTimestamp
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

Write-Host "Normalized ZIP timestamps in $($packages.Count) NuGet packages under '$PackageDirectory'."
