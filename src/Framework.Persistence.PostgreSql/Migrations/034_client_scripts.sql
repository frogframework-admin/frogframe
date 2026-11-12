-- Client Scripts: server scripts of type 'Form' run while users work in a form (load, field change, before save).
ALTER TABLE frogframe_server_scripts ADD COLUMN IF NOT EXISTS form_event TEXT NULL;
ALTER TABLE frogframe_server_scripts ADD COLUMN IF NOT EXISTS form_field TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_server_scripts_form
    ON frogframe_server_scripts (tenant_id, doctype)
    WHERE is_active = true AND script_type = 'Form';
