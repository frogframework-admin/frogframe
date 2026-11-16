-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 7 (Masters & settings: payment terms, CRM, grades, expense types, asset categories,
-- FX rates, projects, Finance Settings, leave allocations 2026, employment contracts)
-- Tenant: acme | Run after phases 1-6. Scenarios: docs/ERP_TEST_SCENARIOS.md
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PaymentTerm', 'Net 15', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"term_name":"Net 15","description":"Lunas 15 hari setelah tanggal faktur","schedule":[{"description":"Pelunasan","invoice_portion":100,"due_date_basis":"Days after invoice date","credit_days":15}],"total_portion":100,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PaymentTerm', 'Net 30', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"term_name":"Net 30","description":"Lunas 30 hari setelah tanggal faktur","schedule":[{"description":"Pelunasan","invoice_portion":100,"due_date_basis":"Days after invoice date","credit_days":30}],"total_portion":100,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PaymentTerm', 'Net 45', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"term_name":"Net 45","description":"Lunas 45 hari setelah tanggal faktur","schedule":[{"description":"Pelunasan","invoice_portion":100,"due_date_basis":"Days after invoice date","credit_days":45}],"total_portion":100,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PaymentTerm', 'DP 30% - Net 30', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"term_name":"DP 30% - Net 30","description":"Uang muka 30% saat faktur, sisa 70% 30 hari setelah faktur","schedule":[{"description":"Uang muka","invoice_portion":30,"due_date_basis":"Days after invoice date","credit_days":0},{"description":"Pelunasan","invoice_portion":70,"due_date_basis":"Days after invoice date","credit_days":30}],"total_portion":100,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeadSource', 'Website', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"source_name":"Website","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeadSource', 'Referral', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"source_name":"Referral","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeadSource', 'Exhibition / Event', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"source_name":"Exhibition / Event","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeadSource', 'Cold Call', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"source_name":"Cold Call","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeadSource', 'LinkedIn', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"source_name":"LinkedIn","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeadSource', 'Partner', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"source_name":"Partner","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LostReason', 'Price too high', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"reason_name":"Price too high","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LostReason', 'Lost to competitor', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"reason_name":"Lost to competitor","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LostReason', 'No budget', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"reason_name":"No budget","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LostReason', 'Postponed by customer', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"reason_name":"Postponed by customer","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LostReason', 'Requirements not met', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"reason_name":"Requirements not met","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Territory', 'Indonesia', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"territory_name":"Indonesia","parent_territory":null,"is_group":true,"territory_manager":"EMP-0004","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Territory', 'Jabodetabek', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"territory_name":"Jabodetabek","parent_territory":"Indonesia","is_group":false,"territory_manager":"EMP-0004","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Territory', 'Jawa Barat', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"territory_name":"Jawa Barat","parent_territory":"Indonesia","is_group":false,"territory_manager":"EMP-0002","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Territory', 'Jawa Timur', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"territory_name":"Jawa Timur","parent_territory":"Indonesia","is_group":false,"territory_manager":"EMP-0002","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Territory', 'Sumatera', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"territory_name":"Sumatera","parent_territory":"Indonesia","is_group":false,"territory_manager":"EMP-0004","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Campaign', 'CMP-2026-0001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"campaign_name":"Webinar Transformasi Digital Q1 2026","campaign_type":"Webinar","status":"Active","start_date":"2026-01-05","end_date":"2026-03-31","budgeted_cost":25000000,"expected_revenue":750000000,"description":"Webinar bulanan untuk CIO/IT Manager perusahaan menengah-besar","sales_rep":"EMP-0004","organization_unit":"DEPT-SALES"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Campaign', 'CMP-2026-0002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"campaign_name":"Pameran Indocomtech 2026","campaign_type":"Event","status":"Planned","start_date":"2026-10-21","end_date":"2026-10-25","budgeted_cost":75000000,"expected_revenue":1500000000,"description":"Booth pameran perangkat & jasa IT","sales_rep":"EMP-0002","organization_unit":"DEPT-SALES"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CommissionPlan', 'Komisi Sales 2026', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"plan_name":"Komisi Sales 2026","rate":2,"valid_from":"2026-01-01","valid_to":"2026-12-31","sales_reps":[{"sales_rep":"EMP-0002","rate":2},{"sales_rep":"EMP-0004","rate":1}],"expense_account":"6500","payable_account":"2180","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Location', 'JKT-HQ', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"location_code":"JKT-HQ","location_name":"Kantor Pusat Jakarta","is_group":false,"company":"ACME","address":"Jl. Jenderal Sudirman Kav. 52-53","city":"Jakarta Selatan","province":"DKI Jakarta","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Location', 'CKR-WH', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"location_code":"CKR-WH","location_name":"Gudang Cikarang","is_group":false,"company":"ACME","address":"Kawasan Industri GIIC Blok AA No. 5","city":"Bekasi","province":"Jawa Barat","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Grade', 'G1', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"grade_code":"G1","grade_name":"Junior Staff","grade_level":1,"pay_ranges":[{"company":"ACME","currency":"IDR","min_salary":4500000,"mid_salary":5750000,"max_salary":7000000,"valid_from":"2026-01-01"}],"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Grade', 'G2', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"grade_code":"G2","grade_name":"Staff","grade_level":2,"pay_ranges":[{"company":"ACME","currency":"IDR","min_salary":6000000,"mid_salary":8000000,"max_salary":10000000,"valid_from":"2026-01-01"}],"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Grade', 'G3', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"grade_code":"G3","grade_name":"Senior Staff / Supervisor","grade_level":3,"pay_ranges":[{"company":"ACME","currency":"IDR","min_salary":8500000,"mid_salary":11250000,"max_salary":14000000,"valid_from":"2026-01-01"}],"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Grade', 'G4', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"grade_code":"G4","grade_name":"Manager","grade_level":4,"pay_ranges":[{"company":"ACME","currency":"IDR","min_salary":15000000,"mid_salary":20000000,"max_salary":25000000,"valid_from":"2026-01-01"}],"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Grade', 'G5', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"grade_code":"G5","grade_name":"Senior Manager","grade_level":5,"pay_ranges":[{"company":"ACME","currency":"IDR","min_salary":18000000,"mid_salary":25000000,"max_salary":32000000,"valid_from":"2026-01-01"}],"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000011', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Sales Staff (2)","job_profile":"JOB-SALES-STAFF","organization_unit":"DEPT-SALES","reports_to_position":"POS-000009","is_manager_position":false,"grade":"G2","status":"Vacant","effective_from":"2026-01-01","location":"JKT-HQ","cost_center":"CC-200","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AppraisalCycle', 'Kinerja 2026 Semester 1', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cycle_name":"Kinerja 2026 Semester 1","company":"ACME","start_date":"2026-01-01","end_date":"2026-06-30","status":"In progress"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AppraisalCycle', 'Kinerja 2026 Semester 2', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"cycle_name":"Kinerja 2026 Semester 2","company":"ACME","start_date":"2026-07-01","end_date":"2026-12-31","status":"Planned"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaimType', 'Perjalanan Dinas', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"type_name":"Perjalanan Dinas","default_account":"6400","requires_receipt":true,"max_amount_per_item":5000000,"is_entertainment":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaimType', 'Akomodasi Hotel', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"type_name":"Akomodasi Hotel","default_account":"6400","requires_receipt":true,"max_amount_per_item":2000000,"is_entertainment":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaimType', 'Transportasi Lokal', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"type_name":"Transportasi Lokal","default_account":"6420","requires_receipt":false,"max_amount_per_item":1000000,"is_entertainment":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaimType', 'Jamuan Klien', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"type_name":"Jamuan Klien","default_account":"6410","requires_receipt":true,"max_amount_per_item":3000000,"is_entertainment":true,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ExpenseClaimType', 'Pelatihan & Seminar', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"type_name":"Pelatihan & Seminar","default_account":"6600","requires_receipt":true,"max_amount_per_item":10000000,"is_entertainment":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AssetCategory', 'Peralatan Kantor & IT', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"category_name":"Peralatan Kantor & IT","depreciation_method":"Straight Line","useful_life_months":48,"fiscal_group":"Group 1","fiscal_method":"Straight Line","fixed_asset_account":"1210","accumulated_depreciation_account":"1211","depreciation_expense_account":"6300","cwip_account":"1230","disposal_gain_loss_account":"8110","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AssetCategory', 'Kendaraan Operasional', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"category_name":"Kendaraan Operasional","depreciation_method":"Straight Line","useful_life_months":96,"fiscal_group":"Group 2","fiscal_method":"Declining Balance","fixed_asset_account":"1220","accumulated_depreciation_account":"1221","depreciation_expense_account":"6300","cwip_account":"1230","disposal_gain_loss_account":"8110","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CurrencyExchangeRate', 'FX-2026-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"rate_date":"2026-01-02","rate_type":"BI Middle Rate","from_currency":"USD","to_currency":"IDR","exchange_rate":16250}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CurrencyExchangeRate', 'FX-2026-00002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"rate_date":"2026-01-02","rate_type":"BI Middle Rate","from_currency":"SGD","to_currency":"IDR","exchange_rate":12100}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CurrencyExchangeRate', 'FX-2026-00003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"rate_date":"2026-01-02","rate_type":"BI Middle Rate","from_currency":"EUR","to_currency":"IDR","exchange_rate":17600}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CurrencyExchangeRate', 'FX-2026-00004', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"rate_date":"2026-01-01","rate_type":"Tax Rate (KMK)","from_currency":"USD","to_currency":"IDR","exchange_rate":16300}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CurrencyExchangeRate', 'FX-2026-00005', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"rate_date":"2026-01-30","rate_type":"BI Middle Rate","from_currency":"USD","to_currency":"IDR","exchange_rate":16400}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Project', 'PRJ-TLK-ERP', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"project_code":"PRJ-TLK-ERP","project_name":"Implementasi ERP Telkom","company":"ACME","customer":"CUST-000001","cost_center":"CC-400","status":"In Progress","start_date":"2026-02-01","end_date":"2026-07-31","contract_value":450000000,"estimated_cost":300000000}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Project', 'PRJ-INT-DC', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"project_code":"PRJ-INT-DC","project_name":"Ekspansi Data Center Cikarang (internal)","company":"ACME","customer":null,"cost_center":"CC-400","status":"In Progress","start_date":"2026-01-01","end_date":"2026-06-30","contract_value":null,"estimated_cost":150000000}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingDimension', 'PROJECT', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"dimension_code":"PROJECT","label":"Project","source_doctype":"Project","mandatory_for_profit_loss":false,"mandatory_for_balance_sheet":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'FinanceSettings', 'FinanceSettings', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"default_output_vat":"PPN-12","default_input_vat":"PPN-MASUKAN-12","auto_create_tax_invoice":true,"credit_limit_action":"Warn","unique_bill_no_per_vendor":true,"three_way_match":"Warn","depreciation_posting_day":"Month end"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000001', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0001","employee_name":"Ahmad Fadilah","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000002', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0002","employee_name":"Citra Dewi","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000003', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0003","employee_name":"Budi Santoso","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000004', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0004","employee_name":"Dian Pratama","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000005', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0005","employee_name":"Eko Prasetyo","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000006', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0006","employee_name":"Fajar Hidayat","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000007', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0008","employee_name":"Gita Pertiwi","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000008', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0009","employee_name":"Hendra Gunawan","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveAllocation', 'LAL-2026-000009', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0010","employee_name":"Indra Kusuma","leave_type":"Annual Leave","from_date":"2026-01-01","to_date":"2026-12-31","leave_policy":"Standard Leave Policy PKWTT","new_leaves":12,"carry_forwarded":0,"total_leaves":12}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'EmploymentContract', 'CTR-2026-00001', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0001","employee_name":"Ahmad Fadilah","contract_type":"PKWTT","contract_no":"PKWTT/ACME/HR/001","start_date":"2021-03-01","end_date":null,"probation_months":3,"job_profile":"JOB-IT-STAFF","basic_salary":6500000,"status":"Active"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'EmploymentContract', 'CTR-2026-00002', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0002","employee_name":"Citra Dewi","contract_type":"PKWTT","contract_no":"PKWTT/ACME/HR/002","start_date":"2022-01-10","end_date":null,"probation_months":3,"job_profile":"JOB-SALES-STAFF","basic_salary":6000000,"status":"Active"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'EmploymentContract', 'CTR-2026-00003', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0009","employee_name":"Hendra Gunawan","contract_type":"PKWTT","contract_no":"PKWTT/ACME/HR/003","start_date":"2022-04-01","end_date":null,"probation_months":3,"job_profile":"JOB-HR-STAFF","basic_salary":6000000,"status":"Active"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'EmploymentContract', 'CTR-2026-00004', 'admin', 1, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0010","employee_name":"Indra Kusuma","contract_type":"PKWTT","contract_no":"PKWTT/ACME/HR/004","start_date":"2020-11-01","end_date":null,"probation_months":3,"job_profile":"JOB-WH-OFFICER","basic_salary":7500000,"status":"Active"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_series (tenant_id, doctype, prefix, current)
SELECT tenant_id, doctype, substring(name from '^(.*-)[0-9]+$'), max(substring(name from '([0-9]+)$')::int)
FROM frogframe_documents
WHERE tenant_id = 'acme' AND name ~ '^[A-Z]{2,5}(-[0-9]{4})?-[0-9]+$'
GROUP BY tenant_id, doctype, substring(name from '^(.*-)[0-9]+$')
ON CONFLICT (tenant_id, doctype, prefix) DO UPDATE SET current = GREATEST(frogframe_series.current, EXCLUDED.current);
COMMIT;
