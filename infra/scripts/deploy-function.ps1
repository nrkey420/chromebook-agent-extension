param(
  [Parameter(Mandatory=$true)][string]$FunctionApp,
  [Parameter(Mandatory=$true)][string]$ResourceGroup
)

Push-Location collector/src/ChromebookCollector
dotnet publish -c Release -o publish
Push-Location publish
Compress-Archive -Path * -DestinationPath ../../../../collector.zip -Force
Pop-Location
Pop-Location

az functionapp deployment source config-zip -g $ResourceGroup -n $FunctionApp --src collector.zip
