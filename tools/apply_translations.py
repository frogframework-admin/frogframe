import json
import re
import openpyxl

from translations_hr import HR_TRANSLATIONS
from translations_fin import FIN_TRANSLATIONS
from translations_acc import ACC_TRANSLATIONS
from translations_prc_stk import PRC_STK_TRANSLATIONS
from translations_crm_mdm import CRM_MDM_TRANSLATIONS

ALL_TRANSLATIONS = {}
ALL_TRANSLATIONS.update(HR_TRANSLATIONS)
ALL_TRANSLATIONS.update(FIN_TRANSLATIONS)
ALL_TRANSLATIONS.update(ACC_TRANSLATIONS)
ALL_TRANSLATIONS.update(PRC_STK_TRANSLATIONS)
ALL_TRANSLATIONS.update(CRM_MDM_TRANSLATIONS)

SUB_AREA_MAP = {
    'Activity': 'Activity',
    'Aset Tetap': 'Fixed Assets',
    'Budget': 'Budget',
    'Campaign': 'Campaign',
    'Contract': 'Contract',
    'Customer': 'Customer',
    'Cuti': 'Leave',
    'Expense & Advance': 'Expense & Advance',
    'General Ledger': 'General Ledger',
    'Goods Receipt': 'Goods Receipt',
    'Kas & Bank': 'Cash & Bank',
    'Kehadiran': 'Attendance',
    'Kinerja': 'Performance',
    'Komisi': 'Commission',
    'Kontrak Kerja': 'Employment Contracts',
    'Laporan Keuangan': 'Financial Statements',
    'Lead': 'Lead',
    'Lembur': 'Overtime',
    'Master Data': 'Master Data',
    'Mutasi & Karier': 'Job Changes & Career',
    'Opportunity': 'Opportunity',
    'Organisasi': 'Organization',
    'Pajak': 'Tax',
    'Payroll': 'Payroll',
    'Pelatihan': 'Training',
    'Period Close': 'Period Close',
    'Persediaan': 'Inventory',
    'Piutang (AR)': 'Accounts Receivable (AR)',
    'Proyek': 'Project',
    'Purchase Order': 'Purchase Order',
    'Purchase Request': 'Purchase Request',
    'Quotation': 'Quotation',
    'Rekrutmen': 'Recruitment',
    'Sales Order': 'Sales Order',
    'Turnover': 'Turnover',
    'Utang (AP)': 'Accounts Payable (AP)',
    'Vendor': 'Vendor',
    'Workforce': 'Workforce'
}

UNIT_MAP = {
    '%': '%',
    'Aktivitas': 'Activities',
    'Akun': 'Accounts',
    'Appraisal': 'Appraisals',
    'Aset': 'Assets',
    'Baris': 'Lines',
    'Bulan': 'Months',
    'Cost center': 'Cost centers',
    'Customer': 'Customers',
    'Dokumen': 'Documents',
    'Faktur': 'Tax invoices',
    'Harga': 'Prices',
    'Hari': 'Days',
    'Hari / Rp': 'Days / IDR',
    'Hari kerja': 'Working days',
    'Interview': 'Interviews',
    'Invoice': 'Invoices',
    'Invoice / Rp': 'Invoices / IDR',
    'Item': 'Items',
    'Jam': 'Hours',
    'Jurnal': 'Journals',
    'Kali': 'Times',
    'Kasus': 'Cases',
    'Kejadian': 'Occurrences',
    'Kontak': 'Contacts',
    'Kontrak': 'Contracts',
    'Lead': 'Leads',
    'Level': 'Levels',
    'Menit': 'Minutes',
    'Offer': 'Offers',
    'Opportunity': 'Opportunities',
    'Orang': 'Persons',
    'PR': 'PRs',
    'Pasangan': 'Pairs',
    'Pelamar': 'Applicants',
    'Pembayaran': 'Payments',
    'Pengajuan': 'Requests',
    'Periode': 'Periods',
    'Poin': 'Points',
    'Posisi': 'Positions',
    'Profil': 'Profiles',
    'Proyek': 'Projects',
    'Quotation': 'Quotations',
    'Rasio': 'Ratio',
    'Record': 'Records',
    'Rep': 'Reps',
    'Requisition': 'Requisitions',
    'Rp': 'IDR',
    'Rp / item': 'IDR / item',
    'Rp/hari': 'IDR/day',
    'Rp/orang': 'IDR/person',
    'Skor': 'Score',
    'Skor 1–5': 'Score 1–5',
    'Statement': 'Statements',
    'Tahun': 'Years',
    'Uang muka': 'Advances',
    'Unit': 'Units',
    'Vendor': 'Vendors',
    'Voucher': 'Vouchers',
    'Ya/Tidak': 'Yes/No'
}

