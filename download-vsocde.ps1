# Get the current script path
$scriptPath = Split-Path -Parent -Path $MyInvocation.MyCommand.Definition

# Set the GitHub repository name
$repoName = "nerocui/jithub-vs-code"

# Set the asset pattern for the zip file
$assetPattern = "*-vs-code.zip"

# Set the destination path for the zip file
$zipPath = Join-Path -Path $scriptPath -ChildPath "jithub-vs-code.zip"

# Set the destination path for the extracted folder
$folderPath = Join-Path -Path $scriptPath -ChildPath "JitHub\Assets"

# Call the GitHub API to get the latest release information
$releasesUri = "https://api.github.com/repos/$repoName/releases/latest"
$release = Invoke-RestMethod -Uri $releasesUri

# Find the download URL for the zip file that matches the asset pattern
$downloadUrl = $release.assets | Where-Object { $_.name -like $assetPattern } | Select-Object -ExpandProperty browser_download_url

# Download the zip file using Invoke-WebRequest
Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

# Extract the zip file using Expand-Archive
Expand-Archive -Path $zipPath -DestinationPath $folderPath

# Remove the zip file using Remove-Item
Remove-Item -Path $zipPath
