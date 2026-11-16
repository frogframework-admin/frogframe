#:property PublishAot=false
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

// ============================================================================
// Phase 8: transaksi skenario uji (SEMUA DRAFT). Langkah, user, urutan submit & angka yang diharapkan: docs/ERP_TEST_SCENARIOS.md
// SQL tidak menjalankan hook, jadi tidak ada baris buku besar/stok sampai dokumen di-submit/disetujui di aplikasi.
// Jalankan dari root repo setelah fase 1–7: dotnet run scripts/GenerateSeedPhase8.cs
// ============================================================================

const string tenant = "acme";
const string owner = "admin";

var records = new List<(string DocType, string Name, object Data)>();
void Add(string docType, string name, object data) => records.Add((docType, name, data));

// ----------------------------------------------------------------------------
// S1. Akuntansi & anggaran
// ----------------------------------------------------------------------------
Add("Budget", "BUD-2026-00001", new
{
    company = "ACME", fiscal_year = "2026", cost_center = "CC-400", control_basis = "Year-to-date (equal months)", action_if_exceeded = "Warn",
    accounts = new[] { new { account = "6400", annual_amount = 12000000m }, new { account = "6600", annual_amount = 24000000m } },
    total_budget = 36000000m,
    remarks = "Anggaran IT 2026: perjalanan dinas Rp1 jt/bulan (YTD), pelatihan Rp2 jt/bulan (YTD). Lewat batas = peringatan.",
});
Add("Budget", "BUD-2026-00002", new
{
    company = "ACME", fiscal_year = "2026", cost_center = "CC-200", control_basis = "Annual budget", action_if_exceeded = "Stop",
    accounts = new[] { new { account = "6410", annual_amount = 2000000m }, new { account = "6500", annual_amount = 30000000m } },
    total_budget = 32000000m,
    remarks = "Anggaran Sales 2026: jamuan klien maksimal Rp2 jt setahun, komisi Rp30 jt. Lewat batas = ditolak.",
});
Add("JournalEntry", "JV-2026-000002", new
{
    company = "ACME", voucher_type = "Journal Entry", posting_date = "2026-01-31", reference_no = "BCA-ADM-202601", currency = "IDR", exchange_rate = 1.0,
    remarks = "Biaya administrasi rekening BCA Januari 2026",
    accounts = new object[]
    {
        new { account = "8400", account_name = "Beban Administrasi Bank", debit = 25000m, credit = 0m, cost_center = "CC-300", description = "Biaya admin bulanan" },
        new { account = "1112", account_name = "Bank BCA Operasional", debit = 0m, credit = 25000m, cost_center = (string?)null, description = "Debet otomatis BCA" },
    },
});
// Setoran BPJS & PPh Januari (angka = hasil Payroll Run PR-2026-00001 + PPh 23 dari PINV-2026-000002). Submit setelah payroll.
Add("JournalEntry", "JV-2026-000003", new
{
    company = "ACME", voucher_type = "Journal Entry", posting_date = "2026-02-10", reference_no = "NTPN/BPJS-202601", currency = "IDR", exchange_rate = 1.0,
    remarks = "Setoran BPJS Ketenagakerjaan, BPJS Kesehatan, PPh 21 & PPh 23 masa Januari 2026",
    accounts = new object[]
    {
        new { account = "2140", account_name = "Hutang BPJS Ketenagakerjaan", debit = 7922367m, credit = 0m, description = "Iuran BPJS TK Januari (JHT, JP, JKK, JKM)" },
        new { account = "2141", account_name = "Hutang BPJS Kesehatan", debit = 3825000m, credit = 0m, description = "Iuran BPJS Kesehatan Januari" },
        new { account = "2150", account_name = "Hutang PPh 21", debit = 5508352m, credit = 0m, description = "PPh 21 masa Januari" },
        new { account = "2152", account_name = "Hutang PPh 23", debit = 50000m, credit = 0m, description = "PPh 23 atas jasa JNE (PINV-2026-000002)" },
        new { account = "1112", account_name = "Bank BCA Operasional", debit = 0m, credit = 17305719m, description = "Setoran pajak & BPJS" },
    },
});

