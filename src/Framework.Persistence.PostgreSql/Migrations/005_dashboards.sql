-- FrogFrame: dashboard (widget SQL) dan skema pelaporan read-only.

-- Widget & layout disimpan sebagai JSON configuration: fitur visual baru tidak butuh perubahan skema.
CREATE TABLE frogframe_dashboard_widgets (
    tenant_id   text        NOT NULL,
    id          text        NOT NULL,
    name        text        NOT NULL,
    description text,
    config      jsonb       NOT NULL,   -- { sql, parameters[], visual{type,xColumn,yColumns[]}, roles[] }
    created_by  text        NOT NULL,
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL,
    version     integer     NOT NULL,
    PRIMARY KEY (tenant_id, id)
);

CREATE TABLE frogframe_dashboards (
    tenant_id  text        PRIMARY KEY,
    layout     jsonb       NOT NULL,    -- { items: [{ widgetId, span }] }
    updated_at timestamptz NOT NULL
);

-- Tenant untuk sesi query pelaporan, diisi aplikasi lewat koneksi utama sebelum query user dijalankan
-- di koneksi read-only (dikunci per process id). User read-only TIDAK diberi hak apa pun atas tabel ini,
-- sehingga tidak bisa mengganti tenant-nya sendiri (berbeda dengan set_config yang bisa diubah dari SQL).
CREATE TABLE frogframe_reporting_sessions (
    pid        integer     PRIMARY KEY,
    tenant_id  text        NOT NULL,
    created_at timestamptz NOT NULL
);

-- View pelaporan (dibuat ulang otomatis dari metadata oleh ReportingSchemaSync).
CREATE SCHEMA IF NOT EXISTS reporting;
