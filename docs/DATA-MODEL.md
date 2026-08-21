# Data model

Where every piece of state lives, and why.

## Cosmos DB

| Container | Partition key | Holds | Notes |
| --- | --- | --- | --- |
| `documents` | `/shardKey` | The authoritative documents | Partitioned by the shard a document routes to |
| `cluster` | `/partition` | The whole allocation table, in one document | Single document so an update is atomic |
| `nodes` | `/nodeId` | One heartbeat document per live node | Per-item TTL, so liveness is self-cleaning |
| `apikeys` | `/keyHash` | Hashed service credentials | Only hashes; never the keys themselves |

### `documents`

```json
{
  "id": "doc-00042",
  "shardKey": "shard-2",
  "fields": { "title": "Fast distributed search", "price": 29.99, "year": 2021 },
  "acl": ["finance"],
  "deleted": false,
  "ttl": null,
  "_ts": 1755792000
}
```

`shardKey` is `shard-{murmur3_32(routingKey ?? id) % numberOfShards}`. Using the routing decision as
the partition key is what lets a node open a change-feed cursor scoped to exactly the shards it owns
— it never reads, or pays for, another shard's documents.

`ttl` is null for live documents and set only on tombstones. **The tombstone retention must exceed
the longest plausible gap between snapshots.** A replica restoring an old snapshot replays the feed
from that point; if the tombstone had already been reclaimed, the replica would never see the
deletion and would resurrect the document.

> **Numbers.** Cosmos has a single numeric type, an IEEE-754 double, so the integer `7` can come back
> as `7.0` — the distinction does not survive storage. The store normalises whole numbers back to
> `long` on read, otherwise a year would reach callers as `2021.0` depending purely on where it had
> been stored. Values are coerced by their declared field type anyway, so this only affects what
> stored fields look like to a caller.

### `cluster`

The entire topology — index mappings, aliases and every shard allocation — lives in one document,
updated under `IfMatchEtag`. One document because reallocating a shard touches several entries at
once, and a partial write would leave shards double-assigned or stranded.

### `nodes`

A node upserts its own document on every heartbeat with a TTL slightly longer than the heartbeat
interval. Liveness therefore needs no reaper: a node that stops heartbeating disappears on its own,
and the next reconciliation reallocates its shards.

## Blob Storage

```
snapshots/
  {index}/{shard}/manifest.json          ← written last; names the files below
  {index}/{shard}/{generation}/segment-00000.dss
  {index}/{shard}/{generation}/segment-00001.dss
  _checkpoints/{node}/{index}/{shard}.token

leases/
  shard-allocation.lease                 ← leased, never read
```

The manifest is published only after every file it references has been uploaded, so a recovering
replica can never observe a snapshot whose files are partly missing. A commit is also refused when a
newer generation already exists, and the write is conditional on the blob's ETag — an out-of-order
or retried upload cannot roll a replica backwards.

`manifest.json`:

```json
{
  "index": "catalog",
  "shardId": 2,
  "generation": 7,
  "files": ["segment-00000.dss"],
  "continuationToken": "<cosmos change feed token>",
  "documentCount": 12043,
  "createdAt": "2026-08-21T15:00:00Z"
}
```

`continuationToken` is the whole point: it is the change-feed position the snapshot corresponds to,
turning recovery into a download plus a short catch-up rather than a replay from the beginning.

The lease blob carries no data. Anything written to it would be unreadable by instances that do not
hold the lease, which is why the pattern requires it be used for nothing else.

## Segment file format

A sealed segment is immutable. Deletes and updates never rewrite it; they clear a bit in a tombstone
bitset, and space is reclaimed only by merging.

```
magic "DSSG" | version | maxDoc
external ids        : maxDoc × length-prefixed string
acls                : maxDoc × (count, strings)
stored fields       : maxDoc × (count, name, tagged value)
fields              : count × (name, docCount, sumFieldLength,
                               norms[maxDoc],
                               terms × (term, docFreq, totalTermFreq, hasPositions,
                                        skips × (docId, offset),
                                        postings bytes))
doc values          : count × (name, kind, values)
live docs           : maxDoc × bit
```

Postings are written **verbatim**. They are already variable-byte delta-gap encoded in memory, so
persisting them is a buffer copy rather than a re-encode, and restoring needs no decode at all.

### Postings encoding

Per posting, all variable-byte:

```
[docId gap] [term frequency] ( [positions byte length] [position gap]* )?
```

Doc ids are stored as gaps from the previous posting, which keeps them small and mostly single-byte:
1000 consecutive documents with one position each encode to exactly 4 bytes per posting, against 12
for a naive fixed-width layout. The position block is length-prefixed so a query that does not need
positions steps over it without decoding it, and a skip index every 128 postings lets `Advance` jump
blocks rather than decode them.

## In-memory index structures

| Structure | Purpose | Cost |
| --- | --- | --- |
| Term dictionary | term → postings, per field | Sorted array built on demand, enabling prefix seeks and fuzzy traversal |
| Norms | Field length per document | One `int` per document per field; BM25 length normalisation |
| Doc values | Column-stride values | Sorting, faceting, and range filters, which the inverted index cannot answer without scanning every term |
| Stored fields | The original values | Returned in hits and re-analyzed for highlighting |
| Live docs | Tombstone bitset | One bit per document |

Range filters over numerics scan the doc-values column rather than enumerating terms. That is
`O(maxDoc)` rather than `O(matching terms)` — a deliberate trade, since ranges are almost always
filters combined with a selective scoring clause that drives iteration, and the scan is a tight loop
over a contiguous array. A dedicated numeric structure (a BKD tree) would be the next step if range
queries ever became the dominant workload.
