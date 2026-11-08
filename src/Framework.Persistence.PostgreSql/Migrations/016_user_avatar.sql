-- FrogFrame: add avatar_id column to frogframe_users for user profile photos.
ALTER TABLE frogframe_users
    ADD COLUMN IF NOT EXISTS avatar_id text;

