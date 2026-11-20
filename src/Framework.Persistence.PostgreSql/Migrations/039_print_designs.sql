-- Migration 039: Letterheads and print formats designed in /admin/print-designs
CREATE TABLE IF NOT EXISTS frogframe_print_designs (
    id           UUID        PRIMARY KEY,
    tenant_id    TEXT        NOT NULL,
    kind         TEXT        NOT NULL,          -- 'letterhead' | 'format'
    doctype      TEXT,                          -- formats only
    name         TEXT        NOT NULL,
    is_default   BOOLEAN     NOT NULL DEFAULT FALSE,
    definition   JSONB       NOT NULL,
    version      INT         NOT NULL DEFAULT 1,
    modified_at  TIMESTAMPTZ NOT NULL,
    modified_by  TEXT        NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_frogframe_print_designs_lookup
    ON frogframe_print_designs (tenant_id, kind, doctype, LOWER(name));
