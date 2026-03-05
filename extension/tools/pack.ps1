$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExtDir = Resolve-Path (Join-Path $ScriptDir '..')
$OutDir = Join-Path $ExtDir 'dist'
if (!(Test-Path $OutDir)) {
  New-Item -Path $OutDir -ItemType Directory | Out-Null
}

$manifest = Get-Content (Join-Path $ExtDir 'manifest.json') -Raw | ConvertFrom-Json
$zipName = "chromebook-activity-extension-v$($manifest.version).zip"
$zipPath = Join-Path $OutDir $zipName

if (Test-Path $zipPath) {
  Remove-Item $zipPath -Force
}

$staging = Join-Path $OutDir '_staging'
if (Test-Path $staging) {
  Remove-Item $staging -Recurse -Force
}
New-Item -Path $staging -ItemType Directory | Out-Null

Copy-Item (Join-Path $ExtDir 'manifest.json') $staging
Copy-Item (Join-Path $ExtDir 'sw.js') $staging
Copy-Item (Join-Path $ExtDir 'src') $staging -Recurse

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
Remove-Item $staging -Recurse -Force

Write-Host "Created $zipPath"
