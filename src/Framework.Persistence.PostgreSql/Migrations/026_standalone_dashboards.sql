-- FrogFrame: Standalone multi-dashboard support (shared admin dashboards & personal user dashboards)

CREATE TABLE frogframe_dashboards_v2 (
    tenant_id   text        NOT NULL,
    id          text        NOT NULL,
    name        text        NOT NULL,
    description text,
    owner_id    text        NOT NULL,
    is_shared   boolean     NOT NULL DEFAULT false,
    is_default  boolean     NOT NULL DEFAULT false,
    roles       jsonb       NOT NULL DEFAULT '[]'::jsonb,
    layout      jsonb       NOT NULL DEFAULT '{"items":[]}'::jsonb,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, id)
);

-- Migrate existing single-tenant dashboard layouts into a default shared dashboard
INSERT INTO frogframe_dashboards_v2 (tenant_id, id, name, description, owner_id, is_shared, is_default, roles, layout, created_at, updated_at)
SELECT
    tenant_id,
    'd_default',
    'Main Dashboard',
    'Default shared tenant dashboard',
    'admin',
    true,
    true,
    '[]'::jsonb,
    layout,
    updated_at,
    updated_at
FROM frogframe_dashboards;

DROP TABLE frogframe_dashboards;

ALTER TABLE frogframe_dashboards_v2 RENAME TO frogframe_dashboards;

CREATE INDEX idx_frogframe_dashboards_lookup ON frogframe_dashboards (tenant_id, is_shared, owner_id);

