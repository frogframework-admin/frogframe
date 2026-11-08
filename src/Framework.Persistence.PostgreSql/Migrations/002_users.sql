-- FrogFrame: akun login (ASP.NET Core Identity dengan penyimpanan FrogFrame).
-- Tenant, departemen, karyawan, dan role disimpan di akun lalu diterbitkan sebagai klaim saat login.

CREATE TABLE frogframe_users (
    id                   text        PRIMARY KEY,
    user_name            text        NOT NULL,
    normalized_user_name text        NOT NULL UNIQUE,
    email                text,
    normalized_email     text,
    full_name            text        NOT NULL,
    tenant_id            text        NOT NULL,
    department           text,
    employee_id          text,
    roles                text[]      NOT NULL DEFAULT '{}',
    is_active            boolean     NOT NULL DEFAULT true,
    password_hash        text,
    security_stamp       text        NOT NULL,
    concurrency_stamp    text        NOT NULL,
    lockout_enabled      boolean     NOT NULL DEFAULT true,
    lockout_end          timestamptz,
    access_failed_count  integer     NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL,
    last_login_at        timestamptz
);
CREATE INDEX ix_users_tenant ON frogframe_users (tenant_id, full_name);
CREATE INDEX ix_users_email  ON frogframe_users (normalized_email);
