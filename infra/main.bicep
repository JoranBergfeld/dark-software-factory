// main.bicep
// Dark Software Factory — Azure resources for one product factory instance.
//
// Provisions the backing services the intake line depends on (Log Analytics +
// Application Insights, Key Vault, App Configuration with seeded flags, Cosmos DB,
// Azure AI Foundry + model deployments) AND the runtime that consumes them: an Azure
// Container Apps environment + a single
// no-ingress orchestrator Container App. DSF is pull-only (ADR 0014): the orchestrator
// sweeps source agents on a schedule, so there is no inbound signal ingestion here.
// The runtime authenticates to the data plane with a user-assigned managed identity
// (ADR 0004); there is no service principal and nothing inbound.
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

@description('Product key this factory instance serves (sets DSF_PRODUCT and names the runtime Container App).')
param product string = 'demo'

@description('Container image for the feature-council orchestrator runtime.')
param runtimeImage string = 'ghcr.io/joranbergfeld/dsf-runtime:latest'

@description('DSF GitHub App id (owner-level; supplied by `dsf new` from the owner Key Vault).')
param githubAppId string = ''

@description('DSF GitHub App installation id (owner-level single installation).')
param githubInstallationId string = ''

@description('Product repository in owner/name form; scopes App tokens to the single repo.')
param githubRepository string = ''

@description('Azure AI Foundry chat model deployed for the runtime (created here, called over the Azure OpenAI data plane).')
param chatModel string = 'gpt-4o'

@description('Version of the chat model deployment.')
param chatModelVersion string = '2024-11-20'

@description('Azure AI Foundry embedding model deployed for the runtime (semantic dedup).')
param embeddingModel string = 'text-embedding-3-large'

@description('Version of the embedding model deployment.')
param embeddingModelVersion string = '1'

@description('Deployment SKU (throughput tier) for both model deployments.')
param modelSkuName string = 'GlobalStandard'

@description('Chat model deployment capacity, in thousands of tokens-per-minute (TPM).')
param chatModelCapacity int = 30

@description('Embedding model deployment capacity, in thousands of tokens-per-minute (TPM).')
param embeddingModelCapacity int = 30

@description('Object ID of a human user/group granted App Configuration Data Owner (to edit flags via the Control Center during dev). Optional.')
param adminPrincipalId string = ''

@description('Enable Key Vault purge protection. Keep true for prod (a deleted vault name is reserved 90 days); set false for dev/throwaway so redeploys can reuse names.')
param enablePurgeProtection bool = true

@description('Key Vault soft-delete retention in days (7-90).')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 90

@description('Gate public network access to backing services. Defaults to false (off). Enable only for dev environments lacking private endpoint connectivity.')
param allowPublicNetworkAccess bool = false

@description('Provision Grounding with Bing Search (Microsoft.Bing/accounts) plus a Foundry project + connection so the WebIQ source agent can research the web. Set false where the tenant policy blocks the Microsoft.Bing provider.')
param enableBingGrounding bool = true

@description('SKU (tier) for the Grounding with Bing Search account.')
param bingGroundingSkuName string = 'G1'

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

var suffix = uniqueString(resourceGroup().id, namePrefix, environmentName)
// Key Vault names are capped at 24 chars. namePrefix (<=12) + 'kv' + suffix (13)
// can reach 27, so truncate to keep the vault name valid; the prefix is preserved.
var keyVaultName = take('${namePrefix}kv${suffix}', 24)
var tags = {
  'azd-env-name': environmentName
  project: 'dark-software-factory'
  'managed-by': 'dsf'
  product: product
  component: 'backing-services'
}

// Built-in role definition IDs.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6' // Key Vault Secrets User
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' // Key Vault Secrets Officer
var appConfigDataReaderRoleId = '516239f1-63e1-4d78-a4de-a74fb236a071' // App Configuration Data Reader
var appConfigDataOwnerRoleId = '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b' // App Configuration Data Owner
var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908' // Cognitive Services User (Foundry projects + Agents)

// Foundry project that hosts the Grounding with Bing Search connection. The
// azure-ai-agents BingGroundingTool the WebIQ agent uses targets this project
// endpoint (AZURE_AI_PROJECT_ENDPOINT) and resolves the connection by id.
var aiProjectName = '${namePrefix}-proj-${suffix}'
var aiProjectEndpoint = enableBingGrounding ? 'https://${namePrefix}-aif-${suffix}.services.ai.azure.com/api/projects/${aiProjectName}' : ''

