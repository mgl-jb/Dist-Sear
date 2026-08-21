# Distributed Search Engine — C# / .NET 10 / Azure

## Context

`mgl-jb/Dist-Sear` is an empty repository (no commits, no default branch content). The goal is a
**distributed search engine built from scratch** — the engine itself is the subject of the repo, not a
wrapper around a managed service. Azure AI Search is explicitly excluded, so relevance ranking,
sharding, replication, recovery and fan-out all have to be implemented.

**Intended outcome:** `docker compose up` yields a working multi-node search cluster locally;
`dotnet test` passes with no Docker required; `az deployment` provisions the same topology on Azure;
and the repo carries a permanent, versioned architecture documentation set.

Work happens on branch `claude/distributed-search-csharp-azure-le11vx`. No PR unless asked.

---

## 1. Decisions

These were settled with the user up front. Each is recorded permanently as an ADR in `docs/adr/`
(see §8) so the reasoning survives the session.

| # | Decision | Chosen | Rejected alternatives & why |
| --- | --- | --- | --- |
| 1 | Index engine | **Hand-written inverted index + BM25**, with small focused libs only where reinventing adds nothing (`Porter2Stemmer`) | *Lucene.NET 4.8 per shard* — its current release is a beta and it would reduce the project to glue code. *Pure from-scratch everything* — reimplementing a stemmer adds no insight. |
| 2 | Source of truth | **Cosmos DB**, with the **Change Feed** driving indexing | *Blob + Service Bus ingest log* — needs a hand-rolled replication log and an extra SQL Server sidecar locally. Cosmos gives durable documents, ordering per partition key, and at-least-once replay for free. |
| 3 | Compute | **Azure Container Apps** — coordinator on external ingress, nodes on internal ingress with DNS service discovery, KEDA autoscaling, managed identity | *AKS StatefulSets* — closest to how Elasticsearch really deploys, but a large ops and IaC surface for no added insight here. |
| 4 | Segment durability + coordination | **Blob Storage** — segment snapshots, and blob **leases** for leader election | *ZooKeeper/etcd* — another stateful service to run. Blob lease is the documented Azure Leader Election pattern and needs no extra infrastructure. |
| 5 | Query cache | **Azure Managed Redis** | In-process cache only — wrong for a fan-out tier that scales horizontally. |
| 6 | Feature scope | Core query/relevance **+** facets/highlight/suggest **+** distributed-systems concerns **+** production hardening (all four groups) | — |
| 7 | Deliverable | Runnable + tested + Bicep IaC + CI + permanent docs | — |

### 1.1 The key consequence of "Cosmos as source of truth"

Because Cosmos holds the durable documents, **replication needs no primary/replica replication log**.
Every replica of a shard independently re-derives its index from the change feed. This is
shared-nothing re-derivation rather than log shipping, and it removes Service Bus from the critical
path entirely — which also keeps the local stack light (no SQL Server sidecar, which the Service Bus
emulator would otherwise require).

Three sub-decisions follow, each of which is a real trap worth getting right:

**(a) Pull model, not the Change Feed Processor.** The processor's lease mechanism *distributes*
ranges across consumers — exactly the wrong shape for replicas, which each need the *complete* feed
for their shard. The **pull model** is the only one that can read a single partition key. So:

- The `documents` container is partitioned by `/shardKey`, where
  `shardKey = "shard-{murmur3(routingKey ?? id) % numShards}"`.
- Each node runs one pull iterator per owned shard, scoped with `FeedRange.FromPartitionKey(shardKey)`.
- A node therefore reads only its own data — no cross-shard RU waste.

**(b) Continuation tokens are the checkpoint.** Persisted per `(node, shard)` to Blob after each
applied batch. This yields at-least-once delivery, and indexing is an upsert keyed by `docId`, so
at-least-once is idempotent. The token also anchors snapshot recovery (§5).

**(c) Deletes are soft.** `_deleted: true` plus Cosmos TTL to reap. The latest-version change feed does
not carry deletes at all, and `AllVersionsAndDeletes` mode has a ~5-minute retention window and cannot
start from the beginning — so it cannot support rebuilding a replica. Soft delete is the correct
workaround; the index turns it into a tombstone.

---

## 2. Solution layout

