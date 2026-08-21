# Architecture Decision Records

Each ADR records one decision, the alternatives that were weighed, and the consequences that
follow from it. They are immutable: when a decision changes, add a new ADR that supersedes the old
one rather than editing history.

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](0001-custom-inverted-index.md) | Build the inverted index rather than embed Lucene.NET | Accepted |
| [0002](0002-cosmos-as-source-of-truth.md) | Cosmos DB is the source of truth; the change feed drives indexing | Accepted |
| [0003](0003-container-apps-hosting.md) | Host on Azure Container Apps rather than AKS | Accepted |
| [0004](0004-blob-lease-leader-election.md) | Elect the coordinator with an Azure Blob lease | Accepted |
| [0005](0005-sharding-and-routing.md) | Fixed shard count, hash routing, rendezvous placement | Accepted |
| [0006](0006-query-then-fetch.md) | Two-phase fan-out, per-shard IDF by default, approximate facets | Accepted |
