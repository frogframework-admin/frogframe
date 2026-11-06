# DocFrame: Laporan Status Proyek & Roadmap Fitur

Dokumen ini memuat rekapitulasi status pengembangan DocFrame, mencakup fitur yang telah selesai, status pengujian, backlog fitur yang belum selesai (*gap analysis* terhadap kapabilitas platform seperti Frappe), serta rencana kerja berikutnya.

*Terakhir diperbarui: 5 November 2026*

---

## 1. Status Kesehatan Proyek Saat Ini

- **Status Kompilasi (`dotnet build`):** `0 Error`, `0 Warning kritis` di semua proyek (`Framework.Core`, `Framework.Persistence.PostgreSql`, `Framework.Web`, `Apps.Sample`, `Framework.Tests`).
- **Status Unit & Integration Test:** **110 Lulus, 0 Gagal** (33 test dengan atribut `PgTest` dilewati saat server PostgreSQL lokal tidak aktif).
- **Status Basis Data & Migrasi:** 23 migrasi PostgreSQL terdaftar dan teruji (`001_initial.sql` hingga `023_user_preferences.sql`).
- **Status Git Repository:** *Clean working tree* (Commit terakhir: `69ac2f3`).

---

## 2. Fitur yang Sudah Selesai (Completed)

### A. Fondasi Inti & Production Hardening (P0 - 100% Selesai)
- **Postgres Distributed Lock & Scheduler:** Penguncian terdistribusi atomik berbasis PostgreSQL advisory lock untuk mencegah eksekusi ganda pada cron job multi-tenant.
- **Data Protection & Enkripsi:** Penyimpanan *key ring* XML persisten di database PostgreSQL (`019_data_protection_keys.sql`).
- **Security & Pipeline Hardening:** Perlindungan rate limiting, anti-tamper form, mitigasi IDOR, sanitasi SQL injection, dan middleware security header (CSP, HSTS, X-Frame-Options).
- **Relational Integrity (Link Checking):** Validasi integritas referensial otomatis yang mencegah penghapusan dokumen induk jika masih ditautkan oleh dokumen lain atau child table.
- **Observability:** OpenTelemetry Tracing, Metrik aktivitas (Meters & Activities), dan Error Logging terpusat (`020_error_logs.sql`).
- **Backup & Disaster Recovery:** Dokumentasi runbook DR & load testing k6 baseline (`docs/backup-disaster-recovery.md`).

### B. UI/UX Modern & Desain
- **Corporate Blue Theme & Full Dark Mode:** Dukungan tema terang dan gelap penuh dengan sinkronisasi variabel CSS tema.
- **Layout & Navigasi:** Topbar sticky, navigasi dinamis berbasis hak akses, sidebar kolapsibel, breadcrumbs, footer range baris, dan ornamen visual gelombang.
- **Admin Drawers:** Komponen drawer standar dan varian **2x lebar** (`admin-drawer-2x`) untuk konfigurasi form kompleks tanpa berpindah halaman.

### C. Fitur Pilot & Produktivitas
1. **Attachments Multi-File per Dokumen (P1-1):**
   - Upload dokumen multi-file, inspeksi tipe MIME (*sniffing*), penyimpanan BLOB PostgreSQL aman, dan penegakan ACL.
2. **Email Outbox & Template Engine (P1-2):**
   - Antrean outbox email idempoten, *retry backoff*, dead-letter queue, dan template email dinamis.
3. **Forgot & Reset Password (P1-3):**
   - Alur reset kata sandi mandiri via tautan token email sekali pakai yang aman dan kedaluwarsa otomatis.
4. **Awesomebar / Global Search (`Ctrl+K` / `Alt+Q`):**
   - Pencarian universal cepat melintasi modul, menu navigasi, aksi cepat (*New record*), dan pencarian data dokumen langsung (*live search*) terisolasi per tenant (`AwesomebarModal.razor`, `GlobalSearchService.cs`).
5. **Notification Rules dari UI (P1-6):**
   - Manajemen aturan notifikasi tanpa koding via drawer 2x lebar: event trigger (*On Create*, *On Submit*, *On Value Change*, *Days Before/After Date*), kondisi ekspresi dinamis, pemilihan penerima (Role/Field/Owner), dan multi-kanal (In-App & Email) (`NotificationRulesAdmin.razor`, `022_notification_rules.sql`).
6. **Realtime Document Presence:**
   - Deteksi otomatis pengguna yang sedang membuka (*Viewing*) atau mengedit (*Editing*) dokumen yang sama dengan avatar indikator dan peringatan dini saat terjadi potensi konflik edit simultan (`DocumentPresenceBar.razor`, `DocumentPresenceService.cs`).