DIRECTION_MAP = {
    'Naik': 'Up',
    'Rentang': 'Range',
    'Turun': 'Down'
}

FREQUENCY_MAP = {
    'Bulanan': 'Monthly',
    'Harian': 'Daily',
    'Kuartalan': 'Quarterly',
    'Mingguan': 'Weekly',
    'Per event': 'Per event',
    'Per kampanye': 'Per campaign',
    'Per siklus': 'Per cycle',
    'Tahunan': 'Annual'
}

IMPLEMENTATION_MAP = {
    'Belum tersedia': 'Not available',
    'Laporan bawaan': 'Built-in report',
    'Widget dashboard': 'Dashboard widget'
}

OWNER_ROLE_MAP = {
    'AP Lead': 'AP Lead',
    'AR Lead': 'AR Lead',
    'Account Manager': 'Account Manager',
    'Asset Accountant': 'Asset Accountant',
    'Benefit Admin': 'Benefits Administrator',
    'Buyer': 'Buyer',
    'C&B': 'Compensation & Benefits',
    'CFO': 'CFO',
    'CFO / CHRO': 'CFO / CHRO',
    'CHRO': 'CHRO',
    'CPO': 'CPO',
    'Chief Accountant': 'Chief Accountant',
    'Controller': 'Controller',
    'Controller + HR': 'Controller + HR',
    'Credit Controller': 'Credit Controller',
    'Expense Approver': 'Expense Approver',
    'FP&A': 'FP&A',
    'Finance Ops': 'Finance Operations',
    'Group Controller': 'Group Controller',
    'HR + Controller': 'HR + Controller',
    'HR Admin': 'HR Administrator',
    'HR Manager': 'HR Manager',
    'HR Ops': 'HR Operations',
    'HR Org Design': 'HR Organization Design',
    'HRBP': 'HRBP',
    'Hiring Manager': 'Hiring Manager',
    'Internal Audit': 'Internal Audit',
    'Inventory Controller': 'Inventory Controller',
    'Inventory Planner': 'Inventory Planner',
    'L&D': 'Learning & Development',
    'Leave Approver': 'Leave Approver',
    'MDM': 'Master Data Management',
    'Marketing': 'Marketing',
    'Operations': 'Operations',
    'PMO': 'PMO',
    'Payroll': 'Payroll',
    'Payroll + Controller': 'Payroll + Controller',
    'Payroll Manager': 'Payroll Manager',
    'Procurement': 'Procurement',
    'Procurement + AP': 'Procurement + AP',
    'Procurement Manager': 'Procurement Manager',
    'Project Controller': 'Project Controller',
    'QC / Procurement': 'QC / Procurement',
    'Recruitment Lead': 'Recruitment Lead',
    'Sales Director': 'Sales Director',
    'Sales Manager': 'Sales Manager',
    'Sales Ops': 'Sales Operations',
    'Sales Ops + AR': 'Sales Operations + AR',
    'Sales Rep': 'Sales Representative',
    'Tax': 'Tax',
    'Treasury': 'Treasury',
    'Warehouse': 'Warehouse'
}

