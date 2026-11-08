-- FrogFrame: dukungan indeks pencarian. pg_trgm membuat ILIKE '%teks%' bisa memakai indeks (GIN trigram).
-- pg_trgm adalah extension "trusted" (PostgreSQL 13+): pemilik database boleh membuatnya tanpa superuser.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Pencarian pada nomor dokumen (kolom standar "name") untuk semua DocType.
CREATE INDEX IF NOT EXISTS ix_documents_name_trgm ON frogframe_documents USING gin (name gin_trgm_ops);