// ----------------------------------------------------------------------------
// S2. Penjualan lanjutan: faktur dengan stok, retur, batas kredit
// ----------------------------------------------------------------------------
Add("SalesInvoice", "SINV-2026-000002", new
{
    company = "ACME", customer = "CUST-000002", customer_name = "PT Bank Central Asia Tbk", sales_rep = "EMP-0002",
    posting_date = "2026-01-26", due_date = "2026-02-25", payment_term = "Net 30", currency = "IDR", exchange_rate = 1.0,
    debit_to = "1121", cost_center = "CC-200", update_stock = true, warehouse = "Gudang Utama Cikarang", vat_code = "PPN-12",
    items = new[]
    {
        new { item = "ITM-LAP-001", item_name = "Laptop Business Ultra 14 Core Ultra 7", qty = 3.0, uom = "UNT", rate = 22500000m, amount = 67500000m, is_vat_applicable = true, income_account = "4100", cost_center = "CC-200" },
        new { item = "ITM-CAB-001", item_name = "Kabel UTP Cat6 305 Meter Box", qty = 2.0, uom = "BOX", rate = 1850000m, amount = 3700000m, is_vat_applicable = true, income_account = "4100", cost_center = "CC-200" },
    },
    total = 71200000m, tax_base = 71200000m, vat_amount = 8544000m, grand_total = 79744000m, outstanding_amount = 79744000m, payment_status = "Unpaid",
    remarks = "Pengadaan laptop & kabel jaringan untuk kantor cabang BCA",
});
Add("SalesCreditNote", "SCN-2026-00001", new
{
    company = "ACME", customer = "CUST-000002", customer_name = "PT Bank Central Asia Tbk", posting_date = "2026-01-29", currency = "IDR", exchange_rate = 1.0,
    debit_to = "1121", cost_center = "CC-200", update_stock = true, warehouse = "Gudang Utama Cikarang",
    return_against = "SINV-2026-000002", reason = "Goods return", vat_code = "PPN-12",
    items = new[]
    {
        new { item = "ITM-LAP-001", item_name = "Laptop Business Ultra 14 Core Ultra 7", qty = 1.0, uom = "UNT", rate = 22500000m, amount = 22500000m, is_vat_applicable = true, income_account = "4100", cost_center = "CC-200" },
    },
    total = 22500000m, tax_base = 22500000m, vat_amount = 2700000m, grand_total = 25200000m,
    remarks = "Retur 1 unit laptop (layar cacat) dari BCA",
});
Add("PaymentEntry", "PAY-2026-000003", new
{
    payment_type = "Receive", company = "ACME", posting_date = "2026-02-10", mode_of_payment = "Bank transfer",
    party_type = "Customer", party = "CUST-000002", bank_account = "BCA-OPS-01", party_account = "1121", currency = "IDR", exchange_rate = 1.0,
    paid_amount = 54544000m, reference_no = "TRF-BCA-20260210-0317", reference_date = "2026-02-10",
    references = new[] { new { reference_doctype = "SalesInvoice", reference_name = "SINV-2026-000002", due_date = "2026-02-25", outstanding_amount = 54544000m, allocated_amount = 54544000m } },
    remarks = "Pelunasan SINV-2026-000002 setelah retur SCN-2026-00001",
});
Add("SalesInvoice", "SINV-2026-000003", new
{
    company = "ACME", customer = "CUST-000003", customer_name = "PT Astra International Tbk", sales_rep = "EMP-0004",
    posting_date = "2026-01-30", due_date = "2026-03-16", payment_term = "Net 45", currency = "IDR", exchange_rate = 1.0,
    debit_to = "1121", cost_center = "CC-400", update_stock = false, vat_code = "PPN-12",
    items = new[]
    {
        new { item = "ITM-SVC-001", item_name = "Jasa Implementasi & Setup Sistem ERP", qty = 25.0, uom = "DAY", rate = 5000000m, amount = 125000000m, is_vat_applicable = true, income_account = "4200", cost_center = "CC-400" },
    },
    total = 125000000m, tax_base = 125000000m, vat_amount = 15000000m, grand_total = 140000000m, outstanding_amount = 140000000m, payment_status = "Unpaid",
    remarks = "Jasa implementasi ERP 25 hari (uji peringatan batas kredit Rp100 jt)",
});