TARGET_MAP = {
    '0 (atau dengan approval)': '0 (or with approval)',
    '0 (kecuali diizinkan)': '0 (unless authorized)',
    '0 (sudah ada PR)': '0 (PR already exists)',
    '0 > 7 hari': '0 > 7 days',
    '0 dalam 3 bulan': '0 within 3 months',
    '0 di akhir bulan': '0 at month end',
    '0 di close': '0 at close',
    '0 expired': '0 expired',
    '0 sebelum batas pelaporan': '0 before filing deadline',
    '100% karyawan eligible tercover': '100% eligible employees covered',
    '100% sebelum close': '100% before close',
    '100% sebelum last_working_date': '100% before last working date',
    '5–10 bawahan': '5–10 subordinates',
    '5–10% per tahun': '5–10% per year',
    '< 70% (aset tidak usang)': '< 70% (assets not obsolete)',
    'Ada successor untuk tiap orang': 'Successor identified for each person',
    'Dievaluasi tahunan': 'Evaluated annually',
    'Dievaluasi untuk penggantian': 'Evaluated for replacement',
    'Mendekati 0': 'Near 0',
    'Mendekati 0 / terjelaskan': 'Near 0 / fully explained',
    'Minimal': 'Minimal',
    'Naik QoQ': 'Increase QoQ',
    'Naik YoY': 'Increase YoY',
    'Outstanding ≤ 10%, Unsatisfactory ≤ 5%': 'Outstanding ≤ 10%, Unsatisfactory ≤ 5%',
    'Positif': 'Positive',
    'Referral ≥ 25%': 'Referral ≥ 25%',
    'Selesai ≤ 60 hari setelah tutup tahun': 'Completed ≤ 60 days post year-end',
    'Semua dihubungi': 'All contacted',
    'Semua ditindaklanjuti': 'All followed up',
    'Sesuai TER': 'Per statutory withholding table (TER)',
    'Sesuai budget': 'Within budget',
    'Sesuai budget L&D': 'Within L&D budget',
    'Sesuai budget capex': 'Within capex budget',
    'Sesuai estimasi proyek': 'Per project estimate',
    'Sesuai industri': 'Industry standard',
    'Sesuai jadwal': 'On schedule',
    'Sesuai job profile': 'Per job profile',
    'Sesuai kebijakan': 'Per company policy',
    'Sesuai kebijakan PKWT': 'Per contract employment policy',
    'Sesuai kebijakan efisiensi': 'Per efficiency policy',
    'Sesuai kebijakan hedging': 'Per hedging policy',
    'Sesuai kebijakan hybrid': 'Per hybrid work policy',
    'Sesuai plan': 'Per plan',
    'Sesuai pro-rata waktu': 'Pro-rated across time',
    'Sesuai progress': 'Per project progress',
    'Sesuai provisi': 'Per provisioning policy',
    'Sesuai quota': 'Per sales quota',
    'Sesuai rate PayrollSettings': 'Per PayrollSettings rate',
    'Sesuai rate plan': 'Per commission rate plan',
    'Sesuai rencana headcount': 'Aligned with headcount plan',
    'Sesuai strategi talenta': 'Per talent strategy',
    'Sesuai target': 'Per target',
    'Sesuai target D&I': 'Per D&I target',
    'Sesuai target marketing': 'Per marketing target',
    'Sesuai term (mis. 30–60)': 'Per payment term (e.g. 30–60)',
    'Tersedia sebelum 1 Jan': 'Available before Jan 1',
    'Turun QoQ': 'Decrease QoQ',
    'Turun YoY': 'Decrease YoY',
    'Turun seiring otomatisasi': 'Decrease with automation',
    '± 3% (di luar THR/bonus)': '± 3% (excluding holiday allowance/bonus)',
    '≈ ShiftType.working_hours_per_day': '≈ ShiftType.working_hours_per_day',
    '≈ tarif PPh badan': '≈ Corporate tax rate',
    '≤ 0% (hemat)': '≤ 0% (favorable/savings)',
    '≤ 0,5% nilai stok': '≤ 0.5% stock value',
    '≤ 10 hari': '≤ 10 days',
    '≤ 10% nilai stok': '≤ 10% stock value',
    '≤ 10% total AR': '≤ 10% total AR',
    '≤ 10%/tahun': '≤ 10%/year',
    '≤ 14 jam/minggu (batas regulasi)': '≤ 14 hours/week (regulatory limit)',
    '≤ 2 hari': '≤ 2 days',
    '≤ 24 jam': '≤ 24 hours',
    '≤ 3 hari': '≤ 3 days',
    '≤ 30 hari': '≤ 30 days',
    '≤ 36 bulan': '≤ 36 months',
    '≤ 45 hari': '≤ 45 days',
    '≤ 48 jam': '≤ 48 hours',
    '≤ 5 hari': '≤ 5 days',
    '≤ 5 hari kerja': '≤ 5 working days',
    '≤ 6 hari di akhir tahun': '≤ 6 days at year end',
    '≤ 6 level': '≤ 6 levels',
    '≤ 60 hari': '≤ 60 days',
    '≤ 7 hari': '≤ 7 days',
    '≤ 7%/tahun': '≤ 7%/year',
    '≤ kebijakan diskon': '≤ discount policy',
    '≤ term pembayaran': '≤ payment terms',
    '≥ 1 (bisnis tumbuh)': '≥ 1 (business growing)',
    '≥ 20 jam/tahun': '≥ 20 hours/year',
    '≥ 3 tahun': '≥ 3 years',
    '≥ 3% spend': '≥ 3% spend',
    '≥ 30 hari (resign)': '≥ 30 days (resignation)',
    '≥ 3x': '≥ 3x',
    '≥ 3x target kuartal': '≥ 3x quarterly quota',
    '≥ 40/minggu': '≥ 40/week',
    '≥ 6 bulan': '≥ 6 months',
    '≥ 6x/tahun': '≥ 6x/year',
    '≥ 8% per tahun': '≥ 8% per year',
    '≥ 80%/tahun': '≥ 80%/year',
    '≥ kebutuhan operasional': '≥ operational requirements',
    '≥ margin rencana': '≥ planned margin',
    '≥ target kuartal': '≥ quarterly quota',
}

