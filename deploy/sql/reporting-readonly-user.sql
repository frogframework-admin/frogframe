-- FrogFrame: user database READ-ONLY untuk Dashboard SQL.
-- Jalankan SEKALI sebagai superuser/DBA, sebelum aplikasi dijalankan dengan
-- ConnectionStrings:FrogFrameReadOnly. Ganti password!
--
-- User ini:
--   - tidak punya hak INSERT/UPDATE/DELETE/DDL di mana pun,
--   - TIDAK punya akses ke tabel inti (dokumen, akun, file, sesi),
--   - hanya membaca view di skema "reporting" (GRANT SELECT diberikan otomatis oleh
--     aplikasi setiap kali view disinkronkan dari metadata).
-- Aplikasi memverifikasi semua hal di atas sebelum menjalankan query; bila tidak
-- terpenuhi, dashboard menolak berjalan.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'frogframe_ro') THEN
        CREATE ROLE frogframe_ro LOGIN PASSWORD 'ganti-password-ini'
            NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
            CONNECTION LIMIT 20;
    END IF;
END $$;

-- Ganti "FrogFrame" dengan nama database aplikasi bila berbeda.
GRANT CONNECT ON DATABASE FrogFrame TO frogframe_ro;

-- Batasan tambahan di sisi server (selain transaksi READ ONLY + statement_timeout dari aplikasi).
ALTER ROLE frogframe_ro SET default_transaction_read_only = on;
ALTER ROLE frogframe_ro SET statement_timeout = '30s';
ALTER ROLE frogframe_ro SET idle_in_transaction_session_timeout = '30s';
