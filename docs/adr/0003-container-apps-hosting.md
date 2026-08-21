# ADR 0003 — Host on Azure Container Apps rather than AKS

**Status:** Accepted

## Context

The cluster is two container roles: a coordinator that fans out queries, and index nodes that own
shards. Nodes hold local state (segment files) but that state is a cache — the durable copy is in
Cosmos and Blob Storage (ADR 0002).

## Decision

Deploy to Azure Container Apps. The coordinator gets external ingress; nodes get internal ingress
with DNS-based service discovery. Both authenticate to Azure services with managed identity.

## Alternatives considered

**AKS with StatefulSets.** This is how Elasticsearch and Solr genuinely deploy, and persistent
volumes plus stable network identities map neatly onto shards. Rejected because the operational and
IaC surface — cluster upgrades, node pools, Helm, CSI drivers — is large, and it buys little here:
since node-local state is rebuildable from Cosmos, stable persistent volumes are an optimisation
rather than a correctness requirement.

**App Service.** Rejected: no good story for internal-only service-to-service addressing between
replica sets of two different roles.

## Consequences

- Node-local disk is ephemeral. Recovery from snapshot plus change-feed catch-up is therefore on the
  normal startup path, not an exceptional one — which is a better-tested design.
- Scaling is KEDA-driven and the coordinator can scale to zero; index nodes set `minReplicas: 3`,
  because a node that has scaled to zero owns no shards and would have to recover to serve anything.
- Vertical scaling is not available, so shard sizing has to keep a single shard within one replica's
  memory budget.
