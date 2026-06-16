// main.bicep
// Dark Software Factory — Azure BACKING SERVICES we rely on (design §8).
//
// This template provisions ONLY the services the intake line depends on. It does
// NOT host or deploy any container/compute: the agent + orchestrator runtime is
// hosted by the user (e.g. a Proxmox homelab) and reaches these services purely
// OUTBOUND over HTTPS — no VNet peering, no inbound to the homelab. See ADR 0002.
//
// Provisions: Log Analytics + Application Insights, Key Vault (RBAC), App
// Configuration with seeded feature flags, Cosmos DB (NoSQL + vector) for unified
// memory, and a signal-ingestion buffer (Event Grid custom topic → Service Bus
// queue) that the homelab orchestrator polls outbound.
//
// Data-plane access is granted to a caller-supplied homelab workload principal
// (an Entra service principal object id). Managed Identity is not used because
// there is no Azure compute here. Azure OpenAI / Foundry is referenced by the
// homelab runtime, not provisioned here.
//
// Validate (no deploy):  az deployment group what-if -g <rg> -f infra/main.bicep -p @infra/main.parameters.json

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

@description('Object ID of the homelab workload service principal granted data-plane access (Cosmos/App Config/Key Vault/Service Bus). Empty = skip role assignments (provision-only).')
param workloadPrincipalId string = ''

@description('Object ID of a human user/group granted App Configuration Data Owner (to edit flags via the Control Center during dev). Optional.')
param adminPrincipalId string = ''

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

var suffix = uniqueString(resourceGroup().id, namePrefix, environmentName)
var tags = {
  'azd-env-name': environmentName
  project: 'dark-software-factory'
  component: 'backing-services'
}

// Built-in role definition IDs.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6' // Key Vault Secrets User
var appConfigDataReaderRoleId = '516239f1-63e1-4d78-a4de-a74fb236a071' // App Configuration Data Reader
var appConfigDataOwnerRoleId = '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b' // App Configuration Data Owner

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
// Key Vault (RBAC auth, soft-delete) + data-plane role to the homelab workload
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

resource kvSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workloadPrincipalId)) {
  name: guid(keyVault.id, workloadPrincipalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: workloadPrincipalId
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

resource appConfigDataReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workloadPrincipalId)) {
  name: guid(appConfig.id, workloadPrincipalId, appConfigDataReaderRoleId)
  scope: appConfig
  properties: {
    principalId: workloadPrincipalId
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
    // Grant the homelab workload Cosmos data-plane access (account-scoped SQL role).
    dataPlanePrincipalId: workloadPrincipalId
  }
}

// ---------------------------------------------------------------------------
// Signal ingestion buffer: Event Grid custom topic -> Service Bus queue
// (homelab orchestrator polls the queue OUTBOUND; nothing pushes into homelab)
// ---------------------------------------------------------------------------

module ingestion 'modules/ingestion.bicep' = {
  name: 'ingestion'
  params: {
    namePrefix: namePrefix
    suffix: suffix
    location: location
    tags: tags
    // Grant the homelab workload Service Bus Data Receiver on the queue.
    receiverPrincipalId: workloadPrincipalId
  }
}

// ---------------------------------------------------------------------------
// Outputs (consumed by the homelab runtime's azure-mode configuration)
// ---------------------------------------------------------------------------

@description('Cosmos DB document endpoint.')
output cosmosEndpoint string = cosmos.outputs.endpoint

@description('App Configuration endpoint.')
output appConfigEndpoint string = appConfig.properties.endpoint

@description('Key Vault URI.')
output keyVaultUri string = keyVault.properties.vaultUri

@description('Application Insights connection string.')
output appInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Event Grid ingestion topic endpoint (where external signal sources publish).')
output eventGridTopicEndpoint string = ingestion.outputs.topicEndpoint

@description('Service Bus namespace hostname (the orchestrator polls the signals queue here).')
output serviceBusNamespace string = ingestion.outputs.namespaceHostname

@description('Service Bus signals queue name.')
output signalsQueueName string = ingestion.outputs.queueName