// ---------------------------------------------------------------------------
// Observability: Log Analytics + Application Insights
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
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
// Runtime identity: user-assigned MI holding the data-plane roles (ADR 0004).
// A USER-assigned (not system-assigned) identity has a stable principalId known
// before the Container App, avoiding a cycle (the app's env wires in the Cosmos /
// App Config endpoints, while those resources' role assignments need this id).
// ---------------------------------------------------------------------------

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${namePrefix}-runtime-${suffix}'
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// Key Vault (RBAC auth, soft-delete) + data-plane role to the runtime identity
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: keyVaultName
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
    softDeleteRetentionInDays: softDeleteRetentionInDays
    // Purge protection cannot be explicitly false (only true or omitted).
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: allowPublicNetworkAccess ? 'Enabled' : 'Disabled'
    networkAcls: {
      defaultAction: allowPublicNetworkAccess ? 'Allow' : 'Deny'
      bypass: 'AzureServices'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource kvSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, runtimeIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// Admin (human/operator) gets Secrets Officer so `dsf new` can seed product
// secrets into the vault, mirroring the App Config admin grant.
// Data-plane reachability still requires allowPublicNetworkAccess=true (or running
// provisioning from inside the vault's network); see docs/site/get-started/operate.md.
resource keyVaultAdminAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(adminPrincipalId)) {
  name: guid(keyVault.id, adminPrincipalId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    principalId: adminPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
  }
}

// The principal running the deployment (the `dsf` CLI's `az login`) gets Key Vault Secrets
// Officer so the provisioner can seed the App private key post-deploy (`_seed_app_key`),
// mirroring the App Configuration deployer grant below. Data-plane reachability still
// requires allowPublicNetworkAccess=true (or provisioning from inside the vault's network).
// Skipped when the deployer IS the admin: that grant above is identical (same scope, role,
// and principal) so emitting both would collide on the deterministic guid() name.
resource keyVaultDeployerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (empty(adminPrincipalId) || toLower(deployer().objectId) != toLower(adminPrincipalId)) {
  name: guid(keyVault.id, deployer().objectId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    principalId: deployer().objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
  }
}

// ---------------------------------------------------------------------------
// App Configuration + seeded feature flags
// ---------------------------------------------------------------------------

resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' = {
  name: '${namePrefix}-appcs-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'standard'
  }
  properties: {
    // Force AAD (no access keys). Key-values/flags are seeded post-deploy by the `dsf`
    // provisioner via `az appconfig kv set --auth-mode login` (with retry), not in this
    // template — that avoids the in-deployment race where ARM-proxied data-plane writes
    // run before the deployer's Data Owner assignment has propagated.
    disableLocalAuth: true
  }
}

resource appConfigDataReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConfig.id, runtimeIdentity.id, appConfigDataReaderRoleId)
  scope: appConfig
  properties: {
    principalId: runtimeIdentity.properties.principalId
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

// The principal running the deployment (the `dsf` CLI's `az login`) gets App Configuration
// Data Owner so the provisioner can seed key-values post-deploy (local auth is disabled).
// Skipped when the deployer IS the admin: that grant above is identical (same scope, role,
// and principal) so emitting both would collide on the deterministic guid() name.
resource appConfigDeployerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (empty(adminPrincipalId) || toLower(deployer().objectId) != toLower(adminPrincipalId)) {
  name: guid(appConfig.id, deployer().objectId, appConfigDataOwnerRoleId)
  scope: appConfig
  properties: {
    principalId: deployer().objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigDataOwnerRoleId)
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
    // Grant the runtime identity Cosmos data-plane access (account-scoped SQL role).
    dataPlanePrincipalId: runtimeIdentity.properties.principalId
  }
}

// ---------------------------------------------------------------------------
// Azure AI Foundry (Cognitive Services) + model deployments (chat + embedding).
// Created here so a product factory is self-contained. The runtime calls these
// over the Azure OpenAI data plane using its user-assigned identity (AAD token,
// no keys). Deployments must be created serially on one account, so the embedding
// deployment dependsOn the chat deployment.
// ---------------------------------------------------------------------------

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: '${namePrefix}-aif-${suffix}'
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  properties: {
    // A custom subdomain is required for AAD token auth and the OpenAI endpoint.
    customSubDomainName: '${namePrefix}-aif-${suffix}'
    publicNetworkAccess: allowPublicNetworkAccess ? 'Enabled' : 'Disabled'
    disableLocalAuth: true
    // Enable the Foundry project sub-resource that hosts the Bing grounding connection.
    allowProjectManagement: true
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundry
  name: chatModel
  sku: {
    name: modelSkuName
    capacity: chatModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: chatModel
      version: chatModelVersion
    }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundry
  name: embeddingModel
  dependsOn: [
    chatDeployment
  ]
  sku: {
    name: modelSkuName
    capacity: embeddingModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModel
      version: embeddingModelVersion
    }
  }
}

// The runtime identity calls inference (chat + embeddings) over AAD.
resource foundryOpenAIUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, runtimeIdentity.id, cognitiveServicesOpenAIUserRoleId)
  scope: foundry
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
  }
}

// ---------------------------------------------------------------------------
// Grounding with Bing Search (issue #85): gives the WebIQ source agent real web
// research for greenfield products with no telemetry yet. The Bing grounding
// account is exposed to the runtime through a Foundry *project* connection; the
// azure-ai-agents BingGroundingTool resolves it by connection id. Gated by
// enableBingGrounding so tenants that block the Microsoft.Bing provider can opt out.
// ---------------------------------------------------------------------------

resource bingAccount 'Microsoft.Bing/accounts@2025-05-01-preview' = if (enableBingGrounding) {
  name: '${namePrefix}-bing-${suffix}'
  location: 'global'
  tags: tags
  kind: 'Bing.Grounding'
  sku: {
    name: bingGroundingSkuName
  }
}

