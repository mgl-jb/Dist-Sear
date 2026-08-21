param namePrefix string
param location string
param imageTag string
param nodeReplicas int

param identityId string
param identityClientId string

param logsCustomerId string
@secure()
param logsSharedKey string
param insightsConnectionString string

param registryServer string
param cosmosEndpoint string
param blobEndpoint string
param redisHost string

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logsCustomerId
        sharedKey: logsSharedKey
      }
    }
  }
}

// Shared by both roles. Managed identity means no keys or connection strings anywhere.
var commonEnv = [
  { name: 'DistSear__Storage', value: 'Azure' }
  { name: 'DistSear__Azure__CosmosEndpoint', value: cosmosEndpoint }
  { name: 'DistSear__Azure__BlobEndpoint', value: blobEndpoint }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insightsConnectionString }
  // Tells DefaultAzureCredential which identity to use when several are available.
  { name: 'AZURE_CLIENT_ID', value: identityClientId }
]

resource coordinator 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-coordinator'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        // The only externally reachable component. Nodes stay internal.
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'coordinator'
          image: '${registryServer}/distsear-coordinator:${imageTag}'
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: concat(commonEnv, [
            { name: 'ConnectionStrings__Redis', value: '${redisHost}:6380,ssl=true,abortConnect=false' }
          ])
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 10
            }
            {
              // Readiness fails while any shard is unreachable, so traffic is withheld rather than
              // being answered with silently partial results.
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // The coordinator is stateless, so it can scale on demand.
        minReplicas: 1
        maxReplicas: 10
        rules: [
          {
            name: 'http-concurrency'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
}

resource node 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-node'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        // Internal only: nodes are reachable by the coordinator through the environment's DNS, and
        // by nothing outside it.
        external: false
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'node'
          image: '${registryServer}/distsear-node:${imageTag}'
          // Index nodes hold segments in memory, so they are sized for memory rather than CPU.
          resources: { cpu: json('2.0'), memory: '4Gi' }
          env: concat(commonEnv, [
            // Container Apps supplies a distinct replica name, which becomes the node identity that
            // shard placement hashes on.
            { name: 'CONTAINER_APP_REPLICA_NAME', value: '' }
          ])
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 10
            }
            {
              // A node still recovering a shard is alive but must not receive queries.
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 5
              failureThreshold: 6
            }
          ]
        }
      ]
      scale: {
        // Never zero. A node with no replicas owns no shards, and the cluster would have nothing to
        // query and no way to come back without one.
        minReplicas: nodeReplicas
        maxReplicas: nodeReplicas
      }
    }
  }
}

output coordinatorUrl string = 'https://${coordinator.properties.configuration.ingress.fqdn}'
output nodeAppName string = node.name