// ----------------------------------------------------------------------------
// S3. Pembelian jasa dengan PPh 23 & stok (transfer, opname)
// ----------------------------------------------------------------------------
Add("PurchaseInvoice", "PINV-2026-000002", new
{
    company = "ACME", vendor = "SUPP-000003", vendor_name = "PT Jalur Nugraha Ekakurir (JNE)", bill_no = "JNE/INV/2026/01/7781", bill_date = "2026-01-26",
    posting_date = "2026-01-26", due_date = "2026-02-10", payment_term = "Net 15", currency = "IDR", exchange_rate = 1.0,
    credit_to = "2110", cost_center = "CC-110", vat_code = "PPN-MASUKAN-12", wht_code = "PPH23-JASA-2", tax_invoice_no = "010.004-26.55512340",
    items = new[]
    {
        new { item = (string?)null, item_name = "Jasa pengiriman barang ke pelanggan Januari 2026", qty = 1.0, rate = 2500000m, amount = 2500000m, is_vat_applicable = true, is_wht_applicable = true, expense_account = "6420", cost_center = "CC-110" },
    },
    total = 2500000m, tax_base = 2500000m, vat_amount = 300000m, grand_total = 2800000m, outstanding_amount = 2750000m, payment_status = "Unpaid",
    remarks = "Ongkos kirim Januari; PPh 23 2% dipotong saat bayar",
});
Add("StockEntry", "STE-2026-000002", new
{
    purpose = "Material Transfer", company = "ACME", posting_date = "2026-01-27", from_warehouse = "Gudang Utama Cikarang", to_warehouse = "Gudang Transit Jakarta",
    cost_center = "CC-110",
    items = new[] { new { item = "ITM-SRV-001", item_name = "Server Rack 2U Enterprise Xeon 64GB", qty = 1.0, uom = "UNT", s_warehouse = "Gudang Utama Cikarang", t_warehouse = "Gudang Transit Jakarta", cost_center = "CC-110" } },
    remarks = "Kirim server ke gudang transit Jakarta untuk instalasi DC",
});
Add("StockReconciliation", "SOP-2026-00001", new
{
    company = "ACME", warehouse = "Gudang Utama Cikarang", posting_date = "2026-01-31", difference_account = "5200",
    items = new[]
    {
        new { item = "ITM-CAB-001", item_name = "Kabel UTP Cat6 305 Meter Box", counted_qty = 5.0 },
        new { item = "ITM-MNT-001", item_name = "Monitor 27 Inch 4K UHD IPS USB-C", counted_qty = 3.0 },
    },
    remarks = "Stock opname akhir Januari 2026 Gudang Utama",
});

