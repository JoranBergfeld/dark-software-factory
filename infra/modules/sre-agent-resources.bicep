// sre-agent-resources.bicep
// The SRE agent's own resources, deployed into its dedicated resource group.
// Provisions the user-assigned managed identity, the Microsoft.App/agents resource,
// and the Log Analytics + Application Insights connector sub-resources.
targetScope = 'resourceGroup'

@description('SRE Agent resource name.')
param agentName string

@description('Azure region. Must be one of swedencentral, eastus2, australiaeast.')
param location string

@description('Resource groups the agent monitors; wired into knowledgeGraphConfiguration.managedResources.')
param targetResourceGroups array

@description('Application Insights resource id (for the log connector).')
param appInsightsId string

@description('Application Insights app id (the GUID from portal, not the resource id).')
param appInsightsAppId string

@description('Application Insights connection string (sensitive).')
@secure()
param appInsightsConnectionString string

@description('Log Analytics workspace resource id.')
param logAnalyticsId string

@description('Tags applied to the resources.')
param tags object = {}

// ---------------------------------------------------------------------------
// User-assigned managed identity — the agent authenticates to Azure with this.
// ---------------------------------------------------------------------------

resource agentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${agentName}-id'
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// Microsoft.App/agents — api-version 2026-01-01 (GA, confirmed against
// https://learn.microsoft.com/azure/templates/microsoft.app/2026-01-01/agents).
//
// knowledgeGraphConfiguration.managedResources lists the resource groups the
// agent is permitted to read from (must be their fully-qualified ARM IDs).
//
// logConfiguration.applicationInsightsConfiguration wires the App Insights
// instance for agent-side telemetry (appId = the GUID "Application ID",
// connectionString = the instrumentation connection string; both sensitive).
//
// actionConfiguration controls how autonomously the agent acts; 'ReadOnly'
// mode with 'Low' access is the DSF default — override with 'Review' to
// require human approval before any write action.
// ---------------------------------------------------------------------------

resource sreAgent 'Microsoft.App/agents@2026-01-01' = {
  name: agentName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${agentIdentity.id}': {}
    }
  }
  properties: {
    upgradeChannel: 'Stable'

    actionConfiguration: {
      // ReadOnly + Low lets the agent read and surface findings without writing
      // tickets or firing alerts automatically. Raise to 'Review' or
      // 'Autonomous' after validating the agent's behaviour in your environment.
      accessLevel: 'Low'
      mode: 'ReadOnly'
      // The UAMI resource id — agent uses this identity for data-plane actions.
      identity: agentIdentity.id
    }

    knowledgeGraphConfiguration: {
      // Same UAMI used for knowledge-graph access.
      identity: agentIdentity.id
      // Resource groups the agent may inspect (ARM resource-group resource ids).
      managedResources: [for rg in targetResourceGroups: subscriptionResourceId('Microsoft.Resources/resourceGroups', rg)]
    }

    logConfiguration: {
      // Application Insights telemetry for the agent itself.
      // appId is the "Application ID" GUID shown in portal > App Insights > Overview.
      // connectionString is the InstrumentationKey/... connection string.
      applicationInsightsConfiguration: {
        appId: appInsightsAppId
        connectionString: appInsightsConnectionString
      }
    }

    defaultModel: {
      // TODO(confirm): verify the exact model id string accepted by the RP for the
      // default Azure-hosted model. The ARM reference shows provider + name as free
      // strings; check the SRE Agent portal or `az sre-agent model list` for valid
      // values. 'MicrosoftFoundry' / 'gpt-4o' is shown in the MS Learn DefaultModel
      // description examples but is not authoritatively confirmed for this RP version.
      provider: 'MicrosoftFoundry'
      name: 'gpt-4o'
    }
  }
}

// ---------------------------------------------------------------------------
// Connector sub-resources (Microsoft.App/agents/connectors@2026-01-01).
// Schema confirmed from https://learn.microsoft.com/azure/templates/microsoft.app/2026-01-01/agents/connectors
// dataConnectorType values ('LogAnalytics', 'AppInsights') confirmed from
// microsoft/sre-agent agent-extensions.bicep template.
// ---------------------------------------------------------------------------

resource logAnalyticsConnector 'Microsoft.App/agents/connectors@2026-01-01' = {
  parent: sreAgent
  name: 'loganalytics'
  properties: {
    // TODO(confirm): 'LogAnalytics' is the dataConnectorType string seen in the
    // microsoft/sre-agent agent-extensions.bicep template. Verify this exact
    // casing is accepted by the 2026-01-01 RP — check the connector docs or
    // a live deployment if the provisioner returns a validation error here.
    dataConnectorType: 'LogAnalytics'
    // dataSource = the workspace resource id (confirmed: agent-extensions.bicep
    // passes lawResourceId as dataSource for the LogAnalytics connector).
    // The ARM schema flags dataSource as sensitive but a resource id is not a secret.
    #disable-next-line use-secure-value-for-secure-inputs
    dataSource: logAnalyticsId
    identity: agentIdentity.id
  }
}

resource appInsightsConnector 'Microsoft.App/agents/connectors@2026-01-01' = {
  parent: sreAgent
  name: 'appinsights'
  properties: {
    // TODO(confirm): 'AppInsights' is the dataConnectorType string seen in the
    // microsoft/sre-agent agent-extensions.bicep template. Verify casing accepted
    // by the 2026-01-01 RP the same way as the LogAnalytics connector above.
    dataConnectorType: 'AppInsights'
    // dataSource = the App Insights resource id (confirmed: agent-extensions.bicep
    // passes appInsightsResourceId as dataSource for the AppInsights connector).
    // The ARM schema flags dataSource as sensitive but a resource id is not a secret.
    #disable-next-line use-secure-value-for-secure-inputs
    dataSource: appInsightsId
    identity: agentIdentity.id
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

@description('Object (principal) ID of the SRE agent UAMI — used for RBAC assignments.')
output principalId string = agentIdentity.properties.principalId

@description('ARM resource id of the SRE agent.')
output agentId string = sreAgent.id

@description('Data-plane endpoint URL for the SRE agent (constructed from name + location).')
// TODO(confirm): agentEndpoint is NOT a first-class ARM output property in the 2026-01-01
// schema (the MS Learn reference does not list it under AgentProperties read-only outputs).
// The endpoint pattern https://{name}.{location}.azuresre.ai is confirmed from the
// microsoft/sre-agent agent-core.bicep template which computes it client-side.
// Verify against actual deployment or `az rest --method get` on the resource.
output agentEndpoint string = 'https://${agentName}.${location}.azuresre.ai'
