-- Migration 045: tenant registry (status and settings per tenant).
-- Data of all tenants stays in the same tables, separated by tenant_id; this table only describes the tenants.

CREATE TABLE IF NOT EXISTS frogframe_tenants (
    id               TEXT        PRIMARY KEY,
    name             TEXT        NOT NULL,
    status           TEXT        NOT NULL DEFAULT 'Active' CHECK (status IN ('Active', 'Suspended')),
    suspended_reason TEXT,
    suspended_at     TIMESTAMPTZ,
    settings         JSONB       NOT NULL DEFAULT '{}'::jsonb,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by       TEXT
);

-- Existing installations: register every tenant that already has user accounts, as active.
INSERT INTO frogframe_tenants (id, name)
SELECT DISTINCT tenant_id, tenant_id FROM frogframe_users WHERE tenant_id <> ''
ON CONFLICT (id) DO NOTHING;
