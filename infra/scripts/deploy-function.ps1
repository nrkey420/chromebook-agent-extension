param(
  [Parameter(Mandatory=$true)][string]$FunctionApp,
  [Parameter(Mandatory=$true)][string]$ResourceGroup
)

$root = Resolve-Path (Join-Path $PSScriptRoot '../..') # repo root, so the script works from any folder
$zip = Join-Path $root 'collector.zip'
Push-Location (Join-Path $root 'collector/src/ChromeCollector.FunctionApp')
dotnet publish -c Release -o publish
Push-Location publish
Compress-Archive -Path * -DestinationPath $zip -Force
Pop-Location
Pop-Location

az functionapp deployment source config-zip -g $ResourceGroup -n $FunctionApp --src $zip
