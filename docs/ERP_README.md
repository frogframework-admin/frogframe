# frogframe ERP — Metadata + Hook (CORE, ACCOUNTING, FINANCE, HR, CRM)

146 DocType, 8 workflow, dan proyek C# `src/Apps.Erp` berisi hook, form action, handler event, dan job harian untuk semua modul, plus dua patch kecil framework.
Polanya mengikuti SAP S/4HANA (company code, chart of accounts bersama, universal journal, asset class),
NetSuite (segment, termin, period close per subledger, vendor bill/credit), Salesforce (Account, Contact, Pricebook, Lead, Opportunity, Quote, Task/Event), dan Workday (Supervisory Organization, Job Profile, Position, Worker, business process Change Job/Terminate, Time Off, Expense Report).

Salin semua `Metadata/*.doctype.json` dan `Metadata/*.workflow.json` ke folder `Metadata/` aplikasi (loader membaca satu folder, tidak rekursif).

## Isi

| Modul | DocType utama | Child | Single |
|---|---|---|---|
| CORE | Company, Currency, CurrencyExchangeRate, Location, Address, Contact, Customer, CustomerGroup, Vendor, VendorGroup, Item, ItemGroup, UOM, PriceList, ItemPrice, PaymentTerm, Bank, HolidayList | PartyLink, PaymentTermDetail, PartyBankAccount, Holiday | GlobalDefaults |
| ACCOUNTING | Account, CostCenter, Project, AccountingDimension, FiscalYear, AccountingPeriod, JournalEntry, PeriodClosingVoucher, Budget | JournalEntryLine, BudgetAccount | AccountingSettings |
| FINANCE | TaxCode, BankAccount, SalesInvoice, SalesCreditNote, PurchaseInvoice, PurchaseDebitNote, PaymentEntry, BankStatement, BankReconciliation, PaymentReconciliation, PaymentRun, PurchaseRequest, PurchaseOrder, GoodsReceipt, TaxInvoice, WithholdingCertificate, AssetCategory, Asset, DepreciationRun, AssetDisposal, ExpenseClaimType, ExpenseClaim, EmployeeAdvance | SalesInvoiceItem, PurchaseInvoiceItem, InvoicePaymentSchedule, PaymentEntryReference, PaymentEntryDeduction, BankStatementLine, BankReconciliationItem, PaymentReconciliationAllocation, PaymentRunItem, PurchaseRequestItem, PurchaseOrderItem, GoodsReceiptItem, AssetDepreciationSchedule, DepreciationRunItem, ExpenseClaimItem | FinanceSettings |
| HR | OrganizationUnit, JobFamilyGroup, JobFamily, JobProfile, Grade, Position, Employee, EmploymentContract, JobChange, EmployeeSeparation, ShiftType, LeaveType, LeavePolicy, LeaveAllocation, LeaveApplication | GradePayRange, PositionDottedLine, EmployeeIdentityDocument, EmployeeDependent, EmployeeEmergencyContact, EmployeeEducation, EmployeeWorkHistory, SeparationChecklistItem, LeavePolicyDetail | HRSettings |
| TIME, RECRUITING, TALENT (modul HR) | Attendance, OvertimeRequest, JobRequisition, JobApplicant, Interview, JobOffer, AppraisalCycle, Appraisal, TrainingEvent | AppraisalGoal, TrainingAttendee | – |
| PAYROLL (modul HR) | SalaryComponent, SalaryAssignment, Payslip, PayrollRun | SalaryDetail, PayslipDetail, PtkpRate, TerRate, TaxBracket | PayrollSettings |
| CRM | LeadSource, Industry, LostReason, SalesStage, Territory, Campaign, Lead, Opportunity, Quotation, SalesOrder, CustomerContract, CommissionPlan, CommissionStatement, Activity | OpportunityLine, OpportunityContactRole, QuotationItem (juga dipakai SalesOrder), CommissionPlanRep, CommissionLine | CRMSettings |

Workflow: `LeaveApproval` (cuti), `ExpenseClaimApproval` (klaim biaya: atasan → Finance), `EmployeeAdvanceApproval` (uang muka: atasan → Finance), `QuotationLifecycle` (penawaran: Draft → Sent → Accepted/Rejected/Expired → Billed), `OvertimeApproval` (lembur: approver cuti karyawan atau HR), `JobOfferLifecycle` (Draft → Sent → Accepted/Declined/Expired), `PurchaseRequestApproval` (approver biaya pemohon), `AppraisalReview` (Draft → Self review done → Completed).

Sejak batch HR, `Employee` juga menjadi jenis pihak di JournalEntryLine dan PaymentEntry (membayar klaim/uang muka), PaymentEntry bisa dialokasikan ke ExpenseClaim/EmployeeAdvance, dan `Asset.custodian` menjadi Link ke Employee.

Sejak batch CRM, Customer punya `industry` dan `account_owner` (sales pemilik akun), Contact/Address bisa dikelola role Sales, dan SalesInvoice punya Link `quotation` untuk jejak order-to-cash: Lead → Opportunity → Quotation → SalesInvoice → PaymentEntry.

## Keputusan desain

