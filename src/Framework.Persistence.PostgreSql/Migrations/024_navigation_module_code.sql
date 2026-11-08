-- FrogFrame: kode unik untuk master modul. DocType.module merujuk ke kode ini.
ALTER TABLE frogframe_navigation_modules ADD COLUMN IF NOT EXISTS code text;

-- Modul yang sudah ada diberi kode. Nama bawaan dipetakan eksplisit; nama lain dijadikan kode dari namanya.
UPDATE frogframe_navigation_modules SET code = CASE
    WHEN name IN ('Human Resources', 'HR')  THEN 'HR'
    WHEN name = 'Procurement'               THEN 'PROCUREMENT'
    WHEN name = 'Analytics'                 THEN 'ANALYTICS'
    WHEN name = 'Administration'            THEN 'ADMINISTRATION'
    ELSE UPPER(REGEXP_REPLACE(name, '[^A-Za-z0-9]+', '_', 'g'))
END
WHERE code IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_nav_modules_code ON frogframe_navigation_modules (tenant_id, code) WHERE code IS NOT NULL;
