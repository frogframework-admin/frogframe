-- Migration 041: background tasks (long-running user operations: imports, bulk actions).
CREATE TABLE IF NOT EXISTS frogframe_background_tasks (
    id               UUID        PRIMARY KEY,
    tenant_id        TEXT        NOT NULL,
    user_id          TEXT        NOT NULL,
    kind             TEXT        NOT NULL,
    title            TEXT        NOT NULL,
    payload          JSONB       NOT NULL DEFAULT '{}'::jsonb,
    input            BYTEA,
    status           TEXT        NOT NULL,
    progress         INTEGER     NOT NULL DEFAULT 0,
    progress_message TEXT,
    result           TEXT,
    output           BYTEA,
    output_name      TEXT,
    error            TEXT,
    cancel_requested BOOLEAN     NOT NULL DEFAULT FALSE,
    created_at       TIMESTAMPTZ NOT NULL,
    started_at       TIMESTAMPTZ,
    finished_at      TIMESTAMPTZ,
    locked_until     TIMESTAMPTZ,
    locked_by        TEXT
);

-- Workers claim the oldest queued task.
CREATE INDEX IF NOT EXISTS ix_frogframe_background_tasks_queue
    ON frogframe_background_tasks (created_at) WHERE status = 'Queued';
-- Leases of running tasks (interrupted workers).
CREATE INDEX IF NOT EXISTS ix_frogframe_background_tasks_running
    ON frogframe_background_tasks (locked_until) WHERE status = 'Running';
-- A user's tasks, newest first.
CREATE INDEX IF NOT EXISTS ix_frogframe_background_tasks_user
    ON frogframe_background_tasks (tenant_id, LOWER(user_id), created_at DESC);
-- Retention of finished tasks.
CREATE INDEX IF NOT EXISTS ix_frogframe_background_tasks_finished
    ON frogframe_background_tasks (finished_at) WHERE finished_at IS NOT NULL;
