@description('Primary Azure region for all resources.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Stable token used to make resource names globally unique.')
param resourceToken string

param universalPrintTenantId string
param universalPrintDefaultPrinterId string
param oracleBiBaseUrl string
param oracleBiUsername string

@secure()
param oracleBiPassword string

@description('Maximum number of Flex Consumption instances the app may scale out to.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

@description('Per-instance memory (MB) for the Flex Consumption plan.')
@allowed([512, 2048, 4096])
param instanceMemoryMB int = 2048

@description('Lock down storage/Key Vault to private endpoints and integrate the app with a VNet.')
param enableNetworkIsolation bool = false

@description('VNet integration subnet (set only when enableNetworkIsolation = true).')
param functionsSubnetResourceId string = ''

@description('Private-endpoint subnet (set only when enableNetworkIsolation = true).')
param privateEndpointSubnetResourceId string = ''

param blobPrivateDnsZoneResourceId string = ''
param queuePrivateDnsZoneResourceId string = ''
param vaultPrivateDnsZoneResourceId string = ''

var pollQueueName = 'print-poll'
var submitQueueName = 'print-submit'
var deadLetterQueueName = 'print-poll-deadletter'
var deploymentContainerName = 'app-package'
var idempotencyContainerName = 'idempotency'
var keyVaultName = 'kv-${resourceToken}'
var oracleBiPasswordSecretName = 'oracle-bi-password'

// Built-in role definition ids used for managed-identity data-plane access.
// (Storage Blob Data Owner is assigned automatically by the site module's identity-based wiring.)
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

// Queue endpoint is derived deterministically so we don't depend on optional service endpoints.
var queueServiceUri = 'https://st${resourceToken}.queue.${environment().suffixes.storage}/'
// Blob endpoint for the submit-idempotency container (identity-based, no shared keys).
var blobServiceUri = 'https://st${resourceToken}.blob.${environment().suffixes.storage}/'

var publicAccess = enableNetworkIsolation ? 'Disabled' : 'Enabled'
var storageNetworkAcls = enableNetworkIsolation ? { bypass: 'AzureServices', defaultAction: 'Deny' } : null
var keyVaultNetworkAcls = enableNetworkIsolation ? { bypass: 'AzureServices', defaultAction: 'Deny' } : null

// ---------------------------------------------------------------------------
// Observability: Log Analytics + workspace-based Application Insights (AVM).
// ---------------------------------------------------------------------------
module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.15.1' = {
  name: 'log-analytics'
  params: {
    name: 'log-${resourceToken}'
    location: location
    tags: tags
    dataRetention: 30
  }
}

module appInsights 'br/public:avm/res/insights/component:0.7.2' = {
  name: 'app-insights'
  params: {
    name: 'appi-${resourceToken}'
    location: location
    tags: tags
    workspaceResourceId: logAnalytics.outputs.resourceId
    applicationType: 'web'
  }
}

