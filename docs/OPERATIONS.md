# Operations

## Deploying

```bash
az group create --name distsear-rg --location westeurope

az deployment group create \
  --resource-group distsear-rg \
  --template-file deploy/bicep/main.bicep \
  --parameters namePrefix=distsear imageTag=v1
```

Then push images to the registry the deployment created:

```bash
ACR=$(az deployment group show -g distsear-rg -n main --query properties.outputs.registryLoginServer.value -o tsv)
az acr login --name "${ACR%%.*}"

for role in Coordinator Node; do
  docker build -f deploy/Dockerfile --build-arg PROJECT=DistSear.$role \
    -t "$ACR/distsear-$(echo $role | tr A-Z a-z):v1" .
  docker push "$ACR/distsear-$(echo $role | tr A-Z a-z):v1"
done
```

No keys or connection strings are configured anywhere. Both roles authenticate with a user-assigned
managed identity; Cosmos has local auth disabled and the storage account has shared-key access
disabled, so key-based access is not merely unused but impossible.

## Sizing

**Shard count is the decision you cannot undo.** Documents route by `hash(id) % shards`, so changing
it invalidates every routing decision and requires a reindex behind an alias.

- Too few shards and one shard exceeds a single replica's memory. Container Apps cannot scale
  vertically, so the ceiling is the container's memory limit.
- Too many and every query pays fan-out overhead against near-empty shards.

A reasonable starting point is enough shards that each holds a few million documents, rounded so
shards divide evenly across the node count.

Replicas buy availability and read throughput, not capacity: every replica holds the whole shard and
independently consumes the change feed, so replication multiplies indexing RU cost.

## Scaling

| Component | How | Constraint |
| --- | --- | --- |
| Coordinator | Autoscales on HTTP concurrency, 1–10 | Stateless; scale freely |
| Index nodes | Fixed at `nodeReplicas` | **Never zero.** A node with no replicas owns no shards |
| Cosmos | Serverless | Watch RU consumption as replica count rises |

Adding a node makes the leader reallocate shards onto it by rendezvous hashing, which moves only the
shards that belong there and leaves every other copy serving throughout.

## Reindexing without downtime

```bash
# 1. Create the new index with the new shard count or mapping
curl -X PUT $URL/indexes/catalog_v2 -d @mapping-v2.json

# 2. Backfill it, then wait for the nodes to catch up
dotnet run --project tools/DistSear.Seeder -- --index=catalog_v2

# 3. Swap atomically
curl -X POST $URL/_aliases -d '{"alias":"catalog","index":"catalog_v2"}'
```

Callers use the alias throughout and never observe a half-built index.

## Monitoring

Traces are emitted per fan-out, with a span per shard, so a slow query is attributed rather than
inferred. The spans to watch are `search.coordinator`, `search.query-phase`, `search.fetch-phase`
and `search.statistics`.

| Signal | Watch for | Likely meaning |
| --- | --- | --- |
| `shards.failed > 0` | Any sustained non-zero | A node is down or a shard is unallocated |
| Query-phase p99 ≫ median | Widening gap | One slow replica setting the tail |
| Shards stuck `Recovering` | Minutes, not seconds | Snapshots too old or missing, forcing a long replay |
| Cosmos RU throttling (429) | Any | Replica count or indexing rate outgrowing throughput |
| Readiness flapping | Repeated 503 | Shards losing and regaining copies |

## Runbook

### Queries return partial results

`shards.failed` names the shards. Check `GET /_cluster/state`: a shard with no copy in `Started` has
no serving replica.

- Copies exist but are `Recovering` → wait; they will start once caught up.
- No copies at all → the leader has not reallocated. Check that a coordinator holds the lease and
  that live nodes are heartbeating.
- Copies `Started` but still failing → the node is unreachable rather than unhealthy. Check its
  internal ingress and readiness probe.

### A shard will not leave `Recovering`

It is replaying the change feed. If it is not progressing:

1. Confirm the node can reach Cosmos, and check for RU throttling.
2. Check whether a snapshot exists at `snapshots/{index}/{shard}/manifest.json`. Without one, the
   shard replays its partition from the beginning, which on a large shard is slow but not stuck.
3. Confirm the snapshot owner is actually snapshotting; if snapshots stopped long ago, every
   recovery pays for the whole gap.

### Deleted documents reappear

Almost certainly tombstone retention shorter than the interval between snapshots. A replica
restoring an old snapshot replays from that point, and if the tombstone had already expired it never
sees the deletion. Raise `DistSear:Azure:TombstoneRetention` above the snapshot interval with room to
spare, then reindex the affected documents.

### Scores differ for identical documents

Expected under the default `QueryThenFetch`: each shard scores from its own document frequencies. Use
`searchType=DfsQueryThenFetch` for exact global scoring, at the cost of one extra round trip. The
effect is largest when shards are small or unevenly composed.

### Facet counts look slightly wrong

Check `docCountErrorUpperBound`. Terms facets are approximate across shards because each reports only
its own top buckets. Raise the facet's `shardSize` to narrow the error. Range facets are always
exact.

### No leader is elected

Every coordinator reports not-leader. Leadership requires a Blob lease, so check Blob Storage
availability and that the identity holds Storage Blob Data Contributor — leasing is a write
operation even though nothing is written to the blob's body. The cluster keeps serving queries
meanwhile; it just stops reallocating shards.

### Rolling back

Container Apps keeps revisions. Roll traffic back to the previous one; nothing in the index format
changed, and the segment format is versioned and rejected outright if unrecognised rather than
misread.

## Backup and restore

Cosmos is the source of truth, so **Cosmos backup is the backup**. Blob snapshots are a recovery
accelerant, not a backup: deleting them costs replay time, not data. Restoring the Cosmos container
and letting nodes rebuild from the change feed reconstructs the entire index.
