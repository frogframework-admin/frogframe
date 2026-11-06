-- DocFrame: pemisahan peran database (lapis 2 append-only buku besar).
--
--   docframe        = PEMILIK skema. Dipakai migrasi & sinkronisasi indeks (ConnectionStrings:DocFrameSchema).
--   docframe_app    = peran RUNTIME aplikasi (ConnectionStrings:DocFrame). Bukan pemilik: tidak bisa ALTER TABLE,
--                     tidak bisa mematikan trigger, dan tidak punya UPDATE/DELETE/TRUNCATE pada buku besar.
--
-- Jalankan SEKALI sebagai superuser, SETELAH migrasi pertama selesai (tabel sudah ada):
--   psql -U postgres -d docframe -f deploy/sql/gl-roles.sql
--   lalu: ALTER ROLE docframe_app PASSWORD '…';
-- Lalu konfigurasi aplikasi:
--   ConnectionStrings__DocFrame       = Host=…;Database=docframe;Username=docframe_app;Password=…
--   ConnectionStrings__DocFrameSchema = Host=…;Database=docframe;Username=docframe;Password=…
-- Catatan: bila dashboard memakai skema reporting, isi ConnectionStrings__DocFrameAnalytics dengan koneksi pemilik
-- (pembuatan view butuh hak pemilik).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'docframe_app') THEN
        CREATE ROLE docframe_app LOGIN PASSWORD 'docframe_app' NOSUPERUSER NOCREATEDB NOCREATEROLE; -- ganti password setelah dibuat
    END IF;
END $$;

GRANT CONNECT ON DATABASE docframe TO docframe_app;
GRANT USAGE ON SCHEMA public TO docframe_app;

-- Operasi data biasa untuk semua tabel yang sudah ada dan yang dibuat migrasi berikutnya.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO docframe_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO docframe_app;
ALTER DEFAULT PRIVILEGES FOR ROLE docframe IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO docframe_app;
ALTER DEFAULT PRIVILEGES FOR ROLE docframe IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO docframe_app;

-- Buku besar: hanya baca dan tambah.
REVOKE UPDATE, DELETE, TRUNCATE ON docframe_gl_entries FROM docframe_app;