// ---------------------------------------------------------------------------
// Key Vault (AVM) holding the Oracle BI password (RBAC-authorized).
// ---------------------------------------------------------------------------
module keyVault 'br/public:avm/res/key-vault/vault:0.13.3' = {
  name: 'key-vault'
  params: {
    name: keyVaultName
    location: location
    tags: tags
    enableRbacAuthorization: true
    enablePurgeProtection: true
    sku: 'standard'
    publicNetworkAccess: publicAccess
    networkAcls: keyVaultNetworkAcls
    privateEndpoints: enableNetworkIsolation
      ? [
          {
            service: 'vault'
            subnetResourceId: privateEndpointSubnetResourceId
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                { privateDnsZoneResourceId: vaultPrivateDnsZoneResourceId }
              ]
            }
          }
        ]
      : []
    secrets: [
      {
        name: oracleBiPasswordSecretName
        value: oracleBiPassword
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Storage: deployment container + poll/dead-letter queues (AVM).
// Shared-key access is disabled — everything authenticates with the identity.
// ---------------------------------------------------------------------------
module storage 'br/public:avm/res/storage/storage-account:0.32.1' = {
  name: 'storage'
  params: {
    name: 'st${resourceToken}'
    location: location
    tags: tags
    skuName: 'Standard_LRS'
    kind: 'StorageV2'
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: publicAccess
    networkAcls: storageNetworkAcls
    privateEndpoints: enableNetworkIsolation
      ? [
          {
            service: 'blob'
            subnetResourceId: privateEndpointSubnetResourceId
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                { privateDnsZoneResourceId: blobPrivateDnsZoneResourceId }
              ]
            }
          }
          {
            service: 'queue'
            subnetResourceId: privateEndpointSubnetResourceId
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                { privateDnsZoneResourceId: queuePrivateDnsZoneResourceId }
              ]
            }
          }
        ]
      : []
    blobServices: {
      containers: [
        { name: deploymentContainerName }
        { name: idempotencyContainerName }
      ]
    }
    queueServices: {
      queues: [
        { name: pollQueueName }
        { name: submitQueueName }
        { name: deadLetterQueueName }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Flex Consumption plan (AVM).
// ---------------------------------------------------------------------------
module serverFarm 'br/public:avm/res/web/serverfarm:0.7.0' = {
  name: 'server-farm'
  params: {
    name: 'plan-${resourceToken}'
    location: location
    tags: tags
    skuName: 'FC1'
    kind: 'functionapp'
    reserved: true
  }
}

// ---------------------------------------------------------------------------
// Function app (.NET 10 isolated) on Flex Consumption (AVM).
// The site module wires AzureWebJobsStorage (identity-based, + Blob Data Owner) and the
// Application Insights connection string for us via the appsettings config.
// ---------------------------------------------------------------------------
module functionApp 'br/public:avm/res/web/site:0.23.1' = {
  name: 'function-app'
  params: {
    name: 'func-${resourceToken}'
    location: location
    tags: union(tags, { 'azd-service-name': 'functions' })
    kind: 'functionapp,linux'
    serverFarmResourceId: serverFarm.outputs.resourceId
    managedIdentities: {
      systemAssigned: true
    }
    httpsOnly: true
    // Remove the shared-secret publishing surfaces (basic auth on SCM/FTP).
    basicPublishingCredentialsPolicies: [
      { name: 'ftp', allow: false }
      { name: 'scm', allow: false }
    ]
    // Outbound VNet integration when network isolation is enabled (reaches private storage/KV).
    virtualNetworkSubnetResourceId: enableNetworkIsolation ? functionsSubnetResourceId : null
    // Flex Consumption does not support alwaysOn; pass an empty siteConfig to avoid the default.
    siteConfig: {}
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.outputs.primaryBlobEndpoint}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    configs: [
      {
        name: 'appsettings'
        applicationInsightResourceId: appInsights.outputs.resourceId
        storageAccountResourceId: storage.outputs.resourceId
        storageAccountUseIdentityAuthentication: true
        properties: {
          // Queue-trigger connection ("QueueStorage") and app queue clients — identity-based.
          QueueStorage__queueServiceUri: queueServiceUri
          Queues__QueueServiceUri: queueServiceUri
          Queues__BlobServiceUri: blobServiceUri
          Queues__PollQueueName: pollQueueName
          Queues__SubmitQueueName: submitQueueName
          Queues__DeadLetterQueueName: deadLetterQueueName
          Queues__IdempotencyContainerName: idempotencyContainerName
          Queues__MaxDeliveryAttempts: '5'
          // Universal Print provider — uses the app's managed identity (no client secret).
          UniversalPrint__TenantId: universalPrintTenantId
          UniversalPrint__UseManagedIdentity: 'true'
          UniversalPrint__DefaultPrinterId: universalPrintDefaultPrinterId
          // Oracle BI Publisher. Password is resolved from Key Vault via the app's identity.
          OracleBi__BaseUrl: oracleBiBaseUrl
          OracleBi__Username: oracleBiUsername
          OracleBi__Password: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=${oracleBiPasswordSecretName})'
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Additional data-plane roles for the managed identity:
//   - Storage Queue Data Contributor (queue triggers + app queue clients)
//   - Key Vault Secrets User (read the Oracle BI password)
// Blob Data Owner is already granted by the site module's identity-based storage wiring.
// ---------------------------------------------------------------------------
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  #disable-next-line BCP334 // resourceToken is a 13-char uniqueString, so the name is always >= 3 chars.
  name: 'st${resourceToken}'
}

resource keyVaultResource 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource queueDataContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, storageQueueDataContributor, 'func-${resourceToken}')
  scope: storageAccount
  properties: {
    principalId: functionApp.outputs.?systemAssignedMIPrincipalId ?? ''
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
  }
}

resource keyVaultSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultResource.id, keyVaultSecretsUser, 'func-${resourceToken}')
  scope: keyVaultResource
  properties: {
    principalId: functionApp.outputs.?systemAssignedMIPrincipalId ?? ''
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}

output functionAppName string = functionApp.outputs.name
output functionAppUri string = 'https://${functionApp.outputs.defaultHostname}'
output functionAppPrincipalId string = functionApp.outputs.?systemAssignedMIPrincipalId ?? ''
output storageAccountName string = storage.outputs.name
output keyVaultName string = keyVault.outputs.name
output applicationInsightsConnectionString string = appInsights.outputs.connectionString
