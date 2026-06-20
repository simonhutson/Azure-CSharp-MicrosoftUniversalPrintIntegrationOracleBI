// Grants the function app's system-assigned managed identity the Microsoft Graph application
// permissions required by the custom Universal Print provider.
//
// Requires the Microsoft Graph Bicep extension (see infra/bicepconfig.json) and a deployer with
// directory privileges (e.g. Privileged Role Administrator / Global Administrator) to consent to
// application permissions. If the deployer lacks those rights, set grantGraphAppRoles=false in
// main.bicep and have an administrator assign the roles out-of-band (see README).
extension microsoftGraphV1

@description('Object (principal) id of the function app system-assigned managed identity.')
param functionAppPrincipalId string

@description('Microsoft Graph application permissions (app role values) to grant.')
param graphAppRoles array = [
  'PrintJob.ReadWrite.All'
  'Printer.Read.All'
]

// Well-known appId of the Microsoft Graph service principal.
resource graphServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: '00000003-0000-0000-c000-000000000000'
}

resource appRoleGrants 'Microsoft.Graph/appRoleAssignedTo@v1.0' = [
  for role in graphAppRoles: {
    appRoleId: filter(graphServicePrincipal.appRoles, r => r.value == role)[0].id
    principalId: functionAppPrincipalId
    resourceId: graphServicePrincipal.id
  }
]