1. **Bagan akun dan cost center dipakai bersama oleh semua perusahaan dalam satu tenant** (pola SAP: satu chart of accounts untuk banyak company code). Pola penamaan frogframe hanya mengenal `{YYYY}`, `{MM}`, `{doctype}`, `{#}`, jadi kode akun "unik per company" tidak bisa dibuat. Konsekuensinya, TaxCode dan AssetCategory cukup menyimpan satu akun per kode, tanpa child per company.
2. **Setiap transaksi membawa field `company`.** Buku besar framework (`LedgerLine`) belum punya kolom company, jadi sebelum perubahan ledger: **satu perusahaan per tenant**. Setelah ledger punya company dan dimensi generik, multi-company langsung jalan tanpa mengubah DocType.
3. **Customer, Vendor, Contact, Address, Item** ada di CORE karena dipakai CRM, AR, AP, dan HR. Contact/Address memakai child `PartyLink` (DynamicLink), sehingga satu kontak bisa mewakili beberapa pihak.
4. **Data turunan tidak disimpan, kecuali cache yang jelas ditandai.** Saldo dan nilai buku dihitung dari ledger (World 2). `outstanding_amount` dan `payment_status` di faktur hanya cache untuk list view/aging; read-only dan diperbarui hook pembayaran.
5. **PPN per faktur, PPh per faktur dengan dasar per baris.** Satu kode PPN di header (praktik umum: satu faktur pajak per faktur). Baris menandai `is_vat_applicable` / `is_wht_applicable`; DPP dan dasar PPh dihitung deklaratif dari tanda itu. Faktor DPP di TaxCode menangani PPN 12% atas DPP nilai lain 11/12 (**verifikasi dengan aturan yang berlaku**).
6. **Nota kredit/debit adalah DocType terpisah** (seperti NetSuite Credit Memo / Vendor Credit), merujuk faktur asal dan memakai child item yang sama.
7. **Satu PaymentEntry** untuk Terima, Bayar, dan Transfer Internal. Potongan (PPh dipotong pelanggan, biaya bank) di child `deductions`; sisa yang belum dialokasikan menjadi uang muka.
8. **HR mengikuti pola Workday:** OrganizationUnit = supervisory organization (hierarki manajemen, bisa ditampilkan dengan `OrgTree`); JobProfile = jabatan generik; Position = slot yang diisi satu karyawan, dengan hierarki "melapor ke posisi" dan dotted line di child. Model staffing per unit: **Posisi** (Position wajib) atau **Job** (cukup JobProfile).
9. **Perubahan karyawan lewat event, bukan edit langsung.** JobChange (promosi/mutasi/rotasi/reorganisasi dalam satu DocType, seperti Workday Change Job) dan EmployeeSeparation menyimpan nilai sebelum (`current_*`, fetch) dan sesudah (`new_*`), lalu hook menerapkannya pada tanggal efektif. Histori = daftar event + track changes Employee.
10. **Data pribadi di `permLevel` 1** (tanggal lahir, agama, NIK, NPWP, PTKP, BPJS, keluarga, rekening, gaji di kontrak, hak pesangon, rentang gaji grade). Hanya HR, System Manager, dan karyawan itu sendiri yang bisa membacanya, sejalan dengan UU PDP.
11. **CRM mengikuti Salesforce:** Lead terpisah dari Customer dan dikonversi lewat aksi; Opportunity memakai SalesStage yang membawa probabilitas dan kategori forecast (Pipeline/Best Case/Commit/Closed/Omitted); nilai tertimbang = nilai × probabilitas. Kepemilikan record lewat `sales_rep` (Employee) dengan scope `match:sales_rep=employee`; manager melihat timnya lewat `match:organization_unit=department`. Pilihan seperti sumber lead, industri, dan alasan kalah dibuat DocType kecil agar admin bisa menambah tanpa developer.
12. **Semua DocType berbahasa Inggris** (judul, label, section/tab, deskripsi, pilihan Select, pesan rule, state & aksi workflow), begitu juga pesan error/notifikasi di hook C#. Nilai pilihan di C# sama persis dengan metadata dan dicek otomatis oleh `tools/check_option_literals.py`. Istilah hukum/pajak Indonesia tetap dipakai sebagai nama diri: PPN, PPh 21/23/4(2), NPWP, NIK, NITKU, PTKP, BPJS, PKWT/PKWTT, e-Faktur, Coretax. Komentar kode masih Bahasa Indonesia.

## Pola persetujuan atasan (tanpa kode)

frogframe menaruh atribut `employee` dan `department` pada user, dan guard workflow bisa membaca `user.employee`. Jadi persetujuan atasan langsung dibuat deklaratif:

1. Employee menyimpan `leave_approver` dan `expense_approver` (default = `reports_to`, diisi hook).
2. Dokumen pengajuan menyalin approver dari karyawan pemohon (`fetchFrom: employee.leave_approver`).
3. Transisi "Approve" memakai syarat `leave_approver == user.employee && employee != user.employee`.
4. Permission memberi role `Employee` dua scope: `match:employee=employee` (dokumen sendiri) dan `match:leave_approver=employee` (dokumen yang harus ia setujui).

Approver tidak perlu memegang role `Manager`; cukup ditunjuk di data karyawan, mirip penugasan role per organisasi di Workday.

**Syarat di akun user:** `EmployeeId` = `employee_number` karyawan (nama dokumen Employee), dan `Department` = kode OrganizationUnit (dipakai scope Manager `match:organization_unit=department`).

## Batasan runtime yang ditemukan (penting untuk hook)

- **`fetchFrom` tidak berjalan di child table.** `DocumentService.ApplyFetchFromAsync` hanya memproses field induk, walaupun compiler menerimanya. Karena itu tidak ada `fetchFrom` di child. Nama item, satuan, dan akun di baris faktur diisi lewat `addFromLookup` (tombol "Add item"); untuk baris yang diketik manual, hook `BeforeValidate` harus mengisinya. Validator di `tools/` menolak `fetchFrom` di child.
- **`compute` berjalan di child dan bisa membaca `parent.<field>`**, tetapi child dihitung sebelum header, jadi child hanya boleh memakai field header yang diisi pengguna (mis. `exchange_rate`), bukan field compute header.
- **Boolean dihitung 1/0** di aritmetika, jadi `amount * is_vat_applicable` aman. Perbandingan dengan nilai null melempar error, jadi aturan memakai `empty(...)` / `coalesce(...)` sebagai pengaman.
- **Rules dievaluasi setiap simpan, bukan hanya saat submit.** Syarat yang hanya berlaku saat submit (mis. selisih rekonsiliasi = 0) ada di hook.

