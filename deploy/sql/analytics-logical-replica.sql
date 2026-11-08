-- FrogFrame: instance ANALITIK terpisah memakai replikasi LOGIS (bukan replika fisik).
--
-- Kenapa logis: dashboard mengisolasi tenant lewat tabel sesi (frogframe_reporting_sessions) yang ditulis aplikasi
-- sebelum query pengguna dijalankan. Replika fisik bersifat read-only sehingga tabel itu tidak bisa ditulis.
-- Pada replika logis, instance analitik adalah database biasa: tabel sesi, view reporting, dan (kelak) tabel
-- proyeksi Dunia 2 hidup di sana, sementara frogframe_documents diisi dari primary.
--
-- ===== 1. Di PRIMARY (sebagai superuser) =====
-- postgresql.conf: wal_level = logical   (butuh restart)
CREATE PUBLICATION frogframe_analytics FOR TABLE frogframe_documents;
-- Buat user replikasi (ganti password):
-- CREATE ROLE frogframe_repl LOGIN REPLICATION PASSWORD 'ganti';
-- GRANT SELECT ON frogframe_documents TO frogframe_repl;

-- ===== 2. Di INSTANCE ANALITIK (sebagai superuser) =====
-- CREATE DATABASE frogframe_analytics OWNER FrogFrame;
-- \c frogframe_analytics
-- Tabel tujuan dengan kolom yang sama dengan primary (lihat Migrations/001_initial.sql):
-- CREATE TABLE frogframe_documents ( ... sama dengan primary ... , PRIMARY KEY (tenant_id, doctype, name));
-- CREATE SUBSCRIPTION frogframe_analytics
--   CONNECTION 'host=primary dbname=FrogFrame user=frogframe_repl password=ganti'
--   PUBLICATION frogframe_analytics;
-- User read-only dashboard di instance ini: jalankan reporting-readonly-user.sql (ganti nama database).

-- ===== 3. Aplikasi =====
-- ConnectionStrings__FrogFrameAnalytics = "Host=analytics;Database=frogframe_analytics;Username=FrogFrame;Password=..."
-- ConnectionStrings__FrogFrameReadOnly  = "Host=analytics;Database=frogframe_analytics;Username=frogframe_ro;Password=..."
-- Saat startup aplikasi membuat skema reporting + tabel sesi di instance analitik dan memberi GRANT ke frogframe_ro.
-- Catatan: replikasi logis bersifat asinkron — dashboard bisa tertinggal beberapa detik dari data operasional.
