# Operational Troubleshooting Runbook

This runbook provides actionable procedures and diagnostic commands for site reliability engineers (SREs), system administrators, and database administrators operating FrogFramework in production environments.

---

## 1. PostgreSQL Deadlocks & Lock Contention

FrogFramework combines optimistic document concurrency with relational transactions in PostgreSQL. While optimistic locking (`Doc.Version` checking) prevents concurrent edit collisions at the application layer, multi-table transactions (such as general ledger posting, child table batch updates, and outbox event dispatching) can encounter database-level lock contention or deadlocks under high concurrency.

### 1.1 Concurrency Layers & Symptoms

| Layer | Mechanism | Symptom | Action |
|---|---|---|---|
| **Application Layer** | Optimistic Versioning (`doc.Version`) | HTTP `409 Conflict` ("Document has been modified by another user") | Client retry / refresh document state. Not a database deadlock. |
| **Distributed Scheduler** | Advisory Locks (`PostgresDistributedLock`) | Background job or migration skips execution or waits | Inspect `pg_locks` for `advisory` lock type. |
| **Transaction / Row Lock** | PostgreSQL Row-Level Locks (`FOR UPDATE`) | Slow requests, transaction timeouts, `deadlock detected` (SQLSTATE `40P01`) | Identify blocking PID, examine locking queries, kill stuck sessions if necessary. |

### 1.2 Diagnostic Queries

#### Check for Currently Active Deadlocks and Blocked Queries
Run this query to inspect blocked transactions and the PIDs holding the locks:

```sql
SELECT
    blocked_locks.pid     AS blocked_pid,
    blocked_activity.usename  AS blocked_user,
    blocking_locks.pid    AS blocking_pid,
    blocking_activity.usename AS blocking_user,
    blocked_activity.query    AS blocked_statement,
    blocking_activity.query   AS blocking_statement,
    now() - blocked_activity.query_start AS blocked_duration
FROM  pg_catalog.pg_locks         blocked_locks
JOIN pg_catalog.pg_stat_activity blocked_activity ON blocked_activity.pid = blocked_locks.pid
JOIN pg_catalog.pg_locks         blocking_locks 
    ON blocking_locks.locktype = blocked_locks.locktype
    AND blocking_locks.database IS NOT DISTINCT FROM blocked_locks.database
    AND blocking_locks.relation IS NOT DISTINCT FROM blocked_locks.relation
    AND blocking_locks.page IS NOT DISTINCT FROM blocked_locks.page
    AND blocking_locks.tuple IS NOT DISTINCT FROM blocked_locks.tuple
    AND blocking_locks.virtualxid IS NOT DISTINCT FROM blocked_locks.virtualxid
    AND blocking_locks.transactionid IS NOT DISTINCT FROM blocked_locks.transactionid
    AND blocking_locks.classid IS NOT DISTINCT FROM blocked_locks.classid
    AND blocking_locks.objid IS NOT DISTINCT FROM blocked_locks.objid
    AND blocking_locks.objsubid IS NOT DISTINCT FROM blocked_locks.objsubid
    AND blocking_locks.pid != blocked_locks.pid
JOIN pg_catalog.pg_stat_activity blocking_activity ON blocking_activity.pid = blocking_locks.pid
WHERE NOT blocked_locks.granted;
```

#### Identify Long-Running Queries and Idle in Transaction Sessions
Sessions left in `idle in transaction` hold locks indefinitely until closed:

```sql
SELECT 
    pid, 
    usename, 
    application_name, 
    client_addr, 
    state, 
    now() - state_change AS state_age, 
    now() - xact_start   AS xact_age, 
    query
FROM pg_stat_activity
WHERE state IN ('idle in transaction', 'active')
  AND (now() - xact_start) > interval '15 seconds'
ORDER BY xact_age DESC;
```

#### Inspect Advisory Locks (Background Schedulers & Migrations)
```sql
SELECT 
    l.pid, 
    a.usename, 
    a.application_name, 
    l.locktype, 
    l.mode, 
    l.granted, 
    a.query, 
    now() - a.query_start AS duration
FROM pg_locks l
JOIN pg_stat_activity a ON l.pid = a.pid
WHERE l.locktype = 'advisory';
```

### 1.3 Resolution & Remediation

1. **Cancel the Non-Responsive Statement (Graceful)**:
   ```sql
   SELECT pg_cancel_backend(12345); -- Replace 12345 with blocked_pid or blocking_pid
   ```
