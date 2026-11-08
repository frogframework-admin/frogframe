-- FrogFrame: Notification Rules (P1-6) - Aturan notifikasi tanpa koding
CREATE TABLE IF NOT EXISTS frogframe_notification_rules (
    id text PRIMARY KEY,
    tenant_id text NOT NULL,
    name text NOT NULL,
    doctype text NOT NULL,
    trigger text NOT NULL,
    trigger_field text,
    days_offset int,
    condition text,
    channel text NOT NULL,
    receiver_type text NOT NULL,
    receiver_value text,
    subject_template text NOT NULL,
    message_template text NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    modified_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_notification_rules_tenant_doctype ON frogframe_notification_rules (tenant_id, doctype, trigger);

