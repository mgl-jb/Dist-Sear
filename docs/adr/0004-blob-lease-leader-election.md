# ADR 0004 — Elect the coordinator with an Azure Blob lease

**Status:** Accepted

## Context

Exactly one process may mutate the shard allocation table at a time. Two processes reallocating
concurrently could assign the same shard twice or strand it unassigned.

## Decision

Use an Azure Blob lease as a distributed mutex, following the Azure Leader Election pattern. The
instance holding the lease on a dedicated blob is the leader; it renews on an interval shorter than
the lease duration, and abandons coordination work the moment renewal fails.

Cluster-state writes are additionally guarded by compare-and-swap on the state's ETag, so even a
split-brain during a handover cannot produce a lost update.

## Alternatives considered

**ZooKeeper, etcd, or Consul.** Genuine consensus, and what a real search cluster would use.
Rejected because it means running and operating another stateful service for a single mutex.

**A Cosmos lease document with TTL.** Workable, but blob leases give server-enforced expiry and
exclusive acquisition semantics directly, with no polling loop to write.

## Consequences

- Blob Storage becomes a dependency of leadership. If it is unavailable no leader can be elected;
  the cluster continues serving queries but stops reallocating shards. Given the durability of Blob
  Storage this is an acceptable failure mode, and degraded-but-serving is the right behaviour.
- A leader whose *work* stalls while its lease renewal keeps succeeding would block reallocation.
  Leadership is therefore paired with the ETag CAS above rather than trusted on its own.
- The lease blob is used for nothing else, as the pattern requires: data written to it would be
  inaccessible to non-holders.
