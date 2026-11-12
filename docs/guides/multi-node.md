# Running on Several Nodes

FrogFrame can run as several application instances ("nodes") behind a load balancer, all using **one PostgreSQL
database**. Documents, files (stored in PostgreSQL), jobs and data-protection keys are already shared through the
database. This page covers what each node keeps in memory and how the nodes keep it in line.

## 1. Requirements

| Requirement | Why |
|---|---|
| **Sticky sessions** (session affinity) on the load balancer for the UI | Blazor Server keeps each open browser tab as a live SignalR connection (circuit) on one node. Use cookie-based affinity; WebSockets must be allowed. The REST API (`/api`) needs no affinity. |
| **PostgreSQL mode** (`AddFrogFrameworkPostgreSql`) | Nodes signal each other with `LISTEN/NOTIFY`. In-memory mode is single-node only. |
| **Direct (session) connections** for the application | Each node keeps **one** connection open for `LISTEN frogframe_cluster`. LISTEN does not work through PgBouncer in *transaction* pooling mode; use session pooling or connect directly. |
| `Cluster:Nodes` = number of nodes | Divides the API rate limit between the nodes (see §3). |

Already safe with several nodes (nothing to configure): schema migrations (advisory lock), scheduled jobs and
server scripts (advisory lock per job), outbox and webhook delivery (`FOR UPDATE SKIP LOCKED` + leases), file storage
(PostgreSQL), data-protection keys (PostgreSQL).

## 2. What is synchronized

Nodes send short signals over PostgreSQL `NOTIFY` (`IClusterBus`, channel `frogframe_cluster`). No Redis or message
broker is needed.

| State in memory | Before | Now |
|---|---|---|
| **Active metadata** (DocTypes, workflows, customizations) | Other nodes saw a publish only after a restart | The publishing node signals; the others reload from the database within about a second (`MetadataRegistry.ReloadFrom`) |
| **"Being edited by" banner** (document presence) | Only users on the same node | Heartbeats and leaves go to every node; users on any node see each other. Sessions of a stopped node disappear after 25 s |
| **Role / module cache** (`RoleCatalog`) | Expired after 30 s (also on a single node) | Cleared at once when roles or modules change, on every node |
| **Compiled server scripts** | — | Already correct: every run loads the script from the database and recompiles when its version changed |
| **API rate limit** | Counted per node (N nodes → N × the limit) | `Cluster:Nodes` divides the limit; with an even load balancer the total stays close to `Api:RateLimitPerMinute` |

Signals are hints, not a durable queue. When a node loses its database connection it reconnects with back-off and
then reloads metadata and clears role caches, because signals sent in the meantime are lost.

Not pushed live between nodes (they refresh on the next page load): the sidebar menu after a menu is edited on another
node. Caches with a time limit are listed in the patches that add them (Client Script list, translations); they use
the same bus.

## 3. Configuration

```json
{
  "Cluster": { "Nodes": 3 },
  "Api": { "RateLimitPerMinute": 300 }
}
```

Each node logs `Cluster signals active (node …)` and `Multi-node synchronization active` at startup.

## 4. Extending

Code that caches shared state in memory publishes a signal after changing it and subscribes to apply signals of
other nodes:

```csharp
public sealed class MyCache(IClusterBus bus)
{
    public void Changed(string tenantId) { ClearLocal(tenantId); bus.Publish("my-cache", tenantId); }
    // at startup: bus.Subscribe("my-cache", ClearLocal); bus.Reconnected += ClearAll;
}
```

Keep payloads small (PostgreSQL limits a notification to 8000 bytes): send ids, not data, and let the receiver read
from the database.