```
Dist-Sear.sln
src/
  DistSear.Abstractions/     contracts: SearchRequest/Response, mappings,
                             IDocumentStore, ISegmentStore, IClusterStore, INodeTransport
  DistSear.Analysis/         tokenizer + token-filter chain + per-field analyzer registry
  DistSear.Index/            segments, postings, BM25, query AST + parser, facets, highlight, suggest
  DistSear.Cluster/          shard map, HRW routing, membership, leader election, replica selection
  DistSear.Storage.Azure/    Cosmos document store, Blob segment store + lease elector, Redis cache
  DistSear.Node/             ASP.NET Core index node: shard-local query + change-feed indexer
  DistSear.Coordinator/      ASP.NET Core gateway: fan-out/merge, auth, cache, rate limiting
  DistSear.Client/           typed HTTP client SDK
tests/
  DistSear.Analysis.Tests   DistSear.Index.Tests   DistSear.Cluster.Tests
  DistSear.IntegrationTests (emulator-backed, trait-gated)
tools/DistSear.Seeder/       loads a sample corpus into Cosmos
deploy/bicep/                main.bicep + modules
deploy/docker-compose.yml    cosmos emulator + azurite + redis + coordinator + 3 nodes
docs/                        permanent architecture documentation (§8)
.github/workflows/ci.yml
```

**Architectural rule: every Azure dependency sits behind an interface in `DistSear.Abstractions`, with
both an Azure and an in-memory implementation.** This is what lets an entire cluster — N nodes,
routing, fan-out, replica failover — run in-process inside unit tests with zero Docker, and keeps
`dotnet test` green on any machine. It is the single most load-bearing design choice for testability.

---

## 3. Analysis chain (`DistSear.Analysis`)

- `ITokenizer` → `StandardTokenizer`: Unicode letter/digit runs, **offsets preserved** (highlighting depends on them).
- `ITokenFilter` chain: `Lowercase`, `AsciiFolding`, `Stopword`, `PorterStem` (via `Porter2Stemmer`),
  `Synonym` (Solr-style rules, both expand and contract forms), `EdgeNGram` (feeds the suggester).
- `Analyzer` = tokenizer + filter chain, emitting `Token(Term, Position, StartOffset, EndOffset)`.
- `AnalyzerRegistry` resolves a per-field analyzer from the index mapping; `keyword` fields bypass analysis entirely.
- Index-time and search-time analyzers are configurable separately (they must differ for synonyms and edge-ngrams).

## 4. Index core (`DistSear.Index`)

**Storage**
- In-memory segment: `field → term → PostingsList`; posting = `(docId, termFreq, positions[])`.
  Plus per-field norms (field length, for BM25), a doc-values column store (sorting + faceting), and
  stored fields (highlight source).
- On-disk segment format: sorted term dictionary with a skip index; postings as **variable-byte
  delta-gap encoded** doc IDs; separate positions block; compressed stored-field blocks. Read via
  `RandomAccess`/memory-mapped file.
- Deletes: `LiveDocs` bitset tombstones. An update is a delete + add.
- Merge: simplified **tiered merge policy** with a background scheduler, so segment count stays bounded.

**Scoring**
- `Bm25Similarity` (k1 = 1.2, b = 0.75) over per-field norms and collection statistics.
- **Distributed IDF is a genuine correctness trap.** Each shard only knows its own document
  frequencies, so identical documents can score differently depending on which shard they landed on.
  Default is per-shard IDF (fast, slightly approximate); `search_type=dfs_query_then_fetch` adds a
  global term-statistics pre-pass across shards for exact scoring. Both are implemented and documented.

**Query**
- AST: Term, Phrase (positional, with slop), Boolean (must / should / must_not + `minimum_should_match`),
  Prefix, Wildcard, Fuzzy (Levenshtein automaton, max edit distance 2), Range, Terms, MatchAll, Boost.
- Recursive-descent parser over Lucene-ish syntax:
  `title:(fast AND search) -tag:draft "exact phrase"~2 price:[10 TO 50] serch~1`
- Multi-field search with per-field boosts.
- Execution: `DocIdSetIterator` scorers with **WAND top-k pruning**; bounded min-heap collector sized
  `from+size`; `FieldComparator` for sort-by-field; `search_after` cursor for deep paging (offset
  pagination degrades badly across shards, so the cursor is the supported deep-paging path).

**Facets, highlighting, suggestions**
- Terms + range facets computed from doc values. Shards over-request `shard_size = size*1.5 + 10`, and
  the response carries `doc_count_error_upper_bound` and `sum_other_doc_count` — the standard, honest
  accounting for the fact that **distributed terms facets are approximate**.
- Highlighting: re-analyze the stored field, match query terms by position, score candidate passages,
  wrap hits in `<em>`.
- Suggest: weighted completion trie with per-node cached top-k for typeahead; term n-gram index plus
  Damerau-Levenshtein rescoring by document frequency for did-you-mean.

## 5. Cluster layer (`DistSear.Cluster`) and the node (`DistSear.Node`)

**Shard map**
- Per index: `numShards`, `numReplicas`, and `shardId → ShardAllocation(primary, replicas[], state)`
  with state ∈ `Unassigned | Initializing | Recovering | Started`.
