param(
    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Store package version must not be empty.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Store package version '$Version' must use the four-part format Major.Minor.Build.Revision."
}

$components = $Version.Split('.')
$parsedComponents = @()

for ($index = 0; $index -lt $components.Count; $index++) {
    $componentValue = 0
    $parsed = [int]::TryParse(
        $components[$index],
        [System.Globalization.NumberStyles]::None,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$componentValue)

    if (-not $parsed -or $componentValue -gt 65535) {
        throw "Store package version '$Version' has a component outside the supported range of 0 through 65535."
    }

    $parsedComponents += $componentValue
}

if ($parsedComponents[0] -eq 0) {
    throw "Store package version '$Version' must have a Major component from 1 through 65535."
}

if ($parsedComponents[3] -ne 0) {
    throw "Store package version '$Version' must end in '.0'. Microsoft Store reserves the Revision component; increment Major, Minor, or Build instead."
}

Write-Host "Validated Microsoft Store package version $Version."
