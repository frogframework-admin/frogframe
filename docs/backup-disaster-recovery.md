# Backup & Disaster Recovery Runbook

Panduan operasional resmi untuk pencadangan (*backup*), pemulihan (*restore*), dan penanganan bencana (*disaster recovery*) pada DocFrame di lingkungan produksi.

---

## 1. Sasaran Pemulihan (RPO & RTO)

| Metrik | Target | Keterangan |
|---|---|---|
| **RPO** (*Recovery Point Objective*) | $\le 24$ jam (harian) atau $\le 1$ jam (jika WAL archiving aktif) | Maksimum rentang data yang hilang saat bencana terjadi. |
| **RTO** (*Recovery Time Objective*) | $< 30$ menit | Waktu maksimum dari insiden hingga sistem kembali beroperasi normal. |

---

## 2. Komponen yang Dicadangkan

DocFrame dirancang dengan arsitektur **database-centric**:
1. **Semua Dokumen & Riwayat**: Tersimpan di tabel `docframe_documents` dan `docframe_audit_trail`.
2. **Kunci Data Protection**: Tersimpan di tabel `docframe_data_protection_keys` (RFC §2, P0-2).
3. **Konfigurasi Sistem**: Metadata DocType (`docframe_metadata_versions`), Workflow, Navigation Menus, Roles, dan Scheduled Job history (`docframe_scheduled_job_runs`).
4. **Log Observabilitas**: Log error server (`docframe_error_logs`) dan outbox pesan (`docframe_outbox`).

> [!NOTE]
> Karena seluruh *state* aplikasi (termasuk session cookie protection keys) tersimpan dalam PostgreSQL, cadangan database tunggal (`pg_dump`) sudah mencakup seluruh integritas sistem DocFrame tanpa memerlukan backup file terpisah untuk keys.

---

## 3. Prosedur Pencadangan (Backup)

### 3.1 Format Backup
Pencadangan selalu menggunakan format biner terkompresi PostgreSQL (**`-Fc` / Custom Format**):
- **Kompresi otomatis** (~70-80% lebih kecil dibanding plain SQL).
- **Integritas arsip dapat diuji** tanpa restore menggunakan `pg_restore --list`.
- **Mendukung restore paralel** (`-j <cores>`) dan seleksi tabel jika diperlukan.

### 3.2 Pencadangan Terjadwal (Linux Cron / Production Host)

Gunakan skrip [deploy/scripts/backup-db.sh](file:///d:/Side%20Projects/DocFrame/deploy/scripts/backup-db.sh):

```bash
# Tambahkan ke crontab (setiap hari pukul 02:00 dini hari)
0 2 * * * /opt/docframe/deploy/scripts/backup-db.sh >> /var/log/docframe-backup.log 2>&1
```

Variabel lingkungan yang didukung:
- `BACKUP_DIR`: Direktori tujuan (default: `/var/backups/docframe`).
- `PGHOST`: Host PostgreSQL (default: `localhost`).
- `PGPORT`: Port PostgreSQL (default: `5432`).
- `PGDATABASE`: Nama database (default: `docframe`).
- `PGUSER`: Akun database (default: `docframe`).
- `RETENTION_DAYS`: Masa retensi dalam hari (default: `14`).

### 3.3 Pencadangan Terjadwal (Windows Host / PowerShell)

Gunakan skrip [deploy/scripts/backup-db.ps1](file:///d:/Side%20Projects/DocFrame/deploy/scripts/backup-db.ps1):

```powershell
powershell -ExecutionPolicy Bypass -File deploy\scripts\backup-db.ps1 -RetentionDays 14
```

Skrip secara otomatis:
1. Mendeteksi ketersediaan `pg_dump` lokal atau beralih ke container Docker `docframe-postgres`.
2. Menghasilkan file terkompresi: `docframe_YYYYMMDD_HHMMSS.dump`.
3. Memverifikasi integritas arsip dengan `pg_restore --list`.
4. Membersihkan file backup yang lebih lama dari masa retensi (`RetentionDays`).

---

## 4. Prosedur Pemulihan (Restoration / Disaster Recovery)

### 4.1 Langkah 1: Hentikan Instance Aplikasi
Cegah penulisan transaksi baru atau perebutan outbox selama proses restore:
```bash
# Jika Docker:
docker stop docframe-app

# Jika systemd:
sudo systemctl stop docframe
```

### 4.2 Langkah 2: Jalankan Pemulihan Database
Jalankan skrip pemulihan dengan file dump yang dipilih:

**Linux / Bash:**
```bash
/opt/docframe/deploy/scripts/restore-db.sh /var/backups/docframe/docframe_20261102_020000.dump --force
```

**Windows / PowerShell:**
```powershell
powershell -ExecutionPolicy Bypass -File deploy\scripts\restore-db.ps1 -BackupFile backups\docframe_20261102_173607.dump -Force
```

Parameter restore menggunakan `--clean --if-exists --no-owner --no-privileges` untuk membersihkan skema lama dan memulihkan seluruh struktur tabel serta indeks secara atomik.

### 4.3 Langkah 3: Konfigurasi Role Read-Only Analitik (Penting)
Pastikan izin role analitik `docframe_ro` tetap memenuhi aturan keamanan (tidak boleh membaca tabel `public` langsung):
```bash
psql -h localhost -U docframe -d docframe -f deploy/sql/reporting-readonly-user.sql
```

### 4.4 Langkah 4: Sinkronisasi Migrasi Skema
Jalankan migrator DocFrame untuk memastikan skema konsisten dengan versi kode terbaru:
```bash
dotnet run --project src/Apps.Sample
```

### 4.5 Langkah 5: Nyalakan Kembali Layanan & Uji Asap (Smoke Test)
```bash
# Nyalakan aplikasi
docker start docframe-app

# Cek live dan ready health check (P0-3)
curl -i http://localhost:5080/health/live
curl -i http://localhost:5080/health/ready
```

Verifikasi halaman admin:
- `/admin/jobs`: Pastikan riwayat scheduler terbaca dan tombol "Run Now" berfungsi.
- `/admin/error-logs`: Periksa apakah ada error startup yang tidak tertangani.

---

## 5. Matriks Retensi & Penyimpanan Cadangan

| Tipe Cadangan | Frekuensi | Masa Retensi | Lokasi Penyimpanan |
|---|---|---|---|
| **Daily Dump** | Setiap hari pukul 02:00 | 14 hari | Penyimpanan lokal server + Object Storage (S3 / GCS) |
| **Weekly Snapshot** | Setiap Minggu pukul 03:00 | 8 minggu | Cloud Object Storage dengan *Object Lock* (WORM) |
| **Pre-Deployment** | Sebelum rilis versi baru | 30 hari | Folder arsip deployment |

---

## 6. Uji Coba Bencana Berkala (DR Drill)
Lakukan simulasi bencana setiap kuartal (3 bulan sekali):
1. Siapkan database PostgreSQL baru / terpisah (`docframe_drill`).
2. Jalankan `restore-db` dari dump terbaru ke `docframe_drill`.
3. Jalankan rangkaian tes:
   ```powershell
   $env:DOCFRAME_TEST_PG="Host=127.0.0.1;Port=5432;Database=docframe_drill;Username=docframe;Password=docframe"
   dotnet run --project tests/Framework.Tests/Framework.Tests.csproj
   ```
4. Dokumentasikan durasi pemulihan untuk memastikan RTO tetap $< 30$ menit.

