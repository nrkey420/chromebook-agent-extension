# Packages the extension into dist/chromebook-activity-extension-v<version>.zip.
# Optional: -CollectorHostPermission overrides the collector host permission in the packaged manifest
# (default https://*.azurewebsites.net/*), e.g. 'https://collector.district.org/*'.
param([string]$CollectorHostPermission = $env:COLLECTOR_HOST_PERMISSION)
$ErrorActionPreference = 'Stop'

$ExtDir = Resolve-Path (Join-Path $PSScriptRoot '..')
$OutDir = Join-Path $ExtDir 'dist'
$Staging = Join-Path $OutDir '_staging'
New-Item -Path $OutDir -ItemType Directory -Force | Out-Null
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -Path $Staging -ItemType Directory | Out-Null

Copy-Item (Join-Path $ExtDir 'manifest.json') $Staging
Copy-Item (Join-Path $ExtDir 'sw.js') $Staging
Copy-Item (Join-Path $ExtDir 'src') $Staging -Recurse
Copy-Item (Join-Path $ExtDir 'policy') $Staging -Recurse

$manifestPath = Join-Path $Staging 'manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($CollectorHostPermission) {
  $manifest.host_permissions = @($CollectorHostPermission)
  $manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath
}

$zipPath = Join-Path $OutDir "chromebook-activity-extension-v$($manifest.version).zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $Staging '*') -DestinationPath $zipPath
Remove-Item $Staging -Recurse -Force

Write-Host "Created $zipPath"
