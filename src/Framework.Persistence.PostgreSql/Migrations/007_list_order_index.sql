-- DocFrame: indeks daftar memuat "name" sebagai penentu urutan kedua, sama dengan ORDER BY halaman daftar
-- (modified DESC, name ASC). Tanpa ini PostgreSQL mengurutkan seluruh baris DocType sebelum mengambil satu halaman.
CREATE INDEX IF NOT EXISTS ix_documents_list_v2 ON docframe_documents (tenant_id, doctype, modified DESC, name);
DROP INDEX IF EXISTS ix_documents_list;
