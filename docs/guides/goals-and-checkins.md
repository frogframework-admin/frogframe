# Goal (OKR) dan 1:1 Check-in

Fitur ini melengkapi Appraisal. Goal OKR berjalan terus sepanjang periode (kuartal atau semester), sedangkan Appraisal tetap menilai di akhir siklus. Check-in 1:1 adalah pertemuan rutin antara karyawan dan atasannya untuk membahas goal dan tindak lanjut.

## Goal (OKR) — `OkrObjective`

| Bagian | Isi |
|---|---|
| Identitas | Karyawan, atasan (`reports_to`, diisi otomatis dari data karyawan), periode `2026-Q1` / `2026-H1` / `2026-FY` |
| Level | Company, Department, Individual |
| Alignment | `parent_objective` harus berada di periode yang sama dan tidak boleh mengarah ke dirinya sendiri |
| Key result (`KeyResult`) | Teks, ukuran (Increase, Decrease, Milestone), nilai awal, target, nilai sekarang, bobot, keyakinan (On track / At risk / Off track) |
| Progres | Dihitung otomatis per key result, lalu dirata-rata berbobot untuk goal |
| Status | Draft → Active → Achieved / Missed / Cancelled |

**Cara menghitung progres per key result**

- **Increase**: (nilai sekarang − awal) ÷ (target − awal) × 100
- **Decrease**: (awal − nilai sekarang) ÷ (awal − target) × 100
- **Milestone**: 100 jika sudah selesai, 0 jika belum
- Hasil dibatasi antara 0 dan 100. Nilai di bawah awal dihitung 0, nilai melewati target dihitung 100.

**Aturan**

- Bobot semua key result harus berjumlah 100 sebelum goal menjadi Active, Achieved, atau Missed. Saat Draft, jumlahnya boleh belum 100.
- Goal yang sudah Achieved, Missed, atau Cancelled terkunci. Hanya HR-Talent yang bisa mengubahnya.
- Karyawan dan atasannya (lewat `reports_to`) bisa membuat dan mengubah goal. Rekan lain tidak bisa mengubahnya.
- **Goal terbuka untuk dibaca semua karyawan**, supaya staf bisa menyejajarkan goal-nya ke goal atasan, departemen, atau perusahaan. Jika perusahaan ingin goal individu tertutup, hapus aturan izin `Employee` / `read` / `all` di `OkrObjective.doctype.json`. Akibatnya staf hanya bisa menyejajarkan ke goal yang memang bisa ia baca.
- Goal level **Company** hanya bisa dibuat atau ditetapkan oleh HR-Talent.
- Alignment tidak boleh berputar (misalnya A → B → A).

## Check-in 1:1 — `OneOnOne`

| Bagian | Siapa yang bisa mengisi |
|---|---|
| Waktu pertemuan, status (Scheduled, Completed, Cancelled) | Karyawan dan atasannya |
| Agenda (bersama) | Karyawan dan atasannya |
| Catatan karyawan (`employee_notes`) | Hanya karyawan |
| Catatan atasan (`manager_notes`) | Hanya atasan |
| Goal yang dibahas (`OneOnOneObjective`) | Karyawan dan atasannya. Progres goal disalin saat check-in disimpan. |
| Action item (`OneOnOneAction`) | Karyawan dan atasannya. Penanggung jawab default adalah karyawan. |

**Aturan**

- Manager diambil dari `reports_to` karyawan saat check-in dibuat, lalu tidak berubah lagi. Jika atasan berubah, check-in lama tetap milik atasan lama.
- Check-in yang Completed atau Cancelled terkunci. HR-Talent tetap bisa mengoreksinya.
- Saat status berubah menjadi Completed, `completed_at` diisi otomatis.

## Laporan — "Goal & 1:1 Coverage" (`erp-goal-checkin`)

Per karyawan: jumlah goal aktif, rata-rata progres, tanggal 1:1 terakhir yang Completed, jumlah action item terbuka, dan jumlah yang sudah lewat tenggat. Filter periode berlaku untuk goal. HR-Talent, HR-Admin, dan administrator melihat semua karyawan. Pengguna lain melihat dirinya sendiri dan bawahan langsungnya.

## Menu

Di Talent Management: **Goals (OKR)** dan **1:1 Check-in**.

## Pengingat dan dashboard

**Pengingat harian** (`Erp.OneOnOneReminders`, setiap hari). Notifikasi masuk ke inbox dan dikirim sekali per item per hari:

- Karyawan dan atasan diberi tahu sehari sebelum dan pada hari 1:1 yang sudah dijadwalkan.
- Penanggung jawab action item diberi tahu pada tenggatnya, lalu setiap hari selama item masih terbuka dan sudah lewat tenggat.
- Setiap hari Senin, atasan diberi tahu tentang bawahan langsung yang perlu dijadwalkan 1:1 (lihat aturan di bawah). Hanya check-in dengan atasan saat ini yang dihitung.

**Widget "1:1 yang perlu dijadwalkan"** di halaman beranda, di panel tim atasan. Bawahan langsung ditampilkan jika:

- belum pernah ada 1:1 yang Completed, atau 1:1 terakhir lebih dari 30 hari lalu, **dan**
- tidak ada 1:1 yang dijadwalkan dalam 14 hari ke depan.

Widget mengurutkan dari yang paling lama tanpa 1:1, dan menampilkan jumlah action item yang terlambat serta goal aktif. Nilai 30 hari dan 14 hari ada di `CheckInRules` (`src/Apps.Erp/Home/CheckInDueService.cs`).

## Batasan versi ini

- Belum ada endpoint ESS (aplikasi mobile) untuk goal dan check-in.
- Goal OKR belum otomatis masuk ke penilaian Appraisal. Goal Appraisal (`AppraisalGoal`) tetap berdiri sendiri.
- Label dalam bahasa Indonesia untuk fitur ini belum lengkap di `framework.id.translations.json`. Hanya label widget yang sudah ditambahkan.