NOTE_MAP = {
    'Butuh daftar tagihan jatuh tempo pada tanggal payment run dibuat (snapshot). Gunakan laporan bawaan PayableAgingReport.':
        'Requires a list of due bills at the time of payment run creation (snapshot). Use the built-in PayableAgingReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. Biaya proyek diambil dari dimensi project di buku besar. Gunakan laporan bawaan ProfitAndLossReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. Project costs are derived from the project dimension in the general ledger. Use the built-in ProfitAndLossReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. DIO butuh nilai persediaan dan HPP. Gunakan laporan bawaan BalanceSheetReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. DIO requires inventory value and COGS. Use the built-in BalanceSheetReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. Gunakan laporan bawaan BalanceSheetReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. Use the built-in BalanceSheetReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. Gunakan laporan bawaan BudgetVsActualReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. Use the built-in BudgetVsActualReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. Gunakan laporan bawaan ProfitAndLossReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. Use the built-in ProfitAndLossReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. Gunakan laporan bawaan StockBalanceReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. Use the built-in StockBalanceReport.',
    'Butuh data buku besar (frogframe_gl_entries), yang tidak masuk skema reporting sehingga tidak bisa dipakai widget. Gunakan laporan bawaan TrialBalanceReport.':
        'Requires general ledger data (frogframe_gl_entries), which is not included in the reporting schema and cannot be used by widgets. Use the built-in TrialBalanceReport.',
    'Butuh pencocokan baris invoice ke baris PO/GR, sedangkan PurchaseInvoiceItem tidak menyimpan referensi baris PO.':
        'Requires matching invoice lines to PO/GR lines, while PurchaseInvoiceItem does not store PO line references.',
    'Butuh perhitungan jendela diskon per termin (PaymentTermDetail) terhadap tanggal bayar; disarankan dibuat sebagai laporan kustom.':
        'Requires calculating early payment discount windows per term (PaymentTermDetail) against payment dates; recommended to build as a custom report.',
    'Butuh saldo piutang awal periode (snapshot aging), bukan hanya outstanding saat ini. Gunakan laporan bawaan ReceivableAgingReport.':
        'Requires opening accounts receivable balances for the period (snapshot aging), not just current outstanding amounts. Use the built-in ReceivableAgingReport.',
    'Butuh saldo stok per item (stock ledger), yang belum diekspos ke skema reporting. Gunakan laporan bawaan StockBalanceReport.':
        'Requires stock balance per item (stock ledger), which is not yet exposed to the reporting schema. Use the built-in StockBalanceReport.',
    'Butuh saldo stok per item (stock ledger), yang belum diekspos ke skema reporting. Gunakan laporan bawaan StockLedgerReport.':
        'Requires stock balance per item (stock ledger), which is not yet exposed to the reporting schema. Use the built-in StockLedgerReport.',
    'Butuh target penjualan per periode, yang belum ada DocType-nya. Gunakan laporan bawaan SalesPipelineReport.':
        'Requires sales targets per period, for which no DocType currently exists. Use the built-in SalesPipelineReport.',
    'Butuh waktu transisi workflow per dokumen (riwayat versi/audit), yang belum diekspos ke skema reporting.':
        'Requires workflow transition timestamps per document (version/audit trail), which are not yet exposed to the reporting schema.',
    'Field sumbernya disembunyikan dari skema reporting (permLevel 1 atau lampiran), jadi tidak bisa dihitung oleh widget.':
        'Source fields are restricted in reporting schema (permLevel 1 or attachment), so it cannot be computed by a widget.',
    'Format field period pada CommissionStatement bebas (Data), sehingga periode berjalan tidak bisa dicocokkan dengan andal.':
        'The period field format on CommissionStatement is free-form (Data), so the current period cannot be reliably matched.',
    'Kejadian blokir/peringatan budget tidak disimpan sebagai dokumen. Gunakan laporan bawaan BudgetVsActualReport.':
        'Budget blocking or warning events are not stored as documents. Use the built-in BudgetVsActualReport.',
    'Periode mengikuti filter tanggal dashboard (@Start/@End).':
        'Period follows dashboard date filter (@Start/@End).',
    'Snapshot kondisi saat ini; tidak dipengaruhi filter tanggal.':
        'Current snapshot; not affected by date filters.',
    'Tidak ada dokumen pengiriman (Delivery Note) yang mencatat tanggal kirim ke customer.':
        'No delivery note document exists recording dispatch dates to customers.'
}

