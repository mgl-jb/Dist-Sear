targetScope = 'resourceGroup'

@description('Prefix for every resource name. Must be globally unique-ish; a suffix is appended.')
@minLength(3)
@maxLength(11)
param namePrefix string

@description('Location for all resources.')
param location string = resourceGroup().location

@description('Container image tag deployed to both roles.')
param imageTag string = 'latest'

@description('Index nodes to run. Never scales to zero: a node with no replicas owns no shards.')
@minValue(1)
param nodeReplicas int = 3

var suffix = uniqueString(resourceGroup().id)

// Storage account names cap at 24 characters, tighter than every other resource here, so the
// suffix is shortened for that one rather than shrinking the prefix everyone else uses.
var shortSuffix = take(suffix, 8)

var cosmosName = toLower('${namePrefix}cos${suffix}')
var storageName = toLower('${take(namePrefix, 11)}st${shortSuffix}')
var registryName = toLower('${namePrefix}acr${suffix}')

// One user-assigned identity shared by both roles. Shared deliberately: they need the same data
// access, and a single identity means one set of role assignments to reason about.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-identity'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: registryName
  location: location
  sku: { name: 'Basic' }
  properties: {
    // Images are pulled with the managed identity, so the admin account stays off.
    adminUserEnabled: false
  }
}

// Serverless: this workload is bursty and mostly idle between indexing runs, so paying per request
// beats provisioning throughput that sits unused.
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: cosmosName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [ { name: 'EnableServerless' } ]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [ { locationName: location, failoverPriority: 0, isZoneRedundant: false } ]
    // Keys are never used; both roles authenticate with the managed identity.
    disableLocalAuth: true
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: 'distsear'
  properties: {
    resource: { id: 'distsear' }
  }
}

// Partitioned by the shard a document routes to. This is what lets an index node read the change
// feed for exactly the shards it owns and nothing else.
resource documents 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'documents'
  properties: {
    resource: {
      id: 'documents'
      partitionKey: { paths: [ '/shardKey' ], kind: 'Hash' }
      // Off by default; only delete tombstones set a per-item TTL.
      defaultTtl: -1
    }
  }
}

resource clusterContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'cluster'
  properties: {
    resource: {
      id: 'cluster'
      partitionKey: { paths: [ '/partition' ], kind: 'Hash' }
    }
  }
}

// Node registrations expire on their own, so liveness detection needs no reaper process.
resource nodesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'nodes'
  properties: {
    resource: {
      id: 'nodes'
      partitionKey: { paths: [ '/nodeId' ], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}

resource apiKeys 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'apikeys'
  properties: {
    resource: {
      id: 'apikeys'
      partitionKey: { paths: [ '/keyHash' ], kind: 'Hash' }
    }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource snapshots 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'snapshots'
}

// Leases live in their own container. The lease blob carries no data of its own: anything written
// to it would be unreadable by instances that do not hold the lease.
resource leases 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'leases'
}

resource redis 'Microsoft.Cache/redis@2024-11-01' = {
  name: '${namePrefix}-redis'
  location: location
  properties: {
    sku: { name: 'Basic', family: 'C', capacity: 0 }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
  }
}

module roles 'modules/role-assignments.bicep' = {
  name: 'role-assignments'
  params: {
    principalId: identity.properties.principalId
    cosmosAccountName: cosmos.name
    storageAccountName: storage.name
    registryName: registry.name
  }
}

module environment 'modules/container-apps.bicep' = {
  name: 'container-apps'
  params: {
    namePrefix: namePrefix
    location: location
    imageTag: imageTag
    nodeReplicas: nodeReplicas
    identityId: identity.id
    identityClientId: identity.properties.clientId
    logsCustomerId: logs.properties.customerId
    logsSharedKey: logs.listKeys().primarySharedKey
    insightsConnectionString: insights.properties.ConnectionString
    registryServer: registry.properties.loginServer
    cosmosEndpoint: cosmos.properties.documentEndpoint
    blobEndpoint: storage.properties.primaryEndpoints.blob
    redisHost: redis.properties.hostName
  }
  dependsOn: [ roles, documents, clusterContainer, nodesContainer, apiKeys, snapshots, leases ]
}

output coordinatorUrl string = environment.outputs.coordinatorUrl
output registryLoginServer string = registry.properties.loginServer
output cosmosEndpoint string = cosmos.properties.documentEndpoint
