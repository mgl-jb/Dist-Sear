# ADR 0006 — Two-phase fan-out, per-shard IDF by default, approximate facets

**Status:** Accepted

## Context

A query must be answered from N shards, each of which knows only its own documents and its own
corpus statistics.

## Decision

Execute `query_then_fetch`:

1. **Query phase** — ask every searchable shard for its top `from + size`, returning only
   `(id, score, sortValues)`. Merge into a global top-k.
2. **Fetch phase** — request stored fields and highlights only from the shards that own the winning
   documents.

Full documents are never shipped from shards whose hits lost the merge.

## Consequences and the trade-offs made explicit

### BM25 IDF is per-shard by default

Each shard computes inverse document frequency from its own document frequencies. Two identical
documents on differently-composed shards can therefore receive different scores. This is
near-invisible on large, evenly-distributed shards and pronounced on small or skewed ones.

`SearchType.DfsQueryThenFetch` adds a pre-pass that sums document frequencies across shards into a
global `CollectionStatistics` before scoring. It is exact, and costs one extra round trip. Both are
implemented; the default favours latency, and the exact mode is one request parameter away.

### Terms facets are approximate

A shard reports only its own top buckets, so a term ranked just below every shard's cutoff is
undercounted. Rather than hide this, shards over-request `size * 1.5 + 10` buckets, and the response
reports `DocCountErrorUpperBound` and `SumOtherDocCount` so a caller can tell exact counts from
estimated ones.

### Deep paging uses a cursor

Offset paging costs `O(from + size)` per shard, because every shard must produce `from + size`
candidates for the merge to be correct. Past a few thousand documents this collapses. `SearchAfter`
takes the previous page's sort values and turns paging into a bounded range scan.

### Partial results beat total failure

A shard that misses its deadline is reported in `ShardStatistics.Failed` and its hits omitted; the
query still returns. A search box that returns 90% of results is more useful than one that returns
an error, provided the shortfall is visible to the caller — which is why the statistics are part of
every response rather than a log line.
