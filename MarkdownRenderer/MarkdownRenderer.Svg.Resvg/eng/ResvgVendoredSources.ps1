function Test-ResvgPackageRelativePath {
    param([AllowNull()][string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or
        $Path.Contains(':') -or $Path.StartsWith('/') -or
        $Path -match '(^|/)\.\.?(/|$)' -or $Path -match '(^|/)($|/)') {
        return $false
    }
    return $true
}

function Get-ResvgVendoredSourcesProvenance {
    param([Parameter(Mandatory)][string] $ProviderRoot)

    # These pins are reviewed source identity, not values trusted from the
    # adjacent UPSTREAM.json files. Both vendor manifests are self-describing,
    # but their digests must also be anchored here so editing a manifest and
    # its hashes cannot turn arbitrary local source into an approved upstream.
    $expectedSources = @(
        @{
            packageName = 'resvg'
            packageVersion = '0.48.1'
            repository = 'https://github.com/linebender/resvg.git'
            revision = '68b14c4c3bccdb60344c777406486b54c36ec1a4'
            packagePath = 'crates/resvg'
            sourceTree = '973a9a5c57b063ad942b22d44dee962bd2638b60'
            license = 'Apache-2.0 OR MIT'
            sourceInventorySha256 = '222759c0ff6177b5bf847b4d984f5d4e5819897edb204b96ecb459687643b3e0'
            patchSetSha256 = '5c5013c19ac3e4cd50c97f15dfd68271493b35fd5f13f418eff37e4531e5902b'
            expectedPatchPaths = @('Cargo.toml', 'src/clip.rs', 'src/filter/box_blur.rs', 'src/filter/convolve_matrix.rs', 'src/filter/iir_blur.rs', 'src/filter/mod.rs', 'src/filter/morphology.rs', 'src/filter/turbulence.rs', 'src/image.rs', 'src/lib.rs', 'src/mask.rs', 'src/path.rs', 'src/render.rs')
            supplementalTestFixtures = @(
                @{
                    path = 'tests/fonts/Amiri-LICENSE-OFL.txt'
                    upstreamPath = 'crates/resvg/tests/fonts/Amiri-LICENSE-OFL.txt'
                    sha256 = '1f91c5ef4a18a747526f9c527d8e39337d31d3f37a8472c89fbdfe874c2d0d69'
                    license = 'OFL-1.1'
                    licenseTextPath = 'tests/fonts/Amiri-LICENSE-OFL.txt'
                    licenseTextSha256 = '1f91c5ef4a18a747526f9c527d8e39337d31d3f37a8472c89fbdfe874c2d0d69'
                },
                @{
                    path = 'tests/fonts/Amiri-Regular.ttf'
                    upstreamPath = 'crates/resvg/tests/fonts/Amiri-Regular.ttf'
                    sha256 = 'f3615b08cc3d78409474c28ed7c9e0fb5cc42435e12080a385e1a4163ab46708'
                    license = 'OFL-1.1'
                    licenseTextPath = 'tests/fonts/Amiri-LICENSE-OFL.txt'
                    licenseTextSha256 = '1f91c5ef4a18a747526f9c527d8e39337d31d3f37a8472c89fbdfe874c2d0d69'
                },
                @{
                    path = 'tests/fonts/NotoSans-Regular.ttf'
                    upstreamPath = 'crates/resvg/tests/fonts/NotoSans-Regular.ttf'
                    sha256 = '6b04c8dd65af6b73eb4279472ed1580b29102d6496a377340e80a40cdb3b22c9'
                    license = 'OFL-1.1'
                    licenseTextPath = 'tests/fonts/Noto-LICENSE-OFL.txt'
                    licenseTextSha256 = '6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2'
                },
                @{
                    path = 'tests/fonts/Noto-LICENSE-OFL.txt'
                    upstreamPath = 'crates/resvg/tests/fonts/Noto-LICENSE-OFL.txt'
                    sha256 = '6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2'
                    license = 'OFL-1.1'
                    licenseTextPath = 'tests/fonts/Noto-LICENSE-OFL.txt'
                    licenseTextSha256 = '6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2'
                },
                @{
                    path = 'tests/fonts/NotoSerif-Regular.ttf'
                    upstreamPath = 'crates/resvg/tests/fonts/NotoSerif-Regular.ttf'
                    sha256 = '0fa6d56e4d826f72c1bc6c287b43fa1ae85b4369339f989cfa5bdf1595f22673'
                    license = 'OFL-1.1'
                    licenseTextPath = 'tests/fonts/Noto-LICENSE-OFL.txt'
                    licenseTextSha256 = '6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2'
                }
            )
        },
        @{
            packageName = 'usvg'
            packageVersion = '0.48.1'
            repository = 'https://github.com/linebender/resvg.git'
            revision = '68b14c4c3bccdb60344c777406486b54c36ec1a4'
            packagePath = 'crates/usvg'
            sourceTree = 'bfeeb1c4e58b9789206e356144ece6e16c591c3a'
            license = 'Apache-2.0 OR MIT'
            sourceInventorySha256 = '3bf99827dc292da604926c26e24d8fac1f1f17591c101cccec443b8f2b66d9c9'
            patchSetSha256 = '12c76fe8831b81fd75e9d5a7b45163fa48c3a95139e612fc1d75465f34a53323'
            expectedPatchPaths = @('Cargo.toml', 'src/parser/text.rs', 'src/text/layout.rs', 'src/tree/text.rs')
        },
        @{
            packageName = 'tiny-skia'
            packageVersion = '0.12.0'
            repository = 'https://github.com/linebender/tiny-skia.git'
            revision = 'e48b9027762d33731e60e40eb17eaa4f583b0185'
            packagePath = '.'
            sourceTree = 'c65bf6145b324fe1d00174aabe6349a736072591'
            archiveSha256 = '47ffee5eaaf5527f630fb0e356b90ebdec84d5d18d937c5e440350f88c5a91ea'
            license = 'BSD-3-Clause'
            vendoredLicense = 'BSD-3-Clause AND MIT'
            sourceInventorySha256 = '5454cbf22bc4f175b21e750839c30b99bc164991c5134f66a5cc1e7fe01bced8'
            patchSetSha256 = '50166575e05a7cdcffb0c8bfb3f1364c7d50810d10df7890c48dfe8797189f32'
            addedFilesSha256 = '36412a6e06cc5fb70e85a2611f292d560fd58a828aa992e514132b53f3742df9'
            addedFiles = @(
                @{
                    path = 'LICENSE-JITHUB-MIT'
                    sha256 = '50a0a014b7017b4cdc90c9097efa8e109a8d84f3ad92c3bfcecb45178fc65b40'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '50a0a014b7017b4cdc90c9097efa8e109a8d84f3ad92c3bfcecb45178fc65b40'
                },
                @{
                    path = 'src/bounded_fill.rs'
                    sha256 = 'ce31a24aebc55e35709991ce1415adb18c04d6408bf95432a2f030c03d2ffe26'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '50a0a014b7017b4cdc90c9097efa8e109a8d84f3ad92c3bfcecb45178fc65b40'
                },
                @{
                    path = 'src/bounded_mask.rs'
                    sha256 = 'd287f5030e7dbbf92ee7a64b551a83453ea931a8a71eabfd3b9c4b8c4b90fa8c'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '50a0a014b7017b4cdc90c9097efa8e109a8d84f3ad92c3bfcecb45178fc65b40'
                },
                @{
                    path = 'src/bounded_stroke.rs'
                    sha256 = '718ab0e7e895a7e6a0e43c3a630bd39eca8bb3b8e4a0830cbf14ecc33aabf2c1'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '50a0a014b7017b4cdc90c9097efa8e109a8d84f3ad92c3bfcecb45178fc65b40'
                },
                @{
                    path = 'src/scan/path_analytic.rs'
                    sha256 = '4afa501640ab21bd21913e07b2ec46af63a7fadc56c42f9fbd7539a17ecdf48a'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '50a0a014b7017b4cdc90c9097efa8e109a8d84f3ad92c3bfcecb45178fc65b40'
                }
            )
            expectedPatchPaths = @('Cargo.toml', 'Cargo.toml.orig', 'src/blitter.rs', 'src/edge_builder.rs', 'src/lib.rs', 'src/mask.rs', 'src/painter.rs', 'src/pipeline/blitter.rs', 'src/pipeline/mod.rs', 'src/pixmap.rs', 'src/scan/hairline_aa.rs', 'src/scan/mod.rs', 'src/scan/path.rs', 'src/scan/path_aa.rs', 'src/shaders/gradient.rs', 'src/shaders/mod.rs', 'tests/integration/fill.rs', 'tests/integration/pattern.rs', 'tests/integration/stroke.rs')
        },
        @{
            packageName = 'tiny-skia-path'
            packageVersion = '0.12.0'
            repository = 'https://github.com/linebender/tiny-skia.git'
            revision = 'e48b9027762d33731e60e40eb17eaa4f583b0185'
            packagePath = 'path'
            sourceTree = 'a4edaf846d0091e0dfa321c0d47d8114415022c7'
            archiveSha256 = 'edca365c3faccca67d06593c5980fa6c57687de727a03131735bb85f01fdeeb9'
            license = 'BSD-3-Clause'
            vendoredLicense = 'BSD-3-Clause AND MIT'
            sourceInventorySha256 = '2e76a06a5225f7bba82c24c0b5ef4520ecf9d2a824f88cca1830e80e90d213c7'
            patchSetSha256 = '728ba904050f56cbd864368dbb03f2a936b99e45afe588852b44e4b5046d8657'
            expectedPatchPaths = @('Cargo.toml', 'Cargo.toml.orig', 'src/dash.rs', 'src/lib.rs', 'src/path.rs', 'src/path_builder.rs', 'src/stroker.rs')
            addedFilesSha256 = '610cdc2e036b1c6cb27145628068f418f9ce0a9a535c67ae72055eff7173285f'
            addedFiles = @(
                @{
                    path = 'LICENSE-JITHUB-MIT'
                    sha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                },
                @{
                    path = 'src/bounded.rs'
                    sha256 = '94b60bfda536ebce1390340aca4dded9bb876e1c9874afaa2ac1553cc5258c8b'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                }
            )
        },
        @{
            packageName = 'image-webp'
            packageVersion = '0.2.4'
            repository = 'https://github.com/image-rs/image-webp.git'
            revision = '08b57d473c90148dd4b37a075df365afc3dfcc2b'
            packagePath = ''
            sourceTree = '705dad83a9ffaf469c492b5ec11defdc861eb93e'
            archiveSha256 = '525e9ff3e1a4be2fbea1fdf0e98686a6d98b4d8f937e1bf7402245af1909e8c3'
            license = 'MIT OR Apache-2.0'
            sourceInventorySha256 = 'fb373b48b33155d4863b5629965a053dfef1cb2b5e5cee7dd9402148de96caa7'
            patchSetSha256 = 'e009af2eab64189e3c910bdf72c227c666a6084f1d8b555e283a33f2c39b2f1a'
            expectedPatchPaths = @('Cargo.lock', 'Cargo.toml', 'src/decoder.rs', 'src/extended.rs', 'src/huffman.rs', 'src/lib.rs', 'src/lossless_transform.rs', 'src/lossless.rs', 'src/vp8_arithmetic_decoder.rs', 'src/vp8.rs', 'src/yuv.rs')
            addedFilesSha256 = '9e0e15c40af49aa1d9390e1f7b186201e9f0875c2b5af97912dcbed340c9eee0'
            addedFiles = @(
                @{
                    path = 'LICENSE-JITHUB-MIT'
                    sha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                },
                @{
                    path = 'src/budget.rs'
                    sha256 = '957fae9b563eecc5064cb2b5e2e3ee9787c977ccb5dde99da9e0ef32f56c7901'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                },
                @{
                    path = 'examples/generate_opaque_vp8_fixture.rs'
                    sha256 = '62ee144e8945f591aa21d87e49d85bb6067ee31c3ca27ff2315546cdfcc268a0'
                    license = 'MIT'
                    licenseTextPath = 'LICENSE-JITHUB-MIT'
                    licenseTextSha256 = '41445984ed7c0ecf79e35bbf7c4afb6dea9eff5b76207ec9531b4be23a5e1683'
                }
            )
        }
    )

    foreach ($expected in $expectedSources) {
        Get-ResvgVendoredPackageProvenance -ProviderRoot $ProviderRoot -Expected $expected
    }
}

function Get-ResvgVendoredPackageProvenance {
    param(
        [Parameter(Mandatory)][string] $ProviderRoot,
        [Parameter(Mandatory)][hashtable] $Expected
    )

    $nativeRoot = Join-Path $ProviderRoot 'native'
    $vendorRoot = Join-Path $nativeRoot ("vendor\{0}" -f $Expected.packageName)
    $manifestPath = Join-Path $vendorRoot 'Cargo.toml'
    $provenancePath = Join-Path $vendorRoot 'UPSTREAM.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
        throw "The pinned vendored $($Expected.packageName) package or its provenance manifest is missing."
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -Depth 32
    $expectedArchiveSha256 = ''
    $archiveSha256Property = $provenance.PSObject.Properties['archiveSha256']
    if ($Expected.ContainsKey('archiveSha256')) {
        $expectedArchiveSha256 = [string]$Expected['archiveSha256']
        if ($expectedArchiveSha256 -notmatch '^[0-9a-f]{64}$' -or
            $null -eq $archiveSha256Property -or
            [string]$archiveSha256Property.Value -cne $expectedArchiveSha256) {
            throw "Vendored $($Expected.packageName) provenance has an unexpected archiveSha256."
        }
    }
    elseif ($null -ne $archiveSha256Property) {
        # Git-pinned source trees do not have a registry archive checksum. Do
        # not accept an adjacent manifest's archive hash without an independent
        # pin; those sources are instead anchored by revision, sourceTree, and
        # the independently pinned source inventory below.
        throw "Vendored $($Expected.packageName) provenance declares an archiveSha256 without an independent pin."
    }

    foreach ($name in @(
        'repository', 'revision', 'packagePath', 'sourceTree',
        'packageName', 'packageVersion', 'license',
        'sourceInventorySha256', 'patchSetSha256')) {
        if ([string]$provenance.$name -cne [string]$Expected[$name]) {
            throw "Vendored $($Expected.packageName) provenance has an unexpected $name."
        }
    }
    if ($Expected.ContainsKey('vendoredLicense')) {
        if ([string]$provenance.vendoredLicense -cne [string]$Expected.vendoredLicense) {
            throw "Vendored $($Expected.packageName) combined license metadata differs from its independently pinned distribution expression."
        }
    }
    elseif ($null -ne $provenance.PSObject.Properties['vendoredLicense']) {
        throw "Vendored $($Expected.packageName) has an unapproved combined license expression."
    }
    if ([int]$provenance.schemaVersion -ne 1 -or
        [string]$provenance.sourceInventorySha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]$provenance.patchSetSha256 -notmatch '^[0-9a-f]{64}$' -or
        ($Expected.ContainsKey('archiveSha256') -and
            $expectedArchiveSha256 -notmatch '^[0-9a-f]{64}$')) {
        throw "Vendored $($Expected.packageName) provenance has an unsupported schema or malformed digest."
    }

    $sourceByPath = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $sourceLines = [Collections.Generic.List[string]]::new()
    foreach ($source in @($provenance.sourceFiles)) {
        $path = [string]$source.path
        $hash = [string]$source.sha256
        if (-not (Test-ResvgPackageRelativePath -Path $path) -or
            $hash -notmatch '^[0-9a-f]{64}$' -or $sourceByPath.ContainsKey($path)) {
            throw "Vendored $($Expected.packageName) source inventory contains an invalid or duplicate entry: $path."
        }
        $sourceByPath[$path] = $hash
        $sourceLines.Add("$path $hash`n")
    }
    if ($sourceByPath.Count -eq 0) {
        throw "Vendored $($Expected.packageName) source inventory is empty."
    }
    $sourceLineArray = $sourceLines.ToArray()
    [Array]::Sort($sourceLineArray, [StringComparer]::Ordinal)
    $sourceInventoryText = [string]::Concat($sourceLineArray)
    $sourceInventoryHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceInventoryText))).ToLowerInvariant()
    if ($sourceInventoryHash -cne [string]$Expected.sourceInventorySha256 -or
        $sourceInventoryHash -cne [string]$provenance.sourceInventorySha256) {
        throw "Vendored $($Expected.packageName) upstream source-inventory digest does not match its independent pin and entries."
    }

    $allowedMetadata = @('UPSTREAM.json', 'UPSTREAM.md', '.cargo-ok')
    $supplementalByPath = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $supplementalRecords = @()
    $supplementalManifestSha256 = $null
    if ($Expected.ContainsKey('supplementalTestFixtures')) {
        $supplementalManifestPath = Join-Path $vendorRoot 'SUPPLEMENTAL-TEST-FIXTURES.json'
        if (-not (Test-Path -LiteralPath $supplementalManifestPath -PathType Leaf)) {
            throw "Vendored $($Expected.packageName) supplemental test-fixture provenance is missing."
        }
        $allowedMetadata += 'SUPPLEMENTAL-TEST-FIXTURES.json'
        $supplementalManifest = Get-Content -LiteralPath $supplementalManifestPath -Raw | ConvertFrom-Json -Depth 16
        if ([int]$supplementalManifest.schemaVersion -ne 1 -or
            [string]$supplementalManifest.packageName -cne [string]$Expected.packageName -or
            [string]$supplementalManifest.packageVersion -cne [string]$Expected.packageVersion -or
            [string]$supplementalManifest.repository -cne [string]$Expected.repository -or
            [string]$supplementalManifest.revision -cne [string]$Expected.revision -or
            [string]$supplementalManifest.purpose -cne 'test-only') {
            throw "Vendored $($Expected.packageName) supplemental fixture manifest does not match the pinned test-only source."
        }
        $expectedFixtures = @($Expected.supplementalTestFixtures)
        $manifestFixtures = @($supplementalManifest.files)
        if ($manifestFixtures.Count -ne $expectedFixtures.Count) {
            throw "Vendored $($Expected.packageName) supplemental fixture count drift: expected $($expectedFixtures.Count), found $($manifestFixtures.Count)."
        }
        $supplementalProperties = @('path', 'upstreamPath', 'sha256', 'license', 'licenseTextPath', 'licenseTextSha256')
        foreach ($fixture in $expectedFixtures) {
            $path = [string]$fixture.path
            $matchedFixture = @($manifestFixtures | Where-Object { [string]$_.path -ceq $path })
            if ($matchedFixture.Count -ne 1 -or $supplementalByPath.ContainsKey($path) -or
                -not (Test-ResvgPackageRelativePath -Path $path) -or
                [string]$matchedFixture[0].upstreamPath -cne [string]$fixture.upstreamPath -or
                [string]$matchedFixture[0].sha256 -cne [string]$fixture.sha256 -or
                [string]$matchedFixture[0].license -cne [string]$fixture.license -or
                [string]$matchedFixture[0].licenseTextPath -cne [string]$fixture.licenseTextPath -or
                [string]$matchedFixture[0].licenseTextSha256 -cne [string]$fixture.licenseTextSha256 -or
                [string]$fixture.sha256 -notmatch '^[0-9a-f]{64}$' -or
                [string]$fixture.licenseTextSha256 -notmatch '^[0-9a-f]{64}$') {
                throw "Vendored $($Expected.packageName) supplemental fixture is unapproved or malformed: $path."
            }
            $propertyNames = @($matchedFixture[0].PSObject.Properties.Name)
            if ($propertyNames.Count -ne $supplementalProperties.Count -or
                @($propertyNames | Where-Object { $supplementalProperties -cnotcontains $_ }).Count -ne 0) {
                throw "Vendored $($Expected.packageName) supplemental fixture has unexpected metadata: $path."
            }
            $fixturePath = Join-Path $vendorRoot ($path.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$fixture.sha256) {
                throw "Vendored $($Expected.packageName) supplemental fixture bytes differ from their pin: $path."
            }
            $licenseTextPath = [string]$fixture.licenseTextPath
            if (-not (Test-ResvgPackageRelativePath -Path $licenseTextPath)) {
                throw "Vendored $($Expected.packageName) supplemental fixture has an invalid license-text path: $licenseTextPath."
            }
            $licenseTextFile = Join-Path $vendorRoot ($licenseTextPath.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $licenseTextFile -PathType Leaf) -or
                (Get-FileHash -LiteralPath $licenseTextFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$fixture.licenseTextSha256) {
                throw "Vendored $($Expected.packageName) supplemental fixture license text differs from its pin: $licenseTextPath."
            }
            $supplementalByPath.Add($path, [string]$fixture.sha256)
            $supplementalRecords += [ordered]@{
                path = $path
                upstreamPath = [string]$fixture.upstreamPath
                sha256 = [string]$fixture.sha256
                license = [string]$fixture.license
                licenseTextPath = $licenseTextPath
                licenseTextSha256 = [string]$fixture.licenseTextSha256
            }
        }
        $supplementalManifestSha256 = (Get-FileHash -LiteralPath $supplementalManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    elseif (Test-Path -LiteralPath (Join-Path $vendorRoot 'SUPPLEMENTAL-TEST-FIXTURES.json')) {
        throw "Vendored $($Expected.packageName) has an unapproved supplemental test-fixture manifest."
    }

    # Locally added files must be pinned independently of UPSTREAM.json's
    # upstream inventory and patch set. New source and its license notice have
    # no upstream hash, but still need reviewed byte hashes and license evidence.
    $expectedAddedFiles = @()
    if ($Expected.ContainsKey('addedFiles')) {
        $expectedAddedFiles = @($Expected.addedFiles)
    }
    $manifestAddedFiles = @()
    if ($null -ne $provenance.PSObject.Properties['addedFiles']) {
        $manifestAddedFiles = @($provenance.addedFiles)
    }
    if ($manifestAddedFiles.Count -ne $expectedAddedFiles.Count) {
        throw "Vendored $($Expected.packageName) added-file count drift: expected $($expectedAddedFiles.Count), found $($manifestAddedFiles.Count)."
    }

    $addedByPath = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $addedLines = [Collections.Generic.List[string]]::new()
    $addedRecords = @()
    $addedManifestProperties = @('path', 'sha256', 'license', 'licenseTextPath', 'licenseTextSha256')
    foreach ($added in $expectedAddedFiles) {
        $path = [string]$added.path
        $hash = [string]$added.sha256
        $license = [string]$added.license
        $licenseTextPath = [string]$added.licenseTextPath
        $licenseTextHash = [string]$added.licenseTextSha256
        $matchedAddedSource = @($manifestAddedFiles | Where-Object { [string]$_.path -ceq $path })
        if (-not (Test-ResvgPackageRelativePath -Path $path) -or
            $sourceByPath.ContainsKey($path) -or $supplementalByPath.ContainsKey($path) -or
            $addedByPath.ContainsKey($path) -or $hash -notmatch '^[0-9a-f]{64}$' -or
            [string]::IsNullOrWhiteSpace($license) -or
            [string]::IsNullOrWhiteSpace($licenseTextPath) -or
            -not (Test-ResvgPackageRelativePath -Path $licenseTextPath) -or
            $licenseTextHash -notmatch '^[0-9a-f]{64}$' -or $matchedAddedSource.Count -ne 1 -or
            [string]$matchedAddedSource[0].sha256 -cne $hash -or
            [string]$matchedAddedSource[0].license -cne $license -or
            [string]$matchedAddedSource[0].licenseTextPath -cne $licenseTextPath -or
            [string]$matchedAddedSource[0].licenseTextSha256 -cne $licenseTextHash) {
            throw "Vendored $($Expected.packageName) has an unapproved or malformed added-file entry: $path."
        }
        $propertyNames = @($matchedAddedSource[0].PSObject.Properties.Name)
        if ($propertyNames.Count -ne $addedManifestProperties.Count -or
            @($propertyNames | Where-Object { $addedManifestProperties -cnotcontains $_ }).Count -ne 0) {
            throw "Vendored $($Expected.packageName) added-file entry has unexpected metadata: $path."
        }

        $filePath = Join-Path $vendorRoot ($path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) {
            throw "Vendored $($Expected.packageName) added-file bytes differ from their independent pin: $path."
        }
        $licenseFilePath = Join-Path $vendorRoot ($licenseTextPath.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $licenseFilePath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $licenseFilePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $licenseTextHash) {
            throw "Vendored $($Expected.packageName) added-file license text differs from its independent pin: $licenseTextPath."
        }

        $addedByPath.Add($path, $hash)
        $addedLines.Add("$path $hash $license $licenseTextPath $licenseTextHash`n")
        $addedRecords += [ordered]@{
            path = $path
            sha256 = $hash
            license = $license
            licenseTextPath = $licenseTextPath
            licenseTextSha256 = $licenseTextHash
        }
    }
    $addedLineArray = $addedLines.ToArray()
    [Array]::Sort($addedLineArray, [StringComparer]::Ordinal)
    $addedFilesHash = if ($expectedAddedFiles.Count -eq 0) {
        $null
    }
    else {
        [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]::Concat($addedLineArray)))).ToLowerInvariant()
    }
    if ($expectedAddedFiles.Count -gt 0 -and
        ($addedFilesHash -cne [string]$Expected.addedFilesSha256 -or
            $addedFilesHash -cne [string]$provenance.addedFilesSha256)) {
        throw "Vendored $($Expected.packageName) added-file digest does not match its independently pinned reviewed file set."
    }
    if ($expectedAddedFiles.Count -eq 0 -and
        $null -ne $provenance.PSObject.Properties['addedFilesSha256'] -and
        -not [string]::IsNullOrWhiteSpace([string]$provenance.addedFilesSha256)) {
        throw "Vendored $($Expected.packageName) has an unapproved added-file digest."
    }

    $actualPaths = [Collections.Generic.List[string]]::new()
    $actualAddedPaths = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $vendorRoot -Force -Recurse) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Vendored $($Expected.packageName) source inventory must not contain a filesystem reparse point: $($file.FullName)."
        }
        if ($file.PSIsContainer) {
            continue
        }
        $relative = [IO.Path]::GetRelativePath($vendorRoot, $file.FullName).Replace('\', '/')
        if (-not $relative.Contains('/') -and $allowedMetadata -ccontains $relative) {
            continue
        }
        if ($supplementalByPath.ContainsKey($relative)) {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -cne $supplementalByPath[$relative]) {
                throw "Vendored $($Expected.packageName) supplemental fixture changed after validation: $relative."
            }
            continue
        }
        if ($addedByPath.ContainsKey($relative)) {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -cne $addedByPath[$relative]) {
                throw "Vendored $($Expected.packageName) added file changed after validation: $relative."
            }
            $actualAddedPaths.Add($relative)
            continue
        }
        $actualPaths.Add($relative)
        if (-not $sourceByPath.ContainsKey($relative)) {
            throw "Vendored $($Expected.packageName) contains an untracked source file: $relative."
        }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $patch = @($provenance.patches | Where-Object { [string]$_.path -ceq $relative })
        if ($patch.Count -eq 0) {
            if ($hash -cne $sourceByPath[$relative]) {
                throw "Unpatched vendored $($Expected.packageName) file differs from the pinned source: $relative."
            }
        }
        elseif ($patch.Count -ne 1 -or
            [string]$patch[0].upstreamSha256 -cne $sourceByPath[$relative] -or
            [string]$patch[0].patchedSha256 -cne $hash) {
            throw "Vendored $($Expected.packageName) patch provenance does not match the file: $relative."
        }
    }
    if ($actualPaths.Count -ne $sourceByPath.Count) {
        throw "Vendored $($Expected.packageName) file inventory drift: expected $($sourceByPath.Count), found $($actualPaths.Count)."
    }
    foreach ($sourcePath in $sourceByPath.Keys) {
        if (-not $actualPaths.Contains([string]$sourcePath)) {
            throw "Pinned vendored $($Expected.packageName) source file is missing: $sourcePath."
        }
    }
    if ($actualAddedPaths.Count -ne $addedByPath.Count) {
        throw "Vendored $($Expected.packageName) added-file inventory drift: expected $($addedByPath.Count), found $($actualAddedPaths.Count)."
    }
    foreach ($addedPath in $addedByPath.Keys) {
        if (-not $actualAddedPaths.Contains([string]$addedPath)) {
            throw "Pinned vendored $($Expected.packageName) added file is missing: $addedPath."
        }
    }

    $expectedPatchPaths = @($Expected.expectedPatchPaths)
    $patches = @($provenance.patches)
    if ($patches.Count -ne $expectedPatchPaths.Count) {
        throw "Vendored $($Expected.packageName) patch count drift: expected $($expectedPatchPaths.Count), found $($patches.Count)."
    }
    $patchByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $patchLines = [Collections.Generic.List[string]]::new()
    foreach ($patch in $patches) {
        $path = [string]$patch.path
        $upstreamHash = [string]$patch.upstreamSha256
        $patchedHash = [string]$patch.patchedSha256
        if ($expectedPatchPaths -cnotcontains $path -or $patchByPath.ContainsKey($path) -or
            $upstreamHash -notmatch '^[0-9a-f]{64}$' -or $patchedHash -notmatch '^[0-9a-f]{64}$' -or
            -not $sourceByPath.ContainsKey($path) -or $sourceByPath[$path] -cne $upstreamHash) {
            throw "Vendored $($Expected.packageName) has an unapproved or malformed patch entry: $path."
        }
        $patchByPath[$path] = $patch
        $patchLines.Add("$path $upstreamHash $patchedHash`n")
    }
    foreach ($path in $expectedPatchPaths) {
        if (-not $patchByPath.ContainsKey($path)) {
            throw "Vendored $($Expected.packageName) is missing its reviewed patch entry: $path."
        }
    }
    $patchLineArray = $patchLines.ToArray()
    [Array]::Sort($patchLineArray, [StringComparer]::Ordinal)
    $patchHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]::Concat($patchLineArray)))).ToLowerInvariant()
    if ($patchHash -cne [string]$Expected.patchSetSha256 -or
        $patchHash -cne [string]$provenance.patchSetSha256) {
        throw "Vendored $($Expected.packageName) patch-set digest does not match the independently pinned reviewed patch set."
    }

    $patchRecords = @($expectedPatchPaths | ForEach-Object {
        $patch = $patchByPath[$_]
        [ordered]@{
            path = [string]$patch.path
            upstreamSha256 = [string]$patch.upstreamSha256
            patchedSha256 = [string]$patch.patchedSha256
        }
    })

    [pscustomobject]@{
        PackageName = [string]$Expected.packageName
        PackageVersion = [string]$Expected.packageVersion
        Repository = [string]$Expected.repository
        Revision = [string]$Expected.revision
        PackagePath = [string]$Expected.packagePath
        SourceTree = [string]$Expected.sourceTree
        ArchiveSha256 = $expectedArchiveSha256
        License = if ($Expected.ContainsKey('vendoredLicense')) { [string]$Expected.vendoredLicense } else { [string]$Expected.license }
        UpstreamLicense = [string]$Expected.license
        ManifestPath = [IO.Path]::GetFullPath($manifestPath)
        SourceInventorySha256 = $sourceInventoryHash
        PatchSetSha256 = $patchHash
        Patches = $patchRecords
        AddedFilesSha256 = $addedFilesHash
        AddedFiles = $addedRecords
        SupplementalTestFixtureManifestSha256 = $supplementalManifestSha256
        SupplementalTestFixtures = $supplementalRecords
        SbomRecord = [ordered]@{
            name = [string]$Expected.packageName
            version = [string]$Expected.packageVersion
            repository = [string]$Expected.repository
            revision = [string]$Expected.revision
            packagePath = [string]$Expected.packagePath
            sourceTree = [string]$Expected.sourceTree
            archiveSha256 = $expectedArchiveSha256
            license = if ($Expected.ContainsKey('vendoredLicense')) { [string]$Expected.vendoredLicense } else { [string]$Expected.license }
            upstreamLicense = [string]$Expected.license
            sourceInventorySha256 = $sourceInventoryHash
            patchSetSha256 = $patchHash
            patches = $patchRecords
            addedFilesSha256 = $addedFilesHash
            addedFiles = $addedRecords
            supplementalTestFixtureManifestSha256 = $supplementalManifestSha256
            supplementalTestFixtures = $supplementalRecords
        }
    }
}

