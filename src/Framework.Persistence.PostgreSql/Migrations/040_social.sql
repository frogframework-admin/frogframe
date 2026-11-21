-- Migration 040: document tags, personal bookmarks and follows (social features).

-- Tags: shared labels on documents. One tag per document regardless of letter case.
CREATE TABLE IF NOT EXISTS frogframe_doc_tags (
    tenant_id   TEXT        NOT NULL,
    doctype     TEXT        NOT NULL,
    doc_name    TEXT        NOT NULL,
    tag         TEXT        NOT NULL,
    tagged_by   TEXT        NOT NULL,
    tagged_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_frogframe_doc_tags
    ON frogframe_doc_tags (tenant_id, doctype, doc_name, LOWER(tag));
-- Tag filter and tag suggestions: documents of a DocType with a tag.
CREATE INDEX IF NOT EXISTS ix_frogframe_doc_tags_tag
    ON frogframe_doc_tags (tenant_id, doctype, LOWER(tag), doc_name);

-- Bookmarks: personal, one per user and document.
CREATE TABLE IF NOT EXISTS frogframe_bookmarks (
    tenant_id   TEXT        NOT NULL,
    user_id     TEXT        NOT NULL,
    doctype     TEXT        NOT NULL,
    doc_name    TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_frogframe_bookmarks
    ON frogframe_bookmarks (tenant_id, LOWER(user_id), doctype, doc_name);
CREATE INDEX IF NOT EXISTS ix_frogframe_bookmarks_doc
    ON frogframe_bookmarks (tenant_id, doctype, doc_name);

-- Follows: users notified when a document changes.
CREATE TABLE IF NOT EXISTS frogframe_doc_follows (
    tenant_id   TEXT        NOT NULL,
    doctype     TEXT        NOT NULL,
    doc_name    TEXT        NOT NULL,
    user_id     TEXT        NOT NULL,
    followed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_frogframe_doc_follows
    ON frogframe_doc_follows (tenant_id, doctype, doc_name, LOWER(user_id));
CREATE INDEX IF NOT EXISTS ix_frogframe_doc_follows_user
    ON frogframe_doc_follows (tenant_id, LOWER(user_id), followed_at DESC);
