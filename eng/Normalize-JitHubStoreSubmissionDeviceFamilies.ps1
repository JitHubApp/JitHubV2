param(
    [Parameter(Mandatory = $true)]
    [string]$ProductId,

    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [string]$ClientId,

    [Parameter(Mandatory = $true)]
    [string]$ClientSecret
)

$ErrorActionPreference = 'Stop'

function Test-RequiredValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required."
    }
}

function Invoke-DevCenterJson {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('GET', 'POST', 'PUT')]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [object]$Body,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers
    )

    $uri = "https://manage.devcenter.microsoft.com$Path"
    $parameters = @{
        Method = $Method
        Uri = $uri
        Headers = $Headers
    }

    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 100
        $parameters.ContentType = 'application/json'
    }

    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        $message = $_.Exception.Message
        $response = $_.Exception.Response
        if (-not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            $message = "$message Response: $($_.ErrorDetails.Message)"
        }
        elseif ($response -and ($response | Get-Member -Name 'Content' -MemberType Property) -and $response.Content) {
            $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
                $message = "$message Response: $responseBody"
            }
        }
        elseif ($response -and ($response | Get-Member -Name 'GetResponseStream' -MemberType Method) -and $response.GetResponseStream()) {
            $reader = [System.IO.StreamReader]::new($response.GetResponseStream())
            try {
                $responseBody = $reader.ReadToEnd()
                if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
                    $message = "$message Response: $responseBody"
                }
            }
            finally {
                $reader.Dispose()
            }
        }

        throw $message
    }
}

Test-RequiredValue -Name 'ProductId' -Value $ProductId
Test-RequiredValue -Name 'TenantId' -Value $TenantId
Test-RequiredValue -Name 'ClientId' -Value $ClientId
Test-RequiredValue -Name 'ClientSecret' -Value $ClientSecret

$tokenResponse = Invoke-RestMethod `
    -Method Post `
    -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{
        client_id = $ClientId
        client_secret = $ClientSecret
        scope = 'https://manage.devcenter.microsoft.com/.default'
        grant_type = 'client_credentials'
    }

if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
    throw 'Could not retrieve a Dev Center access token.'
}

$headers = @{
    Authorization = "Bearer $($tokenResponse.access_token)"
}

$application = Invoke-DevCenterJson -Method GET -Path "/v1.0/my/applications/$ProductId" -Headers $headers
$submissionId = $application.PendingApplicationSubmission.Id

if ([string]::IsNullOrWhiteSpace($submissionId)) {
    throw "No pending Store submission was found for product $ProductId."
}

$submission = Invoke-DevCenterJson -Method GET -Path "/v1.0/my/applications/$ProductId/submissions/$submissionId" -Headers $headers

$desktopOnlyDeviceFamilies = [ordered]@{
    Desktop = $true
    Mobile = $false
    Holographic = $false
    Xbox = $false
    Team = $false
    Core = $false
}

$submission | Add-Member -NotePropertyName 'AllowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies' -NotePropertyValue $false -Force
$submission | Add-Member -NotePropertyName 'AllowTargetFutureDeviceFamilies' -NotePropertyValue $desktopOnlyDeviceFamilies -Force

$null = Invoke-DevCenterJson -Method PUT -Path "/v1.0/my/applications/$ProductId/submissions/$submissionId" -Headers $headers -Body $submission

Write-Host "Normalized Store submission device families to Desktop-only for submission $submissionId."
