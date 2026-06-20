targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment; used to derive resource names and tags.')
param environmentName string

@minLength(1)
@description('Primary Azure region for all resources.')
param location string

@description('Entra ID tenant id used by the Universal Print provider options.')
param universalPrintTenantId string = tenant().tenantId

@description('Default Universal Print printer (or printer share) id to submit jobs to.')
param universalPrintDefaultPrinterId string

@description('Oracle BI Publisher base URL (public endpoint), e.g. https://obi.contoso.com/xmlpserver.')
param oracleBiBaseUrl string

@description('Oracle BI Publisher service account username.')
param oracleBiUsername string

@secure()
@description('Oracle BI Publisher service account password.')
param oracleBiPassword string

@description('Grant the function app managed identity Microsoft Graph application permissions. Requires the deployer to have directory privileges (e.g. Privileged Role Administrator).')
param grantGraphAppRoles bool = true

@description('Lock storage and Key Vault behind private endpoints and integrate the app with a VNet. Defence in depth — the standard (public) deploy works with this set to false.')
param enableNetworkIsolation bool = false

var tags = { 'azd-env-name': environmentName }
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

// Optional network-isolation scaffolding (VNet + subnets + private DNS zones).
module network 'network.bicep' = if (enableNetworkIsolation) {
  name: 'network'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
  }
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
    universalPrintTenantId: universalPrintTenantId
    universalPrintDefaultPrinterId: universalPrintDefaultPrinterId
    oracleBiBaseUrl: oracleBiBaseUrl
    oracleBiUsername: oracleBiUsername
    oracleBiPassword: oracleBiPassword
    enableNetworkIsolation: enableNetworkIsolation
    // These references are guarded by the same enableNetworkIsolation condition as the module.
    #disable-next-line BCP318
    functionsSubnetResourceId: enableNetworkIsolation ? network.outputs.functionsSubnetResourceId : ''
    #disable-next-line BCP318
    privateEndpointSubnetResourceId: enableNetworkIsolation ? network.outputs.privateEndpointSubnetResourceId : ''
    #disable-next-line BCP318
    blobPrivateDnsZoneResourceId: enableNetworkIsolation ? network.outputs.blobPrivateDnsZoneResourceId : ''
    #disable-next-line BCP318
    queuePrivateDnsZoneResourceId: enableNetworkIsolation ? network.outputs.queuePrivateDnsZoneResourceId : ''
    #disable-next-line BCP318
    vaultPrivateDnsZoneResourceId: enableNetworkIsolation ? network.outputs.vaultPrivateDnsZoneResourceId : ''
  }
}

// Microsoft Graph application permissions for the managed identity (Universal Print).
module graphRoles 'graph-roles.bicep' = if (grantGraphAppRoles) {
  name: 'graph-roles'
  scope: rg
  params: {
    functionAppPrincipalId: resources.outputs.functionAppPrincipalId
  }
}

output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = rg.name
output SERVICE_FUNCTIONS_NAME string = resources.outputs.functionAppName
output SERVICE_FUNCTIONS_URI string = resources.outputs.functionAppUri
output AZURE_STORAGE_ACCOUNT string = resources.outputs.storageAccountName
