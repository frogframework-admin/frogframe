# Perencanaan Kompensasi (siklus merit dan bonus)

Siklus tahunan untuk kenaikan gaji (merit) dan bonus. HR-Comben menyiapkan siklus dan anggaran. Setiap atasan mengisi worksheet untuk bawahan langsungnya. HR menyetujui, lalu kenaikan gaji diterapkan sekaligus ke Salary Assignment.

## Alur

| Langkah | Siapa | Hasil |
|---|---|---|
| 1. Buat **Compensation Cycle** | HR-Comben | Perusahaan, siklus appraisal (sumber rating), tanggal berlaku, batas tanggal masuk, anggaran, matriks merit, dan pedoman bonus. Status Draft. |
| 2. **Generate worksheets** | HR-Comben | Satu worksheet per atasan (`reports_to`). Status siklus menjadi Planning dan atasan menerima notifikasi. |
| 3. Isi worksheet | Atasan | Atasan mengisi merit %, bonus, dan justifikasi per karyawan. Anggaran dan sisa dihitung langsung. |
| 4. **Submit to HR** | Atasan | Ditolak jika anggaran terlampaui atau ada usulan di luar pedoman tanpa justifikasi. |
| 5. **Approve** atau **Return to manager** | HR-Comben | Approve di atas anggaran wajib diisi HR comments. Return wajib diberi komentar. |
| 6. **Apply salary changes** | HR-Comben | Semua worksheet harus Approved. Salary Assignment baru dibuat per karyawan sejak tanggal berlaku, dan status siklus menjadi Applied. |

**Cancel cycle** tersedia selama status Draft atau Planning.

## Siapa yang ikut

Karyawan berstatus Active di perusahaan siklus yang:

- masuk pada atau sebelum "Eligible if hired on or before" (jika diisi),
- punya gaji pokok, yaitu komponen **Base salary component** di Payroll Settings pada Salary Assignment yang berlaku sehari sebelum tanggal berlaku,
- punya atasan (`reports_to`).

Pesan hasil Generate menyebut jumlah karyawan yang dilewati beserta alasannya. Generate bisa dijalankan lagi untuk menambahkan karyawan yang baru memenuhi syarat ke worksheet atasannya selama worksheet itu masih Draft.

## Perhitungan

- **Compa-ratio** = gaji pokok ÷ titik tengah rentang gaji grade (baris Grade › Pay ranges untuk perusahaan, berlaku pada tanggal berlaku). Pita: di bawah 0,90; 0,90–1,10; di atas 1,10.
- **Pedoman merit**: baris matriks untuk (rating, pita compa). Jika tidak ada, dipakai baris (rating, Any). Jika tidak ada juga, pedomannya 0–0–0. Rating diambil dari Appraisal yang sudah Completed di siklus appraisal; tanpa Appraisal, ratingnya "Not rated".
- **Merit %** awalnya diisi dengan target pedoman. Kenaikan = gaji pokok × merit %, dibulatkan ke satuan mata uang.
- **Bonus** dihitung sebagai % dari gaji pokok setahun (12 × gaji bulanan) per rating, dan awalnya diisi dengan target.
- **Di luar pedoman**: merit % di bawah minimum atau di atas maksimum, atau bonus di atas maksimum. Kondisi ini wajib diberi justifikasi sebelum Submit.
- **Anggaran worksheet**: merit = total gaji pokok bulanan × merit %, dan bonus = total gaji pokok setahun × bonus %. Persentasenya diambil dari baris **Budget per unit** untuk unit atasan atau unit induk terdekatnya. Jika tidak ada baris yang cocok, dipakai angka default siklus. Toleransi pembulatan 1.

Pedoman dan anggaran dihitung ulang setiap kali worksheet disimpan. Jadi perubahan matriks di siklus juga berlaku untuk worksheet yang masih terbuka.

## Aturan dan keamanan data

- Worksheet hanya bisa dibaca atasannya sendiri, HR-Comben, HR-Payroll, dan System Manager. Karyawan tidak bisa melihatnya.
- Atasan hanya bisa mengubah merit %, bonus, justifikasi, dan komentarnya. Gaji, rentang gaji, rating, dan daftar karyawan tetap seperti hasil Generate. Hanya HR yang bisa mengeluarkan karyawan dari worksheet.
- Worksheet hanya bisa diubah selama siklus berstatus Planning. Setelah Submit, atasan tidak bisa mengubahnya lagi sampai HR mengembalikannya.
- Status siklus hanya berubah lewat action. Siklus yang Applied atau Cancelled terkunci.

## Apply salary changes

Untuk setiap karyawan yang mendapat kenaikan, sistem membuat Salary Assignment baru mulai tanggal berlaku. Isinya salinan assignment yang sedang berlaku, dengan jumlah komponen gaji pokok diganti gaji baru. Potongan dan tanda BPJS ikut disalin. Nomor assignment baru dicatat di baris worksheet.

- Baris yang sudah diterapkan dilewati, sehingga action aman dijalankan ulang setelah masalah diperbaiki.
- Jika gaji pokok berubah sejak worksheet dibuat, misalnya karena Job Change, baris itu tidak diterapkan dan dilaporkan.
- Siklus menjadi Applied hanya jika semua baris berhasil diterapkan.

## Laporan

- **Compensation Budget by Unit** (`erp-compensation-budget`): anggaran, rencana, sisa, dan persentase terpakai untuk merit dan bonus per worksheet dan unit, termasuk jumlah usulan di luar pedoman. Filter unit mencakup unit di bawahnya.
- **Compensation Awards** (`erp-compensation-awards`): gaji lama dan baru, merit %, bonus, dan Salary Assignment yang dibuat per karyawan dari worksheet yang sudah Approved. Payroll memakai laporan ini untuk membayar bonus.

## Menu

Di grup Payroll: **Compensation Cycle** (HR-Comben) dan **Compensation Worksheet** (atasan dan HR).

## Batasan versi ini

- Bonus belum otomatis masuk ke payroll. Gunakan laporan Compensation Awards.
- Belum ada promosi atau penyesuaian grade di dalam worksheet. Gunakan Job Change.
- Belum ada worksheet berjenjang (atasan dari atasan melihat dan menyetujui seluruh cabang di bawahnya). Persetujuan saat ini langsung oleh HR-Comben.
- Belum ada surat pemberitahuan kenaikan gaji (compensation statement) untuk karyawan.
- Mata uang dianggap satu per perusahaan. Konversi kurs belum didukung.
- Label dalam bahasa Indonesia belum dimasukkan ke file terjemahan.
