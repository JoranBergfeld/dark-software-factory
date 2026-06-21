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
// Parameters appInsightsId / logAnalyticsId / appInsightsAppId /
// appInsightsConnectionString come from main.bicep outputs (added in this
// same task). The provisioner (Task 3) threads them through.
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

@description('Application Insights "Application ID" GUID (shown in portal Overview, not the ARM resource id).')
param appInsightsAppId string

@description('Application Insights connection string (instrumentation key URI).')
@secure()
param appInsightsConnectionString string

@description('Log Analytics workspace resource id (consumed by the SRE agent connector + RBAC).')
param logAnalyticsId string

@description('Tags applied to all resources in this deployment.')
param tags object = {
  project: 'dark-software-factory'
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

resource agentRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
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
// Outputs (consumed by the provisioner in Task 3)
// ---------------------------------------------------------------------------

@description('ARM resource id of the SRE agent.')
output agentId string = agentResources.outputs.agentId

@description('Data-plane endpoint URL (https://<name>.<location>.azuresre.ai).')
output agentEndpoint string = agentResources.outputs.agentEndpoint

@description('Object (principal) ID of the SRE agent user-assigned managed identity.')
output agentPrincipalId string = agentResources.outputs.principalId
