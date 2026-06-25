// sre-owner-rg-role.bicep
// Grant the human owner/governance principal Reader on the SRE agent's dedicated
// resource group, so the deployer can open the Microsoft.App/agents resource in the
// portal / `az` after `dsf new` ("lift the hood"). Deployed once, conditionally, by
// infra/sre-agent.bicep when an owner principal id is resolvable (no-op in CI).
targetScope = 'resourceGroup'

@description('Object (principal) id of the human owner/governance principal granted Reader on the SRE agent RG.')
param principalId string

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'

// principalType is intentionally omitted so Azure infers it (the owner is a human user
// or group, not a service principal), mirroring the admin grants in main.bicep.
resource readerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, readerRoleId)
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
  }
}