// ----------------------------------------------------------------------------
// S4. Uang muka, klaim biaya, lembur (workflow persetujuan)
// ----------------------------------------------------------------------------
Add("EmployeeAdvance", "ADV-2026-00001", new
{
    employee = "EMP-0002", employee_name = "Citra Dewi", company = "ACME", expense_approver = "EMP-0004", posting_date = "2026-01-08",
    purpose = "Uang muka perjalanan dinas ke Surabaya (presentasi klien)", advance_amount = 3000000m, settle_by_date = "2026-01-31",
    advance_account = "1123",
});
Add("ExpenseClaim", "EXP-2026-000001", new
{
    employee = "EMP-0002", employee_name = "Citra Dewi", company = "ACME", expense_approver = "EMP-0004", posting_date = "2026-01-25",
    purpose = "Perjalanan dinas Surabaya 12-14 Januari 2026", employee_advance = "ADV-2026-00001", advance_settled = 3000000m,
    items = new[]
    {
        new { expense_date = "2026-01-12", expense_type = "Perjalanan Dinas", description = "Tiket pesawat PP Jakarta-Surabaya", amount = 2400000m, cost_center = "CC-200", account = "6400" },
        new { expense_date = "2026-01-14", expense_type = "Akomodasi Hotel", description = "Hotel 2 malam", amount = 1500000m, cost_center = "CC-200", account = "6400" },
    },
    payable_account = "2110", payment_status = "Unpaid",
});
Add("ExpenseClaim", "EXP-2026-000002", new
{
    employee = "EMP-0001", employee_name = "Ahmad Fadilah", company = "ACME", expense_approver = "EMP-0003", posting_date = "2026-01-27",
    purpose = "Instalasi server di DC Cikarang", advance_settled = 0m,
    items = new[]
    {
        new { expense_date = "2026-01-26", expense_type = "Perjalanan Dinas", description = "Perjalanan & uang harian ke Cikarang (3 hari)", amount = 3000000m, cost_center = "CC-400", account = "6400" },
        new { expense_date = "2026-01-26", expense_type = "Transportasi Lokal", description = "Tol & parkir", amount = 250000m, cost_center = "CC-400", account = "6420" },
    },
    payable_account = "2110", payment_status = "Unpaid",
});
Add("ExpenseClaim", "EXP-2026-000003", new
{
    employee = "EMP-0002", employee_name = "Citra Dewi", company = "ACME", expense_approver = "EMP-0004", posting_date = "2026-01-29",
    purpose = "Jamuan makan malam calon klien BCA", advance_settled = 0m,
    items = new[]
    {
        new { expense_date = "2026-01-28", expense_type = "Jamuan Klien", description = "Makan malam 5 orang", amount = 2500000m, cost_center = "CC-200", account = "6410" },
    },
    payable_account = "2110", payment_status = "Unpaid",
});
Add("OvertimeRequest", "OT-2026-000001", new
{
    employee = "EMP-0010", employee_name = "Indra Kusuma", approver = "EMP-0008", overtime_date = "2026-01-22", day_type = "Working day", hours = 3.0,
    reason = "Bongkar muat & penataan ulang rak setelah penerimaan server",
});

// ----------------------------------------------------------------------------
// S5. Payroll, Payment Run, komisi, aset, rekonsiliasi bank (header; baris diisi lewat aksi di aplikasi)
// ----------------------------------------------------------------------------
Add("PayrollRun", "PR-2026-00001", new
{
    company = "ACME", period = "2026-01", posting_date = "2026-01-28", payment_date = "2026-01-30", remarks = "Payroll Januari 2026",
});
Add("PaymentRun", "PRUN-2026-00001", new
{
    company = "ACME", bank_account = "BCA-OPS-01", posting_date = "2026-01-30", due_until = "2026-02-28",
    include_vendor_invoices = true, include_expense_claims = true, payroll_run = "PR-2026-00001",
    remarks = "Pembayaran gaji Januari, klaim karyawan & faktur pemasok jatuh tempo s.d. 28 Feb",
});
Add("CommissionStatement", "CMS-2026-00001", new
{
    company = "ACME", sales_rep = "EMP-0002", commission_plan = "Komisi Sales 2026", period = "2026-01", posting_date = "2026-01-31",
});
Add("Asset", "AST-2026-00001", new
{
    asset_name = "Laptop Tim IT (10 unit)", asset_category = "Peralatan Kantor & IT", company = "ACME", serial_no = "LOT-IT-2025-01",
    location = "JKT-HQ", cost_center = "CC-400", custodian = "EMP-0003", purchase_date = "2025-01-01", available_for_use_date = "2025-01-01",
    gross_purchase_amount = 30000000m, salvage_value = 0m, is_existing_asset = true, opening_accumulated_depreciation = 7500000m,
    depreciation_start_date = "2026-01-01",
});
Add("DepreciationRun", "DEP-2026-00001", new { company = "ACME", accounting_period = "2026-01", posting_date = "2026-01-31" });