DASHBOARD_NAME_MAP = {
    'd_kpi_hr_workforce': 'KPI HR · Workforce',
    'd_kpi_hr_organisasi': 'KPI HR · Organization',
    'd_kpi_hr_mutasi_dan_karier': 'KPI HR · Job Changes & Career',
    'd_kpi_hr_kontrak_kerja': 'KPI HR · Employment Contracts',
    'd_kpi_hr_rekrutmen': 'KPI HR · Recruitment',
    'd_kpi_hr_kehadiran': 'KPI HR · Attendance',
    'd_kpi_hr_cuti': 'KPI HR · Leave',
    'd_kpi_hr_lembur': 'KPI HR · Overtime',
    'd_kpi_hr_payroll': 'KPI HR · Payroll',
    'd_kpi_hr_kinerja': 'KPI HR · Performance',
    'd_kpi_hr_pelatihan': 'KPI HR · Training',
    'd_kpi_hr_turnover': 'KPI HR · Turnover',
    'd_kpi_fin_piutang_ar': 'KPI Finance · Accounts Receivable (AR)',
    'd_kpi_fin_utang_ap': 'KPI Finance · Accounts Payable (AP)',
    'd_kpi_fin_kas_dan_bank': 'KPI Finance · Cash & Bank',
    'd_kpi_fin_expense_dan_advance': 'KPI Finance · Expense & Advance',
    'd_kpi_fin_pajak': 'KPI Finance · Tax',
    'd_kpi_fin_aset_tetap': 'KPI Finance · Fixed Assets',
    'd_kpi_acc_general_ledger': 'KPI Accounting · General Ledger',
    'd_kpi_acc_period_close': 'KPI Accounting · Period Close',
    'd_kpi_acc_budget': 'KPI Accounting · Budget',
    'd_kpi_acc_proyek': 'KPI Accounting · Project',
    'd_kpi_prc_purchase_request': 'KPI Procurement · Purchase Request',
    'd_kpi_prc_purchase_order': 'KPI Procurement · Purchase Order',
    'd_kpi_prc_goods_receipt': 'KPI Procurement · Goods Receipt',
    'd_kpi_prc_vendor': 'KPI Procurement · Vendor',
    'd_kpi_stk_persediaan': 'KPI Stock · Inventory',
    'd_kpi_crm_lead': 'KPI CRM · Lead',
    'd_kpi_crm_campaign': 'KPI CRM · Campaign',
    'd_kpi_crm_opportunity': 'KPI CRM · Opportunity',
    'd_kpi_crm_quotation': 'KPI CRM · Quotation',
    'd_kpi_crm_sales_order': 'KPI CRM · Sales Order',
    'd_kpi_crm_customer': 'KPI CRM · Customer',
    'd_kpi_crm_contract': 'KPI CRM · Contract',
    'd_kpi_crm_activity': 'KPI CRM · Activity',
    'd_kpi_crm_komisi': 'KPI CRM · Commission',
    'd_kpi_mdm_master_data': 'KPI Master Data · Master Data'
}

