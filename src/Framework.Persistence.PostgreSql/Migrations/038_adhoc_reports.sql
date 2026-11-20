-- Migration 038: Saved ad-hoc reports (report builder)
CREATE TABLE IF NOT EXISTS frogframe_adhoc_reports (
    id           UUID        PRIMARY KEY,
    tenant_id    TEXT        NOT NULL,
    name         TEXT        NOT NULL,
    description  TEXT,
    owner        TEXT        NOT NULL,
    shared_roles TEXT[]      NOT NULL DEFAULT '{}',
    definition   JSONB       NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL,
    modified_at  TIMESTAMPTZ NOT NULL,
    version      INT         NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS ix_frogframe_adhoc_reports_tenant
    ON frogframe_adhoc_reports (tenant_id, LOWER(name));
