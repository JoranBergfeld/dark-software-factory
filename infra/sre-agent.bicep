// sre-agent.bicep
// Provision the Azure SRE Agent for one product: a dedicated resource group,
// a user-assigned managed identity, the Microsoft.App/agents resource,
// cross-RG read RBAC on every monitored resource group, subscription-level
// Monitoring Contributor (alert-lifecycle management), and Azure Monitor
// connectors (Log Analytics + Application Insights).
//
// Deployed with:
//   az deployment sub create -l <sreAgentLocation> -f infra/sre-agent.bicep -p ...
//
// Required params: product, agentName, sreAgentLocation, agentResourceGroup,
// targetResourceGroups, appInsightsId, logAnalyticsId, permissionLevel.
// Optional: appInsightsAppId, appInsightsConnectionString (agent-side log config;
// omit these if main.bicep does not yet output appInsightsAppId).
targetScope = 'subscription'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Short product key (e.g. "microbi"). Used to name the agent and its RG.')
param product string

@description('Full SRE Agent resource name (e.g. "dsf-sre-microbi").')
param agentName string

@description('Azure region for the SRE Agent. Must be swedencentral, eastus2, or australiaeast.')
@allowed(['swedencentral', 'eastus2', 'australiaeast'])
param sreAgentLocation string = 'swedencentral'

@description('Name of the dedicated resource group that will host the SRE agent.')
param agentResourceGroup string

@description('Resource groups the agent monitors (factory RG + any monitored-app RGs).')
param targetResourceGroups array

@description('Application Insights resource id (consumed by the SRE agent connector + RBAC).')
param appInsightsId string

@description('Log Analytics workspace resource id (consumed by the SRE agent connector + RBAC).')
param logAnalyticsId string

@description('Application Insights "Application ID" GUID (shown in portal Overview, not the ARM resource id). Optional — leave empty to skip agent-side App Insights log configuration.')
param appInsightsAppId string = ''

@description('Application Insights connection string (instrumentation key URI). Optional — leave empty to skip agent-side App Insights log configuration.')
@secure()
param appInsightsConnectionString string = ''

@description('''
Permission level granted to the SRE agent. "Reader" is the default and only wired level.
"Privileged" is accepted by the deploy command but additional role wiring is future work
(the RBAC assignments below only cover the Reader surface regardless of this value).
''')
@allowed(['Reader', 'Privileged'])
param permissionLevel string = 'Reader'

@description('Object id of the human owner/governance principal granted Reader + SRE Agent Administrator on the SRE agent RG (so the deployer can open and operate the agent in portal/CLI after `dsf new`). Optional — leave empty in CI / service-principal runs to skip the human grant.')
param ownerPrincipalId string = ''

@description('Tags applied to all resources in this deployment.')
param tags object = {
  project: 'dark-software-factory'
  'managed-by': 'dsf'
  product: product
  component: 'sre-agent'
}

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

// Subscription-level Monitoring Contributor: lets the agent manage alert rules
// and action groups across the subscription (alert-lifecycle management).
var monitoringContributorRoleId = '749f88d5-cbae-40b8-bcfc-e573ddc772fa'

// ---------------------------------------------------------------------------
// Dedicated resource group for the SRE agent
// ---------------------------------------------------------------------------

resource agentRg 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: agentResourceGroup
  location: sreAgentLocation
  tags: tags
}

// ---------------------------------------------------------------------------
// Agent resources (UAMI + Microsoft.App/agents + connectors) — RG-scoped module
// ---------------------------------------------------------------------------

module agentResources 'modules/sre-agent-resources.bicep' = {
  name: 'sre-agent-${product}'
  scope: agentRg
  params: {
    agentName: agentName
    location: sreAgentLocation
    targetResourceGroups: targetResourceGroups
    appInsightsId: appInsightsId
    appInsightsAppId: appInsightsAppId
    appInsightsConnectionString: appInsightsConnectionString
    logAnalyticsId: logAnalyticsId
    permissionLevel: permissionLevel
    tags: tags
  }
}

// ---------------------------------------------------------------------------
// Cross-RG read RBAC: Reader + Monitoring Reader + Log Analytics Reader
// on every resource group in targetResourceGroups.
// ---------------------------------------------------------------------------

module rgRoles 'modules/sre-rg-roles.bicep' = [
  for rg in targetResourceGroups: {
    name: 'sre-roles-${rg}'
    scope: resourceGroup(rg)
    params: {
      principalId: agentResources.outputs.principalId
    }
  }
]

// ---------------------------------------------------------------------------
// Subscription-scope Monitoring Contributor for alert-lifecycle management.
// Name must be computable at deployment start so we derive it from the
// stable agentName rather than the runtime principalId.
// ---------------------------------------------------------------------------

module subMonitoringRole 'modules/sre-sub-role.bicep' = {
  name: 'sre-sub-role-${product}'
  params: {
    principalId: agentResources.outputs.principalId
    agentName: agentName
    roleId: monitoringContributorRoleId
  }
}

// ---------------------------------------------------------------------------
// Human owner/governance grant: Reader + SRE Agent Administrator on the agent's
// dedicated RG so the deployer can open and operate the agent (and its RG) in
// portal/CLI after `dsf new`.
// No-op in CI / service-principal runs where no human principal is resolvable.
// ---------------------------------------------------------------------------

module ownerRgRole 'modules/sre-owner-rg-role.bicep' = if (!empty(ownerPrincipalId)) {
  name: 'sre-owner-rg-role-${product}'
  scope: agentRg
  params: {
    principalId: ownerPrincipalId
  }
}

// ---------------------------------------------------------------------------
// Outputs (consumed by the provisioner in Task 3)
// ---------------------------------------------------------------------------

@description('ARM resource id of the SRE agent.')
output agentId string = agentResources.outputs.agentId

@description('Data-plane endpoint URL (https://<name>.<location>.azuresre.ai).')
output agentEndpoint string = agentResources.outputs.agentEndpoint

@description('Object (principal) ID of the SRE agent user-assigned managed identity.')
output agentPrincipalId string = agentResources.outputs.principalId
