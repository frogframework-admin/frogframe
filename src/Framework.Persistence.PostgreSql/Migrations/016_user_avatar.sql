-- DocFrame: add avatar_id column to docframe_users for user profile photos.
ALTER TABLE docframe_users
    ADD COLUMN IF NOT EXISTS avatar_id text;