## Kontrak dengan framework (jangan diubah namanya)

- `AccountingPeriod`: `status` dengan nilai `Open` / `Closed`, plus `start_date`, `end_date`. Dibaca store saat posting. Framework asli hanya mengenal `Terbuka`, jadi **patch 0002 wajib** (menerima `Open` dan `Terbuka`, sehingga Apps.Sample tetap jalan).
- `Account`: `is_group`, `is_active`. Dibaca store saat posting.
- Nama dokumen Account sekarang `field:account_code` (sample lama memakai `code`). Hook `AccountRulesHook` di sample merujuk field `code`, jadi sesuaikan bila hook itu dipakai ulang.

## Persiapan sebelum dimuat

- Tambahkan modul **CORE**, **FINANCE**, **CRM**, dan **STOCK** ke Master Modul (`NavigationMenuDefaults` bila mau di-seed). `ACCOUNTING` dan `HR` sudah ada.
- Role yang dipakai: `Finance`, `Purchasing`, `HR`, `Manager`, `Employee`, `System Manager` (sudah ada di `RoleDefaults`) dan **`Sales` serta `Stock` (baru, wajib dibuat dulu)**. Tanpa role itu, DocType yang memakainya gagal compile.
- Seed data minimal: Currency `IDR` (beberapa default menunjuk ke sana), FiscalYear, AccountingPeriod, bagan akun (termasuk akun bertipe `Receivable`, `Payable`, `Tax`, `Bank`, `Fixed Asset`, `Accumulated Depreciation`, `Depreciation Expense`, `Retained Earnings`), Company, TaxCode PPN & PPh, akun bertipe `Advance` dan `Payable` untuk karyawan, HolidayList, ShiftType, LeaveType, LeavePolicy, SalesStage (minimal satu tahap berjalan, satu `Won`, satu `Lost`), LeadSource.
- Pengaturan yang dibaca hook: GlobalDefaults (`default_company`), Company (akun default, `round_off_account`, `exchange_gain_loss_account`, `default_cost_center`), AccountingSettings, FinanceSettings (`default_output_vat`, `default_input_vat`), HRSettings (kalender libur, jadwal kerja, kebijakan cuti default), CRMSettings (`default_customer_group`, `default_sales_stage`). Akun bertipe `Advance` dipakai otomatis untuk uang muka karyawan bila akunnya kosong.

## Kode C# (`src/Apps.Erp`)

**Belum di-compile:** .NET SDK tidak tersedia di lingkungan pembuatnya. Lihat "Validasi & verifikasi" untuk apa yang sudah dicek dan langkah build.

### Pemasangan

```bash
cd <repo-frogframe>
git apply <erp>/patches/0001-ledger-get-balances.patch      # ILedgerReader.GetBalancesAsync (in-memory + PostgreSQL)
git apply <erp>/patches/0002-open-period-status-english.patch   # periode berstatus "Open" diterima selain "Terbuka"
cp -r <erp>/src/Apps.Erp src/ && dotnet sln add src/Apps.Erp/Apps.Erp.csproj
dotnet add src/<HostApp>/<HostApp>.csproj reference src/Apps.Erp/Apps.Erp.csproj
# Program.cs host: builder.Services.AddErpHooks();   (ganti registrasi hook Apps.Sample)
cp <erp>/Metadata/* src/<HostApp>/Metadata/ && dotnet build
```

**Patch framework.** Tutup buku tahunan dan rekonsiliasi bank butuh saldo akun per tanggal yang konsisten. `ILedgerReader` hanya membaca per voucher/open item, dan reporting executor membaca read replica (eventual, tidak ada di mode in-memory). Patch menambah `GetBalancesAsync(tenant, from, to)` beserta implementasi in-memory dan PostgreSQL (37 baris, 3 file). Hanya dua kelas yang mengimplementasikan `ILedgerReader`, jadi tidak ada yang rusak.

**Patch 0002.** Store in-memory dan PostgreSQL mencari periode posting dengan `status = 'Terbuka'` (hardcode). Patch menambah `Ledger.OpenPeriodStatuses = ["Open", "Terbuka"]` dan memakainya di kedua store (PostgreSQL: `data->>'status' = ANY($4)`). Pesan error tetap memuat "periode akuntansi yang Terbuka" agar test framework yang ada tetap lolos. Terapkan setelah 0001.

### Isi

