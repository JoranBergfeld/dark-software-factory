// cosmos.bicep
// Cosmos DB for NoSQL account with native vector search enabled, plus the
// unified institutional-memory database and a TTL-enabled 'memory' container.
// Vector search backs dedup / lessons / prior-art retrieval (design §7.2).

@description('Cosmos DB account name (must be globally unique, 3-44 lowercase chars).')
param accountName string

@description('Azure region.')
param location string

@description('Database name for unified memory.')
param databaseName string = 'dsf'

@description('Container name for the memory store.')
param containerName string = 'memory'

@description('Partition key path for the memory container.')
param partitionKeyPath string = '/partitionKey'

@description('Default TTL (seconds) for the working-memory tier. -1 = on but no default expiry; items set their own ttl.')
param defaultTtlSeconds int = -1

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
  }
}

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: containerName
  properties: {
    resource: {
      id: containerName
      partitionKey: {
        paths: [
          partitionKeyPath
        ]
        kind: 'Hash'
      }
      // TTL enabled so the working-memory tier expires automatically.
      defaultTtl: defaultTtlSeconds
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/*'
          }
        ]
        excludedPaths: [
          {
            path: '/embedding/*'
          }
          {
            path: '/_etag/?'
          }
        ]
        // Vector index over the embedding field used for similarity search.
        vectorIndexes: [
          {
            path: '/embedding'
            type: 'diskANN'
          }
        ]
      }
      // Vector embedding policy: 1536-dim cosine vectors (text-embedding-3-small).
      vectorEmbeddingPolicy: {
        vectorEmbeddings: [
          {
            path: '/embedding'
            dataType: 'float32'
            distanceFunction: 'cosine'
            dimensions: 1536
          }
        ]
      }
    }
    options: {
      autoscaleSettings: {
        maxThroughput: 1000
      }
    }
  }
}

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

@description('Container name.')
output containerName string = container.name