- Persisted in a Cosmos `cluster` container, mutated under **ETag optimistic concurrency**.
- Doc → shard: `murmur3(routingKey ?? docId) % numShards`, with a **fixed shard count per index**
  (same choice Elasticsearch makes — it keeps routing stateless and lets `shardKey` double as the
  Cosmos partition key).
- Shard → node: **rendezvous (HRW) hashing** — deterministic, needs no ring state, and moves only the
  minimum number of shards when membership changes.

**Membership and leadership**
- Nodes heartbeat into a `nodes` container with TTL; a missed TTL means the node is dead.
- **Leader election via Blob lease** (`BlobLeaseClient`, 30 s duration, renewed on a shorter interval).
  Exactly one coordinator at a time reconciles the allocation table: detect dead nodes, reassign their
  shards, drive the state machine. The lease blob is used for nothing else, per the documented pattern.
- **Adaptive replica selection** on the coordinator: rank replicas by EWMA response time plus
  outstanding request count, falling back to round-robin. Prevents one slow replica from dominating tail latency.

**Indexing path**
- Change-feed pull iterator per owned shard → `SegmentWriter`; continuation token checkpointed to Blob
  after each applied batch.
- **Near-real-time**: the in-memory buffer is searchable immediately; a `refresh_interval` (default 1 s)
  publishes a new reader snapshot. Bulk supports `?refresh=wait_for` so tests are deterministic.
- **Snapshots**: merged segments are uploaded periodically to Blob at `{index}/{shard}/{gen}/` with a
  `commit.json` manifest recording the change-feed continuation token at that point. A restarted or
  newly-allocated replica downloads the latest snapshot and resumes the feed *from that token* — recovery
  is seconds rather than a full replay from the beginning of the container.
- Internal endpoints: `POST /_internal/shards/{id}/query` (phase 1), `/fetch` (phase 2), `/stats` (DFS pre-pass).

## 6. Coordinator (`DistSear.Coordinator`)

**`query_then_fetch` scatter-gather** — the canonical distributed search algorithm, implemented properly:

1. **Phase 1 (query):** ask every shard for its top `from+size`, returning only
   `(docId, score, sortValues)`. The coordinator merges these into a global top-k.
2. **Phase 2 (fetch):** request stored fields and highlights *only* from the shards that own the
   winning documents.

The point is that full documents are never shipped from every shard — only from the ones that won.

- Per-shard timeouts via linked `CancellationTokenSource`, with **partial results** rather than total
  failure: the response carries `_shards: { total, successful, skipped, failed }` and `timed_out: true`.
- Polly resilience pipeline: retry against a *different* replica, per-node circuit breaker, concurrency limiter.
- Public API: `POST /indexes/{index}/_search`, `_suggest`, `_count`, `_bulk`;
  `PUT /indexes/{index}` (mapping); `POST /_aliases`.
- Write path: `_bulk` validates against the mapping, computes `shardKey`, and upserts into Cosmos using
  `TransactionalBatch` per partition key.

## 7. Production hardening

- **Auth**: Entra ID JWT bearer (`Microsoft.Identity.Web`) for user calls; hashed API keys in Cosmos for
  service calls; `DefaultAzureCredential` (managed identity) for Cosmos/Blob/Redis — **no connection
  strings or keys in configuration**.
- **Document-level security**: every document carries `_acl: string[]`; the coordinator injects a
  mandatory `TermsQuery("_acl", callerGroups)` filter clause that a user query cannot override or remove.
- **Multi-tenancy / zero-downtime reindex**: index aliases held in cluster state — build `products_v2`
  alongside `products_v1`, then swap the alias atomically.
- **Rate limiting**: ASP.NET Core `RateLimiter`, fixed-window plus concurrency, partitioned per API key.
- **Query caching**: Redis, keyed on a hash of (index, normalized query, filters, from/size, **caller ACL
  set** — so cache entries can never leak across principals), short TTL, invalidated by an index
  generation counter bumped on refresh.
- **Observability**: OpenTelemetry `ActivitySource` spanning the fan-out
  (`search.coordinator` → `search.shard`), metrics for query latency, per-shard latency, indexing
  throughput and merge duration; Azure Monitor OTel exporter to Application Insights.
- **Health**: `/health/live` and `/health/ready`, where ready ⇔ every owned shard is `Started`.

## 8. Documentation — permanent, in-repo (`docs/`)

The documentation is a deliverable, committed and versioned alongside the code, not a session artifact:

