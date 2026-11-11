-- =============================================================================
-- Migration 029: Server Scripts (C# Scripting Engine with Roslyn Sandbox)
-- =============================================================================

CREATE TABLE IF NOT EXISTS frogframe_server_scripts (
    id VARCHAR(64) NOT NULL,
    tenant_id VARCHAR(64) NOT NULL,
    name VARCHAR(128) NOT NULL,
    script_type VARCHAR(32) NOT NULL, -- DocEvent, Api, Scheduled
    doctype VARCHAR(128),
    doc_event VARCHAR(64), -- BeforeValidate, BeforeInsert, AfterSave, etc.
    api_method VARCHAR(128),
    cron_expr VARCHAR(64),
    run_as_user_id VARCHAR(128),
    is_active BOOLEAN NOT NULL DEFAULT true,
    version INTEGER NOT NULL DEFAULT 1,
    current_code TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    modified_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_frogframe_server_scripts PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS ix_frogframe_server_scripts_tenant_type
    ON frogframe_server_scripts (tenant_id, script_type);

CREATE INDEX IF NOT EXISTS ix_frogframe_server_scripts_docevent
    ON frogframe_server_scripts (tenant_id, doctype, doc_event)
    WHERE is_active = true AND script_type = 'DocEvent';

CREATE INDEX IF NOT EXISTS ix_frogframe_server_scripts_api
    ON frogframe_server_scripts (tenant_id, api_method)
    WHERE is_active = true AND script_type = 'Api';

CREATE TABLE IF NOT EXISTS frogframe_server_script_versions (
    id VARCHAR(64) NOT NULL,
    tenant_id VARCHAR(64) NOT NULL,
    script_id VARCHAR(64) NOT NULL,
    version INTEGER NOT NULL,
    code TEXT NOT NULL,
    created_by VARCHAR(128) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_frogframe_server_script_versions PRIMARY KEY (tenant_id, script_id, version)
);

CREATE TABLE IF NOT EXISTS frogframe_server_script_runs (
    id UUID NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(64) NOT NULL,
    script_id VARCHAR(64) NOT NULL,
    version INTEGER NOT NULL,
    trigger_type VARCHAR(32) NOT NULL,
    doc_name VARCHAR(128),
    status VARCHAR(32) NOT NULL, -- Success, Failed, BudgetExceeded, Timeout, SecurityViolation
    duration_ms INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    executed_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_frogframe_server_script_runs_history
    ON frogframe_server_script_runs (tenant_id, script_id, executed_at DESC);
