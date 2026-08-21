# API

Everything below is served by the coordinator, the only externally reachable component. Index nodes
expose their own endpoints under `/_internal/` on internal ingress; those are not a public contract.

## Authentication

Two schemes, either of which yields the caller's groups:

```http
Authorization: Bearer <Entra ID token>
X-Api-Key: <service key>
```

Groups drive document-level filtering. **An unauthenticated caller is treated as anonymous and sees
only unrestricted documents** — never as an administrator.

## Create an index

```http
PUT /indexes/catalog
Content-Type: application/json

{
  "fields": [
    { "name": "title", "type": "Text", "analyzer": "english", "boost": 3.0 },
    { "name": "body", "type": "Text", "analyzer": "english" },
    { "name": "category", "type": "Keyword", "docValues": true },
    { "name": "price", "type": "Double", "docValues": true },
    { "name": "year", "type": "Long", "docValues": true }
  ],
  "numberOfShards": 4,
  "numberOfReplicas": 2,
  "defaultField": "title"
}
```

`numberOfShards` **cannot be changed later**: documents route by `hash(id) % shards`, so altering it
would invalidate every existing routing decision. Growing an index means reindexing into a new one
and swapping an alias.

Field options: `analyzer` and `searchAnalyzer` (`standard`, `simple`, `english`, `keyword`,
`suggest`), `stored`, `docValues` (required for sorting, faceting and range filters), `positions`
(required for phrase queries), `boost`, `suggest`.

## Search

```http
POST /indexes/catalog/_search

{
  "query": "title:(fast AND search) -category:draft \"exact phrase\"~2",
  "filter": "price:[10 TO 50]",
  "from": 0,
  "size": 10,
  "sort": [{ "field": "price", "descending": false }],
  "facets": [
    { "name": "by_category", "field": "category", "size": 10 },
    { "name": "by_price", "field": "price", "kind": "Range",
      "ranges": [{ "key": "cheap", "to": 50 }, { "key": "dear", "from": 50 }] }
  ],
  "highlight": { "fields": ["title", "body"], "fragmentSize": 160, "maxFragments": 3 },
  "searchType": "QueryThenFetch",
  "timeout": "00:00:05"
}
```

Response:

```json
{
  "hits": [
    {
      "id": "doc-00042",
      "score": 4.213,
      "shardId": 2,
      "fields": { "title": "Fast distributed search", "price": 29.99 },
      "highlights": { "title": ["<em>Fast</em> distributed <em>search</em>"] },
      "sortValues": [29.99, "doc-00042"]
    }
  ],
  "totalHits": 137,
  "totalIsLowerBound": false,
  "maxScore": 4.213,
  "facets": {
    "by_category": {
      "buckets": [{ "key": "books", "count": 89 }],
      "docCountErrorUpperBound": 0,
      "sumOtherDocCount": 12
    }
  },
  "shards": { "total": 4, "successful": 4, "skipped": 0, "failed": 0, "failures": [] },
  "timedOut": false,
  "tookMilliseconds": 18
}
```

### Reading the response honestly

- **`shards.failed > 0`** means the results are incomplete. The query still answered, and
  `failures` says which shards could not be reached and why.
- **`totalIsLowerBound`** means the count is a floor, not an exact figure — either because a shard
  failed, or because top-k pruning began after the exact-count threshold was crossed.
- **`docCountErrorUpperBound`** on a terms facet is how far any returned count could be short once
  shards are merged. Zero means exact. Range facets are always exact, because every shard counts the
  same caller-defined buckets.

### Query syntax

| Form | Meaning |
| --- | --- |
| `search` | The term, against the default field |
| `title:search` | Against a named field |
| `fast AND search`, `fast OR search` | Explicit boolean; bare terms default to OR |
| `+fast -draft` | Required / prohibited |
| `"distributed search"` | Phrase; terms adjacent and in order |
| `"distributed search"~2` | Phrase with slop |
| `sea*`, `k?tchen` | Prefix and wildcard |
| `serch~1` | Fuzzy, up to 2 edits |
| `price:[10 TO 50]`, `year:{2019 TO 2023}` | Inclusive / exclusive range |
| `price:[10 TO *]` | Half-open range |
| `search^3` | Boost |

A leading `+` or `-` is an operator; elsewhere they are ordinary characters, so `e-mail` and
`covid-19` stay single terms.

### Scoring modes

`searchType` is `QueryThenFetch` (default) or `DfsQueryThenFetch`. The default scores each shard from
its own document frequencies, which is fast but means an identical document can score differently
depending on which shard it landed on. `DfsQueryThenFetch` adds a pre-pass that sums frequencies
across shards for exact global scoring, at the cost of one extra round trip.

### Paging

`from` + `size` works to a bounded depth. Beyond it, use `searchAfter`, passing the previous page's
last hit's `sortValues`:

```json
{ "size": 20, "sort": [{ "field": "year" }], "searchAfter": [2021, "doc-00042"] }
```

Offset paging costs `from + size` on **every** shard, because each must produce that many candidates
for the merge to be correct — so it multiplies across the cluster and is capped deliberately. The
cursor turns paging into a bounded range scan. `sortValues` always carries the document id as a
trailing key, which is what keeps paging correct across ties.

## Index documents

```http
POST /indexes/catalog/_bulk

[
  {
    "id": "doc-1",
    "fields": { "title": "Fast distributed search", "price": 29.99 },
    "acl": ["finance"],
    "routingKey": "tenant-7"
  }
]
```

`acl` empty means unrestricted. `routingKey` co-locates related documents on one shard so queries
scoped to that key avoid a fan-out.

Returns `{ "indexed": 1, "deleted": 0, "errors": [] }`. Unmapped fields are rejected with an error
rather than silently dropped.

**Writes are not immediately searchable.** They are durable once Cosmos accepts them, and become
searchable when nodes pick them up from the change feed — typically within a second.

## Delete documents

```http
POST /indexes/catalog/_delete
["doc-1", "doc-2"]
```

Deletes are tombstones that travel the change feed, then expire by TTL.

## Aliases

```http
POST /_aliases
{ "alias": "catalog", "index": "catalog_v2" }
```

Atomic, which is what makes zero-downtime reindexing possible: build `catalog_v2` alongside
`catalog_v1`, then swap. No query observes a half-built index.

## Cluster state

```http
GET /_cluster/state
```

Returns indexes, aliases, live nodes, and every shard's copies with their state.

## Health

| Endpoint | Meaning |
| --- | --- |
| `GET /health/live` | The process is running |
| `GET /health/ready` | Every shard has a copy that can serve |

Readiness fails while any shard is unreachable, so traffic is withheld rather than answered with
silently partial results. On a node, readiness fails while a shard is still recovering — alive, but
not yet fit to answer.

## Errors

| Status | Cause |
| --- | --- |
| 400 | Malformed query, unknown field, or paging past the offset limit |
| 401 | Missing or invalid credentials |
| 404 | Unknown index or alias |
| 429 | Rate limited; `Retry-After` says when to return |
| 503 | Not ready — shards unavailable or still recovering |
