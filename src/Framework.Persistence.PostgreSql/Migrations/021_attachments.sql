-- FrogFrame: Dokumen attachments multi-file
CREATE TABLE frogframe_attachments (
    id           text        PRIMARY KEY,
    tenant_id    text        NOT NULL,
    doctype      text        NOT NULL,
    doc_name     text        NOT NULL,
    file_name    text        NOT NULL,
    content_type text        NOT NULL,
    size         bigint      NOT NULL,
    sha256       text        NOT NULL,
    uploaded_by  text        NOT NULL,
    created_at   timestamptz NOT NULL,
    is_private   boolean     NOT NULL DEFAULT true,
    content      bytea       NOT NULL
);

CREATE INDEX ix_attachments_doc ON frogframe_attachments (tenant_id, doctype, doc_name);

