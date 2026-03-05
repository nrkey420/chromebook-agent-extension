param(
  [Parameter(Mandatory=$true)][string]$ResourceGroup,
  [Parameter(Mandatory=$true)][string]$Prefix,
  [string]$Location = "eastus"
)

if ($Location -ne "eastus") {
  throw "This PoC is standardized on Azure region eastus. Override is not supported in this script."
}

az group create -n $ResourceGroup -l $Location | Out-Null
az deployment group create -g $ResourceGroup -f infra/bicep/main.bicep -p prefix=$Prefix location=$Location
