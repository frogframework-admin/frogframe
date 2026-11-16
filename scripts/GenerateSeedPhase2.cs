#:property PublishAot=false
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

const string tenant = "acme";
const string owner = "admin";

var records = new List<(string DocType, string Name, object Data)>();

// ============================================================================
// 1. Chart of Accounts (COA) - Indonesia Standard
// ============================================================================

void AddAcc(
    string code,
    string name,
    string rootType,
    string reportType,
    string normalBalance,
    bool isGroup,
    string? parentAccount = null,
    string? accountType = null,
    bool isControlAccount = false,
    bool requiresCostCenter = false)
{
    var acc = new Dictionary<string, object?>
    {
        ["account_code"] = code,
        ["account_name"] = name,
        ["root_type"] = rootType,
        ["report_type"] = reportType,
        ["normal_balance"] = normalBalance,
        ["is_group"] = isGroup,
        ["is_active"] = true,
        ["parent_account"] = parentAccount,
        ["account_type"] = isGroup ? null : accountType,
        ["is_control_account"] = isControlAccount,
        ["requires_cost_center"] = requiresCostCenter
    };
    records.Add(("Account", code, acc));
}

// --- 1. ASET (ASSETS) ---
AddAcc("1000", "Aset", "Asset", "Balance Sheet", "Debit", isGroup: true);

// 1.1 Aset Lancar
AddAcc("1100", "Aset Lancar", "Asset", "Balance Sheet", "Debit", isGroup: true, parentAccount: "1000");

