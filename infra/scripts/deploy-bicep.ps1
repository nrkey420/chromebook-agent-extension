param([string]$ResourceGroup='rg-chromebook-poc',[string]$Location='eastus')
az group create -n $ResourceGroup -l $Location | Out-Null
az deployment group create -g $ResourceGroup -f infra/bicep/main.bicep -p @infra/bicep/main.parameters.json -o table
