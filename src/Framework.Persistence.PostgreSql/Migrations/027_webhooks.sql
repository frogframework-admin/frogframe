-- FrogFrame: Outbound Webhooks (Phase 3 Workstream A)
CREATE TABLE IF NOT EXISTS frogframe_webhooks (
    id text PRIMARY KEY,
    tenant_id text NOT NULL,
    name text NOT NULL,
    doctype text NOT NULL,
    events text[] NOT NULL,
    condition text,
    url text NOT NULL,
    headers jsonb NOT NULL DEFAULT '{}'::jsonb,
    secret_enc text NOT NULL,
    fields text[] NOT NULL DEFAULT '{}',
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    modified_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_webhooks_tenant_doctype ON frogframe_webhooks (tenant_id, doctype, is_active);

CREATE TABLE IF NOT EXISTS frogframe_webhook_deliveries (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    webhook_id text NOT NULL REFERENCES frogframe_webhooks(id) ON DELETE CASCADE,
    event_id uuid NOT NULL,
    event_type text NOT NULL,
    doc_name text NOT NULL,
    payload jsonb NOT NULL,
    status text NOT NULL DEFAULT 'pending', -- pending, sending, sent, failed, dead
    attempts int NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lease_until timestamptz,
    last_status_code int,
    last_error text,
    response_snippet text,
    duration_ms int,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    sent_at timestamptz,
    CONSTRAINT uq_webhook_deliveries_event UNIQUE (event_id, webhook_id)
);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_worker ON frogframe_webhook_deliveries (status, next_attempt_at);
CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_history ON frogframe_webhook_deliveries (tenant_id, webhook_id, created_at DESC);