| Area | File | Yang dikerjakan |
|---|---|---|
| Fondasi | `Common.cs` | Akses data aman, pembaca master via store (tidak terikat izin user), kurs, periode & kunci subledger, penyusun posting (gabung baris, penyeimbang pembulatan/kurs), pembalik, open item (termasuk per arah), event outbox, operasi dokumen sebagai user sistem, notifikasi |
| Akuntansi | `AccountingHooks.cs`, `AccountingExtraHooks.cs` | Aturan Akun/Tahun Buku/Periode; Jurnal (validasi + posting + pembalik); kurs unik; satu alamat/kontak utama per pihak; **Tutup Buku Tahunan** (nolkan akun laba rugi per cost center ke laba ditahan, tahun buku → `Closed`); **job jurnal pembalik otomatis** |
| AR/AP | `InvoiceHooks.cs` | Faktur & nota penjualan/pembelian: default dari master, total = compute metadata, jadwal jatuh tempo, PPN/PPh, kapitalisasi baris aset ke akun aset kategori, batas kredit, posting, status bayar saat dibuka; submit memicu **Faktur Pajak**, **Bukti Potong**, **Asset draft**, dan status penawaran **Billed** |
| Kas & bank | `PaymentHooks.cs`, `TaxBankHooks.cs` | Pembayaran (faktur, saldo awal, klaim & uang muka karyawan, potongan, uang muka, transfer, selisih kurs, tolak tagihan ditahan); **auto-match rekening koran** (jumlah, arah, ±3 hari, nomor referensi); **rekonsiliasi bank** (saldo buku dari ledger, voucher Payment Entry & Jurnal yang belum kliring, submit hanya bila selisih 0) |
| Pajak | `TaxBankHooks.cs` | Faktur Pajak (DPP, DPP nilai lain, PPN, masa, satu aktif per dokumen kecuali pengganti); Bukti Potong (kode, dasar, pihak dari faktur pembelian) |
| Aset | `AssetHooks.cs` | Jadwal penyusutan (garis lurus; saldo menurun ganda dengan pindah ke garis lurus; aset lama dengan akumulasi awal); Proses Penyusutan per periode (ambil jadwal jatuh tempo, posting per kategori & cost center); Pelepasan (nilai buku, wajib penyusutan sampai tanggal pelepasan, jurnal pelepasan) |
| Karyawan (keuangan) | `EmployeeFinanceHooks.cs` | Uang muka: disetujui → piutang uang muka + utang pencairan; Klaim: akun/batas/bukti, potong uang muka, disetujui → beban & utang klaim; status dihitung saat dibuka |
| HR | `HrHooks.cs` | Karyawan (turunan dari posisi, atasan dari hierarki posisi/unit, approver default, keluar); Posisi (pemangku & status dihitung dari karyawan); pergantian pimpinan unit memperbarui atasan; Perubahan Jabatan & Pemberhentian diterapkan per tanggal efektif (idempoten, berurutan; user dinonaktifkan); Kontrak (tidak tumpang tindih, total PKWT ≤ 60 bulan, status); Alokasi & Pengajuan Cuti (hari kerja dari jadwal & kalender libur, saldo, tumpang tindih, dicek ulang saat diajukan) |
| CRM | `CrmHooks.cs` | Lead (peringatan duplikat, hasil konversi); **aksi Konversi** (Customer + Contact + Opportunity); Opportunity (nilai dari produk, tanggal closing); Penawaran (dari opportunity, harga dari daftar harga, PPN default, diterima → opportunity `Won`); **aksi Buat Faktur** |
| Payment Run | `PaymentRunHooks.cs` | Aksi *Get open items* (faktur pemasok jatuh tempo, klaim & uang muka disetujui, gaji dari Payroll Run), validasi sisa utang & rekening tujuan, submit → satu Payment Entry per pihak lewat event (idempoten) |
| Absensi & lembur | `TimeHooks.cs` | Absensi (jam kerja, terlambat, tautan cuti, terkunci setelah payslip); lembur PP 35/2021 (pengali, batas 4 jam/hari kerja & 18 jam/minggu) dibayar di payslip |
| Rekrutmen | `RecruitmentHooks.cs` | Requisition (dari posisi Vacant, status dihitung), pelamar (unik per email), penawaran, aksi *Create employee* (Employee + Salary Assignment, pelamar → Hired) |
| Anggaran | `BudgetHooks.cs` | Kontrol anggaran Warn/Stop saat posting beban (faktur, jurnal, klaim, payroll, komisi), tahunan atau year-to-date |
| CRM lanjutan | `CrmExtraHooks.cs` | Territory, statistik Campaign, Sales Order (dari penawaran, status tagih), aksi *Create sales order* / *Create Sales Invoice*, sales rep di faktur, kontrak pelanggan (status & pengingat), komisi bulanan + jurnal akrual |
| Pengadaan | `ProcurementHooks.cs` | Purchase Request (approval, status pesan), Purchase Order (dari PR, termin & PPN default, status terima/tagih), Goods Receipt (sisa per baris PO), Purchase Invoice dari PO + 3-way match; aksi *Create purchase order* dan *Receive* |
| Kinerja & pelatihan | `TalentHooks.cs` | Appraisal (nilai diri vs atasan dijaga, rating dari skor tertimbang, selesai hanya saat siklus berjalan), Training Event (peserta unik, jumlah hadir) |
| Payroll | `PayrollHooks.cs` | Komponen & penugasan gaji, payslip (BPJS, PPh 21 TER / tahunan, DTP, gaji bersih), payroll run (aksi buat & submit payslip, jurnal payroll) |
| Persediaan | `StockHooks.cs` | Mesin stok rata-rata bergerak (saldo dari dokumen mutasi submit), Stock Entry, Stock Opname (+ aksi *Fetch items*), Goods Receipt ke gudang, faktur/nota dengan *Update stock* (HPP), akun persediaan/GRNI di faktur pembelian |
| Laporan | `Reports/ErpReports.cs` | 15 laporan di menu Reports (lihat bagian Laporan) |
| Lintas dokumen | `EventsAndJobs.cs` | Handler event outbox (idempoten) + job harian: penerapan HR per tanggal efektif, alokasi cuti tahunan (dengan carry forward), pengingat kontrak berakhir, penawaran kedaluwarsa, pengingat opportunity tanpa aktivitas |

### Pola desain yang dipakai (akibat aturan framework)

- **Master dibaca lewat `IDocumentStore`**, bukan `c.Docs`, karena `c.Docs` memakai izin user. Sales/Employee tidak boleh gagal menyimpan dokumen hanya karena tidak bisa membaca Account/Company.
- **Dokumen yang sudah submit tidak bisa diubah**, jadi status yang berubah setelah submit tidak disimpan. Status bayar faktur/klaim, realisasi uang muka, status kontrak, status aset, dan baris jadwal "sudah diposting" dihitung saat dibuka (OnLoad) dari ledger atau dokumen lain. Status penawaran memakai workflow, karena transisi workflow memang boleh pada dokumen yang sudah submit.
- **Field read-only ditolak saat update.** Field turunan (pemangku posisi, hasil konversi lead, tanggal keluar, tanggal tutup periode) diisi hook dokumen itu sendiri dari sumber kebenarannya; handler event cukup "menyentuh" dokumennya.
- **Efek ke dokumen lain lewat event outbox**, diproses setelah commit dengan retry; setiap cabang handler memeriksa dulu apakah efeknya sudah ada. Konsekuensinya, efek itu terjadi beberapa detik setelah dokumen asal tersimpan, bukan dalam transaksi yang sama.
- **Transisi workflow tidak membawa ToState ke AfterSave**, jadi posting saat disetujui dideteksi dari perubahan `workflow_state`.
- **Proyek belum ikut ke buku besar**, karena `LedgerLine` hanya punya `Department` (diisi cost center). Begitu `LedgerLine` punya dimensi generik, cukup ubah kelas `Posting`.

