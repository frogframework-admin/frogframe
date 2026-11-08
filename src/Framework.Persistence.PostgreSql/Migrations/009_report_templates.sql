-- FrogFrame: template laporan Excel/Word. Konfigurasi (query, parameter, format, role) sebagai JSON;
-- file template (.xlsx/.docx, maks. 5 MB) disimpan di kolom bytea agar ikut backup dan tersedia di semua instance.
CREATE TABLE frogframe_report_templates (
    tenant_id          text        NOT NULL,
    id                 text        NOT NULL,
    name               text        NOT NULL,
    description        text,
    config             jsonb       NOT NULL,
    template_file_name text        NOT NULL,
    template           bytea       NOT NULL,
    created_by         text        NOT NULL,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    version            integer     NOT NULL,
    PRIMARY KEY (tenant_id, id)
);
