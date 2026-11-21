-- Migration 042: two-factor authentication (TOTP authenticator app + recovery codes).
-- authenticator_key is encrypted by the application (ASP.NET Core Data Protection);
-- recovery_codes holds SHA-256 hashes of the unused codes, never the codes themselves.
ALTER TABLE frogframe_users
    ADD COLUMN IF NOT EXISTS two_factor_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS authenticator_key  TEXT,
    ADD COLUMN IF NOT EXISTS recovery_codes     TEXT[]  NOT NULL DEFAULT '{}';