| File | Contents |
| --- | --- |
| `docs/ARCHITECTURE.md` | System overview; request path, indexing path, and recovery sequence; shard-allocation state machine; component responsibilities. Mermaid diagrams throughout. |
| `docs/IMPLEMENTATION-PLAN.md` | **This plan**, committed verbatim as the implementation roadmap of record, with phase checkboxes kept up to date as work lands. |
| `docs/adr/0001-custom-inverted-index.md` | Decision 1 — custom engine vs Lucene.NET, with the rejected options and rationale. |
| `docs/adr/0002-cosmos-as-source-of-truth.md` | Decision 2 — and the pull-model / soft-delete / no-replication-log consequences of §1.1. |
| `docs/adr/0003-container-apps-hosting.md` | Decision 3 — Container Apps vs AKS. |
| `docs/adr/0004-blob-lease-leader-election.md` | Decision 4 — blob lease vs an external consensus store. |
| `docs/adr/0005-sharding-and-routing.md` | Fixed shard count, murmur3 doc routing, HRW shard→node placement. |
| `docs/adr/0006-query-then-fetch.md` | Two-phase fan-out; distributed IDF trade-off; approximate facet accounting. |
| `docs/DATA-MODEL.md` | Cosmos containers and partition keys; on-disk segment binary format; Blob snapshot layout. |
| `docs/API.md` | REST surface with request/response examples for search, suggest, bulk, mappings, aliases. |
| `docs/OPERATIONS.md` | Deploying via Bicep, scaling, rebalancing, recovery runbook, metrics and alerts. |
| `docs/LOCAL-DEV.md` | docker-compose quickstart, emulator caveats, seeding, smoke script. |

`README.md` links to all of the above and carries the quickstart.

## 9. Infrastructure

- **`deploy/bicep/main.bicep`** (+ modules): Container Apps Environment; ACR; Cosmos serverless account
  with containers `documents` (pk `/shardKey`), `cluster`, `nodes`, `apikeys`; Storage account
  (snapshots + lease blobs); Azure Managed Redis; Log Analytics + Application Insights; a user-assigned
  managed identity with RBAC role assignments (Cosmos DB Data Contributor, Storage Blob Data
  Contributor). Coordinator = external ingress; nodes = internal ingress, `minReplicas: 3`.
- **`deploy/docker-compose.yml`**: `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest`
  started with `--protocol https` (the .NET SDK does not support the emulator's HTTP mode), so the dev
  configuration registers an `HttpClientFactory` with certificate validation bypassed — **development
  only, behind an explicit env flag, never compiled into the production path**. Plus Azurite, Redis,
  the coordinator, and 3 nodes.
- **`.github/workflows/ci.yml`**: build → unit tests → integration tests with the Cosmos emulator as a
  GitHub Actions service container → `az bicep build` lint → container image build.

---

## 10. Verification

**Unit — no Docker, must always pass**
- Analyzer chain golden tests; postings encode/decode roundtrip.
- BM25 scores checked against hand-computed values.
- Property test: WAND top-k ≡ exhaustive scoring over randomly generated corpora.
- Fuzzy automaton ≡ naive Levenshtein; query parser round-trips.
- Facet counts, highlight offsets, suggester ranking.
- HRW hashing: distribution evenness, and minimal shard movement when a node leaves.
- **In-process cluster tests** on the in-memory stores: 3 nodes × 4 shards × 2 replicas — verifying
  routing, top-k merge correctness *against a single-node index over the same corpus*, shard timeout →
  partial results, and node kill → reallocation and recovery.

**Integration — emulator-backed, `dotnet test --filter Category=Integration`**
`docker compose up -d` → seed → assert end-to-end search, facets, `search_after` deep paging,
soft-delete visibility after refresh, and snapshot + change-feed recovery after killing a node container.

**Manual smoke**: a `curl` script in `docs/` exercising bulk index → search → suggest → facet → alias swap.

**Toolchain**: the .NET SDK is not installed in this container — first step is installing **.NET 10 (LTS)**
via `dotnet-install.sh --channel 10.0`. Verified reachable: NuGet (`api.nuget.org` → 200), MCR, and the
dotnet install script. Host has 30 GB free disk / 15 GB RAM / 4 cores — enough for the emulator stack.

---

## 11. Sequencing and risks

**Phases** (committed at the end of each, so progress is reviewable incrementally):
1. Analysis chain → 2. Index core → 3. Cluster layer → 4. Node + indexing path →
5. Coordinator fan-out → 6. Hardening → 7. Infrastructure → 8. Docs & seed data.

Phases 1–5 are the core deliverable; 6–8 layer on top. `docs/` is written incrementally alongside the
phases rather than left to the end, so the ADRs land with the code that implements them.

**Risks**
- The Cosmos **vNext Linux emulator is preview**: Request Units are not implemented, and parallel
  cross-partition query is "not yet implemented". Our query path is per-partition-key, so this is
  tolerable — and integration tests are trait-gated while the in-memory stores keep unit tests fully
  independent of the emulator.
- Scope is large. If a phase proves bigger than expected I will finish it properly and report status
  rather than silently thinning out later phases.
