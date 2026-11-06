-- DocFrame: RESET metadata untuk lingkungan pengembangan / demo.
-- Menghapus SEMUA versi DocType dan workflow yang tersimpan, sehingga saat aplikasi dijalankan ulang
-- metadata dimuat ulang dari file JSON (Apps.Sample/Metadata). Data dokumen TIDAK dihapus.
--
-- JANGAN dijalankan di produksi: riwayat publish akan hilang.
--   psql -U docframe -d docframe -f deploy/sql/reset-metadata-dev.sql
BEGIN;
DELETE FROM docframe_metadata_versions;
DELETE FROM docframe_workflow_versions;
COMMIT;
