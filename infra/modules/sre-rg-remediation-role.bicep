// sre-rg-remediation-role.bicep
// At "high" operation maturity, grant the SRE agent's managed identity a
// remediation-capable role (all actions except delete) on one monitored
// resource group, on top of the always-on Reader set from sre-rg-roles.bicep.
//
// Azure's built-in Contributor role has no delete-scoped variant, so this
// module defines a custom role (all actions, notActions on every */delete
// action) rather than assigning built-in Contributor. Deployed once per
// monitored RG by infra/sre-agent.bicep, only when operationMaturity == 'high'.
targetScope = 'resourceGroup'

@description('Principal (object) id of the SRE agent user-assigned managed identity.')
param principalId string

// Deterministic, RG-scoped custom role name so redeploys are idempotent.
var roleDefinitionName = guid(resourceGroup().id, 'dsf-sre-remediation-role')

resource remediationRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: roleDefinitionName
  properties: {
    roleName: 'DSF SRE Agent Remediation (no-delete) - ${resourceGroup().name}'
    description: 'Grants the DSF SRE agent all actions except delete on this resource group, for "high" operation-maturity remediation.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: [
          '*'
        ]
        notActions: [
          '*/delete'
        ]
        dataActions: []
        notDataActions: []
      }
    ]
  }
}

resource remediationRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, roleDefinitionName)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: remediationRole.id
  }
}
