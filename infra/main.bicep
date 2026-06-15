// main.bicep
// Dark Software Factory — cloud control plane (design §8 "Infrastructure & deployment").
//
// Provisions the intake-line platform: Log Analytics + App Insights, a
// user-assigned managed identity, Key Vault (RBAC), App Configuration with
// seeded feature flags, Cosmos DB (NoSQL + vector) for unified memory, a
// Container Apps environment, the six cloud Container Apps (orchestrator,
// control-center, ingestion, agent-sentry, agent-foundryiq, agent-webiq), and
// an Event Grid topic for signal ingestion.
//
// NOT created here: the Azure OpenAI / Foundry model deployment. The endpoint
// and deployment name are accepted as parameters and injected as env vars; the
// existing model resource is referenced, never provisioned (design §8: Foundry
// is the brain-services backbone reached outbound).
//
// Deploy: az deployment group create  OR  azd up  (see infra/README.md).
// This template is authored for review and NOT auto-deployed.

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Short name prefix for all resources (lowercase, 3-12 chars). e.g. "dsf".')
@minLength(3)
@maxLength(12)
param namePrefix string = 'dsf'

@description('Azure region for all resources. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Environment moniker (dev/test/prod), tagged on resources and used in names.')
param environmentName string = 'dev'

@description('Existing Azure OpenAI / Foundry endpoint URL. NOT provisioned here. e.g. https://my-aoai.openai.azure.com/')
param openAiEndpoint string = ''

@description('Existing Azure OpenAI chat/model deployment name (e.g. gpt-4o). NOT provisioned here.')
param openAiDeployment string = 'gpt-4o'

@description('Object ID of a user/group to grant Key Vault + App Config data access for local dev (optional).')
param adminPrincipalId string = ''

@description('Container image references per service. Defaults are placeholders; azd overrides at deploy.')
param images object = {
  orchestrator: 'mcr.microsoft.com/k8se/quickstart:latest'
  controlCenter: 'mcr.microsoft.com/k8se/quickstart:latest'
  ingestion: 'mcr.microsoft.com/k8se/quickstart:latest'
  agentSentry: 'mcr.microsoft.com/k8se/quickstart:latest'
  agentFoundryiq: 'mcr.microsoft.com/k8se/quickstart:latest'
  agentWebiq: 'mcr.microsoft.com/k8se/quickstart:latest'
}

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

var suffix = uniqueString(resourceGroup().id, namePrefix, environmentName)
var tags = {
  'azd-env-name': environmentName
  project: 'dark-software-factory'
  component: 'intake-line'
}

// Built-in role definition IDs.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6' // Key Vault Secrets User
var appConfigDataReaderRoleId = '516239f1-63e1-4d78-a4de-a74fb236a071' // App Configuration Data Reader
var appConfigDataOwnerRoleId = '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b' // App Configuration Data Owner

// ---------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-id-${suffix}'
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// Observability: Log Analytics + Application Insights
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-log-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ---------------------------------------------------------------------------
// Key Vault (RBAC auth, soft-delete) + role assignment to the identity
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: '${namePrefix}kv${suffix}'
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource kvSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// ---------------------------------------------------------------------------
// App Configuration + seeded feature flags
// ---------------------------------------------------------------------------

resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-05-01' = {
  name: '${namePrefix}-appcs-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'standard'
  }
  properties: {
    disableLocalAuth: false
  }
}

resource appConfigDataReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConfig.id, identity.id, appConfigDataReaderRoleId)
  scope: appConfig
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigDataReaderRoleId)
  }
}

// Admin (human) gets data-owner so the Control Center can write flags during dev.
resource appConfigAdminAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(adminPrincipalId)) {
  name: guid(appConfig.id, adminPrincipalId, appConfigDataOwnerRoleId)
  scope: appConfig
  properties: {
    principalId: adminPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigDataOwnerRoleId)
  }
}

// Global dry-run kill switch (design §7.1): full line, stop short of filing.
resource flagDryRun 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = {
  parent: appConfig
  name: '.appconfig.featureflag~2Fdry_run'
  properties: {
    contentType: 'application/vnd.microsoft.appconfig.ff+json;charset=utf-8'
    value: '{"id":"dry_run","description":"Global dry-run: run the full line but never file issues.","enabled":true,"conditions":{"client_filters":[]}}'
  }
}

// Trigger pause switches: scheduled sweeps and signal interrupts.
resource flagPauseScheduled 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = {
  parent: appConfig
  name: '.appconfig.featureflag~2Ftriggers_scheduled_paused'
  properties: {
    contentType: 'application/vnd.microsoft.appconfig.ff+json;charset=utf-8'
    value: '{"id":"triggers_scheduled_paused","description":"Pause scheduled sweeps.","enabled":false,"conditions":{"client_filters":[]}}'
  }
}

resource flagPauseSignal 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = {
  parent: appConfig
  name: '.appconfig.featureflag~2Ftriggers_signal_paused'
  properties: {
    contentType: 'application/vnd.microsoft.appconfig.ff+json;charset=utf-8'
    value: '{"id":"triggers_signal_paused","description":"Pause autonomous signal interrupts.","enabled":false,"conditions":{"client_filters":[]}}'
  }
}

// A plain (non-flag) seed value: default per-product confidence threshold.
resource kvDefaultThreshold 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = {
  parent: appConfig
  name: 'dsf:default_confidence_threshold'
  properties: {
    value: '0.65'
  }
}

// ---------------------------------------------------------------------------
// Cosmos DB (unified memory + vector) via module
// ---------------------------------------------------------------------------

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    accountName: '${namePrefix}cos${suffix}'
    location: location
    tags: tags
    // Grant the identity Cosmos data-plane access (account-scoped SQL role).
    dataPlanePrincipalId: identity.properties.principalId
  }
}

