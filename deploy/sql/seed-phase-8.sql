-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 8 (test scenario transactions, ALL DRAFT)
-- Tenant: acme | Run after phases 1-7. Steps, users, submit order and expected amounts: docs/ERP_TEST_SCENARIOS.md
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Budget', 'BUD-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","fiscal_year":"2026","cost_center":"CC-400","control_basis":"Year-to-date (equal months)","action_if_exceeded":"Warn","accounts":[{"account":"6400","annual_amount":12000000},{"account":"6600","annual_amount":24000000}],"total_budget":36000000,"remarks":"Anggaran IT 2026: perjalanan dinas Rp1 jt/bulan (YTD), pelatihan Rp2 jt/bulan (YTD). Lewat batas = peringatan."}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Budget', 'BUD-2026-00002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","fiscal_year":"2026","cost_center":"CC-200","control_basis":"Annual budget","action_if_exceeded":"Stop","accounts":[{"account":"6410","annual_amount":2000000},{"account":"6500","annual_amount":30000000}],"total_budget":32000000,"remarks":"Anggaran Sales 2026: jamuan klien maksimal Rp2 jt setahun, komisi Rp30 jt. Lewat batas = ditolak."}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JournalEntry', 'JV-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","voucher_type":"Journal Entry","posting_date":"2026-01-31","reference_no":"BCA-ADM-202601","currency":"IDR","exchange_rate":1,"remarks":"Biaya administrasi rekening BCA Januari 2026","accounts":[{"account":"8400","account_name":"Beban Administrasi Bank","debit":25000,"credit":0,"cost_center":"CC-300","description":"Biaya admin bulanan"},{"account":"1112","account_name":"Bank BCA Operasional","debit":0,"credit":25000,"cost_center":null,"description":"Debet otomatis BCA"}],"auto_reverse":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JournalEntry', 'JV-2026-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","voucher_type":"Journal Entry","posting_date":"2026-02-10","reference_no":"NTPN/BPJS-202601","currency":"IDR","exchange_rate":1,"remarks":"Setoran BPJS Ketenagakerjaan, BPJS Kesehatan, PPh 21 & PPh 23 masa Januari 2026","accounts":[{"account":"2140","account_name":"Hutang BPJS Ketenagakerjaan","debit":7922367,"credit":0,"description":"Iuran BPJS TK Januari (JHT, JP, JKK, JKM)"},{"account":"2141","account_name":"Hutang BPJS Kesehatan","debit":3825000,"credit":0,"description":"Iuran BPJS Kesehatan Januari"},{"account":"2150","account_name":"Hutang PPh 21","debit":5508352,"credit":0,"description":"PPh 21 masa Januari"},{"account":"2152","account_name":"Hutang PPh 23","debit":50000,"credit":0,"description":"PPh 23 atas jasa JNE (PINV-2026-000002)"},{"account":"1112","account_name":"Bank BCA Operasional","debit":0,"credit":17305719,"description":"Setoran pajak & BPJS"}],"auto_reverse":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesInvoice', 'SINV-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","customer":"CUST-000002","customer_name":"PT Bank Central Asia Tbk","sales_rep":"EMP-0002","posting_date":"2026-01-26","due_date":"2026-02-25","payment_term":"Net 30","currency":"IDR","exchange_rate":1,"debit_to":"1121","cost_center":"CC-200","update_stock":true,"warehouse":"Gudang Utama Cikarang","vat_code":"PPN-12","items":[{"item":"ITM-LAP-001","item_name":"Laptop Business Ultra 14 Core Ultra 7","qty":3,"uom":"UNT","rate":22500000,"amount":67500000,"is_vat_applicable":true,"income_account":"4100","cost_center":"CC-200","discount_percent":0},{"item":"ITM-CAB-001","item_name":"Kabel UTP Cat6 305 Meter Box","qty":2,"uom":"BOX","rate":1850000,"amount":3700000,"is_vat_applicable":true,"income_account":"4100","cost_center":"CC-200","discount_percent":0}],"total":71200000,"tax_base":71200000,"vat_amount":8544000,"grand_total":79744000,"outstanding_amount":79744000,"payment_status":"Unpaid","remarks":"Pengadaan laptop & kabel jaringan untuk kantor cabang BCA"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesCreditNote', 'SCN-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","customer":"CUST-000002","customer_name":"PT Bank Central Asia Tbk","posting_date":"2026-01-29","currency":"IDR","exchange_rate":1,"debit_to":"1121","cost_center":"CC-200","update_stock":true,"warehouse":"Gudang Utama Cikarang","return_against":"SINV-2026-000002","reason":"Goods return","vat_code":"PPN-12","items":[{"item":"ITM-LAP-001","item_name":"Laptop Business Ultra 14 Core Ultra 7","qty":1,"uom":"UNT","rate":22500000,"amount":22500000,"is_vat_applicable":true,"income_account":"4100","cost_center":"CC-200","discount_percent":0}],"total":22500000,"tax_base":22500000,"vat_amount":2700000,"grand_total":25200000,"remarks":"Retur 1 unit laptop (layar cacat) dari BCA"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PaymentEntry', 'PAY-2026-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"payment_type":"Receive","company":"ACME","posting_date":"2026-02-10","mode_of_payment":"Bank transfer","party_type":"Customer","party":"CUST-000002","bank_account":"BCA-OPS-01","party_account":"1121","currency":"IDR","exchange_rate":1,"paid_amount":54544000,"reference_no":"TRF-BCA-20260210-0317","reference_date":"2026-02-10","references":[{"reference_doctype":"SalesInvoice","reference_name":"SINV-2026-000002","due_date":"2026-02-25","outstanding_amount":54544000,"allocated_amount":54544000}],"remarks":"Pelunasan SINV-2026-000002 setelah retur SCN-2026-00001"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesInvoice', 'SINV-2026-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","customer":"CUST-000003","customer_name":"PT Astra International Tbk","sales_rep":"EMP-0004","posting_date":"2026-01-30","due_date":"2026-03-16","payment_term":"Net 45","currency":"IDR","exchange_rate":1,"debit_to":"1121","cost_center":"CC-400","update_stock":false,"vat_code":"PPN-12","items":[{"item":"ITM-SVC-001","item_name":"Jasa Implementasi & Setup Sistem ERP","qty":25,"uom":"DAY","rate":5000000,"amount":125000000,"is_vat_applicable":true,"income_account":"4200","cost_center":"CC-400","discount_percent":0}],"total":125000000,"tax_base":125000000,"vat_amount":15000000,"grand_total":140000000,"outstanding_amount":140000000,"payment_status":"Unpaid","remarks":"Jasa implementasi ERP 25 hari (uji peringatan batas kredit Rp100 jt)"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PurchaseInvoice', 'PINV-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","vendor":"SUPP-000003","vendor_name":"PT Jalur Nugraha Ekakurir (JNE)","bill_no":"JNE/INV/2026/01/7781","bill_date":"2026-01-26","posting_date":"2026-01-26","due_date":"2026-02-10","payment_term":"Net 15","currency":"IDR","exchange_rate":1,"credit_to":"2110","cost_center":"CC-110","vat_code":"PPN-MASUKAN-12","wht_code":"PPH23-JASA-2","tax_invoice_no":"010.004-26.55512340","items":[{"item":null,"item_name":"Jasa pengiriman barang ke pelanggan Januari 2026","qty":1,"rate":2500000,"amount":2500000,"is_vat_applicable":true,"is_wht_applicable":true,"expense_account":"6420","cost_center":"CC-110","discount_percent":0}],"total":2500000,"tax_base":2500000,"vat_amount":300000,"grand_total":2800000,"outstanding_amount":2750000,"payment_status":"Unpaid","remarks":"Ongkos kirim Januari; PPh 23 2% dipotong saat bayar","update_stock":false,"on_hold":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'StockEntry', 'STE-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"purpose":"Material Transfer","company":"ACME","posting_date":"2026-01-27","from_warehouse":"Gudang Utama Cikarang","to_warehouse":"Gudang Transit Jakarta","cost_center":"CC-110","items":[{"item":"ITM-SRV-001","item_name":"Server Rack 2U Enterprise Xeon 64GB","qty":1,"uom":"UNT","s_warehouse":"Gudang Utama Cikarang","t_warehouse":"Gudang Transit Jakarta","cost_center":"CC-110"}],"remarks":"Kirim server ke gudang transit Jakarta untuk instalasi DC"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'StockReconciliation', 'SOP-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","warehouse":"Gudang Utama Cikarang","posting_date":"2026-01-31","difference_account":"5200","items":[{"item":"ITM-CAB-001","item_name":"Kabel UTP Cat6 305 Meter Box","counted_qty":5},{"item":"ITM-MNT-001","item_name":"Monitor 27 Inch 4K UHD IPS USB-C","counted_qty":3}],"remarks":"Stock opname akhir Januari 2026 Gudang Utama"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'EmployeeAdvance', 'ADV-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0002","employee_name":"Citra Dewi","company":"ACME","expense_approver":"EMP-0004","posting_date":"2026-01-08","purpose":"Uang muka perjalanan dinas ke Surabaya (presentasi klien)","advance_amount":3000000,"settle_by_date":"2026-01-31","advance_account":"1123"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaim', 'EXP-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0002","employee_name":"Citra Dewi","company":"ACME","expense_approver":"EMP-0004","posting_date":"2026-01-25","purpose":"Perjalanan dinas Surabaya 12-14 Januari 2026","employee_advance":"ADV-2026-00001","advance_settled":3000000,"items":[{"expense_date":"2026-01-12","expense_type":"Perjalanan Dinas","description":"Tiket pesawat PP Jakarta-Surabaya","amount":2400000,"cost_center":"CC-200","account":"6400"},{"expense_date":"2026-01-14","expense_type":"Akomodasi Hotel","description":"Hotel 2 malam","amount":1500000,"cost_center":"CC-200","account":"6400"}],"payable_account":"2110","payment_status":"Unpaid"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaim', 'EXP-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0001","employee_name":"Ahmad Fadilah","company":"ACME","expense_approver":"EMP-0003","posting_date":"2026-01-27","purpose":"Instalasi server di DC Cikarang","advance_settled":0,"items":[{"expense_date":"2026-01-26","expense_type":"Perjalanan Dinas","description":"Perjalanan & uang harian ke Cikarang (3 hari)","amount":3000000,"cost_center":"CC-400","account":"6400"},{"expense_date":"2026-01-26","expense_type":"Transportasi Lokal","description":"Tol & parkir","amount":250000,"cost_center":"CC-400","account":"6420"}],"payable_account":"2110","payment_status":"Unpaid"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaim', 'EXP-2026-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0002","employee_name":"Citra Dewi","company":"ACME","expense_approver":"EMP-0004","posting_date":"2026-01-29","purpose":"Jamuan makan malam calon klien BCA","advance_settled":0,"items":[{"expense_date":"2026-01-28","expense_type":"Jamuan Klien","description":"Makan malam 5 orang","amount":2500000,"cost_center":"CC-200","account":"6410"}],"payable_account":"2110","payment_status":"Unpaid"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OvertimeRequest', 'OT-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0010","employee_name":"Indra Kusuma","approver":"EMP-0008","overtime_date":"2026-01-22","day_type":"Working day","hours":3,"reason":"Bongkar muat & penataan ulang rak setelah penerimaan server"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PayrollRun', 'PR-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","period":"2026-01","posting_date":"2026-01-28","payment_date":"2026-01-30","remarks":"Payroll Januari 2026"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PaymentRun', 'PRUN-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","bank_account":"BCA-OPS-01","posting_date":"2026-01-30","due_until":"2026-02-28","include_vendor_invoices":true,"include_expense_claims":true,"payroll_run":"PR-2026-00001","remarks":"Pembayaran gaji Januari, klaim karyawan & faktur pemasok jatuh tempo s.d. 28 Feb"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CommissionStatement', 'CMS-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","sales_rep":"EMP-0002","commission_plan":"Komisi Sales 2026","period":"2026-01","posting_date":"2026-01-31"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Asset', 'AST-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"asset_name":"Laptop Tim IT (10 unit)","asset_category":"Peralatan Kantor & IT","company":"ACME","serial_no":"LOT-IT-2025-01","location":"JKT-HQ","cost_center":"CC-400","custodian":"EMP-0003","purchase_date":"2025-01-01","available_for_use_date":"2025-01-01","gross_purchase_amount":30000000,"salvage_value":0,"is_existing_asset":true,"opening_accumulated_depreciation":7500000,"depreciation_start_date":"2026-01-01","status":"Draft"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'DepreciationRun', 'DEP-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company":"ACME","accounting_period":"2026-01","posting_date":"2026-01-31"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'BankStatement', 'BST-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_account":"BCA-OPS-01","company":"ACME","from_date":"2026-01-01","to_date":"2026-01-31","opening_balance":500000000,"closing_balance":417405710,"lines":[{"transaction_date":"2026-01-09","description":"TRF KE CITRA DEWI - UANG MUKA ADV-2026-00001","bank_reference":"BCA-0109-01","deposit":0,"withdrawal":3000000,"match_status":"Unmatched"},{"transaction_date":"2026-01-22","description":"TRF DARI PT TELKOM INDONESIA","bank_reference":"TRF-BCA-20260122-8812","deposit":64960000,"withdrawal":0,"match_status":"Unmatched"},{"transaction_date":"2026-01-28","description":"TRF KE PT METRODATA ELECTRONICS","bank_reference":"TRF-OUT-BCA-20260128-001","deposit":0,"withdrawal":39200000,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL AHMAD FADILAH","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":11336086,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL CITRA DEWI","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":8043414,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL BUDI SANTOSO","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":15963825,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL DIAN PRATAMA","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":17762961,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL EKO PRASETYO","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":8580233,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL FAJAR HIDAYAT","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":7615574,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL GITA PERTIWI","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":16863393,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL HENDRA GUNAWAN","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":7143414,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"PAYROLL INDRA KUSUMA","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":9270390,"match_status":"Unmatched"},{"transaction_date":"2026-01-30","description":"TRF KE PT JALUR NUGRAHA EKAKURIR","bank_reference":"PRUN-2026-00001","deposit":0,"withdrawal":2750000,"match_status":"Unmatched"},{"transaction_date":"2026-01-31","description":"BIAYA ADMINISTRASI","bank_reference":"BCA-ADM-202601","deposit":0,"withdrawal":25000,"match_status":"Unmatched"}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Lead', 'LEAD-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"salutation":"Mrs.","lead_name":"Rina Marlina","job_title":"Head of IT","company_name":"PT Sinar Logistik Nusantara","industry":"Logistics & Transportation","employee_count":"201-1000","email":"rina.marlina@sinarlogistik.example","mobile":"+6281277001122","city":"Jakarta Utara","lead_source":"Exhibition / Event","campaign":"CMP-2026-0001","territory":"Jabodetabek","status":"Qualified","rating":"Hot","estimated_value":250000000,"sales_rep":"EMP-0002","organization_unit":"DEPT-SALES","notes":"Butuh 20 laptop + jasa implementasi ERP gudang. Siap dikonversi menjadi customer & opportunity."}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Lead', 'LEAD-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"salutation":"Mr.","lead_name":"Joko Susilo","job_title":"Owner","company_name":"CV Maju Bersama","industry":"Retail & Distribution","employee_count":"11-50","email":"joko@majubersama.example","mobile":"+6281388002233","city":"Bandung","lead_source":"Website","campaign":null,"territory":"Jawa Barat","status":"Contacted","rating":"Cold","estimated_value":15000000,"sales_rep":"EMP-0002","organization_unit":"DEPT-SALES","notes":"Anggaran belum ada; uji status Unqualified (wajib alasan)."}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Opportunity', 'OPP-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"opportunity_name":"BCA - 10 Laptop & Monitor Kantor Cabang","customer":"CUST-000002","customer_name":"PT Bank Central Asia Tbk","company":"ACME","opportunity_type":"Upsell","lead_source":"Referral","campaign":"CMP-2026-0001","territory":"Jabodetabek","sales_stage":"Proposal","expected_close_date":"2026-02-28","currency":"IDR","amount":290000000,"next_step":"Presentasi penawaran ke divisi procurement","competitor":"Distributor lokal","sales_rep":"EMP-0002","organization_unit":"DEPT-SALES","lines":[{"item":"ITM-LAP-001","item_name":"Laptop Business Ultra 14 Core Ultra 7","qty":10,"uom":"UNT","rate":22500000,"discount_percent":0,"amount":225000000},{"item":"ITM-MNT-001","item_name":"Monitor 27 Inch 4K UHD IPS USB-C","qty":10,"uom":"UNT","rate":6500000,"discount_percent":0,"amount":65000000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Activity', 'ACT-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"activity_type":"Meeting","subject":"Presentasi penawaran laptop & monitor cabang","reference_doctype":"Opportunity","reference_name":"OPP-2026-000001","start_at":"2026-02-03T10:00:00","end_at":"2026-02-03T11:30:00","status":"Planned","priority":"High","assigned_to":"EMP-0002","organization_unit":"DEPT-SALES"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CustomerContract', 'CON-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"contract_title":"Maintenance & Support Telkom 2026","company":"ACME","customer":"CUST-000001","customer_name":"PT Telekomunikasi Indonesia Tbk","contract_type":"Subscription","start_date":"2026-02-01","end_date":"2027-01-31","contract_value":180000000,"auto_renew":true,"notice_days":60,"contract_status":"Draft","terms":"<p>Maintenance & support bulanan Rp15.000.000 (ITM-SVC-002), SLA respon 4 jam kerja.</p>","sales_rep":"EMP-0004","organization_unit":"DEPT-SALES"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobRequisition', 'REQ-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_title":"Sales Staff","company":"ACME","organization_unit":"DEPT-SALES","position":"POS-000011","job_profile":"JOB-SALES-STAFF","grade":"G2","employment_type":"Permanent (PKWTT)","reason":"New headcount","openings":1,"target_start_date":"2026-03-02","hiring_manager":"EMP-0004","recruiter":"EMP-0009","salary_min":6000000,"salary_max":8000000,"description":"<p>Sales staff korporat wilayah Jabodetabek. Pengalaman B2B IT minimal 2 tahun.</p>"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobApplicant', 'APP-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"applicant_name":"Sari Wulandari","email":"sari.wulandari@mail.example","mobile":"+6281299003344","job_requisition":"REQ-2026-00001","organization_unit":"DEPT-SALES","hiring_manager":"EMP-0004","status":"Applied","source":"Job portal","gender":"Female","date_of_birth":"1997-05-14","expected_salary":7000000}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobApplicant', 'APP-2026-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"applicant_name":"Teguh Prakoso","email":"teguh.prakoso@mail.example","mobile":"+6281299005566","job_requisition":"REQ-2026-00001","organization_unit":"DEPT-SALES","hiring_manager":"EMP-0004","status":"Applied","source":"Referral","referred_by":"EMP-0002","gender":"Male","date_of_birth":"1995-09-02","expected_salary":9500000}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Interview', 'INT-2026-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_applicant":"APP-2026-000001","applicant_name":"Sari Wulandari","job_requisition":"REQ-2026-00001","round":"HR screening","scheduled_at":"2026-02-10T10:00:00","interviewer":"EMP-0009","result":"Pending"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Appraisal', 'APR-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0001","employee_name":"Ahmad Fadilah","organization_unit":"DEPT-IT","reviewer":"EMP-0003","appraisal_cycle":"Kinerja 2026 Semester 1","goals":[{"goal":"Uptime layanan internal","target":">= 99,5%","weight":40},{"goal":"Penyelesaian tiket helpdesk tepat waktu","target":">= 95% sesuai SLA","weight":30},{"goal":"Migrasi server ke DC Cikarang","target":"Selesai sebelum 30 Juni 2026","weight":30}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'TrainingEvent', 'TRN-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"event_name":"Pelatihan Keamanan Siber Dasar","training_type":"Classroom","trainer":"Konsultan eksternal","company":"ACME","start_at":"2026-02-12T09:00:00","end_at":"2026-02-12T17:00:00","location":"Ruang Rapat Lt. 17","cost":15000000,"attendees":[{"employee":"EMP-0001","employee_name":"Ahmad Fadilah","attendance":"Invited"},{"employee":"EMP-0003","employee_name":"Budi Santoso","attendance":"Invited"},{"employee":"EMP-0010","employee_name":"Indra Kusuma","attendance":"Invited"}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_series (tenant_id, doctype, prefix, current)
SELECT tenant_id, doctype, substring(name from '^(.*-)[0-9]+$'), max(substring(name from '([0-9]+)$')::int)
FROM frogframe_documents
WHERE tenant_id = 'acme' AND name ~ '^[A-Z]{2,5}(-[0-9]{4})?-[0-9]+$'
GROUP BY tenant_id, doctype, substring(name from '^(.*-)[0-9]+$')
ON CONFLICT (tenant_id, doctype, prefix) DO UPDATE SET current = GREATEST(frogframe_series.current, EXCLUDED.current);
COMMIT;
