-- ============================================================================
-- FrogFrame ERP Seed Data: Phase 1 (Standalone Master Data)
-- Tenant: acme | Generated for initial setup & unit testing
-- ============================================================================
BEGIN;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Currency', 'IDR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"currency_code":"IDR","currency_name":"Indonesian Rupiah","symbol":"Rp","decimals":2,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Currency', 'USD', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"currency_code":"USD","currency_name":"US Dollar","symbol":"$","decimals":2,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Currency', 'EUR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"currency_code":"EUR","currency_name":"Euro","symbol":"€","decimals":2,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Currency', 'SGD', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"currency_code":"SGD","currency_name":"Singapore Dollar","symbol":"S$","decimals":2,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Currency', 'JPY', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"currency_code":"JPY","currency_name":"Japanese Yen","symbol":"¥","decimals":0,"is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'HolidayList', 'Libur Nasional 2026', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"holiday_list_name":"Libur Nasional 2026","from_date":"2026-01-01","to_date":"2026-12-31","weekly_off":"Saturday & Sunday","total_holidays":17,"holidays":[{"idx":1,"holiday_date":"2026-01-01","description":"Tahun Baru 2026 Masehi","holiday_type":"Public holiday"},{"idx":2,"holiday_date":"2026-01-16","description":"Isra Mi''raj Nabi Muhammad SAW","holiday_type":"Public holiday"},{"idx":3,"holiday_date":"2026-02-17","description":"Tahun Baru Imlek 2577 Kongzili","holiday_type":"Public holiday"},{"idx":4,"holiday_date":"2026-03-20","description":"Hari Suci Nyepi (Tahun Baru Saka 1948)","holiday_type":"Public holiday"},{"idx":5,"holiday_date":"2026-03-21","description":"Hari Raya Idul Fitri 1447 H (Hari Pertama)","holiday_type":"Public holiday"},{"idx":6,"holiday_date":"2026-03-22","description":"Hari Raya Idul Fitri 1447 H (Hari Kedua)","holiday_type":"Public holiday"},{"idx":7,"holiday_date":"2026-04-03","description":"Wafat Yesus Kristus (Jumat Agung)","holiday_type":"Public holiday"},{"idx":8,"holiday_date":"2026-04-05","description":"Hari Paskah","holiday_type":"Public holiday"},{"idx":9,"holiday_date":"2026-05-01","description":"Hari Buruh Internasional","holiday_type":"Public holiday"},{"idx":10,"holiday_date":"2026-05-14","description":"Kenaikan Yesus Kristus","holiday_type":"Public holiday"},{"idx":11,"holiday_date":"2026-05-27","description":"Hari Raya Idul Adha 1447 H","holiday_type":"Public holiday"},{"idx":12,"holiday_date":"2026-05-31","description":"Hari Raya Waisak 2570 BE","holiday_type":"Public holiday"},{"idx":13,"holiday_date":"2026-06-01","description":"Hari Lahir Pancasila","holiday_type":"Public holiday"},{"idx":14,"holiday_date":"2026-06-16","description":"Tahun Baru Islam 1448 H","holiday_type":"Public holiday"},{"idx":15,"holiday_date":"2026-08-17","description":"Hari Kemerdekaan RI ke-81","holiday_type":"Public holiday"},{"idx":16,"holiday_date":"2026-08-25","description":"Maulid Nabi Muhammad SAW","holiday_type":"Public holiday"},{"idx":17,"holiday_date":"2026-12-25","description":"Hari Raya Natal","holiday_type":"Public holiday"}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Bank', '014', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_code":"014","bank_name":"PT Bank Central Asia Tbk","swift_code":"CENAIDJA","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Bank', '008', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_code":"008","bank_name":"PT Bank Mandiri (Persero) Tbk","swift_code":"BMRIIDJA","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Bank', '002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_code":"002","bank_name":"PT Bank Rakyat Indonesia (Persero) Tbk","swift_code":"BRINIDJA","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Bank', '009', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_code":"009","bank_name":"PT Bank Negara Indonesia (Persero) Tbk","swift_code":"BNINIDJA","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Bank', '451', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_code":"451","bank_name":"PT Bank Syariah Indonesia Tbk","swift_code":"BSMDIDJA","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Bank', '022', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"bank_code":"022","bank_name":"PT Bank CIMB Niaga Tbk","swift_code":"BNGAIDJA","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'PCS', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"PCS","uom_name":"Pieces","must_be_whole_number":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'BOX', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"BOX","uom_name":"Box","must_be_whole_number":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'KG', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"KG","uom_name":"Kilogram","must_be_whole_number":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'MTR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"MTR","uom_name":"Meter","must_be_whole_number":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'UNT', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"UNT","uom_name":"Unit","must_be_whole_number":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'SET', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"SET","uom_name":"Set","must_be_whole_number":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'LTR', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"LTR","uom_name":"Liter","must_be_whole_number":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'HRS', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"HRS","uom_name":"Hours","must_be_whole_number":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'DAY', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"DAY","uom_name":"Days","must_be_whole_number":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'UOM', 'MTH', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"uom_code":"MTH","uom_name":"Months","must_be_whole_number":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Industry', 'Information Technology', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"industry_name":"Information Technology","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Industry', 'Manufacturing', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"industry_name":"Manufacturing","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Industry', 'Retail & Distribution', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"industry_name":"Retail & Distribution","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Industry', 'Financial Services', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"industry_name":"Financial Services","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Industry', 'Healthcare & Pharmaceuticals', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"industry_name":"Healthcare & Pharmaceuticals","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Industry', 'Logistics & Transportation', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"industry_name":"Logistics & Transportation","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesStage', 'Prospecting', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"stage_name":"Prospecting","sequence":1,"stage_type":"Open","probability":10,"forecast_category":"Pipeline","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesStage', 'Qualification', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"stage_name":"Qualification","sequence":2,"stage_type":"Open","probability":25,"forecast_category":"Pipeline","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesStage', 'Proposal', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"stage_name":"Proposal","sequence":3,"stage_type":"Open","probability":50,"forecast_category":"Best Case","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesStage', 'Negotiation', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"stage_name":"Negotiation","sequence":4,"stage_type":"Open","probability":80,"forecast_category":"Commit","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesStage', 'Closed Won', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"stage_name":"Closed Won","sequence":5,"stage_type":"Won","probability":100,"forecast_category":"Closed","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'SalesStage', 'Closed Lost', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"stage_name":"Closed Lost","sequence":6,"stage_type":"Lost","probability":0,"forecast_category":"Omitted","is_active":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ShiftType', 'Office 08-17', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"shift_name":"Office 08-17","start_time":"08:00:00","end_time":"17:00:00","break_minutes":60,"working_hours_per_day":8,"working_days_per_week":"5","late_grace_minutes":15,"is_night_shift":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ShiftType', 'Shift Pagi 06-14', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"shift_name":"Shift Pagi 06-14","start_time":"06:00:00","end_time":"14:00:00","break_minutes":60,"working_hours_per_day":7,"working_days_per_week":"6","late_grace_minutes":10,"is_night_shift":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ShiftType', 'Shift Siang 14-22', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"shift_name":"Shift Siang 14-22","start_time":"14:00:00","end_time":"22:00:00","break_minutes":60,"working_hours_per_day":7,"working_days_per_week":"6","late_grace_minutes":10,"is_night_shift":false}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'ShiftType', 'Shift Malam 22-06', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"shift_name":"Shift Malam 22-06","start_time":"22:00:00","end_time":"06:00:00","break_minutes":60,"working_hours_per_day":7,"working_days_per_week":"6","late_grace_minutes":10,"is_night_shift":true}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Address', 'ADDR-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"address_title":"Kantor Pusat Sudirman","address_type":"Office","address_line1":"Jl. Jenderal Sudirman Kav. 52-53","address_line2":"Gedung Bursa Efek Indonesia Tower 2 Lt. 17","city":"Jakarta Selatan","province":"DKI Jakarta","postal_code":"12190","country":"ID","phone":"+622151501234","email":"info@acme.co.id","is_primary":true,"is_tax_address":true,"links":[{"idx":1,"link_doctype":"Company","link_name":"ACME"}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Address', 'ADDR-000002', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"address_title":"Gudang Logistik Cikarang","address_type":"Warehouse","address_line1":"Kawasan Industri GIIC Blok AA No. 5","address_line2":"Kota Deltamas","city":"Bekasi","province":"Jawa Barat","postal_code":"17530","country":"ID","phone":"+622189901234","email":"warehouse.cikarang@acme.co.id","is_primary":false,"is_tax_address":false,"links":[{"idx":1,"link_doctype":"Company","link_name":"ACME"}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'Contact', 'CONT-000001', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"salutation":"Mr.","full_name":"Bambang Pristiwanto","designation":"Direktur Utama","department":"Executive Management","email":"bpristiwanto@gmail.com","phone":"+6281234567890","is_primary":true,"is_active":true,"links":[{"idx":1,"link_doctype":"Company","link_name":"ACME"}]}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES ('acme', 'PayrollSettings', 'PayrollSettings', 'admin', 0, NOW(), NOW(), 'admin', 1, 1, '{"jht_employer_rate":3.7,"jht_employee_rate":2.0,"jkk_rate":0.24,"jkm_rate":0.3,"jp_employer_rate":2.0,"jp_employee_rate":1.0,"jp_wage_cap":11086300,"kes_employer_rate":4.0,"kes_employee_rate":1.0,"kes_wage_cap":12000000,"position_cost_rate":5.0,"position_cost_monthly_cap":500000,"deduct_unpaid_leave":true,"deduct_absence":false,"ptkp_rates":[{"idx":1,"ptkp_status":"TK/0","amount":54000000,"ter_category":"A"},{"idx":2,"ptkp_status":"TK/1","amount":58500000,"ter_category":"A"},{"idx":3,"ptkp_status":"TK/2","amount":63000000,"ter_category":"B"},{"idx":4,"ptkp_status":"TK/3","amount":67500000,"ter_category":"B"},{"idx":5,"ptkp_status":"K/0","amount":58500000,"ter_category":"A"},{"idx":6,"ptkp_status":"K/1","amount":63000000,"ter_category":"B"},{"idx":7,"ptkp_status":"K/2","amount":67500000,"ter_category":"B"},{"idx":8,"ptkp_status":"K/3","amount":72000000,"ter_category":"C"}],"tax_brackets":[{"idx":1,"up_to":60000000,"rate":5},{"idx":2,"up_to":250000000,"rate":15},{"idx":3,"up_to":500000000,"rate":25},{"idx":4,"up_to":5000000000,"rate":30},{"idx":5,"up_to":0,"rate":35}],"ter_rates":[{"idx":1,"ter_category":"A","up_to":5400000,"rate":0},{"idx":2,"ter_category":"A","up_to":5650000,"rate":0.25},{"idx":3,"ter_category":"A","up_to":5950000,"rate":0.5},{"idx":4,"ter_category":"A","up_to":6300000,"rate":0.75},{"idx":5,"ter_category":"A","up_to":6750000,"rate":1},{"idx":6,"ter_category":"A","up_to":7500000,"rate":1.25},{"idx":7,"ter_category":"A","up_to":8550000,"rate":1.5},{"idx":8,"ter_category":"A","up_to":9650000,"rate":1.75},{"idx":9,"ter_category":"A","up_to":10050000,"rate":2},{"idx":10,"ter_category":"A","up_to":10350000,"rate":2.25},{"idx":11,"ter_category":"A","up_to":10700000,"rate":2.5},{"idx":12,"ter_category":"A","up_to":11050000,"rate":3},{"idx":13,"ter_category":"A","up_to":11600000,"rate":3.5},{"idx":14,"ter_category":"A","up_to":12500000,"rate":4},{"idx":15,"ter_category":"A","up_to":13750000,"rate":5},{"idx":16,"ter_category":"A","up_to":15100000,"rate":6},{"idx":17,"ter_category":"A","up_to":16950000,"rate":7},{"idx":18,"ter_category":"A","up_to":19750000,"rate":8},{"idx":19,"ter_category":"A","up_to":24150000,"rate":9},{"idx":20,"ter_category":"A","up_to":26450000,"rate":10},{"idx":21,"ter_category":"A","up_to":28000000,"rate":11},{"idx":22,"ter_category":"A","up_to":30050000,"rate":12},{"idx":23,"ter_category":"A","up_to":32400000,"rate":13},{"idx":24,"ter_category":"A","up_to":35400000,"rate":14},{"idx":25,"ter_category":"A","up_to":39100000,"rate":15},{"idx":26,"ter_category":"A","up_to":43850000,"rate":16},{"idx":27,"ter_category":"A","up_to":47800000,"rate":17},{"idx":28,"ter_category":"A","up_to":51400000,"rate":18},{"idx":29,"ter_category":"A","up_to":56300000,"rate":19},{"idx":30,"ter_category":"A","up_to":62200000,"rate":20},{"idx":31,"ter_category":"A","up_to":68600000,"rate":21},{"idx":32,"ter_category":"A","up_to":77500000,"rate":22},{"idx":33,"ter_category":"A","up_to":89000000,"rate":23},{"idx":34,"ter_category":"A","up_to":103000000,"rate":24},{"idx":35,"ter_category":"A","up_to":125000000,"rate":25},{"idx":36,"ter_category":"A","up_to":157000000,"rate":26},{"idx":37,"ter_category":"A","up_to":206000000,"rate":27},{"idx":38,"ter_category":"A","up_to":337000000,"rate":28},{"idx":39,"ter_category":"A","up_to":454000000,"rate":29},{"idx":40,"ter_category":"A","up_to":550000000,"rate":30},{"idx":41,"ter_category":"A","up_to":695000000,"rate":31},{"idx":42,"ter_category":"A","up_to":910000000,"rate":32},{"idx":43,"ter_category":"A","up_to":1400000000,"rate":33},{"idx":44,"ter_category":"A","up_to":0,"rate":34},{"idx":45,"ter_category":"B","up_to":6200000,"rate":0},{"idx":46,"ter_category":"B","up_to":6500000,"rate":0.25},{"idx":47,"ter_category":"B","up_to":6850000,"rate":0.5},{"idx":48,"ter_category":"B","up_to":7300000,"rate":0.75},{"idx":49,"ter_category":"B","up_to":9200000,"rate":1},{"idx":50,"ter_category":"B","up_to":10750000,"rate":1.5},{"idx":51,"ter_category":"B","up_to":11250000,"rate":2},{"idx":52,"ter_category":"B","up_to":11600000,"rate":2.5},{"idx":53,"ter_category":"B","up_to":12600000,"rate":3},{"idx":54,"ter_category":"B","up_to":13600000,"rate":4},{"idx":55,"ter_category":"B","up_to":14950000,"rate":5},{"idx":56,"ter_category":"B","up_to":16400000,"rate":6},{"idx":57,"ter_category":"B","up_to":18450000,"rate":7},{"idx":58,"ter_category":"B","up_to":21850000,"rate":8},{"idx":59,"ter_category":"B","up_to":26000000,"rate":9},{"idx":60,"ter_category":"B","up_to":27700000,"rate":10},{"idx":61,"ter_category":"B","up_to":29350000,"rate":11},{"idx":62,"ter_category":"B","up_to":31450000,"rate":12},{"idx":63,"ter_category":"B","up_to":33950000,"rate":13},{"idx":64,"ter_category":"B","up_to":37100000,"rate":14},{"idx":65,"ter_category":"B","up_to":41100000,"rate":15},{"idx":66,"ter_category":"B","up_to":45800000,"rate":16},{"idx":67,"ter_category":"B","up_to":49500000,"rate":17},{"idx":68,"ter_category":"B","up_to":53800000,"rate":18},{"idx":69,"ter_category":"B","up_to":58500000,"rate":19},{"idx":70,"ter_category":"B","up_to":64000000,"rate":20},{"idx":71,"ter_category":"B","up_to":71000000,"rate":21},{"idx":72,"ter_category":"B","up_to":80000000,"rate":22},{"idx":73,"ter_category":"B","up_to":93000000,"rate":23},{"idx":74,"ter_category":"B","up_to":109000000,"rate":24},{"idx":75,"ter_category":"B","up_to":129000000,"rate":25},{"idx":76,"ter_category":"B","up_to":163000000,"rate":26},{"idx":77,"ter_category":"B","up_to":211000000,"rate":27},{"idx":78,"ter_category":"B","up_to":374000000,"rate":28},{"idx":79,"ter_category":"B","up_to":459000000,"rate":29},{"idx":80,"ter_category":"B","up_to":555000000,"rate":30},{"idx":81,"ter_category":"B","up_to":704000000,"rate":31},{"idx":82,"ter_category":"B","up_to":957000000,"rate":32},{"idx":83,"ter_category":"B","up_to":1405000000,"rate":33},{"idx":84,"ter_category":"B","up_to":0,"rate":34},{"idx":85,"ter_category":"C","up_to":6600000,"rate":0},{"idx":86,"ter_category":"C","up_to":6950000,"rate":0.25},{"idx":87,"ter_category":"C","up_to":7350000,"rate":0.5},{"idx":88,"ter_category":"C","up_to":7800000,"rate":0.75},{"idx":89,"ter_category":"C","up_to":8850000,"rate":1},{"idx":90,"ter_category":"C","up_to":9800000,"rate":1.25},{"idx":91,"ter_category":"C","up_to":10950000,"rate":1.5},{"idx":92,"ter_category":"C","up_to":11200000,"rate":1.75},{"idx":93,"ter_category":"C","up_to":12050000,"rate":2},{"idx":94,"ter_category":"C","up_to":12950000,"rate":3},{"idx":95,"ter_category":"C","up_to":14150000,"rate":4},{"idx":96,"ter_category":"C","up_to":15550000,"rate":5},{"idx":97,"ter_category":"C","up_to":17050000,"rate":6},{"idx":98,"ter_category":"C","up_to":19500000,"rate":7},{"idx":99,"ter_category":"C","up_to":22700000,"rate":8},{"idx":100,"ter_category":"C","up_to":24700000,"rate":9},{"idx":101,"ter_category":"C","up_to":26350000,"rate":10},{"idx":102,"ter_category":"C","up_to":28000000,"rate":11},{"idx":103,"ter_category":"C","up_to":30100000,"rate":12},{"idx":104,"ter_category":"C","up_to":32600000,"rate":13},{"idx":105,"ter_category":"C","up_to":35400000,"rate":14},{"idx":106,"ter_category":"C","up_to":38900000,"rate":15},{"idx":107,"ter_category":"C","up_to":43200000,"rate":16},{"idx":108,"ter_category":"C","up_to":46850000,"rate":17},{"idx":109,"ter_category":"C","up_to":51000000,"rate":18},{"idx":110,"ter_category":"C","up_to":55500000,"rate":19},{"idx":111,"ter_category":"C","up_to":60800000,"rate":20},{"idx":112,"ter_category":"C","up_to":67100000,"rate":21},{"idx":113,"ter_category":"C","up_to":75700000,"rate":22},{"idx":114,"ter_category":"C","up_to":85500000,"rate":23},{"idx":115,"ter_category":"C","up_to":101100000,"rate":24},{"idx":116,"ter_category":"C","up_to":120300000,"rate":25},{"idx":117,"ter_category":"C","up_to":154400000,"rate":26},{"idx":118,"ter_category":"C","up_to":197700000,"rate":27},{"idx":119,"ter_category":"C","up_to":241100000,"rate":28},{"idx":120,"ter_category":"C","up_to":327500000,"rate":29},{"idx":121,"ter_category":"C","up_to":414000000,"rate":30},{"idx":122,"ter_category":"C","up_to":497700000,"rate":31},{"idx":123,"ter_category":"C","up_to":657900000,"rate":32},{"idx":124,"ter_category":"C","up_to":895300000,"rate":33},{"idx":125,"ter_category":"C","up_to":1411400000,"rate":34},{"idx":126,"ter_category":"C","up_to":0,"rate":35}],"base_salary_component":"Gaji Pokok","overtime_component":"Uang Lembur","salary_expense_account":"6100","employer_contribution_account":"6110","salary_payable_account":"2130","pph21_payable_account":"2150","bpjs_employment_payable_account":"2140","bpjs_health_payable_account":"2141","dtp_enabled":false,"dtp_gross_limit":10000000,"dtp_valid_until":"2026-12-31"}'::jsonb)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

COMMIT;
