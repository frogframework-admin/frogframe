# Skenario Uji ERP (tenant `acme`, Januari–Februari 2026)

Dokumen ini memandu uji menyeluruh memakai data seed fase 1–8. Semua transaksi seed berstatus **draft**: buku besar, stok, dan
open item baru terbentuk saat dokumen di-submit/disetujui di aplikasi. Angka "Hasil yang diharapkan" dihitung oleh
`python3 tools/scenario_expected.py` dari file seed yang sama. Jalankan ulang skrip itu bila seed diubah; skrip gagal (exit 1)
bila angka tetap di seed fase 8 tidak lagi cocok.

## 1. Persiapan

1. Muat seed berurutan ke database kosong (setelah migrasi):
   ```bash
   for n in 1 2 3 4 5 6 7 8; do psql "$ERP_DB" -v ON_ERROR_STOP=1 -f deploy/sql/seed-phase-$n.sql; done
   ```
   Fase 6–8 juga memajukan nomor seri (`frogframe_series`) agar dokumen baru tidak bertabrakan dengan nama seed.
2. Cek pengaturan sekali (sudah terisi dari seed, tidak perlu disimpan ulang):
   - **Payroll Settings**: tabel PTKP/TER/Pasal 17, komponen *Gaji Pokok*/*Uang Lembur*, akun 6100/6110/2130/2150/2140/2141.
   - **Stock Settings**: gudang default *Gudang Utama Cikarang*, akun 1131/5100/5200, GRNI 2170.
   - **Finance Settings**: PPN default `PPN-12`/`PPN-MASUKAN-12`, batas kredit = *Warn*, 3-way match = *Warn*.
3. Semua tanggal skenario ada di Januari–Februari 2026 dan periode akuntansi 2026 terbuka. Saat membuat dokumen baru (Payment Entry
   uang muka/komisi, Job Offer), **isi tanggal posting sesuai skenario**; default tanggal adalah hari ini.
4. Kerjakan langkah stok **berurutan menurut tanggal** (STE-1 → SINV-1 → GRN-1 → SINV-2 → STE-2 → SCN-1 → SOP-1). Valuasi rata-rata
   bergerak menolak transaksi mundur tanggal sebelum transaksi stok yang sudah ada.

### Pengguna demo (kata sandi `FrogFrame#2026`)

| User | Karyawan | Peran | Atasan (approver) |
|---|---|---|---|
| `admin` | – | System Manager | – |
| `it01` | EMP-0001 Ahmad Fadilah | Employee | it02 |
| `it02` | EMP-0003 Budi Santoso | Employee, Manager | hr02 |
| `marketing01` | EMP-0002 Citra Dewi | Employee, Sales | marketing02 |
| `marketing02` | EMP-0004 Dian Pratama | Employee, Manager, Sales | hr02 |
| `finance01` | EMP-0005 Eko Prasetyo | Employee, Finance | hr02 |
| `purchasing01` | EMP-0006 Fajar Hidayat | Employee, Purchasing | hr02 |
| `warehouse01` | EMP-0010 Indra Kusuma | Employee, Stock | hr02 |
| `hr01` | EMP-0009 Hendra Gunawan | Employee, HR | hr02 |
| `hr02` | EMP-0008 Gita Pertiwi | Employee, HR, Manager, Payroll | – |

### Isi seed

| Fase | Isi |
|---|---|
| 1 | Mata uang, UOM, bank, shift, kalender libur 2026, Payroll Settings (tabel pajak + akun & komponen) |
| 2 | Bagan akun (termasuk 1123 uang muka karyawan, 1124 piutang karyawan, 2152 PPh 23, 2170 GRNI, 2180 utang komisi, 6410–6600, 8110, grup 8500), cost center, tahun buku |
| 3 | Perusahaan ACME & GLOBEX |
| 4 | Organisasi, posisi, 9 karyawan (PTKP, NIK/NPWP, cost center, approver, rekening bank), pelanggan & pemasok (NPWP 16 digit, PKP, termin, plafon kredit, rekening), gudang, item, tax code |
| 5 | Harga, komponen & penugasan gaji 9 karyawan, Global/Accounting/Stock/CRM/HR Settings |
| 6 | Alur dasar (draft): saldo awal, stok awal, penawaran → SO → faktur → pelunasan, PR → PO → GR → faktur pemasok → pembayaran, absensi, cuti |
| 7 | Termin, sumber lead, alasan kalah, wilayah, kampanye, rencana komisi, lokasi, grade, siklus penilaian, jenis klaim, kategori aset, kurs, proyek, Finance Settings, alokasi cuti 2026 (submitted), kontrak kerja (submitted), posisi kosong POS-000011 |
| 8 | Transaksi skenario (draft): anggaran, jurnal, faktur & retur, PPh 23, transfer & opname, uang muka & klaim, lembur, payroll, payment run, komisi, aset & penyusutan, rekening koran, CRM, rekrutmen, penilaian, pelatihan |

## 2. Skenario

Format tiap langkah: **user** → aksi → hasil yang diharapkan. "Dr/Cr" = baris buku besar (lihat laporan *Trial Balance* atau tab ledger dokumen).

### A. Saldo awal

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| A1 | finance01 | Buka **JV-2026-000001** (Opening Balance), Submit | Dr 1111 50.000.000; Dr 1112 500.000.000; Dr 1121 75.000.000 (CUST-000001); Dr 1124 3.000.000 (EMP-0010); Dr 1210 30.000.000 / Cr 1211 7.500.000; Cr 2110 150.000.000 (SUPP-000001); Cr 3100 400.000.000; Cr 3200 100.500.000. Total 658.000.000 seimbang |
| A2 | warehouse01 | **STE-2026-000001** (Material Receipt), Submit | Stok Gudang Utama: laptop 5 @18.000.000, monitor 5 @4.800.000, kabel 8 @1.375.000. Dr 1131 / Cr 3200 125.000.000 |

Uji negatif: buat jurnal *Journal Entry* biasa ke akun 1121 → ditolak "control account; use invoices/payments, or an Opening Entry journal".

### B. Penjualan (order to cash)

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| B1 | marketing01 | **QTN-2026-000001**: *Send to customer* → *Customer accepted* | Status Accepted; total 58.000.000 + PPN 12% 6.960.000 = 64.960.000 |
| B2 | marketing01 | **SO-2026-000001**: Submit | Billing status *Not billed* |
| B3 | finance01 | **SINV-2026-000001** (Update stock): Submit | Dr 1121 64.960.000 / Cr 4100 58.000.000 / Cr 2151 6.960.000; HPP Dr 5100 / Cr 1131 45.600.000 (2×18 jt + 2×4,8 jt). SO jadi *Fully billed* |
| B4 | finance01 | **PAY-2026-000001** (Receive, 22-01): Submit | Dr 1112 / Cr 1121 64.960.000; SINV-1 *Paid* |
| B5 | finance01 | **SINV-2026-000002** (BCA, 26-01): Submit | Dr 1121 79.744.000 / Cr 4100 71.200.000 / Cr 2151 8.544.000; HPP 56.750.000 (3 laptop 54.000.000 + 2 kabel 2.750.000) |
| B6 | finance01 | **SCN-2026-00001** (retur 1 laptop, 29-01): Submit | Dr 4100 22.500.000, Dr 2151 2.700.000 / Cr 1121 25.200.000; laptop kembali 1 unit @18.000.000 (HPP faktur asal): Dr 1131 / Cr 5100 18.000.000. Sisa SINV-2 = 54.544.000 |
| B7 | finance01 | **SINV-2026-000003** (Astra, jasa 125.000.000): Submit | Pesan peringatan: piutang 140.000.000 melebihi plafon 100.000.000 (aksi *Warn* → tetap tersimpan). Dr 1121 140.000.000 / Cr 4200 125.000.000 / Cr 2151 15.000.000 |
| B8 | finance01 | **PAY-2026-000003** (10-02, 54.544.000): Submit | SINV-2 *Paid* |

Uji negatif: retur kedua 3 laptop atas SINV-2 setelah B8 → ditolak (open item faktur berbalik tanda).

### C. Pengadaan (procure to pay)

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| C1 | it01 | **PRQ-2026-00001**: *Submit for approval* | State *Pending Approval* |
| C2 | it02 | PRQ-2026-00001: *Approve* | State *Approved* (it01 tidak bisa menyetujui permintaannya sendiri) |
| C3 | purchasing01 | **PO-2026-000001**: Submit | PRQ *Ordered*; PO *Not received / Not billed*; total 35.000.000 + PPN 4.200.000 |
| C4 | warehouse01 | **GRN-2026-000001** (24-01): Submit | Server 1 unit masuk Gudang Utama @35.000.000. Dr 1131 / Cr 2170 GRNI 35.000.000. PO *Fully received* |
| C5 | finance01 | **PINV-2026-000001**: Submit | Baris dibebankan ke 2170 (GRNI). Dr 2170 35.000.000, Dr 1151 4.200.000 / Cr 2110 39.200.000 (SUPP-000001). 3-way match lolos. PO *Fully billed* |
| C6 | finance01 | **PAY-2026-000002** (28-01): Submit | Dr 2110 / Cr 1112 39.200.000; PINV-1 *Paid* |
| C7 | finance01 | **PINV-2026-000002** (JNE, jasa kirim + PPh 23): Submit | Dr 6420 2.500.000, Dr 1151 300.000 / Cr 2110 2.750.000 (SUPP-000003), Cr 2152 50.000 (PPh 23 2%) |

Uji negatif: faktur pemasok kedua dengan **No. tagihan** `INV-MDE-2026-00451` untuk Metrodata → ditolak (nomor tagihan unik per pemasok). Faktur PO-1 dengan *Update stock* dicentang → ditolak (sudah diterima lewat GR).

### D. Persediaan

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| D1 | warehouse01 | **STE-2026-000002** (transfer server ke Gudang Transit, 27-01): Submit | Stok server pindah ke Gudang Transit Jakarta @35.000.000. Akun persediaan sama → tanpa jurnal bersih |
| D2 | warehouse01 | **SOP-2026-00001** (opname 31-01): Simpan | Kabel: sistem 6 box (8.250.000) → hitung 5 → selisih −1 / −1.375.000; monitor sistem 3 = hitung 3 (selisih 0) |
| D3 | warehouse01 | SOP-2026-00001: Submit | Dr 5200 / Cr 1131 1.375.000 |

Hasil *Stock Balance* per 31-01-2026: Gudang Utama — laptop 1 (18.000.000), monitor 3 (14.400.000), kabel 5 (6.875.000); Gudang Transit — server 1 (35.000.000). Total 74.275.000 = saldo akun 1131.

Uji negatif: Stock Entry *Material Issue* 10 laptop dari Gudang Utama → ditolak (stok hanya 1). Dokumen stok bertanggal sebelum 31-01 setelah D3 → ditolak (tanggal mundur).

### E. Uang muka, klaim biaya & anggaran

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| E1 | finance01 | **BUD-2026-00001** (IT, YTD, Warn) dan **BUD-2026-00002** (Sales, tahunan, Stop): Submit | Anggaran aktif |
| E2 | marketing01 | **ADV-2026-00001** (3.000.000): *Submit for approval* | *Pending Manager* |
| E3 | marketing02 | ADV-2026-00001: *Approve* | *Pending Finance* |
| E4 | finance01 | ADV-2026-00001: *Approve* | *Approved*. Dr 1123 / Cr 2110 3.000.000 (EMP-0002) |
| E5 | finance01 | **Payment Entry baru**: Pay, Employee EMP-0002, bank BCA-OPS-01, tanggal **09-01-2026**, referensi EmployeeAdvance ADV-2026-00001 3.000.000 → Submit | Dr 2110 / Cr 1112 3.000.000. ADV status *Paid* |
| E6 | marketing01 | **EXP-2026-000001**: lampirkan bukti di tiap baris, *Submit for approval*; marketing02 *Approve*; finance01 *Verify* | Dr 6400 3.900.000 (CC-200) / Cr 2110 900.000 (EMP-0002) / Cr 1123 3.000.000. ADV *Completed* |
| E7 | it01 → it02 → finance01 | **EXP-2026-000002** (lampirkan bukti baris perjalanan dinas) sampai *Verify* | Pesan anggaran: BUD-2026-00001 untuk 6400/CC-400 terlampaui (aktual 0 + 3.000.000 > anggaran YTD Januari 1.000.000) → tetap disetujui. Dr 6400 3.000.000, Dr 6420 250.000 / Cr 2110 3.250.000 (EMP-0001) |
| E8 | marketing01 → marketing02 → finance01 | **EXP-2026-000003** (jamuan 2.500.000) sampai *Verify* | **Ditolak** saat Verify: BUD-2026-00002 6410/CC-200 terlampaui (2.500.000 > 2.000.000, aksi Stop). Finance lalu *Reject* |

Uji negatif: *Submit for approval* EXP-2026-000001 tanpa lampiran → "Perjalanan Dinas requires a receipt". Baris klaim *Transportasi Lokal* 1.500.000 → melebihi batas 1.000.000 per baris. Atasan menyetujui klaimnya sendiri → transisi tidak tersedia.

### F. Cuti & lembur

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| F1 | it01 | **LV-2026-000001** (Annual Leave 19–20 Jan): *Submit for approval* | Total 2 hari kerja, saldo sebelum 12 (alokasi LAL-2026-000001) |
| F2 | it02 | LV-2026-000001: *Approve* | Laporan *Leave Balance* EMP-0001: Annual Leave 12, terpakai 2, sisa 10 |
| F3 | warehouse01 | **OT-2026-000001** (22-01, hari kerja, 3 jam): *Submit for approval* | Jam dibayar 5,5 (1 × 1,5 + 2 × 2), periode 2026-01 |
| F4 | hr02 | OT-2026-000001: *Approve* | *Approved* |

Uji negatif: cuti tahunan 15 hari untuk karyawan mana pun → ditolak (maks 12 hari berturut-turut / sisa saldo). Cuti bertumpuk dengan LV-1 → ditolak. Lembur hari kerja 5 jam → ditolak (maks 4 jam).

### G. Payroll Januari 2026

Prasyarat: F4 (lembur disetujui) selesai sebelum slip dibuat.

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| G1 | hr02 | **PR-2026-00001**: *Create payslips* | 9 payslip draft |
| G2 | hr02 | Periksa payslip (tabel di bawah), lalu *Submit payslips* | 9 payslip submitted |
| G3 | hr02 | PR-2026-00001: Submit | Total bruto 107.770.231; iuran pemberi kerja 8.414.778; PPh 21 5.508.352; neto 98.429.290. Jurnal di bawah |

| Karyawan | PTKP / TER | Bruto | Dasar BPJS | Bruto pajak | Tarif TER | PPh 21 | BPJS pegawai | Pemberi kerja | Potongan lain | **Neto** |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| EMP-0001 Ahmad | TK/0 / A | 8.500.000 | 6.500.000 | 8.795.100 | 1,75% | 153.914 | 260.000 | 665.600 | 0 | **8.086.086** |
| EMP-0002 Citra | TK/0 / A | 7.500.000 | 6.000.000 | 7.772.400 | 1,5% | 116.586 | 240.000 | 614.400 | 0 | **7.143.414** |
| EMP-0003 Budi | K/2 / B | 18.000.000 | 16.000.000 | 18.566.400 | 8% | 1.485.312 | 550.863 | 1.380.126 | 0 | **15.963.825** |
| EMP-0004 Dian | K/3 / C | 20.000.000 | 18.000.000 | 20.577.200 | 8% | 1.646.176 | 590.863 | 1.464.926 | 0 | **17.762.961** |
| EMP-0005 Eko | K/1 / B | 9.000.000 | 7.000.000 | 9.317.800 | 1,5% | 139.767 | 280.000 | 716.800 | 0 | **8.580.233** |
| EMP-0006 Fajar | K/0 / A | 8.000.000 | 6.500.000 | 8.295.100 | 1,5% | 124.426 | 260.000 | 665.600 | 0 | **7.615.574** |
| EMP-0008 Gita | TK/0 / A | 19.000.000 | 17.000.000 | 19.571.800 | 8% | 1.565.744 | 570.863 | 1.422.526 | 0 | **16.863.393** |
| EMP-0009 Hendra | TK/1 / A | 7.500.000 | 6.000.000 | 7.772.400 | 1,5% | 116.586 | 240.000 | 614.400 | 0 | **7.143.414** |
| EMP-0010 Indra | K/2 / B | 10.270.231 | 8.500.000 | 10.656.131 | 1,5% | 159.841 | 340.000 | 870.400 | 500.000 | **9.270.390** |

Catatan perhitungan:
- Dasar BPJS = komponen bertanda *BPJS base* (Gaji Pokok + Tunjangan Jabatan). JP dibatasi 11.086.300 dan BPJS Kesehatan 12.000.000, jadi
  karyawan dengan dasar > batas (EMP-0003, 0004, 0008) membayar JP 110.863 dan Kesehatan 120.000.
- Bruto pajak = pendapatan kena pajak + JKK 0,24% + JKM 0,30% + BPJS Kesehatan pemberi kerja 4%. PPh 21 = bruto pajak × TER kategori PTKP (dibulatkan ke bawah).
- Lembur EMP-0010: upah sebulan 8.500.000 ÷ 173 = 49.132,95/jam × 5,5 jam = **270.231** (baris *Uang Lembur*).
- EMP-0010 mencicil pinjaman 500.000 (potongan *Potongan Pinjaman Karyawan*, akun 1124).

Jurnal PR-2026-00001 (28-01-2026):

| Akun | Debit | Kredit |
|---|---:|---:|
| 6100 Beban gaji (CC-110 10.270.231; CC-200 27.500.000; CC-300 43.500.000; CC-400 26.500.000) | 107.770.231 | |
| 6110 Beban BPJS pemberi kerja (per cost center) | 8.414.778 | |
| 2130 Utang gaji (per karyawan, = neto) | | 98.429.290 |
| 2150 Utang PPh 21 | | 5.508.352 |
| 2140 Utang BPJS Ketenagakerjaan (JHT + JP + JKK + JKM, pegawai & pemberi kerja) | | 7.922.367 |
| 2141 Utang BPJS Kesehatan | | 3.825.000 |
| 1124 Piutang karyawan (EMP-0010) | | 500.000 |
| **Jumlah** | **116.185.009** | **116.185.009** |

Uji negatif: submit PR sebelum semua payslip submit → ditolak. Payroll Run kedua untuk 2026-01 → ditolak (sudah ada). Ubah absensi EMP-0001 di Januari setelah payslip submit → ditolak (bulan terkunci).

### H. Payment Run

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| H1 | finance01 | **PRUN-2026-00001** (30-01, jatuh tempo s.d. 28-02): *Get open items* | 12 baris: 9 gaji (PayrollRun), EXP-2026-000001 900.000, EXP-2026-000002 3.250.000, PINV-2026-000002 2.750.000; rekening tujuan terisi dari data karyawan/pemasok. Total 105.329.290 |
| H2 | finance01 | Submit | 10 Payment Entry (satu per pihak) dibuat & di-submit otomatis. Mis. Citra 8.043.414 (gaji 7.143.414 + klaim 900.000), Ahmad 11.336.086 (8.086.086 + 3.250.000), JNE 2.750.000. Saldo 2130 dan utang klaim/pemasok jadi 0 |

Laporan *Bank Transfer List* menampilkan daftar transfer run ini.

### I. Aset tetap

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| I1 | finance01 | **AST-2026-00001** (aset lama, 30.000.000, akumulasi awal 7.500.000): Simpan | Jadwal 36 bulan × 625.000 mulai 31-01-2026 (sisa umur 48 − 12 bulan) |
| I2 | finance01 | Submit | Status *Active*, tanpa jurnal (perolehan sudah ada di saldo awal) |
| I3 | finance01 | **DEP-2026-00001** (periode 2026-01): Simpan → Submit | Baris AST-2026-00001 625.000. Dr 6300 (CC-400) / Cr 1211 625.000 |

Uji negatif: Cancel AST-2026-00001 setelah I3 → ditolak (sudah disusutkan). Depreciation Run kedua untuk 2026-01 → tidak ada baris jatuh tempo.

### J. Komisi penjualan

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| J1 | finance01 | **CMS-2026-00001** (Citra, 2026-01): Simpan | Baris: SINV-1 58.000.000 → 1.160.000; SINV-2 71.200.000 → 1.424.000; SCN-1 −22.500.000 → −450.000. Dasar 106.700.000 × 2% = **2.134.000** |
| J2 | finance01 | Submit | Dr 6500 (CC-200) / Cr 2180 2.134.000 (EMP-0002) |
| J3 | finance01 | Payment Entry baru: Pay, Employee EMP-0002, tanggal 13-02-2026, referensi CommissionStatement CMS-2026-00001 2.134.000 | Dr 2180 / Cr 1112 2.134.000 |

### K. Bank & pajak

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| K1 | finance01 | **JV-2026-000002** (admin bank 25.000): Submit | Dr 8400 (CC-300) / Cr 1112 25.000 |
| K2 | finance01 | **BST-2026-00001** (rekening koran BCA Januari): Submit | Saldo awal 500.000.000, mutasi masuk 64.960.000, keluar 147.554.290, saldo akhir **417.405.710** = saldo akun 1112 per 31-01-2026 |
| K3 | finance01 | Bank Reconciliation baru (BCA-OPS-01, Januari): cocokkan 14 baris dengan PE/JV | Semua baris *Matched*, selisih 0 |
| K4 | finance01 | **JV-2026-000003** (10-02, setoran BPJS & pajak): Submit | Dr 2140 7.922.367, Dr 2141 3.825.000, Dr 2150 5.508.352, Dr 2152 50.000 / Cr 1112 17.305.719. Saldo 2140/2141/2150/2152 jadi 0 |

### L. CRM

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| L1 | marketing01 | **LEAD-2026-000001** (Qualified): *Convert to customer* | Customer, kontak & opportunity baru dibuat (CRM Settings: *create opportunity on convert*); lead *Converted* |
| L2 | marketing01 | **LEAD-2026-000002**: status *Unqualified* tanpa alasan → Simpan | Ditolak (alasan wajib). Isi *No budget* → tersimpan |
| L3 | marketing01 | **OPP-2026-000001**: ubah tahap ke *Negotiation*, lalu *Closed Lost* tanpa alasan | Probabilitas ikut tahap (50% → 80%); Closed Lost wajib *Lost reason* |
| L4 | marketing01 | **ACT-2026-000001**: status *Completed* tanpa hasil → Simpan | Ditolak (outcome wajib) |
| L5 | marketing02 | **CON-2026-00001** (kontrak maintenance Telkom): Submit | Status kontrak aktif mulai 01-02-2026 |
| L6 | marketing02 | Laporan *Sales Pipeline* | OPP-2026-000001 290.000.000 pada tahapnya; kampanye CMP-2026-0001 |

### M. Talenta (rekrutmen, kinerja, pelatihan)

| # | User | Aksi | Hasil yang diharapkan |
|---|---|---|---|
| M1 | hr01 | **REQ-2026-00001** (Sales Staff, POS-000011): Submit | Requisition *Open* |
| M2 | hr01 | **APP-2026-000001**: status *Interview*; **INT-2026-000001** hasil *Pass* + feedback | Interview tanpa feedback dengan hasil Pass → ditolak |
| M3 | hr01 | Job Offer baru untuk APP-2026-000001: gaji 7.000.000, mulai 02-03-2026, posisi POS-000011, grade G2, nomor karyawan `EMP-0011` → *Send to candidate* → *Candidate accepted* → *Create employee* | Employee EMP-0011 dibuat (data pelamar + jabatan penawaran), Salary Assignment *Gaji Pokok* 7.000.000 mulai 02-03-2026, pelamar *Hired*. Sebelum payroll Maret, lengkapi PTKP & cost center EMP-0011 (tanpa PTKP payslip ditolak) |
| M4 | it01 → it02 | **APR-2026-00001**: isi skor diri (1–5) → *Submit self review*; it02 isi skor manajer → *Complete review* | Rating dari skor atasan tertimbang (40/30/30); *Complete review* hanya bila semua skor atasan terisi dan siklus *In progress* |
| M5 | hr01 | **TRN-2026-00001**: tandai kehadiran (Ahmad & Budi *Attended*, Indra *Absent*) → Submit | Jumlah hadir 2 |

Uji negatif: APP-2026-000002 status *Rejected* tanpa alasan → ditolak. *Create employee* untuk pelamar tanpa jenis kelamin/tanggal lahir → gagal dengan pesan.

### N. Laporan akhir

Setelah langkah A–K (bulan Januari saja; sebelum K4, B8, J3), *Trial Balance* per 31-01-2026:

| Akun | Debit | Kredit |
|---|---:|---:|
| 1111 Kas Kecil | 50.000.000 | |
| 1112 Bank BCA Operasional | 417.405.710 | |
| 1121 Piutang Usaha | 269.544.000 | |
| 1124 Piutang Karyawan | 2.500.000 | |
| 1131 Persediaan | 74.275.000 | |
| 1151 PPN Masukan | 4.500.000 | |
| 1210 Peralatan Kantor & IT | 30.000.000 | |
| 1211 Akum. Penyusutan | | 8.125.000 |
| 2110 Hutang Usaha | | 150.000.000 |
| 2140 Hutang BPJS TK | | 7.922.367 |
| 2141 Hutang BPJS Kesehatan | | 3.825.000 |
| 2150 Hutang PPh 21 | | 5.508.352 |
| 2151 PPN Keluaran | | 27.804.000 |
| 2152 Hutang PPh 23 | | 50.000 |
| 2180 Hutang Komisi | | 2.134.000 |
| 3100 Modal Disetor | | 400.000.000 |
| 3200 Laba Ditahan | | 225.500.000 |
| 4100 Penjualan Produk | | 106.700.000 |
| 4200 Pendapatan Jasa | | 125.000.000 |
| 5100 HPP | 84.350.000 | |
| 5200 Selisih Stok | 1.375.000 | |
| 6100 Beban Gaji | 107.770.231 | |
| 6110 Beban BPJS Pemberi Kerja | 8.414.778 | |
| 6300 Beban Penyusutan | 625.000 | |
| 6400 Beban Perjalanan Dinas | 6.900.000 | |
| 6420 Beban Transportasi & Pengiriman | 2.750.000 | |
| 6500 Beban Komisi | 2.134.000 | |
| 8400 Beban Admin Bank | 25.000 | |
| **Jumlah** | **1.062.568.719** | **1.062.568.719** |

- *Profit and Loss* Januari 2026: pendapatan 231.700.000 − HPP & selisih stok 85.725.000 − beban 128.619.009 = **laba 17.355.991**.
- *Accounts Receivable Aging* per 31-01: CUST-000001 75.000.000 (saldo awal), CUST-000002 54.544.000, CUST-000003 140.000.000.
  Setelah B8 (10-02): CUST-000002 lunas.
- *Accounts Payable Aging*: SUPP-000001 150.000.000 (saldo awal); utang karyawan & JNE 0 setelah Payment Run.
- *Payroll Register* 2026-01 = tabel G; *VAT Register* Januari: keluaran 27.804.000 (setelah retur), masukan 4.500.000;
  *Withholding Tax Register*: PPh 23 JNE 50.000.
- *Budget vs Actual* 2026: CC-400 akun 6400 aktual 3.000.000 vs anggaran 12.000.000; CC-200 akun 6500 aktual 2.134.000 vs 30.000.000.
- Setelah K4, B8, J3: saldo 1112 = 452.509.991; akun 2140/2141/2150/2152/2180 = 0.

## 3. Kalau hasil berbeda

1. Jalankan `python3 tools/check_seed.py` (data seed vs metadata & aturan dasar) dan `python3 tools/scenario_expected.py` (angka skenario).
2. Bandingkan baris jurnal dokumen dengan rincian per langkah dari `scenario_expected.py` (dicetak per voucher, urut tanggal).
3. Pastikan urutan langkah stok mengikuti tanggal dan semua prasyarat (anggaran, alokasi cuti, persetujuan lembur) sudah selesai.