2. **Terminate the Blocking Backend (Immediate)**:
   If the session is stuck in `idle in transaction` and holding locks:
   ```sql
   SELECT pg_terminate_backend(12345);
   ```
3. **Database Configuration Protection (Prevention)**:
   Ensure timeouts are set in `postgresql.conf` or user connection parameters to automatically prevent unyielding locks:
   ```ini
   # Terminate transactions idle with open locks after 30 seconds
   idle_in_transaction_session_timeout = 30000 
   # Abort statements running longer than 60 seconds (adjust for large reporting jobs)
   statement_timeout = 60000 
   # Abort statement if waiting for a lock exceeds 10 seconds
   lock_timeout = 10000 
   # Report deadlocks after 1 second
   deadlock_timeout = 1000
   ```

---

## 2. Outbox Retry Exhaustion & Dead-Letter Handling

FrogFramework uses an asynchronous transactional outbox (`frogframe_outbox`) to reliably publish event notifications, deliver webhooks, and send outgoing emails with at-least-once delivery semantics.

### 2.1 Outbox Lifecycle & State Transitions

```
[Document Saved in DB]
         │
         ▼
(INSERT into frogframe_outbox) ──► processed_at IS NULL, attempts = 0
         │
         ▼
[Outbox Worker Claims Batch]  ──► locked_until = now() + 2 minutes (FOR UPDATE SKIP LOCKED)
         │
    ┌────┴───────────────────────────┐
    ▼                                ▼
[Success]                        [Failure]
    │                                │
    ▼                                ▼
processed_at = now()             attempts = attempts + 1
locked_until = NULL              next_attempt_at = now() + 2^attempts * 10s
                                     │
                             (attempts >= 5?)
                             ┌───────┴───────┐
                             ▼               ▼
                          [No: Retry]     [Yes: Dead-Lettered]
                                          dead_lettered = true
                                          last_error = exception details
```

### 2.2 Diagnosing Outbox Failures

Inspect dead-lettered or repeatedly failing outbox records:

```sql
SELECT 
    id, 
    tenant_id, 
    type, 
    attempts, 
    next_attempt_at, 
    last_error, 
    created_at, 
    payload
FROM frogframe_outbox
WHERE dead_lettered = true OR attempts >= 3
ORDER BY created_at DESC 
LIMIT 50;
```

#### Common Root Causes

1. **Webhook Endpoint Down or Rejecting (4xx/5xx)**:
   - Target URL unreachable, SSL validation failure, or SSRF protection triggered (e.g. attempting to send to a private IP/loopback).
   - Check `last_error` for `HttpRequestException` or `SSRF validation failed`.
2. **SMTP Relay Failure**:
   - Authentication failure, recipient rejected, or rate-limited by upstream mail provider (e.g., SendGrid, AWS SES).
   - Check `last_error` for `SmtpCommandException`.
3. **Malformed Payload**:
   - Target schema changed or JSON serialization issue in background handler.

### 2.3 Reprocessing Dead-Lettered Messages

After resolving the external dependency (e.g. restoring webhook receiver or fixing SMTP credentials), re-queue messages for processing:

#### Requeue Specific Outbox Message:
```sql
UPDATE frogframe_outbox
SET 
    dead_lettered = false,
    attempts = 0,
    next_attempt_at = now(),
    locked_until = NULL,
    last_error = NULL
WHERE id = 'c4b8e23f-1111-4222-9333-888877776666';
```

#### Bulk Requeue by Event Type and Date Range:
```sql
UPDATE frogframe_outbox
SET 
    dead_lettered = false,
    attempts = 0,
    next_attempt_at = now(),
    locked_until = NULL
WHERE dead_lettered = true 
  AND type = 'email.outbox'
  AND created_at >= now() - interval '24 hours';
```

#### Safe Purge of Obsolete or Poison Messages:
```sql
-- Archive or permanently delete poisoned messages that cannot be delivered
DELETE FROM frogframe_outbox
WHERE dead_lettered = true
  AND created_at < now() - interval '30 days';
```

---

## 3. Orphan Data Cleanup & Storage Maintenance

In a high-throughput enterprise environment, abandoned uploads, transient background artifacts, and obsolete error logs must be pruned to avoid database bloating and storage cost overruns.

