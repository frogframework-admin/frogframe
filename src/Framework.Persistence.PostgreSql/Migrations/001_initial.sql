-- FrogFrame: skema awal (strategi "tabel generik + payload JSONB", RFC §7A).
-- Semua query aplikasi difilter tenant_id; primary key diawali tenant_id agar index selalu ter-scope.

CREATE TABLE frogframe_documents (
    tenant_id        text        NOT NULL,
    doctype          text        NOT NULL,
    name             text        NOT NULL,
    owner            text        NOT NULL,
    docstatus        smallint    NOT NULL DEFAULT 0,
    workflow_state   text,
    created          timestamptz NOT NULL,
    modified         timestamptz NOT NULL,
    modified_by      text        NOT NULL,
    version          integer     NOT NULL,
    schema_version   integer     NOT NULL,
    workflow_version integer,
    amended_from     text,
    data             jsonb       NOT NULL,
    PRIMARY KEY (tenant_id, doctype, name)
);
CREATE INDEX ix_documents_list  ON frogframe_documents (tenant_id, doctype, modified DESC);
CREATE INDEX ix_documents_owner ON frogframe_documents (tenant_id, doctype, owner);
CREATE INDEX ix_documents_state ON frogframe_documents (tenant_id, doctype, docstatus, workflow_state);
CREATE INDEX ix_documents_data  ON frogframe_documents USING gin (data jsonb_path_ops);

CREATE TABLE frogframe_series (
    tenant_id text    NOT NULL,
    doctype   text    NOT NULL,
    prefix    text    NOT NULL,
    current   integer NOT NULL,
    PRIMARY KEY (tenant_id, doctype, prefix)
);

CREATE TABLE frogframe_audit (
    id               bigserial   PRIMARY KEY,
    tenant_id        text        NOT NULL,
    doctype          text        NOT NULL,
    doc_name         text        NOT NULL,
    action           text        NOT NULL,
    actor            text        NOT NULL,
    at               timestamptz NOT NULL,
    changes          jsonb       NOT NULL DEFAULT '[]',
    comment          text,
    from_state       text,
    to_state         text,
    workflow_version integer,
    schema_version   integer     NOT NULL
);
CREATE INDEX ix_audit_doc ON frogframe_audit (tenant_id, doctype, doc_name, id DESC);

CREATE TABLE frogframe_outbox (
    id              uuid        PRIMARY KEY,
    tenant_id       text        NOT NULL,
    type            text        NOT NULL,
    payload         jsonb       NOT NULL,
    idempotency_key text        NOT NULL UNIQUE,
    created_at      timestamptz NOT NULL,
    attempts        integer     NOT NULL DEFAULT 0,
    next_attempt_at timestamptz,
    processed_at    timestamptz,
    last_error      text,
    dead_lettered   boolean     NOT NULL DEFAULT false,
    locked_until    timestamptz
);
CREATE INDEX ix_outbox_due ON frogframe_outbox (created_at)
    WHERE processed_at IS NULL AND NOT dead_lettered;

CREATE TABLE frogframe_notifications (
    id        uuid        PRIMARY KEY,
    tenant_id text        NOT NULL,
    user_id   text        NOT NULL,
    subject   text        NOT NULL,
    body      text        NOT NULL,
    doctype   text,
    doc_name  text,
    at        timestamptz NOT NULL,
    read      boolean     NOT NULL DEFAULT false
);
CREATE INDEX ix_notifications_user ON frogframe_notifications (tenant_id, user_id, at DESC);

CREATE TABLE frogframe_metadata_versions (
    doctype      text        NOT NULL,
    version      integer     NOT NULL,
    definition   jsonb       NOT NULL,
    status       text        NOT NULL,
    published_by text        NOT NULL,
    published_at timestamptz NOT NULL,
    changes      jsonb       NOT NULL DEFAULT '[]',
    PRIMARY KEY (doctype, version)
);
CREATE UNIQUE INDEX ux_metadata_one_published ON frogframe_metadata_versions (doctype) WHERE status = 'Published';