// Rekening koran BCA Januari (angka mengikuti skenario; dicocokkan di Bank Reconciliation).
var bstLines = new (string Date, string Desc, string Ref, decimal Dep, decimal Wd)[]
{
    ("2026-01-09", "TRF KE CITRA DEWI - UANG MUKA ADV-2026-00001", "BCA-0109-01", 0m, 3000000m),
    ("2026-01-22", "TRF DARI PT TELKOM INDONESIA", "TRF-BCA-20260122-8812", 64960000m, 0m),
    ("2026-01-28", "TRF KE PT METRODATA ELECTRONICS", "TRF-OUT-BCA-20260128-001", 0m, 39200000m),
    ("2026-01-30", "PAYROLL AHMAD FADILAH", "PRUN-2026-00001", 0m, 11336086m),
    ("2026-01-30", "PAYROLL CITRA DEWI", "PRUN-2026-00001", 0m, 8043414m),
    ("2026-01-30", "PAYROLL BUDI SANTOSO", "PRUN-2026-00001", 0m, 15963825m),
    ("2026-01-30", "PAYROLL DIAN PRATAMA", "PRUN-2026-00001", 0m, 17762961m),
    ("2026-01-30", "PAYROLL EKO PRASETYO", "PRUN-2026-00001", 0m, 8580233m),
    ("2026-01-30", "PAYROLL FAJAR HIDAYAT", "PRUN-2026-00001", 0m, 7615574m),
    ("2026-01-30", "PAYROLL GITA PERTIWI", "PRUN-2026-00001", 0m, 16863393m),
    ("2026-01-30", "PAYROLL HENDRA GUNAWAN", "PRUN-2026-00001", 0m, 7143414m),
    ("2026-01-30", "PAYROLL INDRA KUSUMA", "PRUN-2026-00001", 0m, 9270390m),
    ("2026-01-30", "TRF KE PT JALUR NUGRAHA EKAKURIR", "PRUN-2026-00001", 0m, 2750000m),
    ("2026-01-31", "BIAYA ADMINISTRASI", "BCA-ADM-202601", 0m, 25000m),
};
var opening = 500000000m;
Add("BankStatement", "BST-2026-00001", new
{
    bank_account = "BCA-OPS-01", company = "ACME", from_date = "2026-01-01", to_date = "2026-01-31",
    opening_balance = opening, closing_balance = opening + bstLines.Sum(l => l.Dep - l.Wd),
    lines = bstLines.Select(l => new { transaction_date = l.Date, description = l.Desc, bank_reference = l.Ref, deposit = l.Dep, withdrawal = l.Wd, match_status = "Unmatched" }).ToArray(),
});

// ----------------------------------------------------------------------------
// S6. CRM
// ----------------------------------------------------------------------------
Add("Lead", "LEAD-2026-000001", new
{
    salutation = "Mrs.", lead_name = "Rina Marlina", job_title = "Head of IT", company_name = "PT Sinar Logistik Nusantara", industry = "Logistics & Transportation",
    employee_count = "201-1000", email = "rina.marlina@sinarlogistik.example", mobile = "+6281277001122", city = "Jakarta Utara",
    lead_source = "Exhibition / Event", campaign = "CMP-2026-0001", territory = "Jabodetabek", status = "Qualified", rating = "Hot",
    estimated_value = 250000000m, sales_rep = "EMP-0002", organization_unit = "DEPT-SALES",
    notes = "Butuh 20 laptop + jasa implementasi ERP gudang. Siap dikonversi menjadi customer & opportunity.",
});
Add("Lead", "LEAD-2026-000002", new
{
    salutation = "Mr.", lead_name = "Joko Susilo", job_title = "Owner", company_name = "CV Maju Bersama", industry = "Retail & Distribution",
    employee_count = "11-50", email = "joko@majubersama.example", mobile = "+6281388002233", city = "Bandung",
    lead_source = "Website", campaign = (string?)null, territory = "Jawa Barat", status = "Contacted", rating = "Cold",
    estimated_value = 15000000m, sales_rep = "EMP-0002", organization_unit = "DEPT-SALES",
    notes = "Anggaran belum ada; uji status Unqualified (wajib alasan).",
});
Add("Opportunity", "OPP-2026-000001", new
{
    opportunity_name = "BCA - 10 Laptop & Monitor Kantor Cabang", customer = "CUST-000002", customer_name = "PT Bank Central Asia Tbk", company = "ACME",
    opportunity_type = "Upsell", lead_source = "Referral", campaign = "CMP-2026-0001", territory = "Jabodetabek", sales_stage = "Proposal",
    expected_close_date = "2026-02-28", currency = "IDR", amount = 290000000m, next_step = "Presentasi penawaran ke divisi procurement",
    competitor = "Distributor lokal", sales_rep = "EMP-0002", organization_unit = "DEPT-SALES",
    lines = new[]
    {
        new { item = "ITM-LAP-001", item_name = "Laptop Business Ultra 14 Core Ultra 7", qty = 10.0, uom = "UNT", rate = 22500000m, discount_percent = 0m, amount = 225000000m },
        new { item = "ITM-MNT-001", item_name = "Monitor 27 Inch 4K UHD IPS USB-C", qty = 10.0, uom = "UNT", rate = 6500000m, discount_percent = 0m, amount = 65000000m },
    },
});
Add("Activity", "ACT-2026-000001", new
{
    activity_type = "Meeting", subject = "Presentasi penawaran laptop & monitor cabang", reference_doctype = "Opportunity", reference_name = "OPP-2026-000001",
    start_at = "2026-02-03T10:00:00", end_at = "2026-02-03T11:30:00", status = "Planned", priority = "High", assigned_to = "EMP-0002", organization_unit = "DEPT-SALES",
});
Add("CustomerContract", "CON-2026-00001", new
{
    contract_title = "Maintenance & Support Telkom 2026", company = "ACME", customer = "CUST-000001", customer_name = "PT Telekomunikasi Indonesia Tbk",
    contract_type = "Subscription", start_date = "2026-02-01", end_date = "2027-01-31", contract_value = 180000000m, auto_renew = true, notice_days = 60,
    contract_status = "Draft", terms = "<p>Maintenance & support bulanan Rp15.000.000 (ITM-SVC-002), SLA respon 4 jam kerja.</p>",
    sales_rep = "EMP-0004", organization_unit = "DEPT-SALES",
});

