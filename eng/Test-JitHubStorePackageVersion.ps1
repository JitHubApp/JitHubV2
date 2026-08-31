$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$validatorPath = Join-Path $PSScriptRoot 'Assert-JitHubStorePackageVersion.ps1'

$validVersions = @(
    '1.0.0.0',
    '3.0.2.0',
    '65535.65535.65535.0'
)

foreach ($version in $validVersions) {
    & $validatorPath -Version $version | Out-Null
}

$invalidVersions = @(
    @{ Version = ''; Expected = 'must not be empty' },
    @{ Version = '1.2.3'; Expected = 'four-part format' },
    @{ Version = '1.2.invalid.0'; Expected = 'four-part format' },
    @{ Version = '0.1.0.0'; Expected = 'Major component' },
    @{ Version = '65536.0.0.0'; Expected = 'supported range' },
    @{ Version = '1.65536.0.0'; Expected = 'supported range' },
    @{ Version = '1.0.65536.0'; Expected = 'supported range' },
    @{ Version = '3.0.0.2'; Expected = "must end in '.0'" }
)

foreach ($testCase in $invalidVersions) {
    $rejected = $false

    try {
        & $validatorPath -Version $testCase.Version | Out-Null
    }
    catch {
        $rejected = $true
        if ($_.Exception.Message -notlike "*$($testCase.Expected)*") {
            throw "Version '$($testCase.Version)' failed with an unexpected message: $($_.Exception.Message)"
        }
    }

    if (-not $rejected) {
        throw "Version '$($testCase.Version)' should have been rejected."
    }
}

Write-Host "Validated $($validVersions.Count) accepted and $($invalidVersions.Count) rejected Store package version cases."
