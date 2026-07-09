// sre-owner-rg-role.bicep
// Grant the human owner/governance principal the RG browse role plus the SRE Agent
// data-plane role on the SRE agent's dedicated resource group, so the deployer can
// open and operate the Microsoft.App/agents resource in the portal / `az` after
// `dsf new`. Deployed once, conditionally, by infra/sre-agent.bicep when an owner
// principal id is resolvable (no-op in CI).
targetScope = 'resourceGroup'

@description('Object (principal) id of the human owner/governance principal granted access to the SRE agent RG.')
param principalId string

// Built-in role definition ids granted to the human owner on the SRE agent's RG.
// Reader lets them browse the RG (UAMI, connectors, the agent resource); SRE Agent
// Administrator grants the Microsoft.App/agents data-plane surface the portal UI needs
// (open the agent, chat, approve actions, manage connectors) — generic Reader does not.
var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var sreAgentAdministratorRoleId = 'e79298df-d852-4c6d-84f9-5d13249d1e55'
var ownerRoleIds = [
  readerRoleId
  sreAgentAdministratorRoleId
]

// principalType is intentionally omitted so Azure infers it (the owner is a human user
// or group, not a service principal), mirroring the admin grants in main.bicep.
resource ownerRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in ownerRoleIds: {
    name: guid(resourceGroup().id, principalId, roleId)
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]
