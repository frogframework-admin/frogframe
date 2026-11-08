-- FrogFrame: file unggahan (gambar field Image). Disimpan di database agar ikut backup
-- dan tersedia di semua instance aplikasi. Batas ukuran ditegakkan aplikasi (2 MB).
CREATE TABLE frogframe_files (
    id           text        PRIMARY KEY,
    tenant_id    text        NOT NULL,
    uploaded_by  text        NOT NULL,
    content_type text        NOT NULL,
    size         bigint      NOT NULL,
    sha256       text        NOT NULL,
    created_at   timestamptz NOT NULL,
    doctype      text,
    doc_name     text,
    field        text,
    content      bytea       NOT NULL
);
CREATE INDEX ix_files_doc ON frogframe_files (tenant_id, doctype, doc_name);
