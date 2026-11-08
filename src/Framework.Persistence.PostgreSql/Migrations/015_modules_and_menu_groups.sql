-- FrogFrame: master navigation modules and menu groups.
CREATE TABLE IF NOT EXISTS frogframe_navigation_modules (
    id            text        PRIMARY KEY,
    tenant_id     text        NOT NULL,
    name          text        NOT NULL,
    description   text,
    icon          text        NOT NULL DEFAULT 'layout-grid',
    sort_order    integer     NOT NULL DEFAULT 100,
    allowed_roles text[]      NOT NULL DEFAULT '{}',
    is_system     boolean     NOT NULL DEFAULT false,
    is_active     boolean     NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_nav_modules_tenant ON frogframe_navigation_modules (tenant_id, is_active, sort_order);

CREATE TABLE IF NOT EXISTS frogframe_navigation_menu_groups (
    id            text        PRIMARY KEY,
    tenant_id     text        NOT NULL,
    module_id     text        NOT NULL,
    name          text        NOT NULL,
    icon          text        NOT NULL DEFAULT 'folder',
    sort_order    integer     NOT NULL DEFAULT 100,
    is_system     boolean     NOT NULL DEFAULT false,
    is_active     boolean     NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_nav_menu_groups_tenant ON frogframe_navigation_menu_groups (tenant_id, module_id, sort_order);

ALTER TABLE frogframe_navigation_menus
    ADD COLUMN IF NOT EXISTS group_id text;

