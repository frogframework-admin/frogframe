# Write Performance

Saving a document is one database transaction (document, audit entry, outbox event), plus the reads that validation, "fetch from" fields and hooks need before it. This guide covers how the write path is tuned, how to measure it, and how to size the connection pool.

## What a save does on PostgreSQL

For one Purchase Request with one item row:

| Step | Round trips |
|---|---|
| Number from the naming series (`frogframe_series`, outside the transaction) | 1 |
| Reads of master documents (requester, company, approver, …) | one per distinct document; repeats within the same save are served from the save's read cache |
| Queries made by hooks (for example approvals on leave) | as the hook needs |
| Transaction: DocType lock + published schema check | 1 (batched) |
| Transaction: document insert or update | 1 per document |
| Transaction: audit entries + outbox events | 1 (batched) |
| Commit | 1 |

**Read cache per save** (`DocumentReadScope`):
- Inside an insert, update, delete, action or single-document save, the first read of a document is reused for later reads of the same document, and every caller gets its own copy.
- A save started inside another one (a hook saving another document) joins the outer cache.
- A document written by the save is never served from the cache afterwards.
- Bulk operations start a new cache for each document.
- Outside a save nothing is cached.

## Connection pool

Without a `Maximum Pool Size` in the connection string, FrogFrame uses a pool of **30 connections per application instance** for each PostgreSQL data source (Npgsql's own default is 100). A save holds a connection only briefly, so under a burst a small pool makes requests queue for a few milliseconds instead of:

- overloading the database server with more concurrent work than it has CPU for; and
- exceeding PostgreSQL's `max_connections` (100 by default), which makes saves fail with "sorry, too many clients already". Inside hooks this surfaced as "An error occurred while executing document hooks".

Sizing:

- Keep the sum of all pools below `max_connections`, minus a margin for administrators and backups: application instances × (main pool + read-only dashboard pool) + migrate step + backups.
- A good pool size is a small multiple of the database server's CPU cores (for example 2–4 × cores). More connections do not make PostgreSQL faster.
- Set it per connection string, for example `...;Maximum Pool Size=50`. An explicit value is always kept.

## Measuring

**Write benchmark.** The real pipeline (ERP metadata, hooks, naming, audit, outbox, with the outbox processor running), without HTTP:

```bash
FROGFRAME_TEST_PG="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=..." \
  dotnet run --project tests/Framework.Tests -- --bench-write 100 10    # 100 simultaneous users x 10 Purchase Requests
```

It creates and drops its own database, and prints p50/p95/p99, maximum, throughput and errors. All users start at the same moment, which is a harsher burst than the k6 stages.

**End to end.** The k6 test in `tests/load` (HTTP, authentication, JSON). Its Purchase Request payload matches the current metadata and needs the demo masters (`deploy/sql/seed-phase-1..8.sql`).

## Results

Write benchmark on a 2-CPU machine, application and PostgreSQL 16 on the same host, 10 Purchase Requests per user, all users starting at once. Absolute numbers depend on the hardware; compare before and after.

| Users | Before: p50 / p95 / p99 | Before: inserts/s, errors | After: p50 / p95 / p99 | After: inserts/s, errors |
|---|---|---|---|---|
| 1 (sequential) | 7.7 / 12.9 / 16.7 ms | 120/s, 0 | 5.5 / 9.3 / 15.3 ms | 161/s, 0 |
| 20 | 52.7 / 159 / 224 ms | 275/s, 0 | 39.4 / 121 / 142 ms | 367/s, 0 |
| 50 | 139 / 366 / 583 ms (max 1,122) | 265/s, 0 | 123 / 167 / 189 ms (max 238) | 386/s, 0 |
| 100 | 750 / 1,299 / 1,482 ms | 131/s, **14 errors** | 232 / 292 / 310 ms | 409/s, 0 |

The "before" run reproduces the k6 measurement in `tests/load/README.md`: median about 53 ms, and p95 over a second during the 100-user spike. Under a burst, latency is mostly queueing: 100 simultaneous saves at about 400 saves per second take about a quarter of a second to clear.

What changed:

1. Bounded connection pool (above). This is the largest effect: no more errors, and throughput doubles under the spike.
2. Read cache per save. Database reads per Purchase Request went from 7 to 3.
3. Fewer round trips in the transaction: the lock and the schema check are batched, the second schema check is gone, and audit and outbox rows are batched.
