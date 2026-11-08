-- FrogFrame: kunci API integrasi. Hanya hash secret yang disimpan (secret ditampilkan sekali saat dibuat).
CREATE TABLE frogframe_api_keys (
    id           text        PRIMARY KEY,
    tenant_id    text        NOT NULL,
    user_id      text        NOT NULL,
    name         text        NOT NULL,
    secret_hash  text        NOT NULL,
    scopes       text[]      NOT NULL DEFAULT '{}',
    allowed_ips  text[]      NOT NULL DEFAULT '{}',
    created_by   text        NOT NULL,
    created_at   timestamptz NOT NULL,
    expires_at   timestamptz,
    revoked_at   timestamptz,
    last_used_at timestamptz,
    last_used_ip text
);
CREATE INDEX ix_api_keys_tenant ON frogframe_api_keys (tenant_id, created_at DESC);
