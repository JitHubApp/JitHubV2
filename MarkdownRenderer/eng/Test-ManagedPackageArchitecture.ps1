[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [Parameter(Mandatory = $false)]
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\artifacts\managed-package-architecture.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageCompliance.Common.ps1')

$managedAssetPattern = '(?i)^(?:lib|ref)/.+\.dll$|^runtimes/[^/]+/(?:lib|ref)/.+\.dll$'
$maximumManagedAssemblyBytes = 256MB
$requiredLibraryDllByPackageId = @{
    'MarkdownRenderer' = 'MarkdownRenderer.dll'
    'MarkdownRenderer.Math' = 'MarkdownRenderer.Math.dll'
    'MarkdownRenderer.Mermaid' = 'MarkdownRenderer.Mermaid.dll'
    'MarkdownRenderer.Svg.Resvg' = 'MarkdownRenderer.Svg.Resvg.dll'
    'MarkdownRenderer.SyntaxHighlighting.TextMate' = 'MarkdownRenderer.SyntaxHighlighting.TextMate.dll'
    'MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common' = 'MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common.dll'
    'MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All' = 'MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All.dll'
}
$metapackageIds = @('MarkdownRenderer.All')
$packageFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File | Sort-Object Name)
if ($packageFiles.Count -eq 0) {
    throw "No NuGet packages were found in '$PackageDirectory'."
}

function Get-EntryBytes {
    param([Parameter(Mandatory)][System.IO.Compression.ZipArchiveEntry] $Entry)

    if ($Entry.Length -gt $maximumManagedAssemblyBytes) {
        throw "Managed asset exceeds the $maximumManagedAssemblyBytes-byte inspection limit."
    }

    $entryStream = $Entry.Open()
    $memory = [IO.MemoryStream]::new([int] $Entry.Length)
    try {
        $buffer = [byte[]]::new(65536)
        while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt $maximumManagedAssemblyBytes) {
                throw "Managed asset exceeds the $maximumManagedAssemblyBytes-byte inspection limit."
            }
            $memory.Write($buffer, 0, $read)
        }
        if ($memory.Length -ne $Entry.Length) {
            throw "Archive entry length mismatch: expected $($Entry.Length) bytes, read $($memory.Length)."
        }
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $entryStream.Dispose()
    }
}

function Get-ManagedAssemblyEvidence {
    param([Parameter(Mandatory)][byte[]] $Bytes)

    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $headers = $reader.PEHeaders
            $corHeader = $headers.CorHeader
            if ($null -eq $corHeader) {
                throw 'The DLL has no CLR header.'
            }

            $flags = $corHeader.Flags
            $ilOnly = ($flags -band [System.Reflection.PortableExecutable.CorFlags]::ILOnly) -ne 0
            $requires32Bit = ($flags -band [System.Reflection.PortableExecutable.CorFlags]::Requires32Bit) -ne 0
            $prefers32Bit = ($flags -band [System.Reflection.PortableExecutable.CorFlags]::Prefers32Bit) -ne 0
            $machine = $headers.CoffHeader.Machine.ToString()
            $peMagic = $headers.PEHeader.Magic.ToString()
            $passed = $machine -eq 'I386' -and $peMagic -eq 'PE32' -and
                $ilOnly -and -not $requires32Bit -and -not $prefers32Bit

            return [pscustomobject][ordered]@{
                machine = $machine
                peMagic = $peMagic
                corFlags = $flags.ToString()
                ilOnly = $ilOnly
                requires32Bit = $requires32Bit
                prefers32Bit = $prefers32Bit
                passed = $passed
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$packageResults = [Collections.Generic.List[object]]::new()
$assetResults = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()

foreach ($package in $packageFiles) {
    $packageIdentity = Get-NuGetPackageEvidence -Package $package
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $managedEntries = @($archive.Entries | Where-Object {
            $_.FullName.Replace([char] 92, [char] 47) -match $managedAssetPattern
        } | Sort-Object FullName)

        $packageFailuresBefore = $failures.Count
        if ($packageIdentity.Id -in $metapackageIds -and $managedEntries.Count -ne 0) {
            $failures.Add("$($package.Name): metapackage '$($packageIdentity.Id)' must not contain managed DLL assets; found $($managedEntries.Count).")
        }
        if ($requiredLibraryDllByPackageId.ContainsKey($packageIdentity.Id)) {
            $requiredDll = [string] $requiredLibraryDllByPackageId[$packageIdentity.Id]
            $requiredPattern = '(?i)^lib/[^/]+/' + [regex]::Escape($requiredDll) + '$'
            $requiredEntries = @($managedEntries | Where-Object { $_.FullName -match $requiredPattern })
            if ($requiredEntries.Count -ne 1) {
                $failures.Add("$($package.Name): expected exactly one runtime library asset 'lib/<tfm>/$requiredDll'; found $($requiredEntries.Count).")
            }
        }
        foreach ($entry in $managedEntries) {
            $entryPath = $entry.FullName.Replace([char] 92, [char] 47)
            $evidence = $null
            $errorMessage = $null
            try {
                $bytes = Get-EntryBytes -Entry $entry
                $evidence = Get-ManagedAssemblyEvidence -Bytes $bytes
                if (-not $evidence.passed) {
                    $errorMessage = "expected I386/PE32 ILOnly AnyCPU (no 32-bit flags), got $($evidence.machine)/$($evidence.peMagic) flags '$($evidence.corFlags)'"
                }
            }
            catch {
                $errorMessage = $_.Exception.Message
            }

            $passed = $null -ne $evidence -and [bool] $evidence.passed -and [string]::IsNullOrEmpty($errorMessage)
            if (-not $passed) {
                $failures.Add("$($package.Name): managed asset '$entryPath' $errorMessage.")
            }

            $assetResults.Add([pscustomobject][ordered]@{
                packageFile = $package.Name
                path = $entryPath
                lengthBytes = [long] $entry.Length
                machine = if ($null -eq $evidence) { '' } else { [string] $evidence.machine }
                peMagic = if ($null -eq $evidence) { '' } else { [string] $evidence.peMagic }
                corFlags = if ($null -eq $evidence) { '' } else { [string] $evidence.corFlags }
                ilOnly = if ($null -eq $evidence) { $false } else { [bool] $evidence.ilOnly }
                requires32Bit = if ($null -eq $evidence) { $false } else { [bool] $evidence.requires32Bit }
                prefers32Bit = if ($null -eq $evidence) { $false } else { [bool] $evidence.prefers32Bit }
                passed = $passed
                error = $errorMessage
            })
        }

        $packageResults.Add([pscustomobject][ordered]@{
            packageFile = $package.Name
            managedAssetCount = $managedEntries.Count
            passed = $failures.Count -eq $packageFailuresBefore
        })
    }
    finally {
        $archive.Dispose()
    }
}

$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    policy = 'Every managed DLL under lib/, ref/, or runtimes/<rid>/lib|ref/ must be RID-neutral AnyCPU IL.'
    expectedMachine = 'I386'
    expectedPeMagic = 'PE32'
    packages = @($packageResults | Sort-Object packageFile)
    assets = @($assetResults | Sort-Object packageFile, path)
    failures = @($failures | Sort-Object -Unique)
    passed = $failures.Count -eq 0
}

Write-DeterministicText -Path $OutputPath -Content (ConvertTo-DeterministicJson $report)
if (-not $report.passed) {
    throw "Managed package architecture compliance failed with $($failures.Count) finding(s). See '$OutputPath'."
}

Write-Host "Inspected $($assetResults.Count) managed package assemblies across $($packageFiles.Count) packages."
