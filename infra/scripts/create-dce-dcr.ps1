param(
  [Parameter(Mandatory=$true)][string]$ResourceGroup,
  [string]$Location = "eastus",
  [Parameter(Mandatory=$true)][string]$WorkspaceResourceId,
  [string]$DceName = "chromebook-dce",
  [string]$DcrName = "chromebook-dcr"
)

if ($Location -ne "eastus") {
  throw "This PoC is standardized on Azure region eastus. Override is not supported in this script."
}

$dceId = az monitor data-collection endpoint create -g $ResourceGroup -n $DceName -l $Location --public-network-access Enabled --query id -o tsv

$rule = @{
  properties = @{
    streamDeclarations = @{
      "Custom-ChromebookActivity_CL" = @{
        columns = @(
          @{name="TimeGenerated";type="datetime"}, @{name="EventType";type="string"}, @{name="Url";type="string"}, @{name="Title";type="string"},
          @{name="DownloadState";type="string"}, @{name="DownloadDanger";type="string"}, @{name="UserEmail";type="string"}, @{name="DirectoryDeviceId";type="string"},
          @{name="PayloadJson";type="string"}, @{name="Source";type="string"}
        )
      }
    }
    destinations = @{ logAnalytics = @(@{ workspaceResourceId = $WorkspaceResourceId; name = "la" }) }
    dataFlows = @(@{ streams = @("Custom-ChromebookActivity_CL"); destinations = @("la"); outputStream = "Custom-ChromebookActivity_CL" })
  }
} | ConvertTo-Json -Depth 8

$tmp = New-TemporaryFile
$rule | Set-Content -Path $tmp
az monitor data-collection rule create -g $ResourceGroup -n $DcrName -l $Location --data-collection-endpoint-id $dceId --rule-file $tmp
Remove-Item $tmp -Force
