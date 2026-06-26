// cosmos.bicep
// Cosmos DB for NoSQL account with native vector search enabled, plus the
// product-scoped runtime database and containers.

@description('Cosmos DB account name (must be globally unique, 3-44 lowercase chars).')
param accountName string

@description('Azure region.')
param location string

@description('Database name for unified memory.')
param databaseName string = 'dsf'

@description('Container names for the runtime stores.')
param containerNames array = [
  'working'
  'records'
  'lessons'
  'charters'
]

@description('Shared autoscale maximum RU/s for the runtime database.')
param maxThroughput int = 1000

@description('Principal IDs to grant Cosmos data-plane Data Contributor (account-scoped). Empty list = skip.')
param dataPlanePrincipalIds array = []

@description('Tags applied to the resources.')
param tags object = {}

// Cosmos built-in 'Data Contributor' SQL role (account-scoped).
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: accountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      // Native vector search for NoSQL (design §7.2: Cosmos vector for dedup/lessons).
      {
        name: 'EnableNoSQLVectorSearch'
      }
    ]
    // Cheapest footprint for an authored-but-not-yet-scaled deployment.
    enableFreeTier: false
    disableLocalAuth: true
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
    // Shared autoscale throughput: all runtime containers draw from one RU/s pool
    // (cheaper than provisioning each container separately).
    options: {
      autoscaleSettings: {
        maxThroughput: maxThroughput
      }
    }
  }
}

// One container per runtime store (memory: working/records/lessons; charter: charters).
// Partition key '/id' -- every persisted item carries a unique 'id' and the runtime never
// sets a separate partition field. TTL is enabled (defaultTtl -1) so items that set their
// own 'ttl' (the working-memory tier) expire while the rest persist. Similarity ranking is
// done client-side, so no Cosmos-native vector index is provisioned here.
resource containers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for containerName in containerNames: {
    parent: database
    name: containerName
    properties: {
      resource: {
        id: containerName
        partitionKey: {
          paths: [
            '/id'
          ]
          kind: 'Hash'
        }
        defaultTtl: -1
      }
    }
  }
]

// Data-plane role assignments (SQL RBAC): grant each principal the account-scoped
// Data Contributor role. Includes the runtime identity AND the human operator so both
// the deployed runtime and a laptop `dsf sweep` (operator's az-login principal) can
// read/write institutional memory.
resource cosmosDataAssignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for principalId in dataPlanePrincipalIds: if (!empty(principalId)) {
    parent: account
    name: guid(account.id, principalId, cosmosDataContributorRoleId)
    properties: {
      roleDefinitionId: '${account.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
      principalId: principalId
      scope: account.id
    }
  }
]

@description('The Cosmos account resource ID.')
output id string = account.id

@description('The Cosmos account name.')
output name string = account.name

@description('The Cosmos document endpoint.')
output endpoint string = account.properties.documentEndpoint

@description('Database name.')
output databaseName string = database.name
