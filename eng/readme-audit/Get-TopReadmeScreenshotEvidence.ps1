function Add-TopReadmeAuditTileSetFailures {
    param(
        [AllowNull()][object[]]$Tiles,
        [Parameter(Mandatory)][string]$TileRoot,
        [Parameter(Mandatory)][string]$FilePrefix,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][int]$Rank,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[string]]$Failures,
        [switch]$Required
    )

    $tileItems = @($Tiles)
    if ($tileItems.Count -eq 0) {
        if ($Required) { $Failures.Add("Rank $Rank is missing required $Kind tile metadata.") }
        return
    }
    if ($tileItems.Count -gt 512) {
        $Failures.Add("Rank $Rank has too many $Kind tile metadata entries.")
        return
    }

    for ($index = 0; $index -lt $tileItems.Count; $index++) {
        $tile = $tileItems[$index]
        $expectedName = '{0}-{1:D4}.png' -f $FilePrefix, $index
        if ([int]$tile.index -ne $index -or [string]$tile.file -cne $expectedName) {
            $Failures.Add("Rank $Rank has malformed $Kind tile identity at index $index.")
            continue
        }

        $path = Join-Path $TileRoot $expectedName
        $file = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if ($null -eq $file -or $file.PSIsContainer -or $file.Length -le 0 -or
            $file.Length -gt 256MB -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $Failures.Add("Rank $Rank is missing a bounded, nonempty $Kind tile file '$expectedName'.")
        }
    }
}

function Get-TopReadmeScreenshotEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$AuditRoot,

        [switch]$RequireSameByteTiles
    )

    $AuditRoot = [IO.Path]::GetFullPath($AuditRoot)
    $casesRoot = Join-Path $AuditRoot 'cases'
    $extensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @('.png', '.jpg', '.jpeg', '.webp')) {
        [void]$extensions.Add($extension)
    }

    [long]$screenshotBytes = 0
    $screenshotCount = 0
    $tileValidationFailures = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $casesRoot -PathType Container) {
        $caseResultFiles = @(Get-ChildItem -LiteralPath $casesRoot -Recurse -File -Filter 'result.json' -ErrorAction Stop)
        if ($caseResultFiles.Count -eq 0) {
            $tileValidationFailures.Add('The shard has no case result files from which to validate expected screenshot tiles.')
        }
        foreach ($caseResultFile in $caseResultFiles) {
            try {
                if ($caseResultFile.Length -le 0 -or $caseResultFile.Length -gt 8MB -or
                    ($caseResultFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'invalid result file size or type'
                }
                $case = Get-Content -LiteralPath $caseResultFile.FullName -Raw | ConvertFrom-Json -Depth 16
                $rank = [int]$case.rank
                $caseDirectory = $caseResultFile.DirectoryName
                $browserRoot = Join-Path $caseDirectory 'browser'
                $nativeRoot = Join-Path $caseDirectory 'native'
                $renderedReadme = $case.browser.readmeRendered -eq $true
                Add-TopReadmeAuditTileSetFailures -Tiles $case.browser.tiles -TileRoot $browserRoot `
                    -FilePrefix 'tile' -Kind 'live Edge' -Rank $rank -Failures $tileValidationFailures -Required:$renderedReadme
                Add-TopReadmeAuditTileSetFailures -Tiles $case.native.tiles -TileRoot $nativeRoot `
                    -FilePrefix 'tile' -Kind 'native' -Rank $rank -Failures $tileValidationFailures -Required:$renderedReadme

                if ($RequireSameByteTiles -and $renderedReadme) {
                    Add-TopReadmeAuditTileSetFailures -Tiles $case.browser.sameByteHtmlReplay.tiles `
                        -TileRoot $browserRoot -FilePrefix 'same-byte-tile' `
                        -Kind 'captured-HTML Edge' -Rank $rank -Failures $tileValidationFailures -Required
                    Add-TopReadmeAuditTileSetFailures -Tiles $case.browser.sameByteReplay.tiles `
                        -TileRoot (Join-Path $browserRoot 'source-replay') -FilePrefix 'same-byte-source-tile' `
                        -Kind 'source-bound Edge' -Rank $rank -Failures $tileValidationFailures -Required
                }
            }
            catch {
                $tileValidationFailures.Add("Case result '$($caseResultFile.Name)' could not be checked for screenshot tile identity.")
            }
        }

        foreach ($caseDirectory in @(Get-ChildItem -LiteralPath $casesRoot -Directory -ErrorAction Stop)) {
            foreach ($kind in @('browser', 'native')) {
                $kindRoot = Join-Path $caseDirectory.FullName $kind
                if (-not (Test-Path -LiteralPath $kindRoot -PathType Container)) { continue }
                foreach ($file in @(Get-ChildItem -LiteralPath $kindRoot -Recurse -File -ErrorAction Stop)) {
                    if (-not $extensions.Contains($file.Extension)) { continue }

                    if ($kind -eq 'browser') {
                        $relativePath = [IO.Path]::GetRelativePath($kindRoot, $file.FullName)
                        $segments = $relativePath -split '[\\/]'
                        if ($segments.Count -ge 3 -and
                            $segments[0] -like 'same-byte-corpus-*' -and
                            $segments[1] -ieq 'assets') {
                            continue
                        }
                    }

                    $screenshotBytes += [long]$file.Length
                    $screenshotCount++
                }
            }
        }
    }

    return [pscustomobject]@{
        screenshotCount = $screenshotCount
        screenshotBytes = $screenshotBytes
        caseTileValidationStatus = if ($tileValidationFailures.Count -eq 0) { 'passed' } else { 'failed' }
        caseTileValidationFailureCount = $tileValidationFailures.Count
        caseTileValidationFailures = @($tileValidationFailures | Select-Object -First 20)
    }
}
