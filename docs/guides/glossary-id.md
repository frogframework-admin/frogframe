# Kamus Istilah ERP (Indonesia – Aplikasi)

Pengguna bertanya dalam bahasa Indonesia, sedangkan nama DocType, field, dan menu di aplikasi berbahasa Inggris.
AI Assistant memakai tabel ini untuk memperluas pencarian: bila pertanyaan memuat istilah di kolom pertama, kata di kolom
kedua ikut dicari. Kolom pertama boleh berisi beberapa istilah dipisah koma; kolom kedua berisi nama DocType/istilah aplikasi.
Tambahkan baris baru bila AI gagal menemukan jawaban karena perbedaan istilah.

## Akuntansi & keuangan

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| jurnal, jurnal umum, jurnal memorial, voucher jurnal | JournalEntry Journal Entry | Jurnal manual; saldo awal pakai jenis Opening Balance |
| saldo awal, jurnal pembukaan | JournalEntry Opening Balance | Satu-satunya jurnal manual yang boleh ke akun kontrol (piutang/utang) |
| bagan akun, daftar akun, coa, perkiraan | Account Chart of Accounts | |
| akun kontrol, akun piutang, akun utang | Account is_control_account Receivable Payable | Hanya diisi lewat faktur/pembayaran |
| periode akuntansi, periode, tutup periode, kunci periode | AccountingPeriod | Status Open/Closed, kunci subledger |
| tahun buku, tahun fiskal | FiscalYear | |
| tutup buku, tutup tahun | PeriodClosingVoucher Period Closing Voucher | Nolkan akun laba rugi ke laba ditahan |
| pusat biaya, cost center, departemen biaya | CostCenter | Wajib untuk akun laba rugi |
| buku besar, gl, ledger | General Ledger ledger gl_entries | Terbentuk saat dokumen di-submit |
| neraca saldo | Trial Balance | Laporan erp-trial-balance |
| laba rugi, rugi laba | Profit and Loss | |
| neraca, posisi keuangan | Balance Sheet | |
| anggaran, budget | Budget BudgetAccount Budget vs Actual | Aksi Warn atau Stop |
| kurs, nilai tukar | CurrencyExchangeRate Exchange Rate | |
| mata uang, valas | Currency | |
| proyek | Project AccountingDimension | |
| pengaturan akuntansi, tanggal mundur, posting masa depan | AccountingSettings | |
| pengaturan keuangan, batas kredit | FinanceSettings credit_limit_action | |
| pembayaran, pelunasan, bayar, terima uang, kas masuk, kas keluar | PaymentEntry Payment Entry | Receive/Pay |
| rekonsiliasi pembayaran, alokasi uang muka | PaymentReconciliation | |
| rekening koran, mutasi bank | BankStatement Bank Statement | |
| rekonsiliasi bank | BankReconciliation | |
| rekening bank perusahaan | BankAccount | |
| rekening bank pihak, rekening karyawan, rekening pemasok | PartyBankAccount bank_accounts | |
| pembayaran massal, transfer massal, payment run | PaymentRun Payment Run Bank Transfer List | |
| termin, termin pembayaran, jatuh tempo | PaymentTerm due_date InvoicePaymentSchedule | |
| umur piutang | Accounts Receivable Aging | |
| umur utang, umur hutang | Accounts Payable Aging | |

## Pajak

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| ppn, pajak pertambahan nilai, ppn keluaran, ppn masukan | TaxCode VAT vat_amount VAT Register | PPN 12% |
| faktur pajak, efaktur, e-faktur, coretax | TaxInvoice Tax Invoice | |
| pph 23, pph23, potongan pajak, pajak potong | TaxCode withholding wht_code Withholding Tax Register | |
| bukti potong, bupot | WithholdingCertificate | |
| pph 21, pph21, pajak gaji, ter | Payslip pph21 TerRate PayrollSettings | |
| ptkp, status pajak | ptkp_status PtkpRate | |
| npwp, nitku, pkp | tax_id nitku is_pkp | NPWP 16 digit |
| 1721 a1, spt tahunan karyawan | PPh 21 Annual Summary 1721-A1 | |

## Penjualan & CRM

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| pelanggan, customer, klien | Customer | |
| grup pelanggan | CustomerGroup | |
| penawaran, quotation, penawaran harga | Quotation | |
| pesanan penjualan, so, order penjualan | SalesOrder Sales Order | |
| faktur penjualan, invoice penjualan, tagihan ke pelanggan | SalesInvoice Sales Invoice | |
| retur penjualan, nota kredit | SalesCreditNote Sales Credit Note | |
| piutang, piutang usaha, tagihan | Receivable outstanding_amount Accounts Receivable Aging | |
| plafon kredit, limit kredit, batas kredit | credit_limit | |
| prospek, calon pelanggan | Lead | |
| peluang, opportunity, pipeline | Opportunity Sales Pipeline SalesStage | |
| aktivitas, kunjungan, meeting, follow up | Activity | |
| kampanye, promosi | Campaign | |
| wilayah, area penjualan | Territory | |
| kontrak pelanggan, kontrak langganan | CustomerContract | |
| komisi, komisi penjualan | CommissionPlan CommissionStatement | |
| sales, tenaga penjual | sales_rep | |
| daftar harga, harga jual | PriceList ItemPrice | |