// Kas & Setara Kas
AddAcc("1110", "Kas & Setara Kas", "Asset", "Balance Sheet", "Debit", isGroup: true, parentAccount: "1100");
AddAcc("1111", "Kas Kecil (Petty Cash)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1110", accountType: "Cash");
AddAcc("1112", "Bank BCA Operasional", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1110", accountType: "Bank");
AddAcc("1113", "Bank Mandiri Operasional", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1110", accountType: "Bank");

// Piutang
AddAcc("1120", "Piutang Usaha & Uang Muka", "Asset", "Balance Sheet", "Debit", isGroup: true, parentAccount: "1100");
AddAcc("1121", "Piutang Usaha IDR", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1120", accountType: "Receivable", isControlAccount: true);
AddAcc("1122", "Uang Muka Pembelian (Vendor Advance)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1120", accountType: "Advance");
AddAcc("1123", "Uang Muka Karyawan (Employee Advance)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1120", accountType: "Advance");
AddAcc("1124", "Piutang Karyawan (Pinjaman)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1120", accountType: "Receivable", isControlAccount: true);

// Persediaan
AddAcc("1130", "Persediaan Barang", "Asset", "Balance Sheet", "Debit", isGroup: true, parentAccount: "1100");
AddAcc("1131", "Persediaan Barang Dagang", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1130", accountType: "Inventory");

// Biaya Dibayar Dimuka & Pajak
AddAcc("1140", "Biaya Dibayar Dimuka", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1100", accountType: "Prepaid Expense");
AddAcc("1150", "Pajak Dibayar Dimuka (PPh 22 / PPh 23)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1100", accountType: "Tax");
AddAcc("1151", "PPN Masukan (VAT In)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1100", accountType: "Tax");

// 1.2 Aset Tetap
AddAcc("1200", "Aset Tetap", "Asset", "Balance Sheet", "Debit", isGroup: true, parentAccount: "1000");
AddAcc("1210", "Peralatan Kantor & IT", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1200", accountType: "Fixed Asset");
AddAcc("1211", "Akum. Penyusutan Peralatan Kantor", "Asset", "Balance Sheet", "Credit", isGroup: false, parentAccount: "1200", accountType: "Accumulated Depreciation");
AddAcc("1220", "Kendaraan Operasional", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1200", accountType: "Fixed Asset");
AddAcc("1221", "Akum. Penyusutan Kendaraan", "Asset", "Balance Sheet", "Credit", isGroup: false, parentAccount: "1200", accountType: "Accumulated Depreciation");
AddAcc("1230", "Aset Dalam Penyelesaian (CWIP)", "Asset", "Balance Sheet", "Debit", isGroup: false, parentAccount: "1200", accountType: "Capital Work in Progress");

// --- 2. LIABILITAS (LIABILITIES) ---
AddAcc("2000", "Liabilitas", "Liability", "Balance Sheet", "Credit", isGroup: true);

// 2.1 Liabilitas Jangka Pendek
AddAcc("2100", "Liabilitas Jangka Pendek", "Liability", "Balance Sheet", "Credit", isGroup: true, parentAccount: "2000");
AddAcc("2110", "Hutang Usaha IDR", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Payable", isControlAccount: true);
AddAcc("2120", "Uang Muka Penjualan (Customer Advance)", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Advance");
AddAcc("2130", "Hutang Gaji & Upah (Salary Payable)", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Payable", isControlAccount: true);
// Utang BPJS diposting payroll tanpa pihak (satu saldo ke BPJS), jadi bukan akun kontrol.
AddAcc("2140", "Hutang BPJS Ketenagakerjaan", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Accrual");
AddAcc("2141", "Hutang BPJS Kesehatan", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Accrual");
AddAcc("2150", "Hutang PPh 21", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Tax");
AddAcc("2151", "PPN Keluaran (VAT Out)", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Tax");
AddAcc("2152", "Hutang PPh 23", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Tax");
AddAcc("2160", "Biaya yang Masih Harus Dibayar (Accruals)", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Accrual");
AddAcc("2170", "Barang Diterima Belum Ditagih (GRNI)", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Accrual");
AddAcc("2180", "Hutang Komisi Penjualan", "Liability", "Balance Sheet", "Credit", isGroup: false, parentAccount: "2100", accountType: "Payable", isControlAccount: true);

// --- 3. EKUITAS (EQUITY) ---
AddAcc("3000", "Ekuitas", "Equity", "Balance Sheet", "Credit", isGroup: true);
AddAcc("3100", "Modal Disetor", "Equity", "Balance Sheet", "Credit", isGroup: false, parentAccount: "3000", accountType: "Capital");
AddAcc("3200", "Laba Ditahan (Retained Earnings)", "Equity", "Balance Sheet", "Credit", isGroup: false, parentAccount: "3000", accountType: "Retained Earnings");
AddAcc("3300", "Laba Tahun Berjalan", "Equity", "Balance Sheet", "Credit", isGroup: false, parentAccount: "3000", accountType: "Income Summary");

// --- 4. PENDAPATAN USAHA (REVENUE) ---
AddAcc("4000", "Pendapatan Usaha", "Income", "Profit and Loss", "Credit", isGroup: true);
AddAcc("4100", "Pendapatan Penjualan Produk", "Income", "Profit and Loss", "Credit", isGroup: false, parentAccount: "4000");
AddAcc("4200", "Pendapatan Jasa & Konsultasi", "Income", "Profit and Loss", "Credit", isGroup: false, parentAccount: "4000");
AddAcc("4900", "Potongan & Diskon Penjualan", "Income", "Profit and Loss", "Debit", isGroup: false, parentAccount: "4000");

// --- 5. BEBAN POKOK PENJUALAN (HPP / COGS) ---
AddAcc("5000", "Beban Pokok Penjualan", "Expense", "Profit and Loss", "Debit", isGroup: true);
AddAcc("5100", "Harga Pokok Penjualan (HPP) Barang", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "5000", accountType: "Cost of Goods Sold");
AddAcc("5200", "Selisih Penyesuaian Stok (Stock Adjustment)", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "5000", accountType: "Cost of Goods Sold");

// --- 6. BEBAN OPERASIONAL & UMUM (OPEX) ---
AddAcc("6000", "Beban Operasional & Administrasi", "Expense", "Profit and Loss", "Debit", isGroup: true);
AddAcc("6100", "Beban Gaji & Tunjangan Karyawan", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6110", "Beban BPJS Ketenagakerjaan (Pemberi Kerja)", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6120", "Beban BPJS Kesehatan (Pemberi Kerja)", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6200", "Beban Sewa Gedung & Kantor", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6210", "Beban Listrik, Air & Internet", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6300", "Beban Penyusutan Aset Tetap", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", accountType: "Depreciation Expense", requiresCostCenter: true);
AddAcc("6400", "Beban Perjalanan Dinas & Klaim Karyawan", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6410", "Beban Jamuan & Representasi", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6420", "Beban Transportasi & Pengiriman", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6500", "Beban Komisi Penjualan", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);
AddAcc("6600", "Beban Pelatihan & Pengembangan", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "6000", requiresCostCenter: true);

// --- 8. PENDAPATAN & BEBAN LAIN-LAIN (NON-OPERATING) ---
// Root type akun anak harus sama dengan induknya: pendapatan lain (8000) dan beban lain (8500) dipisah.
AddAcc("8000", "Pendapatan Lain-lain", "Income", "Profit and Loss", "Credit", isGroup: true);
AddAcc("8100", "Pendapatan Bunga Bank", "Income", "Profit and Loss", "Credit", isGroup: false, parentAccount: "8000");
AddAcc("8110", "Laba / Rugi Pelepasan Aset Tetap", "Income", "Profit and Loss", "Credit", isGroup: false, parentAccount: "8000");
AddAcc("8500", "Beban Lain-lain", "Expense", "Profit and Loss", "Debit", isGroup: true);
AddAcc("8200", "Keuntungan / Kerugian Selisih Kurs", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "8500", accountType: "Exchange Gain/Loss");
AddAcc("8300", "Beban Pembulatan (Round-off)", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "8500", accountType: "Round-off");
AddAcc("8400", "Beban Administrasi Bank", "Expense", "Profit and Loss", "Debit", isGroup: false, parentAccount: "8500");

// ============================================================================
// 2. Cost Center
// ============================================================================

void AddCC(string code, string name, bool isGroup, string? parent = null)
{
    var cc = new Dictionary<string, object?>
    {
        ["cost_center_code"] = code,
        ["cost_center_name"] = name,
        ["is_group"] = isGroup,
        ["parent_cost_center"] = parent,
        ["is_active"] = true,
        ["company"] = null // Tenant-wide cost centers
    };
    records.Add(("CostCenter", code, cc));
}

AddCC("CC-000", "Semua Pusat Biaya", isGroup: true);
AddCC("CC-100", "Operasional & Proyek", isGroup: true, parent: "CC-000");
AddCC("CC-110", "Operasional Gudang & Logistik", isGroup: false, parent: "CC-100");
AddCC("CC-120", "Operasional Kantor Pusat", isGroup: false, parent: "CC-100");
AddCC("CC-200", "Penjualan & Pemasaran", isGroup: false, parent: "CC-000");
AddCC("CC-300", "Umum & Administrasi (G&A)", isGroup: false, parent: "CC-000");
AddCC("CC-400", "Teknologi Informasi (IT)", isGroup: false, parent: "CC-000");

// ============================================================================
// 3. Fiscal Year
// ============================================================================

var fiscalYears = new[]
{
    new
    {
        fiscal_year_name = "2026",
        start_date = "2026-01-01",
        end_date = "2026-12-31",
        status = "Open"
    },
    new
    {
        fiscal_year_name = "2027",
        start_date = "2027-01-01",
        end_date = "2027-12-31",
        status = "Open"
    }
};

foreach (var fy in fiscalYears)
{
    records.Add(("FiscalYear", fy.fiscal_year_name, fy));
}

// ============================================================================
// Generate SQL script
// ============================================================================

var jsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var sb = new StringBuilder();
sb.AppendLine("-- ============================================================================");
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 2 (Chart of Accounts, Cost Center, Fiscal Year)");
sb.AppendLine("-- Tenant: acme | Generated for initial setup & unit testing");
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
        SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;
        """);
    sb.AppendLine();
}

sb.AppendLine("COMMIT;");

Directory.CreateDirectory("deploy/sql");
File.WriteAllText("deploy/sql/seed-phase-2.sql", sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"Successfully generated deploy/sql/seed-phase-2.sql with {records.Count} records.");

// Default field dari metadata (src/Apps.Erp/Metadata) diisi bila tidak ditulis di atas: SQL seed melewati framework, jadi tanpa ini
// field seperti is_active (default true) kosong dan dokumen tidak muncul di pilihan Link yang memfilter is_active = true.
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
        if (def.ValueKind == JsonValueKind.String && def.GetString()!.StartsWith('$')) continue; // $today, $employee: diisi saat input
        node[name] = JsonNode.Parse(def.GetRawText());
    }
}
