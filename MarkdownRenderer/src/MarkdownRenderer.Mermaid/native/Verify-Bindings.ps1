[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$contractPath = Join-Path $PSScriptRoot 'abi/mmir-v1.json'
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
$header = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'include/markdown_renderer_mermaid.h') -Raw
$managed = Get-Content -LiteralPath (Join-Path $root 'NativeInterop.Generated.cs') -Raw
$rust = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/abi_generated.rs') -Raw

if ($contract.abi.callingConvention -ne 'cdecl' -or $contract.abi.endianness -ne 'little') {
    throw 'MMIR v1 must remain a little-endian cdecl ABI.'
}
if ($contract.structures.MmirEngineOptions.size -ne 24 -or $contract.structures.MmirRenderOptions.size -ne 48) {
    throw 'The ABI structure sizes changed without a major-version transition.'
}
foreach ($export in $contract.exports) {
    if (-not $header.Contains($export) -or -not $managed.Contains('"' + $export + '"')) {
        throw "Generated bindings are missing export $export."
    }
}
foreach ($status in $contract.status.PSObject.Properties) {
    $value = [string]$status.Value
    if (-not $header.Contains('= ' + $value) -or -not $rust.Contains('= ' + $value)) {
        throw "Generated status $($status.Name)=$value is inconsistent."
    }
}
if (-not $managed.Contains('CallConvCdecl') -or -not $header.Contains('MMIR_CALL __cdecl')) {
    throw 'Generated bindings do not enforce cdecl.'
}

Write-Host 'MMIR generated binding contract verified.'