### 3.1 Orphaned File Pruning (`frogframe_files` & S3)

When users attach files in draft documents but abandon the form without saving, files remain in `frogframe_files` with `doctype IS NULL`.

#### Automated Background Worker
FrogFramework automatically runs `FileOrphanWorker` as a hosted background service:
- Default retention: `Files:OrphanHours = 24` (files older than 24 hours without an attached DocType are deleted).
- Execution frequency: `Files:PruneEveryHours = 6`.

#### Manual Diagnostic Query for Orphan Files:
```sql
SELECT 
    id, 
    tenant_id, 
    file_name, 
    size_bytes, 
    created_at, 
    uploaded_by
FROM frogframe_files
WHERE doctype IS NULL
  AND created_at < now() - interval '24 hours'
ORDER BY size_bytes DESC;
```

#### Manual Cleanup Query (PostgreSQL File Store):
```sql
DELETE FROM frogframe_files
WHERE id IN (
    SELECT id 
    FROM frogframe_files 
    WHERE doctype IS NULL 
      AND created_at < now() - interval '24 hours'
    LIMIT 1000
);
```

> [!NOTE]
> If `Storage:Provider = S3` (e.g. SeaweedFS or AWS S3), pruning rows via `files.PruneOrphansReturningAsync` automatically deletes the binary blobs from the S3 bucket using the stored S3 key path.

### 3.2 Outbox Table Retention Pruning

Messages that have been successfully delivered (`processed_at IS NOT NULL` and `NOT dead_lettered`) must be purged periodically:

```sql
-- Inspect backlog of completed outbox items
SELECT COUNT(*) AS processed_count, MIN(processed_at) AS oldest_processed
FROM frogframe_outbox
WHERE processed_at IS NOT NULL AND NOT dead_lettered;

-- Purge messages older than 7 days in batches to minimize WAL generation
DELETE FROM frogframe_outbox
WHERE id IN (
    SELECT id 
    FROM frogframe_outbox 
    WHERE processed_at < now() - interval '7 days' 
      AND NOT dead_lettered
    LIMIT 5000
);
```

### 3.3 Background Tasks & Job Artifacts Retention

Background tasks (`frogframe_background_tasks`) track import/export progress and store payload/output files.
- Retention policy: Automatically maintained up to 14 days.
- Manual cleanup for completed, cancelled, or failed jobs older than 14 days:
```sql
DELETE FROM frogframe_background_tasks
WHERE status IN ('Completed', 'Failed', 'Cancelled')
  AND completed_at < now() - interval '14 days';
```

### 3.4 Error Logs Pruning (`frogframe_error_logs`)

Centralized application and API unhandled exceptions are logged to `frogframe_error_logs`.
- Inspect top errors:
```sql
SELECT error_type, count(*), max(timestamp) AS last_seen
FROM frogframe_error_logs
WHERE timestamp > now() - interval '7 days'
GROUP BY error_type
ORDER BY count(*) DESC;
```
- Prune acknowledged or old logs:
```sql
DELETE FROM frogframe_error_logs
WHERE timestamp < now() - interval '30 days';
```

---

## 4. Emergency Health & Diagnostic Checklist

When system alerts trigger or the application becomes unresponsive, follow this triage order:

### Step 1: Health Probes Verification
```bash
# Liveness probe (should respond 200 OK immediately)
curl -I http://127.0.0.1:5000/health/live

# Readiness probe (verifies PostgreSQL connection & migrations)
curl -I http://127.0.0.1:5000/health/ready

# Unified health probe
curl -I http://127.0.0.1:5000/healthz
```

### Step 2: Database Connection Pool Exhaustion Check
Check if connections to PostgreSQL are saturated:
```sql
SELECT count(*), max_conn, count(*) * 100.0 / max_conn AS pct_used
FROM pg_stat_activity, (SELECT setting::int AS max_conn FROM pg_settings WHERE name = 'max_connections') s
GROUP BY max_conn;
```
If connection percentage exceeds 85%, check for connection leaks, unclosed web requests, or increase `Npgsql` pool size (`MaxPoolSize=100` in connection string) while tuning PostgreSQL `max_connections`.

### Step 3: Container Diagnostics
```bash
# Check Docker container resource usage
docker stats frogframe-app frogframe-postgres frogframe-seaweedfs

# Inspect recent unhandled application crashes
docker logs --tail 200 frogframe-app
```
