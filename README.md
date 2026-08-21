# Dist-Sear

A distributed full-text search engine written from scratch in C# and .NET 10, running on Azure.

The search engine *is* the project: the inverted index, BM25 ranking, query execution, sharding,
replication and recovery are all implemented here rather than delegated to a managed search service.
Azure supplies durability and compute — Cosmos DB, Blob Storage, Container Apps — not retrieval.

```bash
dotnet test                                            # 333 tests, no Docker required
docker compose -f deploy/docker-compose.yml up -d      # a real cluster, locally
dotnet run --project tools/DistSear.Seeder             # create an index and load a corpus
```

## What it does

**Retrieval** — BM25 relevance; boolean, phrase (with slop), prefix, wildcard, fuzzy, range and
terms queries; multi-field search with per-field boosts; a Lucene-style query syntax; WAND top-k
pruning.

**Beyond matching** — terms and range facets with honest error bounds, snippet highlighting,
typeahead completion, did-you-mean correction, synonyms, stemming.

**Distributed** — sharding by hash routing, replication by independent re-derivation, two-phase
scatter-gather, per-shard timeouts with partial results, adaptive replica selection, cursor-based
deep paging, near-real-time indexing, snapshot-based recovery, leader-elected shard allocation.

**Production concerns** — Entra ID and API-key authentication, document-level security, index
aliases for zero-downtime reindexing, per-caller rate limiting, an ACL-scoped result cache,
OpenTelemetry tracing across the fan-out.

## The central idea

Cosmos DB holds the documents; the search index is a **disposable materialised view** rebuilt from
the Cosmos change feed.

That one decision removes a whole category of machinery. There is no replication protocol, because
every replica of a shard independently re-derives its index from the same durable, ordered feed.
There is no primary, because copies are symmetric. Recovery is not a special path, because a node
starting empty is the normal case on ephemeral compute — it restores the newest snapshot and replays
the feed from the position that snapshot recorded.

What it costs is stated plainly rather than hidden: writes are searchable a moment after they are
durable, every replica pays to read the feed, and deletes must be tombstones because the
latest-version change feed does not carry deletions.

## Honest about the hard parts

Distributed search has three well-known places where correctness quietly degrades. Each is
implemented deliberately and surfaced in the response rather than papered over:

- **Scores are per-shard by default.** Each shard computes IDF from its own document frequencies, so
  identical documents can score differently. `searchType=DfsQueryThenFetch` sums frequencies across
  shards for exact scoring, at the cost of a round trip.
- **Terms facets are approximate.** A shard reports only its own top buckets, so
  `docCountErrorUpperBound` and `sumOtherDocCount` quantify what might be missing. Range facets are
  exact.
- **Total hits can be a lower bound.** Pruning skips documents that cannot place, so counts are exact
  up to a configurable threshold and flagged as a floor beyond it.

## Testing

333 tests, of which 315 need no Docker at all. Every Azure dependency sits behind an interface with
an in-process implementation, so an entire cluster — routing, recovery, fan-out, replica failover —
runs inside a unit test.

Two tests carry unusual weight. `PruningEquivalenceTests` runs randomised queries with WAND pruning
on and off and asserts the results are byte-identical: pruning may change work done, never output.
`LevenshteinAutomatonTests` verifies the fuzzy automaton exhaustively against a reference
implementation. Both have caught real bugs.

The 18 emulator-backed tests are narrow but essential — they proved the Azure SDKs are driven
correctly, and found three bugs the in-memory stand-ins had hidden: a disposed cancellation token
source, a snapshot manifest that could be rolled backwards by an out-of-order upload, and Cosmos
returning integers as doubles.

## Documentation

| Document | Contents |
| --- | --- |
| [Architecture](docs/ARCHITECTURE.md) | How the pieces fit, with diagrams of the write, read and recovery paths |
| [Data model](docs/DATA-MODEL.md) | Cosmos containers, segment binary format, Blob layout |
| [API](docs/API.md) | The REST surface, query syntax, and how to read a response honestly |
| [Operations](docs/OPERATIONS.md) | Deploying, sizing, monitoring, and a runbook |
| [Local development](docs/LOCAL-DEV.md) | Running the stack and the tests |
| [Decision records](docs/adr/README.md) | Why each choice was made, and what was rejected |
| [Implementation plan](docs/IMPLEMENTATION-PLAN.md) | The roadmap this was built against |

## Layout

```
src/DistSear.Abstractions      contracts and storage interfaces
src/DistSear.Analysis          tokenizer and filter chain
src/DistSear.Index             postings, segments, BM25, queries, facets, highlighting
src/DistSear.Cluster           routing, allocation, coordination, in-memory stores
src/DistSear.Storage.Azure     Cosmos and Blob implementations
src/DistSear.Node              shard runtime and change-feed indexer
src/DistSear.Coordinator       fan-out, merge, auth, caching, public API
src/DistSear.Client            typed client SDK
```

## Requirements

.NET 10 SDK. Docker only for the local cluster and the emulator-backed tests.
