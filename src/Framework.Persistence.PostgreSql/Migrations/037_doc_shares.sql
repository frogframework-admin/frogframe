-- Migration 037: Row sharing (per-document read / write / share grants)
CREATE TABLE IF NOT EXISTS frogframe_doc_shares (
    tenant_id   TEXT        NOT NULL,
    doctype     TEXT        NOT NULL,
    doc_name    TEXT        NOT NULL,
    user_id     TEXT        NOT NULL,
    can_write   BOOLEAN     NOT NULL DEFAULT FALSE,
    can_share   BOOLEAN     NOT NULL DEFAULT FALSE,
    shared_by   TEXT        NOT NULL,
    shared_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- One share per document and user (user ids compare case-insensitively).
CREATE UNIQUE INDEX IF NOT EXISTS uq_frogframe_doc_shares
    ON frogframe_doc_shares (tenant_id, doctype, doc_name, LOWER(user_id));

-- List queries: documents of a DocType shared with a user.
CREATE INDEX IF NOT EXISTS ix_frogframe_doc_shares_user
    ON frogframe_doc_shares (tenant_id, doctype, LOWER(user_id), doc_name);
