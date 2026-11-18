-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 4 (Operational Master Data)
-- Tenant: acme | HR, Customers, Vendors, Banks, Warehouses, Items, TaxCodes
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamilyGroup', 'Technology & Engineering', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Technology & Engineering","description":"IT and Software Development"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamilyGroup', 'Finance & Accounting', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Finance & Accounting","description":"Finance, Accounting, and Tax"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamilyGroup', 'Human Resources', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Human Resources","description":"People Operations and Talent Management"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamilyGroup', 'Operations & Supply Chain', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Operations & Supply Chain","description":"Logistics, Warehousing, and Procurement"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamilyGroup', 'Commercial & Sales', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Commercial & Sales","description":"Sales, Marketing, and Account Management"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamily', 'Software Engineering', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"family_name":"Software Engineering","job_family_group":"Technology & Engineering","description":"Software design & infrastructure"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamily', 'Finance & Accounting', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"family_name":"Finance & Accounting","job_family_group":"Finance & Accounting","description":"General ledger, AP, AR, and reporting"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamily', 'Human Resource Management', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"family_name":"Human Resource Management","job_family_group":"Human Resources","description":"HR strategy, relations, and operations"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamily', 'Warehouse & Logistics', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"family_name":"Warehouse & Logistics","job_family_group":"Operations & Supply Chain","description":"Inventory handling and distribution"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobFamily', 'Sales & Business Development', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"family_name":"Sales & Business Development","job_family_group":"Commercial & Sales","description":"Direct sales and client relationships"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-EXEC-DIR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-EXEC-DIR","job_title":"Managing Director","job_family":"Human Resource Management","job_level":"Executive","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-IT-MGR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-IT-MGR","job_title":"IT Manager","job_family":"Software Engineering","job_level":"Manager","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-IT-STAFF', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-IT-STAFF","job_title":"IT Support Specialist","job_family":"Software Engineering","job_level":"Staff","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-HR-MGR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-HR-MGR","job_title":"HR Manager","job_family":"Human Resource Management","job_level":"Manager","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-HR-STAFF', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-HR-STAFF","job_title":"HR Specialist","job_family":"Human Resource Management","job_level":"Staff","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-FIN-STAFF', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-FIN-STAFF","job_title":"Finance & Accounting Specialist","job_family":"Finance & Accounting","job_level":"Staff","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-PURCH-STAFF', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-PURCH-STAFF","job_title":"Procurement Specialist","job_family":"Warehouse & Logistics","job_level":"Staff","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-WH-OFFICER', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-WH-OFFICER","job_title":"Warehouse Officer","job_family":"Warehouse & Logistics","job_level":"Staff","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-SALES-MGR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-SALES-MGR","job_title":"Sales Manager","job_family":"Sales & Business Development","job_level":"Manager","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'JobProfile', 'JOB-SALES-STAFF', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"job_code":"JOB-SALES-STAFF","job_title":"Sales Specialist","job_family":"Sales & Business Development","job_level":"Staff","is_active":true,"is_manager_job":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'BOD', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"BOD","org_unit_name":"Dewan Direksi (BOD)","unit_type":"Board of Directors","parent_org_unit":null,"company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DIV-TECH', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DIV-TECH","org_unit_name":"Direktorat Teknologi & Operasi","unit_type":"Directorate","parent_org_unit":"BOD","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DIV-FIN', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DIV-FIN","org_unit_name":"Direktorat Keuangan & HR","unit_type":"Directorate","parent_org_unit":"BOD","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DIV-COMM', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DIV-COMM","org_unit_name":"Direktorat Komersial","unit_type":"Directorate","parent_org_unit":"BOD","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DEPT-IT', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DEPT-IT","org_unit_name":"Departemen IT","unit_type":"Department","parent_org_unit":"DIV-TECH","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DEPT-WH', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DEPT-WH","org_unit_name":"Departemen Gudang & Logistik","unit_type":"Department","parent_org_unit":"DIV-TECH","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DEPT-FIN', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DEPT-FIN","org_unit_name":"Departemen Keuangan & Akuntansi","unit_type":"Department","parent_org_unit":"DIV-FIN","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DEPT-HR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DEPT-HR","org_unit_name":"Departemen Human Resources","unit_type":"Department","parent_org_unit":"DIV-FIN","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DEPT-SALES', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DEPT-SALES","org_unit_name":"Departemen Penjualan & Pemasaran","unit_type":"Department","parent_org_unit":"DIV-COMM","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'OrganizationUnit', 'DEPT-PURCH', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"org_unit_code":"DEPT-PURCH","org_unit_name":"Departemen Pengadaan (Purchasing)","unit_type":"Department","parent_org_unit":"DIV-COMM","company":"ACME","is_active":true,"staffing_model":"Position","effective_from":"2026-01-01"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Managing Director","job_profile":"JOB-EXEC-DIR","organization_unit":"BOD","reports_to_position":null,"is_manager_position":true,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"IT Manager","job_profile":"JOB-IT-MGR","organization_unit":"DEPT-IT","reports_to_position":"POS-000001","is_manager_position":true,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"IT Staff","job_profile":"JOB-IT-STAFF","organization_unit":"DEPT-IT","reports_to_position":"POS-000002","is_manager_position":false,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000004', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"HR Manager","job_profile":"JOB-HR-MGR","organization_unit":"DEPT-HR","reports_to_position":"POS-000001","is_manager_position":true,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000005', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"HR Staff","job_profile":"JOB-HR-STAFF","organization_unit":"DEPT-HR","reports_to_position":"POS-000004","is_manager_position":false,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000006', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Finance Staff","job_profile":"JOB-FIN-STAFF","organization_unit":"DEPT-FIN","reports_to_position":"POS-000001","is_manager_position":false,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000007', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Purchasing Staff","job_profile":"JOB-PURCH-STAFF","organization_unit":"DEPT-PURCH","reports_to_position":"POS-000001","is_manager_position":false,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000008', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Warehouse Officer","job_profile":"JOB-WH-OFFICER","organization_unit":"DEPT-WH","reports_to_position":"POS-000001","is_manager_position":false,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000009', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Sales Manager","job_profile":"JOB-SALES-MGR","organization_unit":"DEPT-SALES","reports_to_position":"POS-000001","is_manager_position":true,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Position', 'POS-000010', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"position_title":"Sales Staff","job_profile":"JOB-SALES-STAFF","organization_unit":"DEPT-SALES","reports_to_position":"POS-000009","is_manager_position":false,"status":"Filled","effective_from":"2026-01-01","worker_type":"Permanent (PKWTT)","time_type":"Full-time"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0001","full_name":"Ahmad Fadilah","user_id":"it01","gender":"Male","date_of_birth":"1994-06-15","religion":"Islam","nationality":"ID","work_email":"it01@acme.co.id","company":"ACME","organization_unit":"DEPT-IT","position":"POS-000003","job_profile":"JOB-IT-STAFF","reports_to":"EMP-0003","employment_type":"Permanent (PKWTT)","hire_date":"2021-03-01","status":"Active","marital_status":"Single","ptkp_status":"TK/0","cost_center":"CC-400","grade":"G2","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0003","expense_approver":"EMP-0003","nik":"3174061506940001","npwp":"0912345670011000","bpjs_health_no":"00070011001","bpjs_employment_no":"26270011001","mobile":"+628170011001","bank_accounts":[{"bank":"014","account_no":"5270011001","account_holder":"Ahmad Fadilah","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174061506940001"},{"document_type":"NPWP","document_no":"0912345670011000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0002","full_name":"Citra Dewi","user_id":"marketing01","gender":"Female","date_of_birth":"1996-08-20","religion":"Islam","nationality":"ID","work_email":"marketing01@acme.co.id","company":"ACME","organization_unit":"DEPT-SALES","position":"POS-000010","job_profile":"JOB-SALES-STAFF","reports_to":"EMP-0004","employment_type":"Permanent (PKWTT)","hire_date":"2022-01-10","status":"Active","marital_status":"Single","ptkp_status":"TK/0","cost_center":"CC-200","grade":"G2","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0004","expense_approver":"EMP-0004","nik":"3174066008960002","npwp":"0912345670012000","bpjs_health_no":"00070011002","bpjs_employment_no":"26270011002","mobile":"+628170011002","bank_accounts":[{"bank":"014","account_no":"5270011002","account_holder":"Citra Dewi","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174066008960002"},{"document_type":"NPWP","document_no":"0912345670012000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0003","full_name":"Budi Santoso","user_id":"it02","gender":"Male","date_of_birth":"1988-11-05","religion":"Islam","nationality":"ID","work_email":"it02@acme.co.id","company":"ACME","organization_unit":"DEPT-IT","position":"POS-000002","job_profile":"JOB-IT-MGR","reports_to":"EMP-0008","employment_type":"Permanent (PKWTT)","hire_date":"2019-06-01","status":"Active","marital_status":"Married","ptkp_status":"K/2","cost_center":"CC-400","grade":"G4","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0008","expense_approver":"EMP-0008","nik":"3174060511880003","npwp":"0912345670013000","bpjs_health_no":"00030011003","bpjs_employment_no":"26230011003","mobile":"+628130011003","bank_accounts":[{"bank":"008","account_no":"1230011003","account_holder":"Budi Santoso","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174060511880003"},{"document_type":"NPWP","document_no":"0912345670013000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0004', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0004","full_name":"Dian Pratama","user_id":"marketing02","gender":"Male","date_of_birth":"1987-04-12","religion":"Islam","nationality":"ID","work_email":"marketing02@acme.co.id","company":"ACME","organization_unit":"DEPT-SALES","position":"POS-000009","job_profile":"JOB-SALES-MGR","reports_to":"EMP-0008","employment_type":"Permanent (PKWTT)","hire_date":"2018-09-15","status":"Active","marital_status":"Married","ptkp_status":"K/3","cost_center":"CC-200","grade":"G4","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0008","expense_approver":"EMP-0008","nik":"3174061204870004","npwp":"0912345670014000","bpjs_health_no":"00070011004","bpjs_employment_no":"26270011004","mobile":"+628170011004","bank_accounts":[{"bank":"014","account_no":"5270011004","account_holder":"Dian Pratama","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174061204870004"},{"document_type":"NPWP","document_no":"0912345670014000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0005', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0005","full_name":"Eko Prasetyo","user_id":"finance01","gender":"Male","date_of_birth":"1992-12-01","religion":"Islam","nationality":"ID","work_email":"finance01@acme.co.id","company":"ACME","organization_unit":"DEPT-FIN","position":"POS-000006","job_profile":"JOB-FIN-STAFF","reports_to":"EMP-0008","employment_type":"Permanent (PKWTT)","hire_date":"2020-07-01","status":"Active","marital_status":"Married","ptkp_status":"K/1","cost_center":"CC-300","grade":"G2","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0008","expense_approver":"EMP-0008","nik":"3174060112920005","npwp":"0912345670015000","bpjs_health_no":"00070011005","bpjs_employment_no":"26270011005","mobile":"+628170011005","bank_accounts":[{"bank":"014","account_no":"5270011005","account_holder":"Eko Prasetyo","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174060112920005"},{"document_type":"NPWP","document_no":"0912345670015000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0006', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0006","full_name":"Fajar Hidayat","user_id":"purchasing01","gender":"Male","date_of_birth":"1993-02-18","religion":"Islam","nationality":"ID","work_email":"purchasing01@acme.co.id","company":"ACME","organization_unit":"DEPT-PURCH","position":"POS-000007","job_profile":"JOB-PURCH-STAFF","reports_to":"EMP-0008","employment_type":"Permanent (PKWTT)","hire_date":"2021-05-15","status":"Active","marital_status":"Married","ptkp_status":"K/0","cost_center":"CC-300","grade":"G2","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0008","expense_approver":"EMP-0008","nik":"3174061802930006","npwp":"0912345670016000","bpjs_health_no":"00030011006","bpjs_employment_no":"26230011006","mobile":"+628130011006","bank_accounts":[{"bank":"008","account_no":"1230011006","account_holder":"Fajar Hidayat","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174061802930006"},{"document_type":"NPWP","document_no":"0912345670016000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0008', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0008","full_name":"Gita Pertiwi","user_id":"hr02","gender":"Female","date_of_birth":"1985-09-25","religion":"Islam","nationality":"ID","work_email":"hr02@acme.co.id","company":"ACME","organization_unit":"DEPT-HR","position":"POS-000004","job_profile":"JOB-HR-MGR","reports_to":null,"employment_type":"Permanent (PKWTT)","hire_date":"2017-02-01","status":"Active","marital_status":"Married","ptkp_status":"TK/0","cost_center":"CC-300","grade":"G5","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":null,"expense_approver":null,"nik":"3174066509850008","npwp":"0912345670018000","bpjs_health_no":"00070011008","bpjs_employment_no":"26270011008","mobile":"+628170011008","bank_accounts":[{"bank":"014","account_no":"5270011008","account_holder":"Gita Pertiwi","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174066509850008"},{"document_type":"NPWP","document_no":"0912345670018000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0009', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0009","full_name":"Hendra Gunawan","user_id":"hr01","gender":"Male","date_of_birth":"1995-07-30","religion":"Islam","nationality":"ID","work_email":"hr01@acme.co.id","company":"ACME","organization_unit":"DEPT-HR","position":"POS-000005","job_profile":"JOB-HR-STAFF","reports_to":"EMP-0008","employment_type":"Permanent (PKWTT)","hire_date":"2022-04-01","status":"Active","marital_status":"Single","ptkp_status":"TK/1","cost_center":"CC-300","grade":"G2","location":"JKT-HQ","shift_type":"Office 08-17","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0008","expense_approver":"EMP-0008","nik":"3174063007950009","npwp":"0912345670019000","bpjs_health_no":"00030011009","bpjs_employment_no":"26330011009","mobile":"+628130011009","bank_accounts":[{"bank":"002","account_no":"0330011009","account_holder":"Hendra Gunawan","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3174063007950009"},{"document_type":"NPWP","document_no":"0912345670019000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Employee', 'EMP-0010', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"employee_number":"EMP-0010","full_name":"Indra Kusuma","user_id":"warehouse01","gender":"Male","date_of_birth":"1991-10-10","religion":"Islam","nationality":"ID","work_email":"warehouse01@acme.co.id","company":"ACME","organization_unit":"DEPT-WH","position":"POS-000008","job_profile":"JOB-WH-OFFICER","reports_to":"EMP-0008","employment_type":"Permanent (PKWTT)","hire_date":"2020-11-01","status":"Active","marital_status":"Married","ptkp_status":"K/2","cost_center":"CC-110","grade":"G3","location":"CKR-WH","shift_type":"Shift Pagi 06-14","holiday_list":"Libur Nasional 2026","leave_policy":"Standard Leave Policy PKWTT","leave_approver":"EMP-0008","expense_approver":"EMP-0008","nik":"3216061010910010","npwp":"0912345670010000","bpjs_health_no":"00070011010","bpjs_employment_no":"26270011010","mobile":"+628170011010","bank_accounts":[{"bank":"014","account_no":"5270011010","account_holder":"Indra Kusuma","currency":"IDR","is_default":true}],"identity_documents":[{"document_type":"KTP","document_no":"3216061010910010"},{"document_type":"NPWP","document_no":"0912345670010000"}],"attendance_mode":"Assigned location"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CustomerGroup', 'Commercial & Corporate', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Commercial & Corporate","is_group":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CustomerGroup', 'Government & BUMN', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Government & BUMN","is_group":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'CustomerGroup', 'Retail & Individual', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Retail & Individual","is_group":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Customer', 'CUST-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"customer_name":"PT Telekomunikasi Indonesia Tbk","customer_type":"BUMN","customer_group":"Government & BUMN","tax_id":"0010000130093000","receivable_account":"1121","email":"procurement@telkom.co.id","phone":"+62215215123","is_active":true,"tax_id_type":"NPWP","is_pkp":true,"tax_name":"PT Telekomunikasi Indonesia Tbk","nitku":"0010000130093000000000","territory":"Jabodetabek","payment_term":"Net 30","credit_limit":500000000,"account_owner":"EMP-0004","default_price_list":"Standard Selling IDR","default_currency":"IDR","is_internal_customer":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Customer', 'CUST-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"customer_name":"PT Bank Central Asia Tbk","customer_type":"Company","customer_group":"Commercial & Corporate","tax_id":"0013084497092000","receivable_account":"1121","email":"vendor.management@bca.co.id","phone":"+622123588000","is_active":true,"tax_id_type":"NPWP","is_pkp":true,"tax_name":"PT Bank Central Asia Tbk","nitku":"0013084497092000000000","territory":"Jabodetabek","payment_term":"Net 30","credit_limit":300000000,"account_owner":"EMP-0002","default_price_list":"Standard Selling IDR","default_currency":"IDR","is_internal_customer":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Customer', 'CUST-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"customer_name":"PT Astra International Tbk","customer_type":"Company","customer_group":"Commercial & Corporate","tax_id":"0013028387091000","receivable_account":"1121","email":"corpsupply@astra.co.id","phone":"+62216522555","is_active":true,"tax_id_type":"NPWP","is_pkp":true,"tax_name":"PT Astra International Tbk","nitku":"0013028387091000000000","territory":"Jawa Barat","payment_term":"Net 45","credit_limit":100000000,"account_owner":"EMP-0002","default_price_list":"Standard Selling IDR","default_currency":"IDR","is_internal_customer":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'VendorGroup', 'Technology & Hardware', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Technology & Hardware","is_group":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'VendorGroup', 'Office & Operations', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Office & Operations","is_group":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'VendorGroup', 'Logistics & Freight', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Logistics & Freight","is_group":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Vendor', 'SUPP-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"vendor_name":"PT Metrodata Electronics Tbk","vendor_type":"Company","vendor_group":"Technology & Hardware","tax_id":"0013098398092000","payable_account":"2110","email":"enterprise@metrodata.co.id","phone":"+622129345800","is_active":true,"tax_id_type":"NPWP","is_pkp":true,"tax_name":"PT Metrodata Electronics Tbk","nitku":"0013098398092000000000","payment_term":"Net 30","default_withholding":"None","bank_accounts":[{"bank":"014","account_no":"0063001001","account_holder":"PT Metrodata Electronics Tbk","currency":"IDR","is_default":true}],"on_hold":false,"default_currency":"IDR","is_internal_vendor":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Vendor', 'SUPP-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"vendor_name":"PT Indofood CBP Sukses Makmur Tbk","vendor_type":"Company","vendor_group":"Office & Operations","tax_id":"0013374245091000","payable_account":"2110","email":"corporate.sales@icbp.indofood.co.id","phone":"+622157958822","is_active":true,"tax_id_type":"NPWP","is_pkp":true,"tax_name":"PT Indofood CBP Sukses Makmur Tbk","nitku":"0013374245091000000000","payment_term":"Net 30","default_withholding":"None","bank_accounts":[{"bank":"008","account_no":"1220001002","account_holder":"PT Indofood CBP Sukses Makmur Tbk","currency":"IDR","is_default":true}],"on_hold":false,"default_currency":"IDR","is_internal_vendor":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Vendor', 'SUPP-000003', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"vendor_name":"PT Jalur Nugraha Ekakurir (JNE)","vendor_type":"Company","vendor_group":"Logistics & Freight","tax_id":"0013484937092000","payable_account":"2110","email":"corpsales@jne.co.id","phone":"+622129278888","is_active":true,"tax_id_type":"NPWP","is_pkp":true,"tax_name":"PT Jalur Nugraha Ekakurir (JNE)","nitku":"0013484937092000000000","payment_term":"Net 15","default_withholding":"PPh 23","bank_accounts":[{"bank":"014","account_no":"0063001003","account_holder":"PT Jalur Nugraha Ekakurir (JNE)","currency":"IDR","is_default":true}],"on_hold":false,"default_currency":"IDR","is_internal_vendor":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'BankAccount', 'BCA-OPS-01', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_account_code":"BCA-OPS-01","account_name":"BCA Operasional ACME","company":"ACME","bank":"014","account_no":"1234567890","branch":"KCP Sudirman","currency":"IDR","gl_account":"1112","is_default":true,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'BankAccount', 'MANDIRI-OPS-01', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_account_code":"MANDIRI-OPS-01","account_name":"Mandiri Operasional ACME","company":"ACME","bank":"008","account_no":"9876543210","branch":"KCP Thamrin","currency":"IDR","gl_account":"1113","is_default":false,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Warehouse', 'Gudang Utama Cikarang', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"warehouse_name":"Gudang Utama Cikarang","company":"ACME","is_group":false,"inventory_account":"1131","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Warehouse', 'Gudang Transit Jakarta', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"warehouse_name":"Gudang Transit Jakarta","company":"ACME","is_group":false,"inventory_account":"1131","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Warehouse', 'Gudang Retur Cikarang', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"warehouse_name":"Gudang Retur Cikarang","company":"ACME","is_group":false,"inventory_account":"1131","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemGroup', 'Perangkat Komputer & Hardware', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Perangkat Komputer & Hardware","is_group":false,"income_account":"4100","expense_account":"5100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemGroup', 'Aksesoris & Peralatan IT', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Aksesoris & Peralatan IT","is_group":false,"income_account":"4100","expense_account":"5100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ItemGroup', 'Jasa Konsultasi & Maintenance', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"group_name":"Jasa Konsultasi & Maintenance","is_group":false,"income_account":"4200","expense_account":"6100"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Item', 'ITM-SRV-001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item_code":"ITM-SRV-001","item_name":"Server Rack 2U Enterprise Xeon 64GB","item_type":"Goods","item_group":"Perangkat Komputer & Hardware","uom":"UNT","is_sales_item":true,"is_purchase_item":true,"is_stock_item":true,"default_warehouse":"Gudang Utama Cikarang","is_active":true,"income_account":"4100","expense_account":"5100","tax_category":"Subject to VAT","withholding_type":"None"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Item', 'ITM-LAP-001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item_code":"ITM-LAP-001","item_name":"Laptop Business Ultra 14 Core Ultra 7","item_type":"Goods","item_group":"Perangkat Komputer & Hardware","uom":"UNT","is_sales_item":true,"is_purchase_item":true,"is_stock_item":true,"default_warehouse":"Gudang Utama Cikarang","is_active":true,"income_account":"4100","expense_account":"5100","tax_category":"Subject to VAT","withholding_type":"None"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Item', 'ITM-MNT-001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item_code":"ITM-MNT-001","item_name":"Monitor 27 Inch 4K UHD IPS USB-C","item_type":"Goods","item_group":"Perangkat Komputer & Hardware","uom":"UNT","is_sales_item":true,"is_purchase_item":true,"is_stock_item":true,"default_warehouse":"Gudang Utama Cikarang","is_active":true,"income_account":"4100","expense_account":"5100","tax_category":"Subject to VAT","withholding_type":"None"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Item', 'ITM-CAB-001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item_code":"ITM-CAB-001","item_name":"Kabel UTP Cat6 305 Meter Box","item_type":"Goods","item_group":"Aksesoris & Peralatan IT","uom":"BOX","is_sales_item":true,"is_purchase_item":true,"is_stock_item":true,"default_warehouse":"Gudang Utama Cikarang","is_active":true,"income_account":"4100","expense_account":"5100","tax_category":"Subject to VAT","withholding_type":"None"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Item', 'ITM-SVC-001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item_code":"ITM-SVC-001","item_name":"Jasa Implementasi & Setup Sistem ERP","item_type":"Service","item_group":"Jasa Konsultasi & Maintenance","uom":"DAY","is_sales_item":true,"is_purchase_item":false,"is_stock_item":false,"default_warehouse":null,"is_active":true,"income_account":"4200","expense_account":"6100","tax_category":"Subject to VAT","withholding_type":"None"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Item', 'ITM-SVC-002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"item_code":"ITM-SVC-002","item_name":"Jasa Maintenance & Support Bulanan","item_type":"Service","item_group":"Jasa Konsultasi & Maintenance","uom":"MTH","is_sales_item":true,"is_purchase_item":false,"is_stock_item":false,"default_warehouse":null,"is_active":true,"income_account":"4200","expense_account":"6100","tax_category":"Subject to VAT","withholding_type":"None"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'TaxCode', 'PPN-12', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"tax_code":"PPN-12","tax_name":"PPN Keluaran 12%","tax_type":"Output VAT","rate":12.0,"dpp_factor":1,"is_withholding":false,"account":"2151","prepaid_account":null,"tax_object_code":null,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'TaxCode', 'PPN-MASUKAN-12', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"tax_code":"PPN-MASUKAN-12","tax_name":"PPN Masukan 12%","tax_type":"Input VAT","rate":12.0,"dpp_factor":1,"is_withholding":false,"account":"1151","prepaid_account":null,"tax_object_code":null,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'TaxCode', 'PPH23-JASA-2', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"tax_code":"PPH23-JASA-2","tax_name":"PPh 23 Jasa 2%","tax_type":"PPh 23","rate":2.0,"dpp_factor":1,"is_withholding":true,"account":"2152","prepaid_account":"1150","tax_object_code":null,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'TaxCode', 'PPH21-TER', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"tax_code":"PPH21-TER","tax_name":"PPh 21 Potongan Payroll","tax_type":"PPh 21","rate":0.0,"dpp_factor":1,"is_withholding":true,"account":"2150","prepaid_account":null,"tax_object_code":null,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

COMMIT;
