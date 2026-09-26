param(
    [switch]$NoBuild,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\winapp-sandbox')
)

$ErrorActionPreference = 'Stop'

function Invoke-WinAppJson {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowNoMatch
    )

    $output = & winapp @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not ($AllowNoMatch -and $exitCode -eq 1)) {
        throw "winapp $($Arguments -join ' ') failed ($exitCode): $($output | Out-String)"
    }

    $text = $output | Out-String
    $jsonStart = [regex]::Match($text, '(?m)^\s*\{')
    if (-not $jsonStart.Success) {
        throw "winapp did not return JSON: $text"
    }

    $result = $text.Substring($jsonStart.Index).Trim() | ConvertFrom-Json
    if ($exitCode -ne 0 -and $result.matchCount -ne 0) {
        throw "winapp $($Arguments -join ' ') failed ($exitCode): $text"
    }

    return $result
}

if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    throw 'WinApp CLI 0.7 or newer is required.'
}

$versionText = (& winapp --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionText -notmatch '(\d+)\.(\d+)\.(\d+)') {
    throw "Cannot determine WinApp CLI version: $versionText"
}

$version = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
if ($version -lt [version]'0.7.0') {
    throw "WinApp CLI 0.7 or newer is required; found $versionText."
}

$projectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj'))
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = "markdown-sandbox-$([guid]::NewGuid().ToString('N'))"
$sandboxStarted = $false

try {
    $runArguments = @('run', $projectPath, '--on', 'sandbox', '--detach', '--json', '-c', 'Debug', '--arch', 'x64')
    if ($NoBuild) {
        $runArguments += '--no-build'
    }

    $run = Invoke-WinAppJson -Arguments $runArguments
    if (-not $run.Sandbox -or $run.ProcessScope -ne 'sandbox') {
        throw 'WinApp CLI did not report a sandbox-scoped launch.'
    }
    $sandboxStarted = $true

    $readyDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $snapshot = Invoke-WinAppJson -Arguments @('target', 'snapshot', 'sandbox', '--json')
        if ($snapshot.running -and $snapshot.desktop.effectiveInputReady -and $snapshot.desktop.effectiveCaptureReady) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $readyDeadline)
    if (-not $snapshot.running -or -not $snapshot.desktop.effectiveInputReady -or -not $snapshot.desktop.effectiveCaptureReady) {
        throw 'The sandbox desktop is not ready for input and capture.'
    }

    $pages = @(
        @{ Key = 'Math'; Name = 'Math'; Heading = 'Native mathematics' },
        @{ Key = 'Mermaid'; Name = 'Mermaid'; Heading = 'Native Mermaid' },
        @{ Key = 'Html'; Name = 'Safe HTML'; Heading = 'Native safe HTML' },
        @{ Key = 'SvgStress'; Name = 'SVG rendering'; Heading = 'Browser-class static SVG' }
    )

    $results = foreach ($page in $pages) {
        $navigationId = "SampleNav_$($page.Key)"
        $matches = Invoke-WinAppJson -Arguments @(
            'ui', 'search', $page.Name, '--on', 'sandbox', '-a', 'MarkdownRenderer.Sample',
            '--root', 'MenuItemsScrollViewer', '--type', 'ListItem',
            '--class-name', 'Microsoft.UI.Xaml.Controls.NavigationViewItem', '--json'
        )
        if ($matches.matchCount -ne 1 -or $matches.matches[0].automationId -ne $navigationId) {
            throw "Expected one scoped navigation item $navigationId; found $($matches.matchCount)."
        }

        $null = Invoke-WinAppJson -Arguments @(
            'ui', 'invoke', $navigationId, '--on', 'sandbox', '-a', 'MarkdownRenderer.Sample',
            '--action', 'select', '--json'
        )
        $null = Invoke-WinAppJson -Arguments @(
            'ui', 'wait-for', 'MarkdownRenderer', '--on', 'sandbox', '-a', 'MarkdownRenderer.Sample',
            '--type', 'Document', '--value', $page.Heading, '--contains', '-t', '15000', '--json'
        )
        if ($page.Key -eq 'Mermaid') {
            # The document heading precedes asynchronous native compilation.
            # A real hyperlink appears only after the diagram scene is rendered.
            $diagram = Invoke-WinAppJson -Arguments @(
                'ui', 'wait-for', 'Invokable Mermaid node', '--on', 'sandbox', '-a', 'MarkdownRenderer.Sample',
                '--type', 'Hyperlink', '-t', '15000', '--json'
            )
            if ($diagram.element.isOffscreen -or $diagram.element.width -le 0 -or $diagram.element.height -le 0) {
                throw 'The Mermaid hyperlink exists in UIA but has no visible diagram geometry.'
            }
            $unavailable = Invoke-WinAppJson -AllowNoMatch -Arguments @(
                'ui', 'search', 'MMR0006', '--on', 'sandbox', '-a', 'MarkdownRenderer.Sample',
                '--json'
            )
            if ($unavailable.matchCount -gt 0) {
                throw 'The sandbox sample reports MMR0006: the native Mermaid engine is unavailable.'
            }
        }
        if ($page.Key -eq 'SvgStress') {
            # An exact accessible image name replaces the transient Loading name
            # only after the isolated worker has produced a raster.
            $svgImage = Invoke-WinAppJson -Arguments @(
                'ui', 'wait-for', 'Theme-aware SVG feature matrix', '--on', 'sandbox',
                '-a', 'MarkdownRenderer.Sample', '--type', 'Image', '--property', 'Name',
                '--value', 'Theme-aware SVG feature matrix', '-t', '15000', '--json'
            )
            if ($svgImage.element.isOffscreen -or $svgImage.element.width -le 0 -or $svgImage.element.height -le 0) {
                throw 'The SVG image loaded but has no visible image geometry.'
            }
        }

        $imagePath = Join-Path $outputPath "$($page.Key).png"
        $capture = Invoke-WinAppJson -Arguments @(
            'ui', 'screenshot', '--on', 'sandbox', '-a', 'MarkdownRenderer.Sample',
            '-o', $imagePath, '--json'
        )
        if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
            throw "Sandbox screenshot was not written: $imagePath"
        }

        [pscustomobject]@{
            page = $page.Key
            heading = $page.Heading
            screenshot = $imagePath
            width = $capture.width
            height = $capture.height
        }
    }

    $results | ConvertTo-Json -Depth 4
}
finally {
    if ($sandboxStarted) {
        try {
            & winapp ui yield --on sandbox --quiet | Out-Null
        }
        catch {
            Write-Warning "Could not release the sandbox UI turn: $_"
        }
    }
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId
}
