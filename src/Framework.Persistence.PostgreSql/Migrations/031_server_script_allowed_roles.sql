-- =============================================================================
-- Migration 031: Server Script API role restriction
-- Api scripts may only be called by the listed roles. An empty list means only
-- System Manager / Administrator may call the script (secure default).
-- =============================================================================

ALTER TABLE frogframe_server_scripts
    ADD COLUMN IF NOT EXISTS allowed_roles TEXT[] NOT NULL DEFAULT '{}';