### Batasan yang tersisa

- Rekonsiliasi rekening valas dan transfer antar mata uang belum didukung (butuh kurs dua sisi + selisih kurs di PaymentEntry dan saldo valas di ledger; menunggu perubahan `LedgerLine` untuk valas).
- Alokasi uang muka valas lewat Payment Reconciliation ditolak; sementara pakai jurnal dengan selisih kurs.

### Ditutup di tahap 8

- **Konversi Lead dengan kompensasi.** Bila gagal di tengah, Opportunity → Contact → Customer yang sudah dibuat dihapus lagi sebagai user sistem (tautan `primary_contact` dilepas dulu karena framework menolak hapus dokumen yang masih ditautkan). Hanya bila penghapusan gagal, pesan menyebut dokumen yang tertinggal.
- **Payment Reconciliation** (DocType baru, `PREC-`): alokasi uang muka pelanggan/pemasok ke faktur tanpa mutasi kas. Submit membukukan akun kontrol yang sama dua kali: sisi pembayaran (against = PaymentEntry) dan sisi faktur (against = faktur). Hook mengecek pihak, perusahaan, akun kontrol, mata uang fungsional, dan total per pembayaran/faktur; penjaga `OpenItemViolation` framework juga menolak alokasi berlebih (diuji di skenario 9).
- **Faktur Pajak retur otomatis** untuk Sales Credit Note ber-PPN (setelan `auto_create_tax_invoice` yang sama). Field baru `TaxInvoice.return_of` diisi hook dengan faktur pajak aktif milik faktur asal.
- **`auto_create_user`**: karyawan baru dengan email kantor dibuatkan akun (id & username = email, role `Employee`, atribut employee & department) lewat event outbox; email yang sudah punya akun di tenant yang sama cukup ditautkan. Akun dibuat tanpa password, jadi karyawan memakai alur lupa password atau reset oleh admin.

## Payroll

Pola Workday Payroll / SAP PY-ID: **SalaryComponent** (earning/deduction; kena pajak, regular, dasar BPJS, akun) → **SalaryAssignment** per karyawan berlaku mulai tanggal → **PayrollRun** per perusahaan & masa dengan aksi *Create payslips* dan *Submit payslips* → **Payslip** per karyawan (dihitung hook) → submit run membukukan satu jurnal.

**Perhitungan payslip**
- **BPJS Ketenagakerjaan:** JHT 3,7% pemberi kerja + 2% pekerja; JKK 0,24–1,74% sesuai kelas risiko; JKM 0,3%; JP 2% + 1% dengan batas upah JP (Rp11.086.300 sejak Maret 2026). Dasar = komponen bertanda *BPJS base* (upah pokok + tunjangan tetap).
- **BPJS Kesehatan:** 4% pemberi kerja + 1% pekerja, plafon Rp12.000.000 (Perpres 59/2024; belum berubah per Oktober 2026), batas bawah UMK opsional.
- **PPh 21 Januari–November:** bruto × TER bulanan (PP 58/2023) menurut kategori PTKP (A: TK/0, TK/1, K/0; B: TK/2, TK/3, K/1, K/2; C: K/3). Bruto = pendapatan kena pajak + JKK + JKM + BPJS Kesehatan yang dibayar pemberi kerja (JHT/JP pemberi kerja tidak termasuk).
- **Masa terakhir** (Desember, atau bulan `separation_date`): bruto setahun − biaya jabatan (5%, maks Rp500.000 × bulan kerja) − JHT & JP pekerja − PTKP → PKP dibulatkan ke bawah ribuan → tarif Pasal 17 (5/15/25/30/35%) → dikurangi PPh yang sudah dipotong. Hasil negatif = lebih potong, dikembalikan lewat gaji Desember. Pegawai yang masuk di tengah tahun tidak disetahunkan.
- **PPh 21 DTP (PMK 105/2025, tahun 2026):** hanya bila `dtp_enabled` dicentang (pemberi kerja di sektor/KLU yang tercantum). Hook mengusulkan *DTP eligible* saat payslip dibuat bila pendapatan regular ≤ Rp10 juta dan karyawan punya NPWP/NIK; HR bisa menghapus centangnya. Bagian DTP dibayarkan tunai ke karyawan dan tidak disetor. Kelayakan resmi diuji pada gaji Januari 2026 (atau bulan pertama bekerja); hook hanya memakai gaji bulan berjalan, jadi periksa manual untuk karyawan yang gajinya naik di tengah tahun.
- Tabel PTKP, TER (A/B/C), dan Pasal 17 diisi otomatis saat Payroll Settings pertama kali disimpan, lalu bisa diubah di layar itu bila aturan berubah. Tarif BPJS dan plafon ada sebagai default field. **Periksa ulang setiap awal tahun** (batas upah JP berubah tiap Maret; insentif DTP 2027 baru direncanakan).

