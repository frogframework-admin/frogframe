-- DocFrame: instance ANALITIK terpisah memakai replikasi LOGIS (bukan replika fisik).
--
-- Kenapa logis: dashboard mengisolasi tenant lewat tabel sesi (docframe_reporting_sessions) yang ditulis aplikasi
-- sebelum query pengguna dijalankan. Replika fisik bersifat read-only sehingga tabel itu tidak bisa ditulis.
-- Pada replika logis, instance analitik adalah database biasa: tabel sesi, view reporting, dan (kelak) tabel
-- proyeksi Dunia 2 hidup di sana, sementara docframe_documents diisi dari primary.
--
-- ===== 1. Di PRIMARY (sebagai superuser) =====
-- postgresql.conf: wal_level = logical   (butuh restart)
CREATE PUBLICATION docframe_analytics FOR TABLE docframe_documents;
-- Buat user replikasi (ganti password):
-- CREATE ROLE docframe_repl LOGIN REPLICATION PASSWORD 'ganti';
-- GRANT SELECT ON docframe_documents TO docframe_repl;

-- ===== 2. Di INSTANCE ANALITIK (sebagai superuser) =====
-- CREATE DATABASE docframe_analytics OWNER docframe;
-- \c docframe_analytics
-- Tabel tujuan dengan kolom yang sama dengan primary (lihat Migrations/001_initial.sql):
-- CREATE TABLE docframe_documents ( ... sama dengan primary ... , PRIMARY KEY (tenant_id, doctype, name));
-- CREATE SUBSCRIPTION docframe_analytics
--   CONNECTION 'host=primary dbname=docframe user=docframe_repl password=ganti'
--   PUBLICATION docframe_analytics;
-- User read-only dashboard di instance ini: jalankan reporting-readonly-user.sql (ganti nama database).

-- ===== 3. Aplikasi =====
-- ConnectionStrings__DocFrameAnalytics = "Host=analytics;Database=docframe_analytics;Username=docframe;Password=..."
-- ConnectionStrings__DocFrameReadOnly  = "Host=analytics;Database=docframe_analytics;Username=docframe_ro;Password=..."
-- Saat startup aplikasi membuat skema reporting + tabel sesi di instance analitik dan memberi GRANT ke docframe_ro.
-- Catatan: replikasi logis bersifat asinkron — dashboard bisa tertinggal beberapa detik dari data operasional.
