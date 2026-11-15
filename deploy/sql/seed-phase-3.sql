-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 3 (Legal Entity - Company)
-- Tenant: acme | Generated for initial setup & unit testing
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Company', 'ACME', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company_code":"ACME","company_name":"PT Acme Nusantara Teknologi","legal_name":"PT Acme Nusantara Teknologi Tbk","is_group":false,"parent_company":null,"base_currency":"IDR","country":"ID","fiscal_year_start_month":1,"default_holiday_list":"Libur Nasional 2026","tax_id":"0123456789012345","nitku":"0123456789012345000000","is_pkp":true,"email":"corporate@acme.co.id","phone":"+622151501234","website":"https://acme.co.id","default_receivable_account":"1121","default_payable_account":"2110","default_cash_account":"1111","default_income_account":"4100","default_expense_account":"6100","retained_earnings_account":"3200","exchange_gain_loss_account":"8200","round_off_account":"8300","default_cost_center":"CC-300","round_off_cost_center":"CC-300","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Company', 'GLOBEX', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"company_code":"GLOBEX","company_name":"PT Globex Indonesia","legal_name":"PT Globex Indonesia","is_group":false,"parent_company":null,"base_currency":"IDR","country":"ID","fiscal_year_start_month":1,"default_holiday_list":"Libur Nasional 2026","tax_id":"0234567890123000","nitku":"0234567890123000000000","is_pkp":true,"email":"info@globex.co.id","phone":"+62315678900","website":"https://globex.co.id","default_receivable_account":"1121","default_payable_account":"2110","default_cash_account":"1111","default_income_account":"4100","default_expense_account":"6100","retained_earnings_account":"3200","exchange_gain_loss_account":"8200","round_off_account":"8300","default_cost_center":"CC-300","round_off_cost_center":"CC-300","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

COMMIT;
