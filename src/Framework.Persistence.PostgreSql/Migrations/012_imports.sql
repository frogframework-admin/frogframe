-- DocFrame: riwayat impor Excel/CSV. Dokumen hasil impor juga tercatat di audit masing-masing ("Dibuat lewat impor #…").
CREATE TABLE docframe_imports (
    id            text        PRIMARY KEY,
    tenant_id     text        NOT NULL,
    doctype       text        NOT NULL,
    file_name     text        NOT NULL,
    atomic        boolean     NOT NULL,
    total         integer     NOT NULL,
    succeeded     integer     NOT NULL,
    failed        integer     NOT NULL,
    created_names text[]      NOT NULL DEFAULT '{}',
    user_id       text        NOT NULL,
    user_name     text        NOT NULL,
    created_at    timestamptz NOT NULL
);
CREATE INDEX ix_imports_doctype ON docframe_imports (tenant_id, doctype, created_at DESC);