**Jurnal Payroll Run:** Dr beban gaji per komponen & cost center, Dr beban BPJS pemberi kerja; Cr utang gaji per karyawan (against = Payroll Run, dibayar lewat Payment Entry tipe Pay ke Employee dengan referensi PayrollRun), Cr utang PPh 21 (setelah DTP), Cr utang BPJS TK, Cr utang BPJS Kesehatan, Cr akun komponen potongan. Kunci periode `lock_payroll` berlaku.

**Belum ditangani:** proporsi gaji karyawan masuk/keluar di tengah bulan dan cuti tanpa upah (ubah nominal di payslip sebelum submit); tarif 20% lebih tinggi untuk penerima tanpa NPWP; pegawai tidak tetap/harian (TER harian); bukti potong A1 & file e-Bupot; Payment Run massal (bayar per karyawan masih satu Payment Entry).

## Modul P2 lainnya (tahap 11)

**Payment Run** (SAP F110 / NetSuite Pay Bills): pilih rekening sumber, tanggal, dan cakupan, lalu *Get open items*. Baris bisa dihapus atau jumlahnya dikurangi. Submit membuat dan men-submit satu Payment Entry per pihak (`payment_run` terisi, idempoten bila event diulang). Run tidak bisa dibatalkan selama Payment Entry-nya masih ada. Payment Entry kini menerima referensi `PayrollRun` dan `CommissionStatement`. File bulk transfer berbeda tiap bank, jadi laporan *Bank Transfer List* diekspor CSV lalu disesuaikan.

**Absensi & lembur** (PP 35/2021): upah sejam = 1/173 upah sebulan; upah sebulan = komponen regular bertanda dasar BPJS (upah pokok + tunjangan tetap), atau 75% total pendapatan regular bila lebih kecil (Pasal 32 ayat 4). Pengali Pasal 31: hari kerja 1,5× jam pertama lalu 2×; hari istirahat 6 hari kerja 7 jam 2×, jam ke-8 3×, jam 9–11 4×; libur di hari kerja terpendek 5/1/3 jam; 5 hari kerja 8/1/3 jam. Batas 4 jam per hari kerja dan 18 jam per minggu. Lembur *Approved* dimasukkan ke payslip bulan itu sebagai komponen `overtime_component` (Payroll Settings). Jabatan tertentu yang tidak berhak lembur (Pasal 27) cukup tidak diajukan. Absensi tidak memotong gaji otomatis; cuti tanpa upah tetap diatur manual di payslip.

**Rekrutmen**: Job Requisition (submit = disetujui) → Job Applicant (data pribadi permLevel 1) → Interview (pewawancara mengisi feedback sendiri) → Job Offer (workflow; nomor induk karyawan diisi di penawaran) → aksi *Create employee*. Gaji pokok penawaran menjadi Salary Assignment bila `base_salary_component` diisi di Payroll Settings.

**Anggaran**: Budget per tahun buku, akun laba rugi, dan (opsional) cost center; *Warn*, *Stop*, atau *Ignore* bila terlampaui, dibanding anggaran tahunan atau year-to-date (tahunan × bulan berjalan / 12). Satu anggaran aktif per (perusahaan, tahun buku, cost center); revisi lewat amend. Kontrol berjalan di posting faktur, jurnal, klaim biaya, payroll, dan komisi; aset dan pembayaran tidak dicek.

**CRM lanjutan**: Territory (pohon) di Customer, Lead, Opportunity; Campaign dengan jumlah lead, opportunity, dan nilai menang; Sales Order dari penawaran Accepted (status tagih dihitung dari faktur); Customer Contract (status Active / Expiring soon / Expired / Renewed, pengingat lewat job CRM harian); Commission Plan + Commission Statement bulanan (tarif × total sebelum PPN faktur milik sales rep, dikurangi nota kredit; submit = Dr beban komisi / Cr utang komisi per sales). Sales rep faktur diisi dari sales order → penawaran → account owner pelanggan.

## Persediaan (tahap 13)

Pola SAP MM-IM / NetSuite Inventory, **perpetual** dengan **rata-rata bergerak** (PSAK 14 rata-rata tertimbang). Modul `STOCK`, role `Stock` (role `Purchasing` juga boleh).

- **Master:** Warehouse (pohon, akun persediaan per gudang opsional), Item: *Maintain stock*, gudang default, titik pesan ulang. Stock Settings: gudang default, akun Persediaan, HPP, Selisih persediaan, **Barang diterima belum ditagih (GRNI)**, izin stok negatif (default tidak).
- **Barang masuk:** Goods Receipt dari PO (Dr Persediaan / Cr GRNI, harga netto PO × kurs) lalu faktur pemasok dengan PO (baris stok otomatis ke GRNI); atau faktur pembelian dengan *Update stock* (Dr Persediaan langsung). Keduanya untuk PO yang sama ditolak. Selisih harga PO vs faktur tertinggal di GRNI dan dibersihkan dengan jurnal.
- **Barang keluar:** faktur penjualan dengan *Update stock* (Dr HPP / Cr Persediaan pada rata-rata gudang); nota kredit dengan *Update stock* mengembalikan barang pada HPP faktur asal. Retur ke pemasok lewat Stock Entry *Material Issue* (nota debit tidak menggerakkan stok).
- **Stock Entry:** *Material Receipt* (stok awal, temuan; akun lawan default Selisih persediaan atau ekuitas saldo awal), *Material Issue* (pemakaian; dicek anggaran), *Material Transfer* antar gudang.
- **Stock Opname:** *Fetch items* mengisi semua item bersaldo di gudang; isi qty hitung (dan rate baru bila perlu); submit membukukan selisih nilai.
- **Laporan:** *Stock Balance* (qty, rata-rata, nilai, di bawah titik pesan ulang, plus cek nilai stok = saldo akun bertipe Inventory) dan *Stock Ledger* (kartu stok dengan saldo berjalan).

