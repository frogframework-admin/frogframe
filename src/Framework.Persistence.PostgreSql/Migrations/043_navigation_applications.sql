-- FrogFrame: master navigation applications (HRM, Finance, Accounting, IT, etc.).
CREATE TABLE IF NOT EXISTS frogframe_navigation_applications (
    id            text        PRIMARY KEY,
    tenant_id     text        NOT NULL,
    code          text        NOT NULL,
    name          text        NOT NULL,
    description   text,
    icon          text        NOT NULL DEFAULT 'app-window',
    sort_order    integer     NOT NULL DEFAULT 100,
    allowed_roles text[]      NOT NULL DEFAULT '{}',
    is_system     boolean     NOT NULL DEFAULT false,
    is_active     boolean     NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_nav_applications_tenant ON frogframe_navigation_applications (tenant_id, is_active, sort_order);
CREATE UNIQUE INDEX IF NOT EXISTS ux_nav_applications_tenant_code ON frogframe_navigation_applications (tenant_id, UPPER(code));