SQL_FALLBACK_REPLACEMENTS = [
    ("''(belum di-assign)''", "''(unassigned)''"),
    ("''(belum dinilai)''", "''(unrated)''"),
    ("''(kosong)''", "''(blank)''"),
    ("''(tanpa alasan)''", "''(no reason)''"),
    ("''(tanpa grup)''", "''(no group)''"),
    ("''(tanpa proyek)''", "''(no project)''"),
    ("''(tanpa sales)''", "''(no sales)''"),
    ("''(tanpa wilayah)''", "''(no territory)''"),
    ("'(belum di-assign)'", "'(unassigned)'"),
    ("'(belum dinilai)'", "'(unrated)'"),
    ("'(kosong)'", "'(blank)'"),
    ("'(tanpa alasan)'", "'(no reason)'"),
    ("'(tanpa grup)'", "'(no group)'"),
    ("'(tanpa proyek)'", "'(no project)'"),
    ("'(tanpa sales)'", "'(no sales)'"),
    ("'(tanpa wilayah)'", "'(no territory)'")
]

def clean_sql_fallbacks(sql: str) -> str:
    for old, new in SQL_FALLBACK_REPLACEMENTS:
        sql = sql.replace(old, new)
    return sql

print("1. Translating seed-kpi-definitions.sql...")
with open('deploy/sql/seed-kpi-definitions.sql', 'r', encoding='utf-8') as f:
    seed_sql = f.read()

new_seed_lines = []
translated_kpis_map = {}

for line in seed_sql.splitlines():
    if not line.startswith('INSERT INTO frogframe_documents'):
        new_seed_lines.append(line)
        continue
    
    idx1 = line.find("'{")
    idx2 = line.rfind("}'::jsonb")
    if idx1 == -1 or idx2 == -1:
        new_seed_lines.append(line)
        continue
    
    prefix = line[:idx1+1]
    suffix = line[idx2+1:]
    raw_json = line[idx1+1:idx2+1].replace("''", "'")
    data = json.loads(raw_json)
    
    kpi_code = data['kpi_code']
    tr = ALL_TRANSLATIONS.get(kpi_code, {})
    
    data['kpi_name'] = tr.get('name', data['kpi_name'])
    data['sub_area'] = SUB_AREA_MAP.get(data.get('sub_area'), data.get('sub_area'))
    data['definition'] = tr.get('definition', data['definition'])
    data['formula'] = tr.get('formula', data['formula'])
    data['unit'] = UNIT_MAP.get(data.get('unit'), data.get('unit'))
    data['direction'] = DIRECTION_MAP.get(data.get('direction'), data.get('direction'))
    data['target'] = TARGET_MAP.get(data.get('target'), data.get('target'))
    data['frequency'] = FREQUENCY_MAP.get(data.get('frequency'), data.get('frequency'))
    data['owner_role'] = OWNER_ROLE_MAP.get(data.get('owner_role'), data.get('owner_role'))
    data['implementation'] = IMPLEMENTATION_MAP.get(data.get('implementation'), data.get('implementation'))
    if data.get('implementation_note'):
        data['implementation_note'] = NOTE_MAP.get(data['implementation_note'], data['implementation_note'])
    if data.get('widget_sql'):
        data['widget_sql'] = clean_sql_fallbacks(data['widget_sql'])
    
    translated_kpis_map[kpi_code] = data
    if data.get('widget_id'):
        translated_kpis_map[data['widget_id']] = data
    
    new_json_str = json.dumps(data, ensure_ascii=False)
    # escape single quotes for SQL string literal
    escaped_json_str = new_json_str.replace("'", "''")
    new_seed_lines.append(f"{prefix}{escaped_json_str}{suffix}")

with open('deploy/sql/seed-kpi-definitions.sql', 'w', encoding='utf-8') as f:
    f.write('\n'.join(new_seed_lines) + '\n')
print("seed-kpi-definitions.sql updated successfully.")

print("2. Translating kpi_widgets_dashboards.sql...")
with open('deploy/sql/kpi_widgets_dashboards.sql', 'r', encoding='utf-8') as f:
    widgets_sql = f.read()

