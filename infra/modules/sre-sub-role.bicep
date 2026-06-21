// sre-sub-role.bicep
// Assigns one role to the SRE agent UAMI at subscription scope.
// Kept as a separate module because the role-assignment name must be computable
// at deployment start (BCP120 constraint), and the principalId only becomes
// known after the UAMI is created inside the agentResources module.
// Using agentName (a parameter, always known up front) instead of principalId
// in the guid() call keeps the name stable and satisfies BCP120.
targetScope = 'subscription'

@description('Principal (object) id of the SRE agent user-assigned managed identity.')
param principalId string

@description('SRE Agent resource name — used to derive a stable role-assignment name.')
param agentName string

@description('Built-in role definition GUID to assign.')
param roleId string

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, agentName, roleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
  }
}
