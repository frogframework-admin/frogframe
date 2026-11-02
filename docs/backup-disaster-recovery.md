# Backup & Disaster Recovery Runbook

Panduan pencadangan (*backup*) dan pemulihan (*restore*) DocFrame di produksi.

---

## 1. Sasaran Pemulihan

| Metrik | Target | Catatan |
|---|---|---|
| **RPO** (data maksimum yang boleh hilang) | ≤ 24 jam | Dengan backup harian `pg_dump`. RPO lebih kecil butuh WAL archiving / point-in-time recovery, yang **belum** disiapkan. |
| **RTO** (waktu pulih maksimum) | < 30 menit | Uji restore 2 Nov 2026: 1,1 detik untuk dump 0,08 MB (data dev). Ukur ulang dengan volume data produksi. |

---

## 2. Apa yang Dicadangkan

Semua *state* DocFrame ada di PostgreSQL, jadi satu `pg_dump` sudah mencakup:
- Dokumen dan riwayat: `docframe_documents`, `docframe_audit`
- File/gambar yang diunggah: `docframe_files`
- Kunci Data Protection (cookie sesi): `docframe_data_protection_keys`
- Metadata, workflow, menu, role, akun: `docframe_metadata_versions`, `docframe_workflow_versions`, `docframe_navigation_*`, `docframe_roles`, `docframe_users`
- Outbox, riwayat job, error log: `docframe_outbox`, `docframe_scheduled_job_runs`, `docframe_error_logs`

> [!CAUTION]
> Kunci Data Protection tersimpan **tanpa enkripsi**. Siapa pun yang memegang file backup bisa memalsukan cookie sesi dan membaca hash password. Simpan backup terenkripsi (mis. bucket dengan enkripsi server-side) dan batasi aksesnya seperti menyimpan rahasia.

> [!NOTE]
> Role `docframe_ro` (read-only untuk dashboard) tidak ikut dipulihkan, karena restore memakai `--no-owner --no-privileges`. Jalankan ulang `deploy/sql/reporting-readonly-user.sql` setelah restore (langkah 4.3).

---

## 3. Backup

Skrip memakai format custom terkompresi (`pg_dump -Fc`). Setiap backup langsung diverifikasi dengan `pg_restore --list`. Bila ada langkah yang gagal, skrip keluar dengan kode non-zero dan file setengah jadi dihapus, supaya tidak ada file rusak yang terlihat seperti backup valid.

### Linux (cron)
[backup-db.sh](file:///d:/Side%20Projects/DocFrame/deploy/scripts/backup-db.sh):
```bash
# Setiap hari 02:00. Password lewat ~/.pgpass atau PGPASSWORD.
0 2 * * * BACKUP_DIR=/var/backups/docframe /opt/docframe/deploy/scripts/backup-db.sh >> /var/log/docframe-backup.log 2>&1
```
Variabel: `BACKUP_DIR` (default `/var/backups/docframe`), `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `RETENTION_DAYS` (default 14).

### Windows / dev (PowerShell)
[backup-db.ps1](file:///d:/Side%20Projects/DocFrame/deploy/scripts/backup-db.ps1):
```powershell
$env:PGPASSWORD = "<password>"
powershell -ExecutionPolicy Bypass -File deploy\scripts\backup-db.ps1 -RetentionDays 14
```
Bila `pg_dump` tidak ada di host, skrip memakai container Docker `docframe-postgres` (ganti dengan `-Container`). `PGPASSWORD` atau `-Password` wajib diisi; tidak ada password default.

> [!IMPORTANT]
> Backup yang hanya ada di server yang sama tidak melindungi dari kehilangan server. Salin hasil backup ke penyimpanan lain (object storage / server lain). Langkah ini belum diotomatiskan oleh skrip.

---

## 4. Restore

Restore berjalan dalam **satu transaksi** (`--single-transaction --exit-on-error`). Bila ada error, semua perubahan dibatalkan dan database tetap seperti sebelum restore.

### 4.1 Hentikan aplikasi
Supaya tidak ada transaksi baru atau worker outbox yang berjalan selama restore:
```bash
docker stop docframe-app        # atau: sudo systemctl stop docframe
```

### 4.2 Jalankan restore
```bash
# Linux
/opt/docframe/deploy/scripts/restore-db.sh /var/backups/docframe/docframe_20261102_020000.dump --force
```
```powershell
# Windows
$env:PGPASSWORD = "<password>"
powershell -ExecutionPolicy Bypass -File deploy\scripts\restore-db.ps1 -BackupFile backups\docframe_20261102_020000.dump -Force
```
Tanpa `--force` / `-Force`, skrip meminta konfirmasi lebih dulu.

### 4.3 Pasang ulang role read-only
```bash
psql -h localhost -U docframe -d docframe -f deploy/sql/reporting-readonly-user.sql
```

### 4.4 Nyalakan aplikasi
Saat start, aplikasi menjalankan `SchemaMigrator`. Bila versi kode lebih baru dari backup, migrasi yang kurang akan diterapkan.
```bash
docker start docframe-app
curl -i http://localhost:5080/health/live
curl -i http://localhost:5080/health/ready
```

### 4.5 Uji asap
- Login, buka beberapa dokumen.
- `/admin/jobs`: riwayat job terbaca.
- `/admin/error-logs`: tidak ada error baru sejak start.

---

## 5. Uji Restore Berkala (DR Drill)

Lakukan minimal tiap kuartal, dan setelah perubahan skema besar. **Jangan uji ke database produksi**; selalu ke database terpisah.

```powershell
$env:PGPASSWORD = "<password>"
docker exec docframe-postgres psql -U docframe -d postgres -c "CREATE DATABASE docframe_drill;"
powershell -ExecutionPolicy Bypass -File deploy\scripts\restore-db.ps1 -BackupFile <file.dump> -Database docframe_drill -Force
# Bandingkan jumlah baris tiap tabel antara docframe dan docframe_drill, lalu:
docker exec docframe-postgres psql -U docframe -d postgres -c "DROP DATABASE docframe_drill;"
```

### Hasil uji terakhir

| Tanggal | Sumber | Hasil |
|---|---|---|
| 2 Nov 2026 | DB dev (`docframe`, 0,08 MB) | Restore ke `docframe_drill` sukses dalam 1,1 detik. 24 tabel, 0 selisih jumlah baris. File rusak ditolak dengan exit code 1. |
