-- FrogFrame: pemisahan peran database (lapis 2 append-only buku besar).
--
--   FrogFrame        = PEMILIK skema. Dipakai migrasi & sinkronisasi indeks (ConnectionStrings:FrogFrameSchema).
--   frogframe_app    = peran RUNTIME aplikasi (ConnectionStrings:FrogFrame). Bukan pemilik: tidak bisa ALTER TABLE,
--                     tidak bisa mematikan trigger, dan tidak punya UPDATE/DELETE/TRUNCATE pada buku besar.
--
-- Jalankan SEKALI sebagai superuser, SETELAH migrasi pertama selesai (tabel sudah ada):
--   psql -U postgres -d FrogFrame -f deploy/sql/gl-roles.sql
--   lalu: ALTER ROLE frogframe_app PASSWORD '…';
-- Lalu konfigurasi aplikasi:
--   ConnectionStrings__FrogFrame       = Host=…;Database=FrogFrame;Username=frogframe_app;Password=…
--   ConnectionStrings__FrogFrameSchema = Host=…;Database=FrogFrame;Username=FrogFrame;Password=…
-- Catatan: bila dashboard memakai skema reporting, isi ConnectionStrings__FrogFrameAnalytics dengan koneksi pemilik
-- (pembuatan view butuh hak pemilik).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'frogframe_app') THEN
        CREATE ROLE frogframe_app LOGIN PASSWORD 'frogframe_app' NOSUPERUSER NOCREATEDB NOCREATEROLE; -- ganti password setelah dibuat
    END IF;
END $$;

GRANT CONNECT ON DATABASE FrogFrame TO frogframe_app;
GRANT USAGE ON SCHEMA public TO frogframe_app;

-- Operasi data biasa untuk semua tabel yang sudah ada dan yang dibuat migrasi berikutnya.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO frogframe_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO frogframe_app;
ALTER DEFAULT PRIVILEGES FOR ROLE FrogFrame IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO frogframe_app;
ALTER DEFAULT PRIVILEGES FOR ROLE FrogFrame IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO frogframe_app;

-- Buku besar: hanya baca dan tambah.
REVOKE UPDATE, DELETE, TRUNCATE ON frogframe_gl_entries FROM frogframe_app;
