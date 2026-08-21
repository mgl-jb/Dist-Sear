# ADR 0005 — Fixed shard count, hash routing, rendezvous placement

**Status:** Accepted

## Context

Two separate routing questions: which shard does a document belong to, and which node hosts a shard.

## Decision

**Document to shard:** `murmur3(routingKey ?? id) % numberOfShards`, with the shard count fixed at
index creation.

**Shard to node:** rendezvous (highest random weight) hashing over the live node set.

## Rationale

Fixing the shard count is what Elasticsearch does, and for the same reason: routing stays stateless
and needs no lookup table, and the computed shard is usable directly as the Cosmos partition key
(ADR 0002). The cost is that changing shard count requires reindexing into a new index and swapping
an alias — which the alias mechanism exists to make routine.

Rendezvous hashing was chosen over a consistent-hash ring because it needs no ring state or virtual
nodes, is a pure function of (shard, node set), and moves only the shards that were on a departed
node. Two coordinators computing placement independently reach the same answer.

## Consequences

- Shard count must be chosen up front from expected corpus size. Too few and a shard exceeds one
  replica's memory; too many and every query pays fan-out overhead against near-empty shards.
- Document distribution is only as even as the hash. A caller supplying an explicit `routingKey`
  (for example, tenant id) deliberately co-locates documents, trading evenness for the ability to
  answer that tenant's queries from a single shard.
- Placement is deterministic but not balanced by size: rendezvous hashing balances shard *count*
  per node, not bytes.
