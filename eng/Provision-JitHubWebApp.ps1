param(
    [string]$ResourceGroup = 'JitHub',
    [string]$Location = 'westus',
    [string]$PlanName = 'ASP-JitHub-Web',
    [string]$WebAppName = 'jithub-web-prod',
    [string]$Sku = 'B1',
    [string]$Runtime = 'dotnet:10'
)

$ErrorActionPreference = 'Stop'

function Get-AzureCliCommand {
    $azCommand = Get-Command az -ErrorAction SilentlyContinue
    if ($azCommand) {
        return $azCommand.Source
    }

    $defaultWindowsPath = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
    if (Test-Path -LiteralPath $defaultWindowsPath) {
        return $defaultWindowsPath
    }

    throw 'Azure CLI was not found. Install Azure CLI or add az to PATH before running this script.'
}

function Invoke-Az {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $script:AzPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') failed."
    }
}

function Invoke-AzJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $previousNativeCommandPreference = $null
    $hasNativeCommandPreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue

    if ($hasNativeCommandPreference) {
        $previousNativeCommandPreference = $global:PSNativeCommandUseErrorActionPreference
    }

    try {
        $ErrorActionPreference = 'Continue'
        if ($hasNativeCommandPreference) {
            $global:PSNativeCommandUseErrorActionPreference = $false
        }

        $json = & $script:AzPath @Arguments --output json 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($hasNativeCommandPreference) {
            $global:PSNativeCommandUseErrorActionPreference = $previousNativeCommandPreference
        }
    }

    if ($exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    return $json | ConvertFrom-Json
}

$script:AzPath = Get-AzureCliCommand

$account = Invoke-AzJson -Arguments @('account', 'show')
if (-not $account) {
    throw 'Azure CLI is not logged in. Run az login, then retry.'
}

Write-Host "Using Azure subscription '$($account.name)' ($($account.id))."

$resourceGroupInfo = Invoke-AzJson -Arguments @('group', 'show', '--name', $ResourceGroup)
if (-not $resourceGroupInfo) {
    Write-Host "Creating resource group '$ResourceGroup' in '$Location'."
    Invoke-Az -Arguments @('group', 'create', '--name', $ResourceGroup, '--location', $Location, '--output', 'none')
}

$existingSite = Invoke-AzJson -Arguments @(
    'resource',
    'show',
    '--resource-group', $ResourceGroup,
    '--resource-type', 'Microsoft.Web/sites',
    '--name', $WebAppName
)

if ($existingSite -and $existingSite.kind -like '*functionapp*') {
    throw @"
The site name '$WebAppName' is already used by an Azure Function App.

The unified JitHub website is a regular ASP.NET Core App Service Web App, so it cannot be provisioned over that existing Function App.

Choose one migration path:
1. Delete/retire the Function App, then rerun this script with -WebAppName '$WebAppName' to preserve https://$WebAppName.azurewebsites.net.
2. Provision a different Web App name, then set GitHub variable JITHUB_WEBAPP_NAME to that name and update GitHub OAuth/custom-domain routing separately.
"@
}

$plan = Invoke-AzJson -Arguments @(
    'appservice',
    'plan',
    'show',
    '--resource-group', $ResourceGroup,
    '--name', $PlanName
)

if (-not $plan) {
    Write-Host "Creating Windows App Service plan '$PlanName' ($Sku) in '$Location'."
    Invoke-Az -Arguments @(
        'appservice',
        'plan',
        'create',
        '--resource-group', $ResourceGroup,
        '--name', $PlanName,
        '--location', $Location,
        '--sku', $Sku,
        '--output', 'none'
    )
}

$webApp = Invoke-AzJson -Arguments @(
    'webapp',
    'show',
    '--resource-group', $ResourceGroup,
    '--name', $WebAppName
)

if (-not $webApp) {
    Write-Host "Creating Web App '$WebAppName' with runtime '$Runtime'."
    Invoke-Az -Arguments @(
        'webapp',
        'create',
        '--resource-group', $ResourceGroup,
        '--plan', $PlanName,
        '--name', $WebAppName,
        '--runtime', $Runtime,
        '--output', 'none'
    )
}

Write-Host "Applying baseline Web App configuration."
Invoke-Az -Arguments @(
    'webapp',
    'update',
    '--resource-group', $ResourceGroup,
    '--name', $WebAppName,
    '--https-only',
    'true',
    '--output',
    'none'
)

Invoke-Az -Arguments @(
    'webapp',
    'config',
    'set',
    '--resource-group', $ResourceGroup,
    '--name', $WebAppName,
    '--always-on',
    'true',
    '--output',
    'none'
)

Write-Host ''
Write-Host 'Provisioning complete. Next steps:'
Write-Host '1. Configure app settings for the GitHub OAuth app:'
Write-Host "   az webapp config appsettings set -g $ResourceGroup -n $WebAppName --settings JitHubClientId=<client-id> JithubAppSecret=<client-secret>"
Write-Host '2. Download the publish profile:'
Write-Host "   az webapp deployment list-publishing-profiles -g $ResourceGroup -n $WebAppName --xml > jithub-webapp.PublishSettings"
Write-Host '3. Save it as the GitHub secret JITHUB_WEBAPP_PUBLISH_PROFILE:'
Write-Host '   gh secret set JITHUB_WEBAPP_PUBLISH_PROFILE --repo JitHubApp/JitHubV2 < jithub-webapp.PublishSettings'
Write-Host '4. Save the target app name as a GitHub Actions variable:'
Write-Host "   gh variable set JITHUB_WEBAPP_NAME --repo JitHubApp/JitHubV2 --body $WebAppName"
Write-Host '5. Move custom domains/OAuth callback hosts after the new Web App passes a smoke test.'
