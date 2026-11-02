-- DocFrame: Tabel Error Log untuk menangkap unhandled exception di HTTP request, API, dan background jobs.
CREATE TABLE IF NOT EXISTS docframe_error_logs (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    timestamp timestamptz NOT NULL,
    method text NOT NULL,
    path text NOT NULL,
    user_id text,
    error_type text NOT NULL,
    message text NOT NULL,
    stack_trace text,
    seen boolean NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_error_logs_tenant ON docframe_error_logs (tenant_id, timestamp DESC);

