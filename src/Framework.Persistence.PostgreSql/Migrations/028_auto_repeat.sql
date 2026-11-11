-- 028_auto_repeat.sql
-- Auto Repeat recurring document automation and run history

CREATE TABLE IF NOT EXISTS frogframe_auto_repeats (
    id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    reference_doctype TEXT NOT NULL,
    reference_docname TEXT NOT NULL,
    frequency TEXT NOT NULL, -- Daily, Weekly, Monthly, Quarterly, Yearly
    start_date DATE NOT NULL,
    end_date DATE,
    next_schedule_date DATE NOT NULL,
    status TEXT NOT NULL, -- Active, Paused, Completed
    run_as_user_id TEXT NOT NULL,
    notify_by_email BOOLEAN NOT NULL DEFAULT FALSE,
    recipients TEXT[] NOT NULL DEFAULT '{}',
    submit_on_creation BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    modified_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS idx_frogframe_auto_repeats_due 
    ON frogframe_auto_repeats (tenant_id, status, next_schedule_date);

CREATE INDEX IF NOT EXISTS idx_frogframe_auto_repeats_ref 
    ON frogframe_auto_repeats (tenant_id, reference_doctype, reference_docname);

CREATE TABLE IF NOT EXISTS frogframe_auto_repeat_runs (
    id UUID PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    auto_repeat_id TEXT NOT NULL,
    scheduled_for DATE NOT NULL,
    generated_docname TEXT NOT NULL,
    status TEXT NOT NULL, -- Success, Failed
    error TEXT,
    executed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_frogframe_auto_repeat_run UNIQUE (tenant_id, auto_repeat_id, scheduled_for)
);

CREATE INDEX IF NOT EXISTS idx_frogframe_auto_repeat_runs_history 
    ON frogframe_auto_repeat_runs (tenant_id, auto_repeat_id, executed_at DESC);