function Assert-ResvgNativePackageSource {
    param(
        [Parameter(Mandatory)] $Package,
        [Parameter(Mandatory)] $LockedPackage,
        [Parameter(Mandatory)][string] $ProviderRoot,
        [Parameter(Mandatory)][object[]] $VendoredSources
    )

    $identity = "$($Package.name)@$($Package.version)"
    $source = if ($null -eq $Package.source) { $null } else { [string]$Package.source }
    $lockedSource = if ($null -eq $LockedPackage.source) { $null } else { [string]$LockedPackage.source }
    $expectedManifest = if ($identity -ceq 'markdown-renderer-resvg-worker@1.0.0') {
        Join-Path $ProviderRoot 'native\Cargo.toml'
    }
    else {
        $vendoredPackage = @($VendoredSources | Where-Object {
            [string]$_.PackageName -ceq [string]$Package.name -and
            [string]$_.PackageVersion -ceq [string]$Package.version
        })
        if ($vendoredPackage.Count -gt 1) {
            throw "Multiple pinned vendored packages match $identity."
        }
        if ($vendoredPackage.Count -eq 1) { [string]$vendoredPackage[0].ManifestPath } else { $null }
    }

    if ($null -ne $expectedManifest) {
        if ($null -ne $source -or $null -ne $lockedSource) {
            throw "Local package $identity must not claim a registry or remote Cargo source."
        }
        $expectedFullPath = [IO.Path]::GetFullPath($expectedManifest)
        $actualFullPath = [IO.Path]::GetFullPath([string]$Package.manifest_path)
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals($expectedFullPath, $actualFullPath)) {
            throw "Local package $identity resolved from an unapproved manifest path: $actualFullPath."
        }
        if ($identity -ceq 'markdown-renderer-resvg-worker@1.0.0' -and [string]$Package.license -cne 'MIT') {
            throw 'The local worker crate license expression differs from the repository MIT license.'
        }
        if ($identity -cne 'markdown-renderer-resvg-worker@1.0.0') {
            $approvedVendorLicense = [string]$vendoredPackage[0].License
            if ([string]$Package.license -cne $approvedVendorLicense) {
                throw "Vendored $identity license expression differs from its pinned upstream license."
            }
        }
        return
    }

    if ([string]::IsNullOrWhiteSpace($source) -or [string]::IsNullOrWhiteSpace($lockedSource) -or
        -not [StringComparer]::Ordinal.Equals($source, $lockedSource)) {
        throw "Package $identity has an unapproved local/path source or source drift."
    }
}