**Desain & batasan:** tidak ada tabel buku stok terpisah; saldo dihitung dari dokumen mutasi yang sudah submit dan nilai tiap baris dikunci saat submit. Konsekuensinya: (1) posting stok bertanggal sebelum mutasi terakhir item-gudang itu ditolak, (2) pembatalan hanya bila belum ada mutasi sesudahnya, (3) performa turun bila dokumen mutasi sangat banyak (ratusan ribu); saat itu sebaiknya framework punya tabel stock ledger sendiri seperti `LedgerLine`. Belum ada: satuan konversi (UOM ganda), batch/serial number, FIFO, landed cost, surat jalan terpisah dari faktur (Delivery Note), dan reservasi stok untuk Sales Order. Dua submit bersamaan pada item-gudang yang sama bisa sama-sama lolos cek stok (tidak ada penguncian baris).

## Tahap 12

**Proporsi gaji di payslip** (saat payslip dibuat): hari kerja periode dari kalender libur & libur mingguan karyawan, dibatasi tanggal masuk/keluar, dikurangi cuti *tidak dibayar* (`LeaveType.is_paid` tidak dicentang) yang Approved dan, bila `deduct_absence` dicentang di Payroll Settings, absensi berstatus Absent. Pendapatan regular dikali hari dibayar ÷ hari kerja; lembur dan pendapatan tidak regular tidak diproporsikan. Nominal masih bisa diubah manual sebelum submit.

**Pengadaan (tanpa persediaan)**: Purchase Request (karyawan, disetujui approver biaya) → Purchase Order (role Purchasing) → Goods Receipt / BAST per baris PO → Purchase Invoice yang menunjuk PO (baris disalin otomatis). 3-way match di Finance Settings: *Warn* (default), *Stop*, atau *Off* bila total tagihan PO melebihi nilai yang diterima. Nilai persediaan, gudang, dan HPP belum ada: barang dibukukan langsung ke akun beban/aset di faktur.

**Kinerja & pelatihan**: Appraisal Cycle → Appraisal per karyawan (sasaran berbobot 100%, nilai 1–5 dari karyawan dan atasan, skor tertimbang → rating) dengan workflow; Training Event dengan daftar peserta, kehadiran, nilai, dan sertifikat.

## Laporan (`src/Apps.Erp/Reports`)

Semua laporan adalah `IReportProvider` (menu **Reports**, ekspor CSV bawaan). Izin mengikuti izin `report` pada DocType sumber, dan laporan berbasis dokumen memakai `QueryForReportAsync`, jadi row scope berlaku (Sales hanya melihat opportunity miliknya).

| Laporan | DocType izin | Sumber | Filter |
|---|---|---|---|
| Trial Balance | Account | `GetBalancesAsync` (patch 0001) | from, to, akun bersaldo/semua |
| Profit and Loss | Account | idem, jurnal Tutup Buku dikeluarkan | from, to, per akun / per cost center |
| Balance Sheet | Account | idem, laba belum ditutup ditampilkan di ekuitas + baris cek seimbang | as of |
| Accounts Receivable Aging | SalesInvoice | open item buku besar per tanggal (setelah bayar, nota, alokasi) + uang muka | as of, per faktur / per pelanggan |
| Accounts Payable Aging | PurchaseInvoice | idem | as of, per faktur / per pemasok |
| VAT Register | TaxInvoice | faktur pajak per masa (retur negatif), PPN kurang/lebih bayar | masa from–to, arah |
| Withholding Tax Register | WithholdingCertificate | bukti potong per masa & kode | masa from–to, arah |
| Leave Balance | LeaveAllocation | alokasi berlaku + pengajuan Approved/Pending | as of |
| PPh 21 Annual Summary (1721-A1) | Payslip | per pegawai setahun: gaji, tunjangan & lembur, premi pemberi kerja, bonus/THR, bruto, biaya jabatan, iuran pensiun, PTKP, PKP, PPh terutang & dipotong (baris 1721-A1) | tahun |
| Payroll Register | Payslip | payslip submit per masa: bruto, PPh 21, DTP, BPJS, gaji bersih (dasar SPT Masa PPh 21 & setoran BPJS) | masa from–to |
| Stock Balance | StockEntry | qty, rata-rata, nilai per item-gudang; di bawah titik pesan ulang; cek ke akun persediaan | as of, tampilan |
| Stock Ledger | StockEntry | kartu stok dengan saldo berjalan | from–to |
| Bank Transfer List | PaymentRun | transfer per Payment Run dengan rekening tujuan (dasar upload bulk transfer) | tanggal bayar from–to |
| Budget vs Actual | Budget | anggaran tahunan, anggaran s.d. bulan, realisasi, sisa, % terpakai | as of |
| Sales Pipeline | Opportunity | jumlah, nilai, nilai tertimbang | close from–to, group by, open/won/lost |

Kenapa C#, bukan SQL template World 2: buku besar tidak diekspos di skema `reporting` (view hanya per DocType), dan halaman Reports framework hanya punya filter **Date** dan **Select**. Filter teks bebas (pihak, karyawan) tidak tersedia, jadi saring lewat CSV. Batas framework: 10.000 dokumen per query laporan. Untuk dashboard SQL atas data dokumen (mis. pipeline per bulan), view `reporting.opportunity`, `reporting.sales_invoice`, dst. sudah tersedia otomatis.

## Validasi & verifikasi

`tools/validate_metadata.py` adalah port aturan `MetadataCompiler.CompileDocType`: nama, tipe field, Link/Table/DynamicLink, linkFilters (termasuk nilai Select di DocType tujuan), fetchFrom, addFromLookup, default, akar ekspresi & path child/parent, list view, permission, naming, modul, dan role. Ditambah satu aturan runtime: `fetchFrom` di child ditolak. Validator ini diuji pada metadata sample repo (lolos) dan pada error yang disuntikkan sengaja (tertangkap). Workflow juga diperiksa (port `CompileWorkflow`: state, docStatus tidak turun, aksi unik per state, role, akar kondisi, DocType menunjuk workflow yang sama) plus satu cek tambahan: state draft harus punya transisi keluar. Hasil untuk folder ini: **146 DocType, 8 workflow, 0 masalah**.

