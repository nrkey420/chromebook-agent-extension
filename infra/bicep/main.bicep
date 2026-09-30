// PoC infrastructure: Function App (Flex Consumption, .NET 8 isolated) + Storage + App Insights + Key Vault + Azure SQL.
// No keys or passwords: storage, SQL and Key Vault are all accessed with the Function App's system-assigned
// managed identity. The storage account has shared-key access disabled.
// Azure SQL uses Microsoft Entra-only authentication (no SQL logins or passwords). The Function App connects
// with its system-assigned managed identity; its database user is created by infra/scripts/init-sql.*
// (run as the Entra admin), because Bicep cannot create database users.
// The HMAC key is stored in Key Vault and read through a Key Vault reference using the same identity.

@minLength(3)
@maxLength(20)
param prefix string = 'scpschrome'
param environment string = 'poc'
param location string = 'eastus'

@description('Object ID of the Microsoft Entra user or group that administers Azure SQL (a group is recommended).')
param sqlEntraAdminObjectId string

@description('Display name / UPN of the SQL Entra admin (shown in the portal; must match the principal).')
param sqlEntraAdminName string

@allowed([
  'User'
  'Group'
  'Application'
])
param sqlEntraAdminPrincipalType string = 'User'

@secure()
@description('Base64 HMAC shared secret for key id KEY1 (same value goes in the extension policy).')
param hmacKey string

@description('Optional object ID of a user or group to grant Key Vault Secrets Officer (read/rotate secrets). Leave empty to skip.')
param keyVaultAdminObjectId string = ''

@description('Flex Consumption: maximum number of instances the app can scale out to.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 40

@description('Flex Consumption: memory per instance.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

@description('Azure SQL serverless: maximum vCores the database can scale up to.')
@allowed([
  1
  2
  4
  6
  8
  10
  12
  14
  16
])
param sqlMaxVcores int = 2

@description('Azure SQL serverless: minimum vCores while online (decimal as string, e.g. "0.5"). Must be valid for sqlMaxVcores.')
param sqlMinVcores string = '0.5'

@description('Azure SQL serverless: minutes of inactivity before the database pauses (minimum 15), or -1 to never pause.')
param sqlAutoPauseDelayMinutes int = 60

@description('Azure SQL: maximum database size in GB.')
param sqlMaxSizeGB int = 32

var suffix = toLower(uniqueString(resourceGroup().id, prefix))
var storageName = take(toLower(replace('${prefix}${suffix}', '-', '')), 24)
var functionName = '${prefix}-func-${environment}'
var sqlServerName = '${prefix}-sql-${suffix}'
var sqlDbName = '${prefix}-db-${environment}'
var keyVaultName = '${take(prefix, 9)}kv${suffix}' // <= 24 chars
var deploymentContainerName = 'app-package-${take(toLower(functionName), 32)}'

// Built-in role definition IDs.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'

resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  #disable-next-line BCP334 // prefix is at least 3 chars plus a 13-char unique suffix
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // identity-only access
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: sa
  name: 'default'
}

// Flex Consumption deploys the app package into this container.
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
}

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-law-${environment}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi-${environment}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${prefix}-plan-${environment}'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true // Linux
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      login: sqlEntraAdminName
      sid: sqlEntraAdminObjectId
      tenantId: subscription().tenantId
      principalType: sqlEntraAdminPrincipalType
      azureADOnlyAuthentication: true
    }
  }
}

// Lets Azure services (including the Flex Consumption Function App, whose outbound IPs vary) reach SQL.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Serverless General Purpose: scales between sqlMinVcores and sqlMaxVcores, billed per second of vCore use,
// and pauses after sqlAutoPauseDelayMinutes of inactivity (storage is still billed while paused).
// The first connection after a pause fails while the database resumes (about a minute); the collector
// returns 503 and devices retry with their queued events.
resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: sqlDbName
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: sqlMaxVcores
  }
  properties: {
    minCapacity: json(sqlMinVcores)
    autoPauseDelay: sqlAutoPauseDelayMinutes
    maxSizeBytes: sqlMaxSizeGB * 1024 * 1024 * 1024
    zoneRedundant: false
  }
}

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
  }
}

// No secret: the Function App authenticates with its system-assigned managed identity.
var sqlConnectionString = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Initial Catalog=${db.name};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource hmacKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'HmacKey1'
  properties: { value: hmacKey }
}

// Versionless secret URIs, so a new secret version is picked up without redeploying.
func kvRef(secretUri string) string => '@Microsoft.KeyVault(SecretUri=${secretUri})'

resource func 'Microsoft.Web/sites@2024-04-01' = {
  name: functionName
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${sa.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      // Flex Consumption manages runtime, version and package settings through functionAppConfig;
      // FUNCTIONS_WORKER_RUNTIME, FUNCTIONS_EXTENSION_VERSION and WEBSITE_RUN_FROM_PACKAGE are not allowed.
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: sa.name } // identity-based host storage
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appi.properties.ConnectionString }
        { name: 'SQL_CONNECTION_STRING', value: sqlConnectionString }
        { name: 'HMAC_KEYS__KEY1', value: kvRef(hmacKeySecret.properties.secretUri) }
      ]
    }
  }
  dependsOn: [deploymentContainer]
}

// Host storage, deployment packages and the raw event archive all use the app's identity.
resource funcStorageBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sa
  name: guid(sa.id, func.id, storageBlobDataOwnerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// The Function App's identity may read secrets (needed to resolve the Key Vault references).
resource funcSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, func.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource adminSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyVaultAdminObjectId)) {
  scope: kv
  name: guid(kv.id, keyVaultAdminObjectId, keyVaultSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: keyVaultAdminObjectId
  }
}

output functionAppName string = func.name
output functionHostname string = func.properties.defaultHostName
output collectorUrl string = 'https://${func.properties.defaultHostName}'
output storageAccountName string = sa.name
output sqlServerName string = sql.name
output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output sqlDatabaseName string = db.name
output keyVaultName string = kv.name
