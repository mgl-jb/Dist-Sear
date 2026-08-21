# Local development

## Running the tests

```bash
dotnet test
```

No Docker required. Every Azure dependency sits behind an interface with an in-process
implementation, so the whole cluster — routing, change-feed indexing, recovery, fan-out, replica
failover — is exercised in-process. The emulator-backed tests report as **skipped** rather than
passing vacuously when the emulators are absent.

To run those too:

```bash
docker compose -f deploy/docker-compose.yml up -d cosmos azurite

DISTSEAR_TEST_COSMOS="https://localhost:8081/" \
DISTSEAR_TEST_BLOB="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://localhost:10000/devstoreaccount1;" \
dotnet test --filter "Category=Integration"
```

These are narrow but essential: the in-process stores prove the cluster logic, and only these prove
the Azure SDKs are driven correctly. Change-feed semantics, ETag concurrency and blob leases are
exactly where an in-memory stand-in can quietly diverge — all three of those areas had a real bug
that only the emulator tests caught.

## Running the cluster

```bash
docker compose -f deploy/docker-compose.yml up -d --build
dotnet run --project tools/DistSear.Seeder
```

That brings up the Cosmos and Azurite emulators, Redis, a coordinator and three index nodes, then
creates the `catalog` index and loads a deterministic corpus.

```bash
curl -s localhost:8080/health/ready
curl -s localhost:8080/_cluster/state | jq '.shards'

curl -s localhost:8080/indexes/catalog/_search \
  -H 'content-type: application/json' \
  -d '{"query":"distributed search","size":3,
       "facets":[{"name":"by_category","field":"category"}],
       "highlight":{"fields":["title"]}}' | jq
```

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `DistSear:Storage` | `InMemory` | `InMemory` or `Azure` |
| `DistSear:Node:NodeId` | replica name | Shard placement hashes on this; it must be stable |
| `DistSear:Node:RefreshInterval` | `00:00:01` | How soon writes become searchable |
| `DistSear:Node:SnapshotInterval` | `00:05:00` | Bounds how much feed a recovery must replay |
| `DistSear:Coordinator:ShardTimeout` | `00:00:10` | Per-shard deadline |
| `DistSear:Coordinator:MaxPageSize` | `1000` | Offset-paging ceiling |
| `DistSear:Coordinator:CacheDuration` | `00:00:10` | Result staleness bound |
| `DistSear:Azure:TombstoneRetention` | `7.00:00:00` | Must exceed the snapshot interval |
| `ConnectionStrings:Redis` | unset | Falls back to an in-process cache |

## Emulator caveats

- **The Cosmos emulator must run in HTTPS mode.** The .NET SDK does not support its HTTP mode. Its
  certificate is self-signed, which the app trusts only when
  `DistSear:Azure:AllowUntrustedCertificate` is explicitly set — development only, never a deployment.
- **The vNext emulator is preview.** Request Units are not implemented and parallel cross-partition
  query is unimplemented. Neither matters here: every query path is scoped to a single partition key.
- **The emulator takes ~30 seconds to become ready.** The compose file gates the app containers on
  its health probe, so nodes do not race the database into existence.

## Project layout

```
src/DistSear.Abstractions      contracts and storage interfaces
src/DistSear.Analysis          tokenizer and filter chain
src/DistSear.Index             postings, segments, BM25, queries, facets, highlighting
src/DistSear.Cluster           routing, allocation, coordination, in-memory stores
src/DistSear.Storage.Azure     Cosmos and Blob implementations
src/DistSear.Node              shard runtime and change-feed indexer
src/DistSear.Coordinator       fan-out, merge, auth, caching, public API
src/DistSear.Client            typed client SDK
tools/DistSear.Seeder          sample corpus loader
deploy/                        Dockerfile, compose, Bicep
docs/                          architecture, data model, API, operations, ADRs
```

## Working on the engine

The index is the interesting part, and it is testable in isolation — `DistSear.Index.Tests` needs no
cluster at all. Two tests are worth knowing about before changing anything in the query path:

- `PruningEquivalenceTests` runs the same query with WAND pruning on and off over randomised corpora
  and asserts the results are identical. Pruning may change work done, never output.
- `LevenshteinAutomatonTests` verifies the fuzzy automaton exhaustively against a reference
  implementation over every string up to length three.

If either starts failing, the optimisation is wrong, not the test.
