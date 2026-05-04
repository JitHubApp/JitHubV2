param(
    [string]$ProductId = '9MXRBJBB552V',
    [string]$ExpectedName = 'JitHub',
    [switch]$SkipSearch
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command store -ErrorAction SilentlyContinue)) {
    throw 'The Microsoft Store client CLI (store) is not available. Update Microsoft Store or run this on a supported Windows build.'
}

Write-Host "Checking Microsoft Store listing for $ExpectedName ($ProductId)..."
$details = & store show $ProductId 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "store show failed for $ProductId. Output: $details"
}

if ($details -notmatch [regex]::Escape($ExpectedName)) {
    throw "Store listing output did not contain expected app name '$ExpectedName'."
}

if ($details -notmatch [regex]::Escape($ProductId)) {
    throw "Store listing output did not contain expected product ID '$ProductId'."
}

Write-Host $details

if (-not $SkipSearch) {
    Write-Host "Checking Store search ranking for '$ExpectedName'..."
    $search = & store search $ExpectedName 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "store search failed for '$ExpectedName'. Output: $search"
    }

    if ($search -notmatch [regex]::Escape($ProductId)) {
        throw "Store search output did not include product ID '$ProductId'."
    }

    Write-Host $search
}

Write-Host 'Store listing smoke check complete.'