// ----------------------------------------------------------------------------
// S7. Talenta: rekrutmen, penilaian kinerja, pelatihan
// ----------------------------------------------------------------------------
Add("JobRequisition", "REQ-2026-00001", new
{
    job_title = "Sales Staff", company = "ACME", organization_unit = "DEPT-SALES", position = "POS-000011", job_profile = "JOB-SALES-STAFF", grade = "G2",
    employment_type = "Permanent (PKWTT)", reason = "New headcount", openings = 1, target_start_date = "2026-03-02",
    hiring_manager = "EMP-0004", recruiter = "EMP-0009", salary_min = 6000000m, salary_max = 8000000m,
    description = "<p>Sales staff korporat wilayah Jabodetabek. Pengalaman B2B IT minimal 2 tahun.</p>",
});
Add("JobApplicant", "APP-2026-000001", new
{
    applicant_name = "Sari Wulandari", email = "sari.wulandari@mail.example", mobile = "+6281299003344", job_requisition = "REQ-2026-00001",
    organization_unit = "DEPT-SALES", hiring_manager = "EMP-0004", status = "Applied", source = "Job portal", gender = "Female",
    date_of_birth = "1997-05-14", expected_salary = 7000000m,
});
Add("JobApplicant", "APP-2026-000002", new
{
    applicant_name = "Teguh Prakoso", email = "teguh.prakoso@mail.example", mobile = "+6281299005566", job_requisition = "REQ-2026-00001",
    organization_unit = "DEPT-SALES", hiring_manager = "EMP-0004", status = "Applied", source = "Referral", referred_by = "EMP-0002", gender = "Male",
    date_of_birth = "1995-09-02", expected_salary = 9500000m,
});
Add("Interview", "INT-2026-000001", new
{
    job_applicant = "APP-2026-000001", applicant_name = "Sari Wulandari", job_requisition = "REQ-2026-00001", round = "HR screening",
    scheduled_at = "2026-02-10T10:00:00", interviewer = "EMP-0009", result = "Pending",
});
Add("Appraisal", "APR-2026-00001", new
{
    employee = "EMP-0001", employee_name = "Ahmad Fadilah", organization_unit = "DEPT-IT", reviewer = "EMP-0003", appraisal_cycle = "Kinerja 2026 Semester 1",
    goals = new[]
    {
        new { goal = "Uptime layanan internal", target = ">= 99,5%", weight = 40m },
        new { goal = "Penyelesaian tiket helpdesk tepat waktu", target = ">= 95% sesuai SLA", weight = 30m },
        new { goal = "Migrasi server ke DC Cikarang", target = "Selesai sebelum 30 Juni 2026", weight = 30m },
    },
});
Add("TrainingEvent", "TRN-2026-00001", new
{
    event_name = "Pelatihan Keamanan Siber Dasar", training_type = "Classroom", trainer = "Konsultan eksternal", company = "ACME",
    start_at = "2026-02-12T09:00:00", end_at = "2026-02-12T17:00:00", location = "Ruang Rapat Lt. 17", cost = 15000000m,
    attendees = new[]
    {
        new { employee = "EMP-0001", employee_name = "Ahmad Fadilah", attendance = "Invited" },
        new { employee = "EMP-0003", employee_name = "Budi Santoso", attendance = "Invited" },
        new { employee = "EMP-0010", employee_name = "Indra Kusuma", attendance = "Invited" },
    },
});

