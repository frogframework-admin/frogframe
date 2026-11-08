-- FrogFrame: API dari query tersimpan (GET /api/query/{slug}). source_ref menautkan API yang diterbitkan dari
-- widget dashboard / template laporan agar ikut diperbarui saat sumbernya disimpan.
CREATE TABLE frogframe_query_apis (
    tenant_id   text        NOT NULL,
    id          text        NOT NULL,
    slug        text        NOT NULL,
    name        text        NOT NULL,
    description text,
    config      jsonb       NOT NULL,
    source_ref  text,
    created_by  text        NOT NULL,
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL,
    version     integer     NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, slug)
);
