param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$Location,
    [Parameter(Mandatory)][string]$ResourceGroupName,
    [Parameter(Mandatory)][string]$AppServicePlanName,
    [Parameter(Mandatory)][string]$LogAnalyticsName,
    [Parameter(Mandatory)][string]$ApplicationInsightsName,
    [Parameter(Mandatory)][string]$WebAppName,
    [Parameter(Mandatory)][string]$VaultName,
    [Parameter(Mandatory)][string]$DeploymentIdentityName,
    [Parameter(Mandatory)][string]$OAuthClientId,
    [Parameter(Mandatory)][string]$OAuthCallbackUrl,
    [string]$GithubBranch = 'main',
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot '..\infra\production.bicep'

function Invoke-AzureCli {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments[0..([Math]::Min(2, $Arguments.Length - 1))] -join ' ')"
    }
    return $result
}

$account = Invoke-AzureCli -Arguments @('account', 'show', '--subscription', $subscriptionId, '--output', 'json') | ConvertFrom-Json
if ($account.id -ne $subscriptionId -or $account.tenantId -ne $tenantId) {
    throw 'Azure CLI is not signed in to the intended subscription and tenant.'
}

foreach ($provider in @('Microsoft.Web', 'Microsoft.ManagedIdentity', 'Microsoft.KeyVault', 'Microsoft.OperationalInsights', 'Microsoft.Insights')) {
    $registrationState = Invoke-AzureCli -Arguments @('provider', 'show', '--namespace', $provider, '--subscription', $subscriptionId, '--query', 'registrationState', '--output', 'tsv')
    if ($registrationState -ne 'Registered') {
        Invoke-AzureCli -Arguments @('provider', 'register', '--namespace', $provider, '--subscription', $subscriptionId, '--wait', '--output', 'none') | Out-Null
    }
}

# These global names must be checked before any resource is created.
foreach ($check in @(
    @{ Name = $webAppName; Type = 'Microsoft.Web/sites'; ApiVersion = '2024-11-01'; Provider = 'Microsoft.Web'; Action = 'checknameavailability' },
    @{ Name = $vaultName; Type = 'Microsoft.KeyVault/vaults'; ApiVersion = '2023-07-01'; Provider = 'Microsoft.KeyVault'; Action = 'checkNameAvailability' }
)) {
    $body = @{ name = $check.Name; type = $check.Type } | ConvertTo-Json -Compress
    $url = "https://management.azure.com/subscriptions/$subscriptionId/providers/$($check.Provider)/$($check.Action)?api-version=$($check.ApiVersion)"
    $bodyFile = New-TemporaryFile
    try {
        Set-Content -LiteralPath $bodyFile.FullName -Value $body -NoNewline -Encoding utf8
        $result = Invoke-AzureCli -Arguments @('rest', '--method', 'post', '--url', $url, '--headers', 'Content-Type=application/json', '--body', "@$($bodyFile.FullName)", '--output', 'json') | ConvertFrom-Json
    }
    finally {
        Remove-Item -LiteralPath $bodyFile.FullName -ErrorAction SilentlyContinue
    }
    if (-not $result.nameAvailable) {
        $expectedId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroupName/providers/$($check.Type)/$($check.Name)"
        $existingId = & az resource show --ids $expectedId --subscription $subscriptionId --query id --output tsv 2>$null
        if ($LASTEXITCODE -ne 0 -or -not [string]::Equals($existingId, $expectedId, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The global name '$($check.Name)' is unavailable: $($result.reason). Revise both Bicep and this script before deployment."
        }
        Write-Host "Already owned in the target group: $($check.Name)"
        continue
    }
    Write-Host "Available: $($check.Name)"
}

Invoke-AzureCli -Arguments @('bicep', 'build', '--file', $template, '--stdout') | Out-Null
Write-Host 'Bicep build passed.'

$deploymentName = 'jithub-production-website'
$templateParameters = @(
    "location=$Location"
    "resourceGroupName=$ResourceGroupName"
    "appServicePlanName=$AppServicePlanName"
    "logAnalyticsName=$LogAnalyticsName"
    "applicationInsightsName=$ApplicationInsightsName"
    "webAppName=$WebAppName"
    "keyVaultName=$VaultName"
    "deploymentIdentityName=$DeploymentIdentityName"
    "oauthClientId=$OAuthClientId"
    "oauthCallbackUrl=$OAuthCallbackUrl"
    "githubBranch=$GithubBranch"
)
Invoke-AzureCli -Arguments (@('deployment', 'sub', 'what-if', '--name', $deploymentName, '--location', $Location, '--subscription', $SubscriptionId, '--template-file', $template, '--parameters') + $templateParameters + @('--output', 'table'))

if (-not $Deploy) {
    Write-Host 'Review the what-if, then rerun with -Deploy to create the resources.'
    return
}

Invoke-AzureCli -Arguments (@('deployment', 'sub', 'create', '--name', $deploymentName, '--location', $Location, '--subscription', $SubscriptionId, '--template-file', $template, '--parameters') + $templateParameters + @('--output', 'none')) | Out-Null

# Application Insights creates this action group outside the Bicep deployment.
# Give it the same tags as the group once Azure has materialized it.
$smartDetectionId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroupName/providers/Microsoft.Insights/actionGroups/Application Insights Smart Detection"
$groupTags = Invoke-AzureCli -Arguments @('group', 'show', '--name', $resourceGroupName, '--subscription', $subscriptionId, '--query', 'tags', '--output', 'json') | ConvertFrom-Json
$tagPairs = @($groupTags.PSObject.Properties | ForEach-Object { '{0}={1}' -f $_.Name, $_.Value })
$smartDetectionFound = $false
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    $existingId = & az resource show --ids $smartDetectionId --subscription $subscriptionId --api-version 2023-01-01 --query id --output tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and $existingId) {
        Invoke-AzureCli -Arguments (@('resource', 'tag', '--ids', $smartDetectionId, '--subscription', $subscriptionId, '--tags') + $tagPairs + @('--output', 'none')) | Out-Null
        $smartDetectionFound = $true
        break
    }
    Start-Sleep -Seconds 5
}
if (-not $smartDetectionFound) {
    throw 'Application Insights Smart Detection action group was not found for tagging after deployment.'
}

Write-Host 'Website resources created. Complete the private secret, DNS, TLS, OAuth, and release cutover checklist.'