// ============================================================================
// Generate SQL script
// ============================================================================
var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var sb = new StringBuilder();
sb.AppendLine("-- ============================================================================");
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 8 (test scenario transactions, ALL DRAFT)");
sb.AppendLine("-- Tenant: acme | Run after phases 1-7. Steps, users, submit order and expected amounts: docs/ERP_TEST_SCENARIOS.md");
sb.AppendLine("-- ============================================================================");
sb.AppendLine("BEGIN;");
sb.AppendLine();
foreach (var (docType, name, data) in records)
{
    var dataJson = WithDefaults(docType, data, jsonOptions).ToJsonString(jsonOptions).Replace("'", "''");
    var nameEscaped = name.Replace("'", "''");
    sb.AppendLine($"""
        INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
        VALUES ('{tenant}', '{docType}', '{nameEscaped}', '{owner}', 0, NOW(), NOW(), '{owner}', 1, 1, '{dataJson}'::jsonb)
        ON CONFLICT (tenant_id, doctype, name) DO UPDATE
        SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;
        """);
    sb.AppendLine();
}
sb.AppendLine(SeriesSync);
sb.AppendLine("COMMIT;");
Directory.CreateDirectory("deploy/sql");
File.WriteAllText("deploy/sql/seed-phase-8.sql", sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Successfully generated deploy/sql/seed-phase-8.sql with {records.Count} records.");

static JsonObject WithDefaults(string docType, object data, JsonSerializerOptions options)
{
    var node = JsonSerializer.SerializeToNode(data, options)!.AsObject();
    FillDefaults(docType, node);
    return node;
}

static void FillDefaults(string docType, JsonObject node)
{
    var path = Path.Combine("src", "Apps.Erp", "Metadata", docType + ".doctype.json");
    if (!File.Exists(path)) return;
    using var meta = JsonDocument.Parse(File.ReadAllText(path));
    foreach (var f in meta.RootElement.GetProperty("fields").EnumerateArray())
    {
        var name = f.GetProperty("name").GetString()!;
        if (f.GetProperty("type").GetString() == "Table" && f.TryGetProperty("childDocType", out var child) && node[name] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>()) FillDefaults(child.GetString()!, row);
            continue;
        }
        if (node.ContainsKey(name) || !f.TryGetProperty("default", out var def)) continue;
        if (def.ValueKind == JsonValueKind.String && def.GetString()!.StartsWith('$')) continue;
        node[name] = JsonNode.Parse(def.GetRawText());
    }
}

partial class Program
{
    // Nomor seri (frogframe_series) dimajukan ke nomor seed tertinggi; tanpa ini dokumen baru di aplikasi bertabrakan dengan nama seed.
    const string SeriesSync = """
        INSERT INTO frogframe_series (tenant_id, doctype, prefix, current)
        SELECT tenant_id, doctype, substring(name from '^(.*-)[0-9]+$'), max(substring(name from '([0-9]+)$')::int)
        FROM frogframe_documents
        WHERE tenant_id = 'acme' AND name ~ '^[A-Z]{2,5}(-[0-9]{4})?-[0-9]+$'
        GROUP BY tenant_id, doctype, substring(name from '^(.*-)[0-9]+$')
        ON CONFLICT (tenant_id, doctype, prefix) DO UPDATE SET current = GREATEST(frogframe_series.current, EXCLUDED.current);
        """;
}
