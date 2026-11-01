CREATE TABLE IF NOT EXISTS docframe_scheduled_job_runs (
    id              bigserial       PRIMARY KEY,
    job_name        text            NOT NULL,
    tenant_id       text            NOT NULL,
    started_at      timestamptz     NOT NULL,
    completed_at    timestamptz,
    duration_ms     integer,
    status          text            NOT NULL,
    error           text
);

CREATE INDEX IF NOT EXISTS ix_scheduled_jobs_lookup
    ON docframe_scheduled_job_runs (job_name, tenant_id, started_at DESC);

CREATE INDEX IF NOT EXISTS ix_scheduled_jobs_status
    ON docframe_scheduled_job_runs (status, started_at DESC);