7. **Kustomisasi DataGrid per Pengguna (Resize & Kolom Visibility):**
   - Penyesuaian lebar kolom secara interaktif (*drag handle*), pemilih kolom tampil/sembunyi (*column visibility*), tombol *"Reset Kolom"*, dan persistensi otomatis per user & per DocType menggunakan `IUserPreferenceStore` dan tabel `023_user_preferences.sql` (`ListPage.razor`, `UserPreferences.cs`).

---

## 3. Fitur yang Belum Selesai (Backlog Roadmap)

### Prioritas 1 (P1 - Kebutuhan Harian & Operasional Pilot)

| # | Modul / Fitur | Deskripsi | Urgensi |
|---|---|---|:---:|
| **P1-4** | **Comments & @mention Timeline** | Kolaborasi internal langsung di bawah form dokumen (timeline percakapan berdampingan dengan audit trail) dengan fitur tagar `@user`. | **Tinggi** |
| **P1-5** | **Export List & Bulk Actions** | Ekspor data tabel ke file Excel (`.xlsx`) dan `.csv` (menghormati izin & scope user), serta aksi massal tambahan (*Bulk Submit*, *Bulk Cancel*, *Bulk Edit* selain Delete yang sudah ada). | **Tinggi** |
| **P1-7** | **Assign To / ToDo & Widget "Tugas Saya"** | Delegasi tugas dokumen ke staf/user tertentu, pelacakan tanggal batas waktu (*due date*), dan widget dashboard *"Tugas Saya"* untuk memantau pekerjaan yang tertunda. | **Sedang** |
| **P1-8** | **Saved Filters / Custom Views** | Pengguna dapat menyimpan kombinasi filter pencarian yang sering dipakai per DocType (memanfaatkan fondasi `IUserPreferenceStore` yang sudah siap). | **Sedang** |

### Prioritas 2 (P2 - Skalabilitas & Fleksibilitas Platform)

- **Custom Fields / Customize Form per Tenant:** Kemampuan menambah/mengubah urutan field DocType dari antarmuka web per tenant tanpa menyentuh file JSON asli.
- **Webhooks:** Mengirimkan muatan event dokumen dari outbox ke webhook URL eksternal (dilengkapi HMAC signature dan auto-retry).
- **Report Builder Ad-Hoc:** Pengguna non-teknis dapat menyusun kolom, sorting, dan pengelompokan (*group by*) sendiri dari data dokumen lalu menyimpannya sebagai laporan kustom.
- **User Permissions per Record (Row-Level Security Dinamis):** Hak akses spesifik per dokumen individual yang dapat dibagikan (*share*) ke pengguna/peran tertentu dari UI.
- **Autentikasi Lanjutan (SSO & 2FA):** Integrasi Google/Microsoft Entra ID (OIDC) dan autentikasi dua faktor berbasis TOTP app.
- **Generic Document Rename:** Fitur mengubah nomor/ID dokumen yang otomatis memperbarui seluruh relasi Link di child table maupun dokumen lain.
- **Background Jobs Ad-Hoc (`frappe.enqueue`):** Pemrosesan antrean tugas berat dengan indikator *progress bar* di antarmuka.

### Prioritas 3 (P3 - Tampilan Khusus & Nilai Tambah)

- **Alternate Views:** Kanban board (berdasarkan workflow status), Calendar view (berdasarkan tanggal), dan Tree view (bagan hierarki/CoA).
- **Visual Print Format Builder & Letter Head UI:** Editor kop surat dan template tata letak cetak dokumen visual.
- **Social Features:** Tagging warna-warni, bookmark/favorit, dan fitur *Follow Document* (menerima notifikasi otomatis jika ada pembaruan).
- **Multi-language (i18n):** Lokalisasi UI dinamis yang mudah dialihkan (Bahasa Indonesia & English).

---

## 4. Rekomendasi Rencana Kerja Selanjutnya

Urutan prioritas rekomendasi untuk fase pengembangan berikutnya:

1. **P1-5 — Export List ke Excel/CSV & Bulk Actions:**
   - Menyediakan tombol *Export to Excel/CSV* pada Toolbar `ListPage.razor`.
   - Menambahkan aksi massal: *Bulk Submit*, *Bulk Cancel*, dan *Bulk Edit Field*.
2. **P1-4 — Comments & @mention Timeline:**
   - Menambahkan panel komentar dan riwayat aktivitas terpadu di bagian bawah `FormPage.razor`.
   - Menambahkan notifikasi in-app otomatis saat pengguna di-mention (`@username`).
3. **P1-8 — Saved Filters / Custom Views:**
   - Memanfaatkan store `IUserPreferenceStore` yang sudah ada untuk menyimpan preset filter pengguna per DocType.

