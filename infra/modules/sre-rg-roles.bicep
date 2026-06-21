// sre-rg-roles.bicep
// Assign the SRE agent's managed identity the read roles on one target resource
// group. Deployed once per monitored RG by infra/sre-agent.bicep.
targetScope = 'resourceGroup'

@description('Principal (object) id of the SRE agent user-assigned managed identity.')
param principalId string

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var monitoringReaderRoleId = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'
var logAnalyticsReaderRoleId = '73c42c96-874c-492b-b04d-ab87d138a893'

var roleIds = [
  readerRoleId
  monitoringReaderRoleId
  logAnalyticsReaderRoleId
]

resource roleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in roleIds: {
    name: guid(resourceGroup().id, principalId, roleId)
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]
