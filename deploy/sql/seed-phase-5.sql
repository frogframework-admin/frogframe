-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 5 (Pricing, Policies & System Settings)
-- Tenant: acme | PriceLists, ItemPrices, AccountingPeriods, Leaves, Salaries, Settings
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PriceList', 'Standard Selling IDR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"price_list_name":"Standard Selling IDR","price_list_type":"Sales","currency":"IDR","price_includes_tax":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PriceList', 'Standard Buying IDR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"price_list_name":"Standard Buying IDR","price_list_type":"Purchase","currency":"IDR","price_includes_tax":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PriceList', 'Standard Selling USD', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"price_list_name":"Standard Selling USD","price_list_type":"Sales","currency":"USD","price_includes_tax":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-SRV-001","price_list":"Standard Selling IDR","uom":"UNT","rate":45000000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-SRV-001","price_list":"Standard Buying IDR","uom":"UNT","rate":35000000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-LAP-001","price_list":"Standard Selling IDR","uom":"UNT","rate":22500000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000004', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-LAP-001","price_list":"Standard Buying IDR","uom":"UNT","rate":18000000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000005', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-MNT-001","price_list":"Standard Selling IDR","uom":"UNT","rate":6500000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000006', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-MNT-001","price_list":"Standard Buying IDR","uom":"UNT","rate":4800000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000007', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-CAB-001","price_list":"Standard Selling IDR","uom":"BOX","rate":1850000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000008', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-CAB-001","price_list":"Standard Buying IDR","uom":"BOX","rate":1350000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000009', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-SVC-001","price_list":"Standard Selling IDR","uom":"DAY","rate":5000000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemPrice', 'IP-000010', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item":"ITM-SVC-002","price_list":"Standard Selling IDR","uom":"MTH","rate":15000000,"valid_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-01', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-01","fiscal_year":"2026","start_date":"2026-01-01","end_date":"2026-01-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-02', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-02","fiscal_year":"2026","start_date":"2026-02-01","end_date":"2026-02-28","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-03', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-03","fiscal_year":"2026","start_date":"2026-03-01","end_date":"2026-03-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-04', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-04","fiscal_year":"2026","start_date":"2026-04-01","end_date":"2026-04-30","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-05', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-05","fiscal_year":"2026","start_date":"2026-05-01","end_date":"2026-05-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-06', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-06","fiscal_year":"2026","start_date":"2026-06-01","end_date":"2026-06-30","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-07', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-07","fiscal_year":"2026","start_date":"2026-07-01","end_date":"2026-07-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-08', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-08","fiscal_year":"2026","start_date":"2026-08-01","end_date":"2026-08-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-09', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-09","fiscal_year":"2026","start_date":"2026-09-01","end_date":"2026-09-30","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-10', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-10","fiscal_year":"2026","start_date":"2026-10-01","end_date":"2026-10-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-11', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-11","fiscal_year":"2026","start_date":"2026-11-01","end_date":"2026-11-30","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingPeriod', '2026-12', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"period_name":"2026-12","fiscal_year":"2026","start_date":"2026-12-01","end_date":"2026-12-31","status":"Open","lock_receivable":false,"lock_payable":false,"lock_fixed_asset":false,"lock_payroll":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveType', 'Annual Leave', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"leave_type_name":"Annual Leave","is_paid":true,"is_annual_quota":true,"max_consecutive_days":12,"include_holidays":false,"requires_attachment":false,"applicable_gender":"All","allow_carry_forward":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveType', 'Sick Leave', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"leave_type_name":"Sick Leave","is_paid":true,"is_annual_quota":false,"max_consecutive_days":14,"include_holidays":false,"requires_attachment":true,"applicable_gender":"All","allow_carry_forward":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveType', 'Maternity Leave', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"leave_type_name":"Maternity Leave","is_paid":true,"is_annual_quota":false,"max_consecutive_days":90,"include_holidays":true,"requires_attachment":true,"applicable_gender":"All","allow_carry_forward":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveType', 'Compassionate Leave', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"leave_type_name":"Compassionate Leave","is_paid":true,"is_annual_quota":false,"max_consecutive_days":3,"include_holidays":false,"requires_attachment":false,"applicable_gender":"All","allow_carry_forward":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeaveType', 'Unpaid Leave', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"leave_type_name":"Unpaid Leave","is_paid":false,"is_annual_quota":false,"max_consecutive_days":30,"include_holidays":false,"requires_attachment":false,"applicable_gender":"All","allow_carry_forward":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'LeavePolicy', 'Standard Leave Policy PKWTT', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"policy_name":"Standard Leave Policy PKWTT","is_active":true,"details":[{"idx":1,"leave_type":"Annual Leave","annual_allocation":12,"min_service_months":12},{"idx":2,"leave_type":"Compassionate Leave","annual_allocation":3,"min_service_months":0}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryComponent', 'Gaji Pokok', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"component_name":"Gaji Pokok","component_type":"Earning","abbreviation":"GP","is_active":true,"is_taxable":true,"is_regular":true,"in_bpjs_base":true,"account":"6100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryComponent', 'Tunjangan Jabatan', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"component_name":"Tunjangan Jabatan","component_type":"Earning","abbreviation":"TJ","is_active":true,"is_taxable":true,"is_regular":true,"in_bpjs_base":true,"account":"6100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryComponent', 'Tunjangan Transport & Makan', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"component_name":"Tunjangan Transport & Makan","component_type":"Earning","abbreviation":"TTM","is_active":true,"is_taxable":true,"is_regular":true,"in_bpjs_base":false,"account":"6100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryComponent', 'Uang Lembur', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"component_name":"Uang Lembur","component_type":"Earning","abbreviation":"OT","is_active":true,"is_taxable":true,"is_regular":false,"in_bpjs_base":false,"account":"6100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryComponent', 'Bonus Kinerja', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"component_name":"Bonus Kinerja","component_type":"Earning","abbreviation":"BONUS","is_active":true,"is_taxable":true,"is_regular":false,"in_bpjs_base":false,"account":"6100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryComponent', 'Potongan Pinjaman Karyawan', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"component_name":"Potongan Pinjaman Karyawan","component_type":"Deduction","abbreviation":"LOAN","is_active":true,"account":"1124","is_taxable":true,"is_regular":true,"in_bpjs_base":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0001","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":6500000},{"idx":2,"salary_component":"Tunjangan Transport & Makan","amount":2000000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0003","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":12000000},{"idx":2,"salary_component":"Tunjangan Jabatan","amount":4000000},{"idx":3,"salary_component":"Tunjangan Transport & Makan","amount":2000000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0005","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":7000000},{"idx":2,"salary_component":"Tunjangan Transport & Makan","amount":2000000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00004', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0008","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":13000000},{"idx":2,"salary_component":"Tunjangan Jabatan","amount":4000000},{"idx":3,"salary_component":"Tunjangan Transport & Makan","amount":2000000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00005', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0002","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":6000000},{"idx":2,"salary_component":"Tunjangan Transport & Makan","amount":1500000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00006', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0004","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":14000000},{"idx":2,"salary_component":"Tunjangan Jabatan","amount":4000000},{"idx":3,"salary_component":"Tunjangan Transport & Makan","amount":2000000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00007', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0006","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":6500000},{"idx":2,"salary_component":"Tunjangan Transport & Makan","amount":1500000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00008', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0009","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":6000000},{"idx":2,"salary_component":"Tunjangan Transport & Makan","amount":1500000}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalaryAssignment', 'SA-00009', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee":"EMP-0010","from_date":"2026-01-01","is_active":true,"bpjs_employment":true,"pension_member":true,"bpjs_health":true,"earnings":[{"idx":1,"salary_component":"Gaji Pokok","amount":7500000},{"idx":2,"salary_component":"Tunjangan Jabatan","amount":1000000},{"idx":3,"salary_component":"Tunjangan Transport & Makan","amount":1500000}],"deductions":[{"idx":1,"salary_component":"Potongan Pinjaman Karyawan","amount":500000}],"remarks":"Cicilan pinjaman karyawan Rp500.000/bulan (saldo awal pinjaman Rp3.000.000 di jurnal pembukaan)"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'GlobalDefaults', 'GlobalDefaults', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"default_company":"ACME","default_currency":"IDR","default_country":"ID","default_holiday_list":"Libur Nasional 2026"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'AccountingSettings', 'AccountingSettings', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"allow_future_posting":false,"enforce_cost_center_on_pl":true,"default_exchange_rate_type":"BI Middle Rate"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'StockSettings', 'StockSettings', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"default_warehouse":"Gudang Utama Cikarang","allow_negative_stock":false,"inventory_account":"1131","cogs_account":"5100","stock_adjustment_account":"5200","grni_account":"2170"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CRMSettings', 'CRMSettings', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"default_sales_stage":"Prospecting","default_customer_group":"Commercial & Corporate","create_opportunity_on_convert":true,"duplicate_check":"Email & mobile","quotation_validity_days":30,"stale_opportunity_days":14}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'HRSettings', 'HRSettings', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"default_holiday_list":"Libur Nasional 2026","default_shift_type":"Office 08-17","default_leave_policy":"Standard Leave Policy PKWTT","default_probation_months":3,"retirement_age":58,"contract_expiry_reminder_days":30,"leave_year_start":"January 1","auto_create_user":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

COMMIT;