// ---------------------------------------------------------------------------
// Event Grid topic for signal ingestion (design §8: alert → webhook → Event Grid → ingestion)
// ---------------------------------------------------------------------------

resource ingestionTopic 'Microsoft.EventGrid/topics@2024-06-01-preview' = {
  name: '${namePrefix}-egt-${suffix}'
  location: location
  tags: tags
  properties: {
    inputSchema: 'EventGridSchema'
    publicNetworkAccess: 'Enabled'
  }
}

// ---------------------------------------------------------------------------
// Container Apps managed environment (wired to Log Analytics)
// ---------------------------------------------------------------------------

resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-cae-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// Common env vars injected into every app (design §8: DSF_MODE=azure, identity-driven).
var commonEnv = [
  {
    name: 'DSF_MODE'
    value: 'azure'
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
  {
    name: 'AZURE_COSMOS_ENDPOINT'
    value: cosmos.outputs.endpoint
  }
  {
    name: 'AZURE_APPCONFIG_ENDPOINT'
    value: appConfig.properties.endpoint
  }
  {
    name: 'AZURE_KEYVAULT_URI'
    value: keyVault.properties.vaultUri
  }
  {
    name: 'AZURE_OPENAI_ENDPOINT'
    value: openAiEndpoint
  }
  {
    name: 'AZURE_OPENAI_DEPLOYMENT'
    value: openAiDeployment
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsights.properties.ConnectionString
  }
  {
    name: 'EVENTGRID_TOPIC_ENDPOINT'
    value: ingestionTopic.properties.endpoint
  }
]

// ---------------------------------------------------------------------------
// Container Apps (via reusable module)
// ---------------------------------------------------------------------------

// Control Center — external ingress (the human write surface).
module controlCenter 'modules/containerapp.bicep' = {
  name: 'control-center'
  params: {
    name: '${namePrefix}-control-center'
    serviceName: 'control-center'
    location: location
    environmentId: containerEnv.id
    identityId: identity.id
    image: images.controlCenter
    ingressEnabled: true
    externalIngress: true
    targetPort: 8080
    env: commonEnv
    minReplicas: 1
    maxReplicas: 2
    tags: tags
  }
}

// Ingestion endpoint — external ingress (receives Event Grid / webhook POSTs).
module ingestion 'modules/containerapp.bicep' = {
  name: 'ingestion'
  params: {
    name: '${namePrefix}-ingestion'
    serviceName: 'ingestion'
    location: location
    environmentId: containerEnv.id
    identityId: identity.id
    image: images.ingestion
    ingressEnabled: true
    externalIngress: true
    targetPort: 8080
    env: commonEnv
    minReplicas: 1
    maxReplicas: 3
    tags: tags
  }
}

// Orchestrator — internal ingress only (driven by triggers / ingestion).
module orchestrator 'modules/containerapp.bicep' = {
  name: 'orchestrator'
  params: {
    name: '${namePrefix}-orchestrator'
    serviceName: 'orchestrator'
    location: location
    environmentId: containerEnv.id
    identityId: identity.id
    image: images.orchestrator
    ingressEnabled: true
    externalIngress: false
    targetPort: 8080
    env: commonEnv
    minReplicas: 1
    maxReplicas: 2
    tags: tags
  }
}

// Source agents — internal ingress (A2A reachable only inside the environment).
module agentSentry 'modules/containerapp.bicep' = {
  name: 'agent-sentry'
  params: {
    name: '${namePrefix}-agent-sentry'
    serviceName: 'agent-sentry'
    location: location
    environmentId: containerEnv.id
    identityId: identity.id
    image: images.agentSentry
    ingressEnabled: true
    externalIngress: false
    targetPort: 8080
    env: commonEnv
    tags: tags
  }
}

module agentFoundryiq 'modules/containerapp.bicep' = {
  name: 'agent-foundryiq'
  params: {
    name: '${namePrefix}-agent-foundryiq'
    serviceName: 'agent-foundryiq'
    location: location
    environmentId: containerEnv.id
    identityId: identity.id
    image: images.agentFoundryiq
    ingressEnabled: true
    externalIngress: false
    targetPort: 8080
    env: commonEnv
    tags: tags
  }
}

module agentWebiq 'modules/containerapp.bicep' = {
  name: 'agent-webiq'
  params: {
    name: '${namePrefix}-agent-webiq'
    serviceName: 'agent-webiq'
    location: location
    environmentId: containerEnv.id
    identityId: identity.id
    image: images.agentWebiq
    ingressEnabled: true
    externalIngress: false
    targetPort: 8080
    env: commonEnv
    tags: tags
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

@description('Public URL of the Control Center web UI.')
output controlCenterUrl string = 'https://${controlCenter.outputs.fqdn}'

@description('Public URL of the signal-ingestion endpoint.')
output ingestionUrl string = 'https://${ingestion.outputs.fqdn}'

@description('Cosmos DB document endpoint.')
output cosmosEndpoint string = cosmos.outputs.endpoint

@description('App Configuration endpoint.')
output appConfigEndpoint string = appConfig.properties.endpoint

@description('Key Vault URI.')
output keyVaultUri string = keyVault.properties.vaultUri

@description('Event Grid ingestion topic endpoint.')
output eventGridTopicEndpoint string = ingestionTopic.properties.endpoint

@description('Client ID of the user-assigned managed identity (for AZURE_CLIENT_ID).')
output identityClientId string = identity.properties.clientId

@description('Application Insights connection string.')
output appInsightsConnectionString string = appInsights.properties.ConnectionString