## Pembelian & persediaan

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| pemasok, supplier, vendor | Vendor | |
| permintaan pembelian, pr, permintaan barang | PurchaseRequest Purchase Request | |
| pesanan pembelian, po, order pembelian | PurchaseOrder Purchase Order | |
| penerimaan barang, gr, grn, terima barang, barang masuk dari pemasok | GoodsReceipt Goods Receipt | |
| faktur pembelian, tagihan pemasok, invoice pemasok | PurchaseInvoice Purchase Invoice | |
| retur pembelian, nota debit | PurchaseDebitNote Purchase Debit Note | |
| utang, hutang, utang usaha, hutang usaha | Payable Accounts Payable Aging | |
| grni, barang diterima belum ditagih | grni_account StockSettings | |
| barang, item, produk | Item ItemGroup | |
| satuan | UOM | |
| gudang | Warehouse | |
| stok, persediaan, inventori | Stock Balance StockSettings inventory | |
| mutasi stok, transfer gudang, barang masuk, barang keluar, pemakaian barang | StockEntry Stock Entry Material Receipt Material Issue Material Transfer | |
| stock opname, opname, hitung fisik, penyesuaian stok | StockReconciliation Stock Reconciliation | |
| kartu stok | Stock Ledger | |
| hpp, harga pokok penjualan, cogs | cogs_account valuation_rate | Rata-rata bergerak |
| stok minus, stok negatif | allow_negative_stock | |

## Aset tetap

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| aset, aset tetap, inventaris kantor | Asset AssetCategory | |
| penyusutan, depresiasi | DepreciationRun AssetDepreciationSchedule | |
| pelepasan aset, jual aset, hapus aset | AssetDisposal | |

## SDM, absensi & payroll

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| karyawan, pegawai, staf | Employee | |
| struktur organisasi, bagan organisasi, unit kerja, departemen, divisi | OrganizationUnit Organization Tree | |
| jabatan, posisi | Position JobProfile | |
| golongan, grade, level | Grade GradePayRange | |
| mutasi karyawan, promosi, rotasi, perubahan jabatan | JobChange | |
| resign, berhenti kerja, pemutusan hubungan kerja, phk | EmployeeSeparation | |
| kontrak kerja, pkwt, pkwtt | EmploymentContract | |
| cuti, izin cuti, pengajuan cuti | LeaveApplication Leave Application | |
| jatah cuti, saldo cuti, alokasi cuti | LeaveAllocation Leave Balance | |
| jenis cuti | LeaveType LeavePolicy | |
| absensi, kehadiran | Attendance | |
| lembur | OvertimeRequest Overtime Request | |
| shift, jam kerja | ShiftType | |
| hari libur, kalender libur | HolidayList Holiday | |
| gaji, penggajian, payroll | PayrollRun Payslip Payroll Register | |
| slip gaji | Payslip | |
| komponen gaji, tunjangan, potongan | SalaryComponent | |
| struktur gaji, gaji karyawan | SalaryAssignment | |
| pengaturan payroll | PayrollSettings | |
| bpjs, bpjs ketenagakerjaan, bpjs kesehatan, jht, jp, jkk, jkm | PayrollSettings bpjs | |
| thr, bonus | SalaryComponent | Komponen tidak tetap |
| pinjaman karyawan, kasbon | SalaryComponent deductions Piutang Karyawan | |
| uang muka karyawan, panjar | EmployeeAdvance Employee Advance | |
| klaim, reimburse, penggantian biaya | ExpenseClaim Expense Claim ExpenseClaimType | |
| atasan, approver, persetujuan | leave_approver expense_approver reports_to workflow | |
| lowongan, permintaan karyawan baru | JobRequisition | |
| pelamar, kandidat | JobApplicant | |
| wawancara, interview | Interview | |
| penawaran kerja, offering | JobOffer | |
| penilaian kinerja, appraisal, kpi | Appraisal AppraisalCycle | |
| pelatihan, training | TrainingEvent | |

## Status & proses umum

| Istilah (Indonesia) | Di aplikasi (DocType / istilah) | Keterangan |
|---|---|---|
| draf, belum diposting, belum disubmit | Draft docstatus | Belum masuk buku besar/stok |
| submit, posting, kirim, ajukan | Submitted docstatus submit | |
| batal, pembatalan | Cancelled cancel | |
| setujui, approve, persetujuan | workflow transition Approve | |
| tolak, reject | Reject | |
| ditolak, gagal simpan, error, tidak bisa simpan | validation error | Gunakan explain_error dengan teks pesannya |
| tombol, tombol tidak muncul, tidak ada tombol, tombol hilang | form action IsVisible Tombol aksi | Lihat bagian Tombol aksi di docs/rules |
| laporan kosong, laporan nol | report Draft | Biasanya dokumen sumber masih draft |
| impor, unggah excel | Excel Import | |
| templat laporan, laporan word, laporan excel | Report Templates | |
