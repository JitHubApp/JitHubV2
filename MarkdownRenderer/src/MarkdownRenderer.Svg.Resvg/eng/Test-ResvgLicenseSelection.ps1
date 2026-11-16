$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'SpdxLicenseSelection.ps1')

$approved = @('Apache-2.0', 'BSD-3-Clause', 'MIT', 'Unicode-3.0')
$additionalKnown = @('0BSD')
$checks = 0

function Assert-LicenseSelectionAccepted {
    param(
        [string] $Expression,
        [string] $Selection,
        [string] $Name
    )

    try {
        [void](Assert-SpdxLicenseSelection `
            -Expression $Expression `
            -Selection $Selection `
            -ApprovedIdentifiers $script:approved `
            -AdditionalKnownIdentifiers $script:additionalKnown `
            -Subject $Name)
        $script:checks++
    }
    catch {
        throw "Expected SPDX case '$Name' to pass, but it failed: $($_.Exception.Message)"
    }
}

function Assert-LicenseSelectionRejected {
    param(
        [string] $Expression,
        [string] $Selection,
        [string] $Name
    )

    $rejected = $false
    try {
        [void](Assert-SpdxLicenseSelection `
            -Expression $Expression `
            -Selection $Selection `
            -ApprovedIdentifiers $script:approved `
            -AdditionalKnownIdentifiers $script:additionalKnown `
            -Subject $Name)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Expected SPDX case '$Name' to be rejected."
    }
    $script:checks++
}

Assert-LicenseSelectionAccepted 'MIT' 'MIT' 'single license'
Assert-LicenseSelectionAccepted '(MIT OR Apache-2.0) AND Unicode-3.0' 'MIT AND Unicode-3.0' 'parenthesized alternative plus required conjunction'
Assert-LicenseSelectionAccepted 'MIT OR Apache-2.0 AND Unicode-3.0' 'MIT' 'AND binds more tightly than OR'
Assert-LicenseSelectionAccepted 'MIT OR Apache-2.0 AND Unicode-3.0' 'Apache-2.0 AND Unicode-3.0' 'precedence conjunction branch'
Assert-LicenseSelectionAccepted '0BSD OR MIT OR Apache-2.0' 'MIT' 'additional known declared identifier'
Assert-LicenseSelectionAccepted 'MIT/Apache-2.0' 'MIT' 'exact Cargo legacy spelling'
Assert-LicenseSelectionAccepted 'BSD-3-Clause AND MIT' 'BSD-3-Clause AND MIT' 'combined vendored crate licenses'

Assert-LicenseSelectionRejected '(MIT OR Apache-2.0) AND Unicode-3.0' 'MIT' 'required AND term omitted'
Assert-LicenseSelectionRejected 'BSD-3-Clause AND MIT' 'BSD-3-Clause' 'combined vendored crate must retain both license terms'
Assert-LicenseSelectionRejected 'MIT AND Unicode-3.0' 'MIT OR Unicode-3.0' 'selection cannot state alternatives'
Assert-LicenseSelectionRejected 'MIT OR Apache-2.0' 'BSD-3-Clause' 'approved but unoffered selection'
Assert-LicenseSelectionRejected 'MIT OR Apache-2.0' 'GPL-3.0-only' 'unknown selected identifier'
Assert-LicenseSelectionRejected 'MIT OR GPL-3.0-only' 'MIT' 'invalid unselected declared branch'
Assert-LicenseSelectionRejected 'MIT OR OR Apache-2.0' 'MIT' 'repeated operator'
Assert-LicenseSelectionRejected 'MIT garbage' 'MIT' 'trailing token'
Assert-LicenseSelectionRejected 'MIT AND' 'MIT' 'missing right operand'
Assert-LicenseSelectionRejected 'MIT AND (Apache-2.0 OR Unicode-3.0' 'MIT AND Unicode-3.0' 'unclosed parenthesis'
Assert-LicenseSelectionRejected 'MIT/ BSD-3-Clause' 'MIT' 'arbitrary slash spelling'
Assert-LicenseSelectionRejected 'MIT+' 'MIT' 'unsupported plus syntax'
Assert-LicenseSelectionRejected 'MIT WITH LLVM-exception' 'MIT' 'unsupported WITH syntax'

$overLength = 'MIT' + (' ' * 1022)
Assert-LicenseSelectionRejected $overLength 'MIT' 'expression character bound'

$tooDeep = ('(' * 17) + 'MIT' + (')' * 17)
Assert-LicenseSelectionRejected $tooDeep 'MIT' 'expression nesting bound'

$tooManyTokens = (@('MIT') * 65) -join ' OR '
Assert-LicenseSelectionRejected $tooManyTokens 'MIT' 'expression token bound'

$duplicateSelection = 'MIT AND MIT'
Assert-LicenseSelectionRejected 'MIT OR Apache-2.0' $duplicateSelection 'duplicate selected identifier'

$recognized = Get-ResvgAdditionalKnownLicenseIdentifiers `
    -ApprovedIdentifiers $approved `
    -InventoryIdentifiers @('0BSD', 'MIT', 'Unicode-3.0')
if (@($recognized).Count -ne 1 -or $recognized[0] -cne '0BSD') {
    throw 'The inventory may recognize only the pinned unselected 0BSD alternative.'
}
$checks++

$inventoryRejected = $false
try {
    [void](Get-ResvgAdditionalKnownLicenseIdentifiers `
        -ApprovedIdentifiers $approved `
        -InventoryIdentifiers @('0BSD', 'GPL-3.0-only'))
}
catch { $inventoryRejected = $true }
if (-not $inventoryRejected) { throw 'The inventory granted recognition to an unreviewed license.' }
$checks++
Assert-LicenseSelectionRejected '0BSD OR MIT' '0BSD' 'known alternative is not an approved selection'

Write-Output "Passed $checks bounded SPDX selection fixtures."
