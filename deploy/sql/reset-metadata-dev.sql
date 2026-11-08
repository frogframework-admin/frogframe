-- FrogFrame: RESET metadata untuk lingkungan pengembangan / demo.
-- Menghapus SEMUA versi DocType dan workflow yang tersimpan, sehingga saat aplikasi dijalankan ulang
-- metadata dimuat ulang dari file JSON (Apps.Sample/Metadata). Data dokumen TIDAK dihapus.
--
-- JANGAN dijalankan di produksi: riwayat publish akan hilang.
--   psql -U FrogFrame -d FrogFrame -f deploy/sql/reset-metadata-dev.sql
BEGIN;
DELETE FROM frogframe_metadata_versions;
DELETE FROM frogframe_workflow_versions;
COMMIT;
