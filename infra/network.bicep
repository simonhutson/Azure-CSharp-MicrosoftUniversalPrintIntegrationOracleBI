// Optional network-isolation building blocks: a VNet with a Flex-integration subnet and a
// private-endpoint subnet, plus private DNS zones for blob, queue and Key Vault.
// Deployed only when enableNetworkIsolation = true in main.bicep.
@description('Primary Azure region for all resources.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Stable token used to make resource names unique.')
param resourceToken string

var blobPrivateDnsZoneName = 'privatelink.blob.${environment().suffixes.storage}'
var queuePrivateDnsZoneName = 'privatelink.queue.${environment().suffixes.storage}'
var vaultPrivateDnsZoneName = 'privatelink.vaultcore.azure.net'

module virtualNetwork 'br/public:avm/res/network/virtual-network:0.9.0' = {
  name: 'vnet'
  params: {
    name: 'vnet-${resourceToken}'
    location: location
    tags: tags
    addressPrefixes: [
      '10.10.0.0/24'
    ]
    subnets: [
      {
        // Flex Consumption outbound VNet integration — must be delegated to Microsoft.App/environments.
        name: 'snet-functions'
        addressPrefix: '10.10.0.0/27'
        delegation: 'Microsoft.App/environments'
      }
      {
        name: 'snet-private-endpoints'
        addressPrefix: '10.10.0.32/27'
        privateEndpointNetworkPolicies: 'Disabled'
      }
    ]
  }
}

module blobPrivateDnsZone 'br/public:avm/res/network/private-dns-zone:0.8.1' = {
  name: 'pdz-blob'
  params: {
    name: blobPrivateDnsZoneName
    tags: tags
    virtualNetworkLinks: [
      { virtualNetworkResourceId: virtualNetwork.outputs.resourceId }
    ]
  }
}

module queuePrivateDnsZone 'br/public:avm/res/network/private-dns-zone:0.8.1' = {
  name: 'pdz-queue'
  params: {
    name: queuePrivateDnsZoneName
    tags: tags
    virtualNetworkLinks: [
      { virtualNetworkResourceId: virtualNetwork.outputs.resourceId }
    ]
  }
}

module vaultPrivateDnsZone 'br/public:avm/res/network/private-dns-zone:0.8.1' = {
  name: 'pdz-vault'
  params: {
    name: vaultPrivateDnsZoneName
    tags: tags
    virtualNetworkLinks: [
      { virtualNetworkResourceId: virtualNetwork.outputs.resourceId }
    ]
  }
}

output functionsSubnetResourceId string = virtualNetwork.outputs.subnetResourceIds[0]
output privateEndpointSubnetResourceId string = virtualNetwork.outputs.subnetResourceIds[1]
output blobPrivateDnsZoneResourceId string = blobPrivateDnsZone.outputs.resourceId
output queuePrivateDnsZoneResourceId string = queuePrivateDnsZone.outputs.resourceId
output vaultPrivateDnsZoneResourceId string = vaultPrivateDnsZone.outputs.resourceId
