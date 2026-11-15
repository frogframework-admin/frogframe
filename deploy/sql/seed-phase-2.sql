-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 2 (Chart of Accounts, Cost Center, Fiscal Year)
-- Tenant: acme | Generated for initial setup & unit testing
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1000","account_name":"Aset","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1100","account_name":"Aset Lancar","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":"1000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1110', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1110","account_name":"Kas & Setara Kas","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":"1100","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1111', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1111","account_name":"Kas Kecil (Petty Cash)","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1110","account_type":"Cash","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1112', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1112","account_name":"Bank BCA Operasional","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1110","account_type":"Bank","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1113', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1113","account_name":"Bank Mandiri Operasional","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1110","account_type":"Bank","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1120', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1120","account_name":"Piutang Usaha & Uang Muka","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":"1100","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1121', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1121","account_name":"Piutang Usaha IDR","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1120","account_type":"Receivable","is_control_account":true,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1122', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1122","account_name":"Uang Muka Pembelian (Vendor Advance)","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1120","account_type":"Advance","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1130', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1130","account_name":"Persediaan Barang","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":"1100","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1131', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1131","account_name":"Persediaan Barang Dagang","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1130","account_type":"Inventory","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1140', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1140","account_name":"Biaya Dibayar Dimuka","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1100","account_type":"Prepaid Expense","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1150', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1150","account_name":"Pajak Dibayar Dimuka (PPh 22 / PPh 23)","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1100","account_type":"Tax","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1151', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1151","account_name":"PPN Masukan (VAT In)","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1100","account_type":"Tax","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1200","account_name":"Aset Tetap","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":"1000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1210', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1210","account_name":"Peralatan Kantor & IT","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1200","account_type":"Fixed Asset","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1211', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1211","account_name":"Akum. Penyusutan Peralatan Kantor","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"1200","account_type":"Accumulated Depreciation","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1220', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1220","account_name":"Kendaraan Operasional","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1200","account_type":"Fixed Asset","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1221', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1221","account_name":"Akum. Penyusutan Kendaraan","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"1200","account_type":"Accumulated Depreciation","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '1230', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"1230","account_name":"Aset Dalam Penyelesaian (CWIP)","root_type":"Asset","report_type":"Balance Sheet","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"1200","account_type":"Capital Work in Progress","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2000","account_name":"Liabilitas","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2100","account_name":"Liabilitas Jangka Pendek","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":true,"is_active":true,"parent_account":"2000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2110', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2110","account_name":"Hutang Usaha IDR","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Payable","is_control_account":true,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2120', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2120","account_name":"Uang Muka Penjualan (Customer Advance)","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Advance","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2130', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2130","account_name":"Hutang Gaji & Upah (Salary Payable)","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Payable","is_control_account":true,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2140', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2140","account_name":"Hutang BPJS Ketenagakerjaan","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Payable","is_control_account":true,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2141', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2141","account_name":"Hutang BPJS Kesehatan","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Payable","is_control_account":true,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2150', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2150","account_name":"Hutang PPh 21","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Tax","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2151', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2151","account_name":"PPN Keluaran (VAT Out)","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Tax","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '2160', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"2160","account_name":"Biaya yang Masih Harus Dibayar (Accruals)","root_type":"Liability","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"2100","account_type":"Accrual","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '3000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"3000","account_name":"Ekuitas","root_type":"Equity","report_type":"Balance Sheet","normal_balance":"Credit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '3100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"3100","account_name":"Modal Disetor","root_type":"Equity","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"3000","account_type":"Capital","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '3200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"3200","account_name":"Laba Ditahan (Retained Earnings)","root_type":"Equity","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"3000","account_type":"Retained Earnings","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '3300', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"3300","account_name":"Laba Tahun Berjalan","root_type":"Equity","report_type":"Balance Sheet","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"3000","account_type":"Income Summary","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '4000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"4000","account_name":"Pendapatan Usaha","root_type":"Income","report_type":"Profit and Loss","normal_balance":"Credit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '4100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"4100","account_name":"Pendapatan Penjualan Produk","root_type":"Income","report_type":"Profit and Loss","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"4000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '4200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"4200","account_name":"Pendapatan Jasa & Konsultasi","root_type":"Income","report_type":"Profit and Loss","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"4000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '4900', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"4900","account_name":"Potongan & Diskon Penjualan","root_type":"Income","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"4000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '5000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"5000","account_name":"Beban Pokok Penjualan","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '5100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"5100","account_name":"Harga Pokok Penjualan (HPP) Barang","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"5000","account_type":"Cost of Goods Sold","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '5200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"5200","account_name":"Selisih Penyesuaian Stok (Stock Adjustment)","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"5000","account_type":"Cost of Goods Sold","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6000","account_name":"Beban Operasional & Administrasi","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6100","account_name":"Beban Gaji & Tunjangan Karyawan","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":null,"is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6110', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6110","account_name":"Beban BPJS Ketenagakerjaan (Pemberi Kerja)","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":null,"is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6120', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6120","account_name":"Beban BPJS Kesehatan (Pemberi Kerja)","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":null,"is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6200","account_name":"Beban Sewa Gedung & Kantor","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":null,"is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6210', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6210","account_name":"Beban Listrik, Air & Internet","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":null,"is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6300', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6300","account_name":"Beban Penyusutan Aset Tetap","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":"Depreciation Expense","is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '6400', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"6400","account_name":"Beban Perjalanan Dinas & Klaim Karyawan","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"6000","account_type":null,"is_control_account":false,"requires_cost_center":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '8000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"8000","account_name":"Pendapatan & Beban Lain-lain","root_type":"Income","report_type":"Profit and Loss","normal_balance":"Credit","is_group":true,"is_active":true,"parent_account":null,"account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '8100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"8100","account_name":"Pendapatan Bunga Bank","root_type":"Income","report_type":"Profit and Loss","normal_balance":"Credit","is_group":false,"is_active":true,"parent_account":"8000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '8200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"8200","account_name":"Keuntungan / Kerugian Selisih Kurs","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"8000","account_type":"Exchange Gain/Loss","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '8300', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"8300","account_name":"Beban Pembulatan (Round-off)","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"8000","account_type":"Round-off","is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Account', '8400', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"account_code":"8400","account_name":"Beban Administrasi Bank","root_type":"Expense","report_type":"Profit and Loss","normal_balance":"Debit","is_group":false,"is_active":true,"parent_account":"8000","account_type":null,"is_control_account":false,"requires_cost_center":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-000', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-000","cost_center_name":"Semua Pusat Biaya","is_group":true,"parent_cost_center":null,"is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-100', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-100","cost_center_name":"Operasional & Proyek","is_group":true,"parent_cost_center":"CC-000","is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-110', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-110","cost_center_name":"Operasional Gudang & Logistik","is_group":false,"parent_cost_center":"CC-100","is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-120', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-120","cost_center_name":"Operasional Kantor Pusat","is_group":false,"parent_cost_center":"CC-100","is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-200', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-200","cost_center_name":"Penjualan & Pemasaran","is_group":false,"parent_cost_center":"CC-000","is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-300', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-300","cost_center_name":"Umum & Administrasi (G&A)","is_group":false,"parent_cost_center":"CC-000","is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CostCenter', 'CC-400', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cost_center_code":"CC-400","cost_center_name":"Teknologi Informasi (IT)","is_group":false,"parent_cost_center":"CC-000","is_active":true,"company":null}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'FiscalYear', '2026', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"fiscal_year_name":"2026","start_date":"2026-01-01","end_date":"2026-12-31","status":"Open"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'FiscalYear', '2027', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"fiscal_year_name":"2027","start_date":"2027-01-01","end_date":"2027-12-31","status":"Open"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

COMMIT;