// Foundry project (child of the account) that owns the grounding connection.
resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = if (enableBingGrounding) {
  parent: foundry
  name: aiProjectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'DSF ${product} WebIQ'
    description: 'Feature Council WebIQ web-research project for ${product}.'
  }
}

// The Grounding with Bing Search connection the agent calls (API-key auth; the
// key is read from the Bing account at deploy time, never surfaced as an output).
resource bingConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = if (enableBingGrounding) {
  parent: aiProject
  name: '${namePrefix}-bing-conn-${suffix}'
  properties: {
    category: 'GroundingWithBingSearch'
    authType: 'ApiKey'
    target: bingAccount!.properties.endpoint
    isSharedToAll: false
    credentials: {
      key: bingAccount!.listKeys().key1
    }
    metadata: {
      type: 'bing_grounding'
      ApiType: 'Azure'
      ResourceId: bingAccount!.id
      location: 'global'
    }
  }
}

// The runtime identity needs Cognitive Services User on the account to use the
// Foundry Agents/project data plane (the grounding tool runs as an agent).
resource foundryAgentsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableBingGrounding) {
  name: guid(foundry.id, runtimeIdentity.id, cognitiveServicesUserRoleId)
  scope: foundry
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
  }
}

// ---------------------------------------------------------------------------
// Runtime compute: Azure Container Apps environment + orchestrator app (ADR 0004)
// ---------------------------------------------------------------------------

resource containerEnv 'Microsoft.App/managedEnvironments@2025-01-01' = {
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

resource orchestratorApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'dsf-orchestrator-${product}'
  location: location
  tags: tags
  dependsOn: [
    chatDeployment
    embeddingDeployment
    foundryOpenAIUserAssignment
  ]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
    }
    template: {
      containers: [
        {
          name: 'orchestrator'
          image: runtimeImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'DSF_PRODUCT', value: product }
            { name: 'AZURE_CLIENT_ID', value: runtimeIdentity.properties.clientId }
            { name: 'AZURE_APPCONFIG_ENDPOINT', value: appConfig.properties.endpoint }
            { name: 'AZURE_KEYVAULT_URI', value: keyVault.properties.vaultUri }
            { name: 'AZURE_COSMOS_ENDPOINT', value: cosmos.outputs.endpoint }
            { name: 'AZURE_OPENAI_ENDPOINT', value: foundry.properties.endpoint }
            { name: 'AZURE_OPENAI_DEPLOYMENT', value: chatModel }
            { name: 'AZURE_OPENAI_EMBEDDING_DEPLOYMENT', value: embeddingModel }
            { name: 'AZURE_AI_PROJECT_ENDPOINT', value: aiProjectEndpoint }
            { name: 'WEBIQ_BING_CONNECTION_ID', value: enableBingGrounding ? bingConnection.id : '' }
            { name: 'WEBIQ_PROVIDER', value: 'foundry' }
            { name: 'GITHUB_APP_ID', value: githubAppId }
            { name: 'GITHUB_INSTALLATION_ID', value: githubInstallationId }
            { name: 'GITHUB_REPOSITORY', value: githubRepository }
            { name: 'GITHUB_APP_PRIVATE_KEY_SECRET', value: 'github-app-private-key' }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.properties.ConnectionString
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs (consumed by the ACA runtime's azure-mode configuration)
// ---------------------------------------------------------------------------

@description('Cosmos DB document endpoint.')
output cosmosEndpoint string = cosmos.outputs.endpoint

@description('App Configuration endpoint.')
output appConfigEndpoint string = appConfig.properties.endpoint

@description('Key Vault URI.')
output keyVaultUri string = keyVault.properties.vaultUri

@description('Application Insights connection string.')
output appInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Azure OpenAI endpoint the runtime calls (the created Azure AI Foundry account).')
output openaiEndpoint string = foundry.properties.endpoint

@description('Azure OpenAI chat deployment the runtime calls (created here).')
output openaiDeployment string = chatModel

@description('Azure OpenAI embedding deployment the runtime calls (created here).')
output openaiEmbeddingDeployment string = embeddingModel

@description('Principal ID of the runtime user-assigned identity (data-plane RBAC holder).')
output runtimePrincipalId string = runtimeIdentity.properties.principalId

@description('Name of the orchestrator Container App.')
output orchestratorAppName string = orchestratorApp.name

@description('Name of the per-product Key Vault.')
output keyVaultName string = keyVault.name

@description('Application Insights resource id (consumed by the SRE agent connector + RBAC).')
output appInsightsId string = appInsights.id

@description('Log Analytics workspace resource id (consumed by the SRE agent connector + RBAC).')
output logAnalyticsId string = logAnalytics.id

@description('Foundry project endpoint the WebIQ source agent calls (empty when Bing grounding is disabled).')
output aiProjectEndpoint string = aiProjectEndpoint

@description('Grounding with Bing Search connection id for WEBIQ_BING_CONNECTION_ID (empty when disabled).')
output bingConnectionId string = enableBingGrounding ? bingConnection.id : ''
