-- Attach fields (Gap #3): keep the original file name so downloads get a sensible name.
ALTER TABLE frogframe_files ADD COLUMN IF NOT EXISTS file_name TEXT NULL;
