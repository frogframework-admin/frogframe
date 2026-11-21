-- FrogFrame: module parent application_id
ALTER TABLE frogframe_navigation_modules
    ADD COLUMN IF NOT EXISTS application_id text;

CREATE INDEX IF NOT EXISTS ix_nav_modules_application ON frogframe_navigation_modules (tenant_id, application_id);
