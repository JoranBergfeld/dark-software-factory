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

@description('Application Insights app id (the GUID from portal, not the resource id). Optional — leave empty to skip agent-side App Insights log configuration.')
// TODO(confirm): appInsightsAppId is a distinct GUID from the resource id, shown in portal
// > App Insights > Overview > "Application ID". main.bicep does not yet output this value;
// until it does, leave this param empty and the logConfiguration block will be omitted.
param appInsightsAppId string = ''

@description('Application Insights connection string (sensitive). Optional — leave empty to skip agent-side App Insights log configuration.')
@secure()
param appInsightsConnectionString string = ''

@description('Log Analytics workspace resource id.')
param logAnalyticsId string

@description('Permission level; only "Reader" RBAC is wired today. "Privileged" accepted for future use.')
@allowed(['Reader', 'Privileged'])
// Intentionally declared but not yet used to drive RBAC logic — the deploy command supplies
// it so this param must exist. TODO(future): wire Privileged-tier role assignments (e.g.
// Monitoring Contributor on the agent's own RG) when the Privileged level is productionised.
#disable-next-line no-unused-params
param permissionLevel string = 'Reader'

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

    // TODO(confirm): logConfiguration is omitted when appInsightsAppId is empty (provisioner
    // does not supply it today). Set appInsightsAppId + appInsightsConnectionString once
    // main.bicep outputs the App Insights Application ID GUID.
    logConfiguration: empty(appInsightsAppId) ? null : {
      applicationInsightsConfiguration: {
        appId: appInsightsAppId
        connectionString: appInsightsConnectionString
      }
    }

    defaultModel: {
      // Confirmed by a live deploy: a specific model id ('gpt-4o') is rejected when it
      // is not offered in the agent's region. 'Automatic' lets the RP pick an available
      // model per region, so it deploys everywhere. Region-specific model ids seen in
      // Sweden Central: Automatic, gpt-5.3-codex, gpt-5.4.
      provider: 'MicrosoftFoundry'
      name: 'Automatic'
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

@description('Data-plane endpoint URL for the SRE agent.')
// Confirmed by a live deploy: agentEndpoint IS a read-only ARM property and carries a
// per-agent infix the constructed pattern misses, e.g.
// https://dsf-sre-demo--66161fbc.f83faa4a.swedencentral.azuresre.ai. Read it from the
// resource rather than computing https://{name}.{location}.azuresre.ai.
output agentEndpoint string = sreAgent.properties.agentEndpoint
