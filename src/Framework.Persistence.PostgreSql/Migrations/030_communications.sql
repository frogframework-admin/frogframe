-- Migration 030: Inbound email accounts and communication logs
CREATE TABLE IF NOT EXISTS frogframe_email_accounts (
    id UUID PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    email_address TEXT NOT NULL,
    imap_host TEXT NOT NULL,
    imap_port INTEGER NOT NULL DEFAULT 993,
    use_ssl BOOLEAN NOT NULL DEFAULT TRUE,
    username TEXT NOT NULL,
    password_enc TEXT NOT NULL,
    folder TEXT NOT NULL DEFAULT 'INBOX',
    append_to_doctype TEXT,
    field_map JSONB NOT NULL DEFAULT '{}'::jsonb,
    service_user_id TEXT NOT NULL,
    uid_validity BIGINT NOT NULL DEFAULT 0,
    last_uid BIGINT NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    last_polled_at TIMESTAMPTZ,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    modified_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_frogframe_email_accounts_tenant_active 
    ON frogframe_email_accounts (tenant_id, is_active);

CREATE INDEX IF NOT EXISTS ix_frogframe_email_accounts_tenant_name 
    ON frogframe_email_accounts (tenant_id, name);

CREATE TABLE IF NOT EXISTS frogframe_communications (
    id UUID PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    reference_doctype TEXT,
    reference_name TEXT,
    direction TEXT NOT NULL, -- 'In' or 'Out'
    message_id TEXT NOT NULL,
    in_reply_to TEXT,
    subject TEXT NOT NULL,
    sender TEXT NOT NULL,
    recipients TEXT[] NOT NULL DEFAULT '{}',
    cc TEXT[] NOT NULL DEFAULT '{}',
    content_html TEXT,
    content_text TEXT,
    email_account_id UUID REFERENCES frogframe_email_accounts(id) ON DELETE SET NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_frogframe_communications_tenant_msgid UNIQUE (tenant_id, message_id)
);

CREATE INDEX IF NOT EXISTS ix_frogframe_communications_timeline 
    ON frogframe_communications (tenant_id, reference_doctype, reference_name, occurred_at DESC);

CREATE INDEX IF NOT EXISTS ix_frogframe_communications_unlinked 
    ON frogframe_communications (tenant_id, created_at DESC)
    WHERE reference_doctype IS NULL;
