-- DocFrame: master roles pengguna.
CREATE TABLE docframe_roles (
    id          text        PRIMARY KEY,
    tenant_id   text        NOT NULL,
    name        text        NOT NULL,
    description text,
    is_system   boolean     NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL,
    CONSTRAINT uq_roles_tenant_name UNIQUE (tenant_id, name)
);
CREATE INDEX ix_roles_tenant ON docframe_roles (tenant_id, name);

