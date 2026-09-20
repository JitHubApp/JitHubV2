[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$Count = 500,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is required to generate the live top-repository corpus."
}

& gh auth status 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI must be authenticated before generating the README corpus."
}

$repositories = [System.Collections.Generic.List[object]]::new()
$pageCount = [Math]::Ceiling($Count / 100)
for ($page = 1; $page -le $pageCount; $page++) {
    $response = & gh api -X GET search/repositories `
        -f q='stars:>1' `
        -f sort='stars' `
        -f order='desc' `
        -f per_page='100' `
        -f page=$page | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub repository search failed for page $page."
    }

    foreach ($item in $response.items) {
        if ($repositories.Count -ge $Count) { break }
        $repositories.Add($item)
    }
}

if ($repositories.Count -ne $Count) {
    throw "GitHub search returned $($repositories.Count) repositories; expected $Count."
}

$duplicateRepositoryIds = @($repositories | Group-Object id | Where-Object Count -ne 1)
$duplicateRepositoryNames = @($repositories | Group-Object full_name | Where-Object Count -ne 1)
if ($duplicateRepositoryIds.Count -ne 0 -or $duplicateRepositoryNames.Count -ne 0) {
    throw "GitHub search pagination returned duplicate repositories while rankings were changing; regenerate the corpus."
}

$rankedRepositories = for ($index = 0; $index -lt $repositories.Count; $index++) {
    [pscustomobject]@{
        Rank = $index + 1
        Repository = $repositories[$index]
    }
}

# Starting one gh process per repository serially makes a 500-entry refresh
# needlessly slow. Keep fan-out bounded so the generator remains friendly to
# both GitHub's API and constrained CI hosts, then restore deterministic rank
# order before writing the manifest.
$manifestRepositories = @(@($rankedRepositories | ForEach-Object -Parallel {
    function Invoke-GitHubJson([string]$route, [string]$ref, [bool]$allowNotFound) {
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            $apiOutput = if ([string]::IsNullOrWhiteSpace($ref)) {
                & gh api -X GET $route 2>&1
            }
            else {
                & gh api -X GET $route -f "ref=$ref" 2>&1
            }
            $exitCode = $LASTEXITCODE
            $text = $apiOutput -join [Environment]::NewLine
            if ($exitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($text)) {
                return $text | ConvertFrom-Json
            }

            if ($allowNotFound -and $text -match 'HTTP 404') {
                return $null
            }

            if ($attempt -lt 4) {
                Start-Sleep -Milliseconds (250 * [Math]::Pow(2, $attempt - 1))
                continue
            }

            throw "GitHub API failed after $attempt attempts for '$route': $text"
        }
    }

    $ranked = $_
    $repository = $ranked.Repository
    $commit = Invoke-GitHubJson `
        "repos/$($repository.full_name)/commits/$($repository.default_branch)" `
        "" `
        $false
    $readme = Invoke-GitHubJson `
        "repos/$($repository.full_name)/readme" `
        ([string]$commit.sha) `
        $true

    [pscustomobject][ordered]@{
        rank = [int]$ranked.Rank
        id = [long]$repository.id
        fullName = [string]$repository.full_name
        defaultBranch = [string]$repository.default_branch
        commitSha = if ($null -ne $commit) { [string]$commit.sha } else { "" }
        stars = [long]$repository.stargazers_count
        url = [string]$repository.html_url
        pushedAtUtc = [DateTimeOffset]$repository.pushed_at
        readme = [pscustomobject][ordered]@{
            available = $null -ne $readme
            byteSize = if ($null -ne $readme) { [long]$readme.size } else { 0 }
            path = if ($null -ne $readme) { [string]$readme.path } else { "" }
            sha = if ($null -ne $readme) { [string]$readme.sha } else { "" }
            htmlUrl = if ($null -ne $readme) { [string]$readme.html_url } else { "" }
            downloadUrl = if ($null -ne $readme) { [string]$readme.download_url } else { "" }
        }
    }
} -ThrottleLimit 6) | Sort-Object rank)

$missingCommits = @($manifestRepositories | Where-Object {
    [string]::IsNullOrWhiteSpace($_.commitSha)
})
if ($missingCommits.Count -ne 0) {
    $names = ($missingCommits | Select-Object -First 10 -ExpandProperty fullName) -join ", "
    throw "Could not pin a commit for $($missingCommits.Count) repositories: $names"
}

$manifest = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTimeOffset]::UtcNow
    source = "GitHub REST API"
    query = "stars:>1 sort:stars order:desc"
    requestedCount = $Count
    repositories = $manifestRepositories
}
$temporaryPath = "$OutputPath.tmp"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
$withoutReadme = @($manifestRepositories | Where-Object { -not $_.readme.available }).Count
Write-Host "Pinned $Count repositories to '$OutputPath' ($withoutReadme without a README)."
