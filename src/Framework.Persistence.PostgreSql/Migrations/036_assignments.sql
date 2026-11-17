-- Migration 036: Document assignments (ToDo / Assign To)
CREATE TABLE IF NOT EXISTS frogframe_assignments (
    id           UUID        PRIMARY KEY,
    tenant_id    TEXT        NOT NULL,
    doctype      TEXT        NOT NULL,
    doc_name     TEXT        NOT NULL,
    assigned_to  TEXT        NOT NULL,
    assigned_by  TEXT        NOT NULL,
    description  TEXT,
    due_date     DATE,
    priority     SMALLINT    NOT NULL DEFAULT 1,
    status       SMALLINT    NOT NULL DEFAULT 0,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at    TIMESTAMPTZ,
    closed_by    TEXT,
    close_note   TEXT,
    version      INT         NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS ix_frogframe_assignments_user
    ON frogframe_assignments (tenant_id, LOWER(assigned_to), status, due_date);

CREATE INDEX IF NOT EXISTS ix_frogframe_assignments_assigner
    ON frogframe_assignments (tenant_id, LOWER(assigned_by), status, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_frogframe_assignments_doc
    ON frogframe_assignments (tenant_id, doctype, doc_name);

CREATE UNIQUE INDEX IF NOT EXISTS uq_frogframe_assignments_open
    ON frogframe_assignments (tenant_id, doctype, doc_name, LOWER(assigned_to))
    WHERE status = 0;