```bash
python3 tools/validate_metadata.py Metadata "CORE,ACCOUNTING,FINANCE,HR,CRM,STOCK" "Finance,Purchasing,HR,Manager,Employee,Sales,Stock,System Manager"
```

Ini bukan pengganti compiler .NET. Setelah file disalin, jalankan aplikasi atau `dotnet test` untuk validasi final, termasuk parser ekspresi yang sebenarnya.

### Kode C#

Belum di-compile. Yang sudah dicek:
- **Sintaks:** 22 file (±6.300 baris) lolos parser C# tree-sitter; file hook Apps.Sample dipakai sebagai kontrol.
- **API framework:** setiap tipe & anggota yang dipakai dicocokkan dengan source, antara lain `HookContext`, interface hook (`IBefore*`, `IAfterSave`, `IOnCancel`, `IOnLoad`, `IBeforeTransition`), `LedgerLine`, `ILedgerReader`, `IDocumentStore`/`StoreQuery`, `DocumentService` (Insert/Update/RunAction/Get), `IFormAction`/`FormActionResult`, `IScheduledJob`/`ScheduledJobContext`/`ISchedulerLogger`, `IEventHandler`/`OutboxMessage`, `INotificationStore`/`Notification`, `IUserDirectory`, `IUserAccountRepository`, `AddDocHook`/`AddFormAction`/`AddEventHandler`/`AddScheduledJob`. Juga cara store PostgreSQL menerjemahkan filter boolean, null, dan `docstatus`. Bug yang ditemukan & diperbaiki: `d[key] ??= v` pada Dictionary (melempar bila key belum ada), dan `using Framework.Metadata` yang kurang untuk `DocActions`.
- **Nama field:** `tools/check_hook_fields.py` mencocokkan semua nama field di kode dengan metadata: 35 query, 17 lookup, 9 payload insert, 5 payload update (termasuk larangan field read-only), dan 725 akses field. Hasilnya 0 masalah, dan 4 dari 4 kesalahan suntikan tertangkap.
- **Nilai pilihan:** `tools/check_option_literals.py` memastikan setiap literal yang dibandingkan/di-assign di C# (`== "Open"`, `is "Won"`, `["status"] = "Closed"`, dst.) ada di opsi Select, state, atau aksi workflow metadata. Literal lama `"Terbuka"` yang disuntikkan tertangkap.
- **Logika posting:** `tools/sim_posting.py` mem-port aturan posting hook dan dua penjaga ledger framework (`ValidateShape`, `OpenItemViolation`), lalu menjalankan 15 skenario, semuanya lolos:
  1. Penjualan PPN 12% DPP 11/12 dengan pembayaran sebagian, potongan PPh 23, nota kredit, dan pelunasan; lebih bayar ditolak.
  2. Pembelian jasa dengan PPN + PPh 23 dan kelebihan bayar menjadi uang muka.
  3. Nota debit yang melebihi sisa ditolak.
  4. Faktur USD dengan selisih kurs.
  5. Neraca saldo seimbang.
  6. Uang muka karyawan → klaim → pembayaran → pengembalian, dengan pengembalian/klaim berlebih ditolak.
  7. Tiga jadwal penyusutan habis tepat di nilai sisa.
  8. Tutup buku menolkan akun laba rugi.
  9. Payment Reconciliation: uang muka pelanggan & pemasok dialokasikan ke faktur; alokasi berlebih, pihak lain, dan penjaga ledger tanpa cek hook ditolak.
  10. Aritmetika laporan: neraca seimbang dengan laba belum ditutup, laba rugi tanpa jurnal tutup buku = laba yang ditutup + laba sesudahnya, sisa aging.
  11. Payroll: titik batas TER kategori A, contoh hitung manual (bruto 10 jt TK/0 → TER 2%, PKP 60 jt → PPh 3 jt), gaji 15 jt + THR Maret sepanjang tahun (total PPh setahun = Pasal 17 atas PKP tahunan, Desember lebih potong dikembalikan), jurnal payroll seimbang.
  12. Lembur PP 35/2021: 12 kasus pengali (hari kerja, hari istirahat 5/6 hari, libur di hari terpendek) dan aturan dasar upah 75%.
  13. Anggaran year-to-date (lolos/ditolak) dan komisi penjualan dikurangi nota kredit, jurnal akrual komisi.
  14. Proporsi gaji (masuk tengah bulan + cuti tanpa upah), nilai diterima untuk 3-way match, skor & rating kinerja.
  15. Persediaan: dua penerimaan → rata-rata 115.000, HPP penjualan, transfer, opname, tolak posting mundur & stok kurang; nilai stok = saldo akun persediaan, GRNI = penerimaan yang belum ditagih.

Disarankan menambah test di `tests/Framework.Tests` dengan skenario yang sama, memakai store in-memory.

## Berikutnya

1. Terapkan patch, build, dan jalankan aplikasi dengan seed minimal; perbaiki error compile yang muncul.
2. Test otomatis untuk skenario `sim_posting.py` di `tests/Framework.Tests`.
3. Perubahan `LedgerLine`: company + dimensi generik (proyek) + valas, sebelum ada data posting nyata.
4. Laporan tambahan: arus kas, buku besar per akun (detail voucher; butuh method baca entri per akun di `ILedgerReader`), neraca per cost center.
5. Lanjutan: persediaan tahap 2 (Delivery Note, batch/serial, UOM konversi, landed cost, tabel stock ledger di framework), file impor e-Bupot dari laporan 1721-A1, integrasi API bank untuk Payment Run, rekrutmen ke portal lowongan.
