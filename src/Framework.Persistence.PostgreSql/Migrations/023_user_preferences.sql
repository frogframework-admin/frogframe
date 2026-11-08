-- FrogFrame: User Preferences (Grid customization, UI states per-user)
CREATE TABLE IF NOT EXISTS frogframe_user_preferences (
    tenant_id text NOT NULL,
    user_id text NOT NULL,
    pref_key text NOT NULL,
    pref_value jsonb NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, user_id, pref_key)
);

CREATE INDEX IF NOT EXISTS ix_user_preferences_user ON frogframe_user_preferences (tenant_id, user_id);

