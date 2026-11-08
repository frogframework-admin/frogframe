-- FrogFrame: master navigasi menu sidebar.
CREATE TABLE frogframe_navigation_menus (
    id            text        PRIMARY KEY,
    tenant_id     text        NOT NULL,
    title         text        NOT NULL,
    href          text        NOT NULL,
    icon          text        NOT NULL DEFAULT 'file-text',
    group_name    text        NOT NULL DEFAULT 'Umum',
    sort_order    integer     NOT NULL DEFAULT 100,
    allowed_roles text[]      NOT NULL DEFAULT '{}',
    is_system     boolean     NOT NULL DEFAULT false,
    is_active     boolean     NOT NULL DEFAULT true,
    badge_key     text,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
CREATE INDEX ix_nav_menus_tenant ON frogframe_navigation_menus (tenant_id, is_active, sort_order);

