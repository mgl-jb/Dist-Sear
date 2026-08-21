# ADR 0002 — Cosmos DB is the source of truth; the change feed drives indexing

**Status:** Accepted

## Context

A distributed index needs durable documents, a way to replicate an index across replicas, and a way
to rebuild a replica that was lost. Doing this with a hand-rolled replication log means inventing
ordering, retention, and catch-up semantics.

## Decision

Azure Cosmos DB holds the authoritative documents. Every index replica derives its own index by
reading the Cosmos **change feed**. The search index is a disposable materialised view.

## Consequences

### There is no write-path primary

Because every copy re-derives from the same durable source, copies are symmetric. Replication is
shared-nothing re-derivation, not log shipping. `ShardCopy.IsSnapshotOwner` exists only so that N
replicas do not each upload identical snapshot bytes to Blob Storage.

This also removes Service Bus from the design entirely, which keeps the local development stack
light — the Service Bus emulator requires an additional SQL Server container.

### The pull model, not the Change Feed Processor

The Change Feed Processor's lease mechanism *distributes* feed ranges across consumers. That is
exactly the wrong shape here: replicas each need the *complete* feed for their shard, not a share of
it. The **pull model** is the only one that can read a single partition key.

So the `documents` container is partitioned by `/shardKey`, and each node opens one pull cursor per
owned shard scoped with `FeedRange.FromPartitionKey(shardKey)`. A node therefore reads only its own
data and never pays to scan other shards.

### Continuation tokens are the checkpoint

The token is persisted after a batch has been applied, giving at-least-once delivery. Indexing is an
upsert keyed by document id, so replaying a batch is idempotent. The same token is recorded in each
snapshot manifest, which is what lets a recovering replica download a snapshot and resume from that
exact point instead of replaying from the beginning.

### Deletes are soft

The latest-version change feed does not carry deletions. The `AllVersionsAndDeletes` mode does, but
it has roughly a five-minute retention window and cannot start from the beginning of a container —
so it cannot rebuild a replica. Deletes are therefore modelled as an update setting `_deleted: true`,
which the index turns into a tombstone; Cosmos TTL reaps the row later.

## Costs

- Every replica of a shard consumes RUs reading the feed. This is the price of not maintaining a
  replication protocol.
- Cosmos is the most expensive component in the deployment. It is provisioned serverless for that reason.