new_widget_lines = []
for line in widgets_sql.splitlines():
    if line.startswith('INSERT INTO frogframe_dashboard_widgets'):
        # Parse: VALUES (:'tenant', 'w_id', 'w_name', 'w_desc', 'config'::jsonb, :'owner', now(), now(), 1)
        m = re.search(r"VALUES\s*\([^,]+,\s*'([^']+)',\s*'([^']+)',\s*'([^']*)',\s*'(.*)'::jsonb,\s*:'owner'", line)
        if m:
            w_id, w_name, w_desc, raw_config = m.groups()
            k = translated_kpis_map.get(w_id)
            if k:
                # Construct clean English widget name
                # If original had unit parentheses at end, use (k['unit'])
                if '(' in w_name and w_name.endswith(')'):
                    new_w_name = f"{k['kpi_code']} {k['kpi_name']} ({k['unit']})"
                else:
                    new_w_name = f"{k['kpi_code']} {k['kpi_name']}"
                
                # Construct clean English widget description
                new_w_desc = f"{k['definition']} Target: {k['target']}. Direction: {k['direction']}. {k['implementation_note']}"
                
                # Clean SQL in raw_config
                clean_config = clean_sql_fallbacks(raw_config)
                # Translate parameter labels:
                clean_config = clean_config.replace('"label": "Dari tanggal"', '"label": "Start date"')
                clean_config = clean_config.replace('"label": "Sampai tanggal"', '"label": "End date"')
                
                # Escape single quotes for SQL
                esc_w_name = new_w_name.replace("'", "''")
                esc_w_desc = new_w_desc.replace("'", "''")
                
                new_line = (
                    f"INSERT INTO frogframe_dashboard_widgets (tenant_id, id, name, description, config, created_by, created_at, updated_at, version) "
                    f"VALUES (:'tenant', '{w_id}', '{esc_w_name}', '{esc_w_desc}', '{clean_config}'::jsonb, :'owner', now(), now(), 1) "
                    f"ON CONFLICT (tenant_id, id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description, config = EXCLUDED.config, updated_at = now(), version = frogframe_dashboard_widgets.version + 1;"
                )
                new_widget_lines.append(new_line)
                continue
    elif line.startswith('INSERT INTO frogframe_dashboards'):
        m = re.search(r"VALUES\s*\([^,]+,\s*'([^']+)',\s*'([^']+)',\s*'([^']+)'", line)
        if m:
            d_id, d_name, d_desc = m.groups()
            new_d_name = DASHBOARD_NAME_MAP.get(d_id, d_name)
            # Translate description: e.g. "15 KPI dari katalog KPI FrogFrame (HR)." -> "15 KPIs from the FrogFrame KPI catalog (HR)."
            new_d_desc = re.sub(
                r"(\d+)\s+KPI dari katalog KPI FrogFrame\s*\(([^)]+)\)\.",
                r"\1 KPIs from the FrogFrame KPI catalog (\2).",
                d_desc
            )
            line = line.replace(f"'{d_name}'", f"'{new_d_name}'")
            line = line.replace(f"'{d_desc}'", f"'{new_d_desc}'")
    
    new_widget_lines.append(line)

with open('deploy/sql/kpi_widgets_dashboards.sql', 'w', encoding='utf-8') as f:
    f.write('\n'.join(new_widget_lines) + '\n')
print("kpi_widgets_dashboards.sql updated successfully.")

print("3. Updating deploy/data/KpiDefinition_import.xlsx...")
wb = openpyxl.load_workbook('deploy/data/KpiDefinition_import.xlsx')
ws = wb.active

for row in range(2, ws.max_row + 1):
    kpi_code = ws.cell(row, 1).value
    if not kpi_code or kpi_code not in translated_kpis_map:
        continue
    k = translated_kpis_map[kpi_code]
    ws.cell(row, 2).value = k['kpi_name']
    ws.cell(row, 4).value = k['sub_area']
    ws.cell(row, 5).value = 'Yes' if ws.cell(row, 5).value in ('Ya', 'Yes', True) else 'No'
    ws.cell(row, 6).value = k['definition']
    ws.cell(row, 7).value = k['formula']
    ws.cell(row, 8).value = k['unit']
    ws.cell(row, 9).value = k['direction']
    ws.cell(row, 10).value = k['target']
    ws.cell(row, 11).value = k['frequency']
    ws.cell(row, 12).value = k['owner_role']
    ws.cell(row, 16).value = k['implementation']
    if k.get('implementation_note'):
        ws.cell(row, 19).value = k['implementation_note']
    if k.get('widget_sql'):
        ws.cell(row, 20).value = k['widget_sql']

wb.save('deploy/data/KpiDefinition_import.xlsx')
print("deploy/data/KpiDefinition_import.xlsx updated successfully.")
print("All KPI translations applied successfully!")
