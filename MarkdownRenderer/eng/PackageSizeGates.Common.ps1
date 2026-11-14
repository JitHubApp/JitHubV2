Set-StrictMode -Version Latest

function Assert-ResvgPackageContentBoundaries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Entries
    )

    # This provider deploys only its managed library and selected RID worker.
    # Build/test fixtures must not become NuGet consumer Content, including
    # duplicated legacy content/ and modern contentFiles/ image globs.
    foreach ($entry in $Entries) {
        $name = ([string] $entry.Name).Replace('\', '/')
        if ($name -match '^(?i:content|contentFiles)/') {
            throw "The resvg package must not deploy consumer Content '$name'; native fixtures are build/test inputs, not runtime assets."
        }
    }
}

function Get-ResvgSelectedDeploymentPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Entries,

        [Parameter(Mandatory = $true)]
        [ValidateSet('win-x86', 'win-x64', 'win-arm64')]
        [string] $Rid,

        [Parameter(Mandatory = $false)]
        [string] $ProviderAssemblyPath = 'lib/net10.0-windows10.0.26100/MarkdownRenderer.Svg.Resvg.dll'
    )

    if ([string]::IsNullOrWhiteSpace($ProviderAssemblyPath) -or
        $ProviderAssemblyPath.StartsWith('/') -or
        $ProviderAssemblyPath.Contains('\')) {
        throw "Provider assembly package path '$ProviderAssemblyPath' must be a non-empty NuGet archive path using '/' separators."
    }

    $workerPath = "runtimes/$Rid/native/MarkdownRenderer.Svg.Resvg.Worker.exe"
    $requiredEntries = @(
        [pscustomobject]@{ Label = 'worker'; Path = $workerPath },
        [pscustomobject]@{ Label = 'provider runtime DLL'; Path = $ProviderAssemblyPath }
    )
    $sizes = @{}

    foreach ($required in $requiredEntries) {
        $matches = @($Entries | Where-Object {
            $nameProperty = $_.PSObject.Properties['Name']
            $null -ne $nameProperty -and
            [StringComparer]::Ordinal.Equals([string] $nameProperty.Value, $required.Path)
        })
        if ($matches.Count -ne 1) {
            throw "Selected deployment for '$Rid' requires exactly one $($required.Label) at '$($required.Path)'; found $($matches.Count)."
        }

        $lengthProperty = $matches[0].PSObject.Properties['Length']
        if ($null -eq $lengthProperty) {
            throw "Selected deployment entry '$($required.Path)' has no uncompressed Length metadata."
        }

        try {
            $length = [long] $lengthProperty.Value
        }
        catch {
            throw "Selected deployment entry '$($required.Path)' has invalid uncompressed Length metadata."
        }
        if ($length -le 0) {
            throw "Selected deployment entry '$($required.Path)' has no payload bytes."
        }

        $sizes[$required.Label] = $length
    }

    $workerBytes = [long] $sizes['worker']
    $providerAssemblyBytes = [long] $sizes['provider runtime DLL']
    if ($providerAssemblyBytes -gt ([long]::MaxValue - $workerBytes)) {
        throw "Selected deployment size for '$Rid' exceeds the supported byte-count range."
    }

    return [pscustomobject][ordered]@{
        rid = $Rid
        workerPath = $workerPath
        workerBytes = $workerBytes
        providerAssemblyPath = $ProviderAssemblyPath
        providerAssemblyBytes = $providerAssemblyBytes
        totalBytes = ($workerBytes + $providerAssemblyBytes)
    }
}

function Assert-ResvgSelectedDeploymentWithinCap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Deployment,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, [long]::MaxValue)]
        [long] $MaximumBytes
    )

    if ([long] $Deployment.totalBytes -gt $MaximumBytes) {
        throw "Selected resvg deployment '$($Deployment.rid)' (worker plus provider runtime DLL) is $($Deployment.totalBytes) bytes; the gate is $MaximumBytes bytes."
    }
}
