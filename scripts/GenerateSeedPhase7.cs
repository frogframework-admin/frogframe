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
// Phase 7: master & pengaturan yang belum ada di fase 1–6 (dipakai fase 8 dan skenario uji docs/ERP_TEST_SCENARIOS.md)
// Jalankan dari root repo: dotnet run scripts/GenerateSeedPhase7.cs
// ============================================================================

const string tenant = "acme";
const string owner = "admin";

var records = new List<(string DocType, string Name, int DocStatus, object Data)>();
void Add(string docType, string name, object data, int docStatus = 0) => records.Add((docType, name, docStatus, data));

// ----------------------------------------------------------------------------
// 1. Termin pembayaran (dipakai Customer/Vendor di fase 4)
// ----------------------------------------------------------------------------
foreach (var (name, days) in new[] { ("Net 15", 15), ("Net 30", 30), ("Net 45", 45) })
    Add("PaymentTerm", name, new
    {
        term_name = name,
        description = $"Lunas {days} hari setelah tanggal faktur",
        schedule = new[] { new { description = "Pelunasan", invoice_portion = 100m, due_date_basis = "Days after invoice date", credit_days = days } },
        total_portion = 100m,
    });
Add("PaymentTerm", "DP 30% - Net 30", new
{
    term_name = "DP 30% - Net 30",
    description = "Uang muka 30% saat faktur, sisa 70% 30 hari setelah faktur",
    schedule = new[]
    {
        new { description = "Uang muka", invoice_portion = 30m, due_date_basis = "Days after invoice date", credit_days = 0 },
        new { description = "Pelunasan", invoice_portion = 70m, due_date_basis = "Days after invoice date", credit_days = 30 },
    },
    total_portion = 100m,
});

// ----------------------------------------------------------------------------
// 2. CRM: sumber lead, alasan kalah, wilayah, kampanye, rencana komisi
// ----------------------------------------------------------------------------
foreach (var s in new[] { "Website", "Referral", "Exhibition / Event", "Cold Call", "LinkedIn", "Partner" })
    Add("LeadSource", s, new { source_name = s });
foreach (var r in new[] { "Price too high", "Lost to competitor", "No budget", "Postponed by customer", "Requirements not met" })
    Add("LostReason", r, new { reason_name = r });

Add("Territory", "Indonesia", new { territory_name = "Indonesia", parent_territory = (string?)null, is_group = true, territory_manager = "EMP-0004" });
foreach (var (t, mgr) in new[] { ("Jabodetabek", "EMP-0004"), ("Jawa Barat", "EMP-0002"), ("Jawa Timur", "EMP-0002"), ("Sumatera", "EMP-0004") })
    Add("Territory", t, new { territory_name = t, parent_territory = "Indonesia", is_group = false, territory_manager = mgr });

Add("Campaign", "CMP-2026-0001", new
{
    campaign_name = "Webinar Transformasi Digital Q1 2026", campaign_type = "Webinar", status = "Active",
    start_date = "2026-01-05", end_date = "2026-03-31", budgeted_cost = 25000000m, expected_revenue = 750000000m,
    description = "Webinar bulanan untuk CIO/IT Manager perusahaan menengah-besar", sales_rep = "EMP-0004", organization_unit = "DEPT-SALES",
});
Add("Campaign", "CMP-2026-0002", new
{
    campaign_name = "Pameran Indocomtech 2026", campaign_type = "Event", status = "Planned",
    start_date = "2026-10-21", end_date = "2026-10-25", budgeted_cost = 75000000m, expected_revenue = 1500000000m,
    description = "Booth pameran perangkat & jasa IT", sales_rep = "EMP-0002", organization_unit = "DEPT-SALES",
});

Add("CommissionPlan", "Komisi Sales 2026", new
{
    plan_name = "Komisi Sales 2026", rate = 2m, valid_from = "2026-01-01", valid_to = "2026-12-31",
    sales_reps = new[] { new { sales_rep = "EMP-0002", rate = (decimal?)2m }, new { sales_rep = "EMP-0004", rate = (decimal?)1m } },
    expense_account = "6500", payable_account = "2180",
});

// ----------------------------------------------------------------------------
// 3. Lokasi, grade & rentang gaji, siklus penilaian kinerja
// ----------------------------------------------------------------------------
Add("Location", "JKT-HQ", new { location_code = "JKT-HQ", location_name = "Kantor Pusat Jakarta", is_group = false, company = "ACME", address = "Jl. Jenderal Sudirman Kav. 52-53", city = "Jakarta Selatan", province = "DKI Jakarta" });
Add("Location", "CKR-WH", new { location_code = "CKR-WH", location_name = "Gudang Cikarang", is_group = false, company = "ACME", address = "Kawasan Industri GIIC Blok AA No. 5", city = "Bekasi", province = "Jawa Barat" });

foreach (var (code, name, level, min, mid, max) in new[]
{
    ("G1", "Junior Staff", 1, 4500000m, 5750000m, 7000000m),
    ("G2", "Staff", 2, 6000000m, 8000000m, 10000000m),
    ("G3", "Senior Staff / Supervisor", 3, 8500000m, 11250000m, 14000000m),
    ("G4", "Manager", 4, 15000000m, 20000000m, 25000000m),
    ("G5", "Senior Manager", 5, 18000000m, 25000000m, 32000000m),
})
    Add("Grade", code, new
    {
        grade_code = code, grade_name = name, grade_level = level,
        pay_ranges = new[] { new { company = "ACME", currency = "IDR", min_salary = min, mid_salary = mid, max_salary = max, valid_from = "2026-01-01" } },
    });

// Posisi kosong untuk skenario rekrutmen (REQ-2026-00001 → offer → Create employee).
Add("Position", "POS-000011", new
{
    position_title = "Sales Staff (2)", job_profile = "JOB-SALES-STAFF", organization_unit = "DEPT-SALES", reports_to_position = "POS-000009",
    is_manager_position = false, grade = "G2", status = "Vacant", effective_from = "2026-01-01", location = "JKT-HQ", cost_center = "CC-200",
});

Add("AppraisalCycle", "Kinerja 2026 Semester 1", new { cycle_name = "Kinerja 2026 Semester 1", company = "ACME", start_date = "2026-01-01", end_date = "2026-06-30", status = "In progress" });
Add("AppraisalCycle", "Kinerja 2026 Semester 2", new { cycle_name = "Kinerja 2026 Semester 2", company = "ACME", start_date = "2026-07-01", end_date = "2026-12-31", status = "Planned" });

// ----------------------------------------------------------------------------
// 4. Keuangan: jenis klaim, kategori aset, kurs, proyek, dimensi, pengaturan
// ----------------------------------------------------------------------------
foreach (var (name, account, receipt, max, entertainment) in new[]
{
    ("Perjalanan Dinas", "6400", true, 5000000m, false),
    ("Akomodasi Hotel", "6400", true, 2000000m, false),
    ("Transportasi Lokal", "6420", false, 1000000m, false),
    ("Jamuan Klien", "6410", true, 3000000m, true),
    ("Pelatihan & Seminar", "6600", true, 10000000m, false),
})
    Add("ExpenseClaimType", name, new { type_name = name, default_account = account, requires_receipt = receipt, max_amount_per_item = max, is_entertainment = entertainment });

Add("AssetCategory", "Peralatan Kantor & IT", new
{
    category_name = "Peralatan Kantor & IT", depreciation_method = "Straight Line", useful_life_months = 48,
    fiscal_group = "Group 1", fiscal_method = "Straight Line",
    fixed_asset_account = "1210", accumulated_depreciation_account = "1211", depreciation_expense_account = "6300",
    cwip_account = "1230", disposal_gain_loss_account = "8110",
});
Add("AssetCategory", "Kendaraan Operasional", new
{
    category_name = "Kendaraan Operasional", depreciation_method = "Straight Line", useful_life_months = 96,
    fiscal_group = "Group 2", fiscal_method = "Declining Balance",
    fixed_asset_account = "1220", accumulated_depreciation_account = "1221", depreciation_expense_account = "6300",
    cwip_account = "1230", disposal_gain_loss_account = "8110",
});

// Kurs contoh untuk uji transaksi valas (bukan kurs resmi).
var fx = new[]
{
    ("2026-01-02", "BI Middle Rate", "USD", 16250.0), ("2026-01-02", "BI Middle Rate", "SGD", 12100.0), ("2026-01-02", "BI Middle Rate", "EUR", 17600.0),
    ("2026-01-01", "Tax Rate (KMK)", "USD", 16300.0), ("2026-01-30", "BI Middle Rate", "USD", 16400.0),
};
for (var i = 0; i < fx.Length; i++)
    Add("CurrencyExchangeRate", $"FX-2026-{i + 1:D5}", new { rate_date = fx[i].Item1, rate_type = fx[i].Item2, from_currency = fx[i].Item3, to_currency = "IDR", exchange_rate = fx[i].Item4 });

Add("Project", "PRJ-TLK-ERP", new
{
    project_code = "PRJ-TLK-ERP", project_name = "Implementasi ERP Telkom", company = "ACME", customer = "CUST-000001", cost_center = "CC-400",
    status = "In Progress", start_date = "2026-02-01", end_date = "2026-07-31", contract_value = 450000000m, estimated_cost = 300000000m,
});
Add("Project", "PRJ-INT-DC", new
{
    project_code = "PRJ-INT-DC", project_name = "Ekspansi Data Center Cikarang (internal)", company = "ACME", customer = (string?)null, cost_center = "CC-400",
    status = "In Progress", start_date = "2026-01-01", end_date = "2026-06-30", contract_value = (decimal?)null, estimated_cost = 150000000m,
});
Add("AccountingDimension", "PROJECT", new { dimension_code = "PROJECT", label = "Project", source_doctype = "Project" });

Add("FinanceSettings", "FinanceSettings", new
{
    default_output_vat = "PPN-12", default_input_vat = "PPN-MASUKAN-12", auto_create_tax_invoice = true,
    credit_limit_action = "Warn", unique_bill_no_per_vendor = true, three_way_match = "Warn", depreciation_posting_day = "Month end",
});

// ----------------------------------------------------------------------------
// 5. HR: alokasi cuti tahunan 2026 (submitted; tidak memposting buku besar) & kontrak kerja
// Tanpa alokasi, pengajuan Annual Leave ditolak ("There is no Annual Leave allocation for this period").
// Job harian Erp.LeaveAllocation melewati karyawan yang sudah punya alokasi yang mencakup hari ini.
// ----------------------------------------------------------------------------
var employees = new[]
{
    ("EMP-0001", "Ahmad Fadilah"), ("EMP-0002", "Citra Dewi"), ("EMP-0003", "Budi Santoso"), ("EMP-0004", "Dian Pratama"), ("EMP-0005", "Eko Prasetyo"),
    ("EMP-0006", "Fajar Hidayat"), ("EMP-0008", "Gita Pertiwi"), ("EMP-0009", "Hendra Gunawan"), ("EMP-0010", "Indra Kusuma"),
};
for (var i = 0; i < employees.Length; i++)
    Add("LeaveAllocation", $"LAL-2026-{i + 1:D6}", new
    {
        employee = employees[i].Item1, employee_name = employees[i].Item2, leave_type = "Annual Leave",
        from_date = "2026-01-01", to_date = "2026-12-31", leave_policy = "Standard Leave Policy PKWTT",
        new_leaves = 12.0, carry_forwarded = 0.0, total_leaves = 12.0,
    }, docStatus: 1);

var contracts = new[]
{
    ("EMP-0001", "Ahmad Fadilah", "2021-03-01", "JOB-IT-STAFF", 6500000m),
    ("EMP-0002", "Citra Dewi", "2022-01-10", "JOB-SALES-STAFF", 6000000m),
    ("EMP-0009", "Hendra Gunawan", "2022-04-01", "JOB-HR-STAFF", 6000000m),
    ("EMP-0010", "Indra Kusuma", "2020-11-01", "JOB-WH-OFFICER", 7500000m),
};
for (var i = 0; i < contracts.Length; i++)
    Add("EmploymentContract", $"CTR-2026-{i + 1:D5}", new
    {
        employee = contracts[i].Item1, employee_name = contracts[i].Item2, contract_type = "PKWTT", contract_no = $"PKWTT/ACME/HR/{i + 1:D3}",
        start_date = contracts[i].Item3, end_date = (string?)null, probation_months = 3, job_profile = contracts[i].Item4, basic_salary = contracts[i].Item5,
        status = "Active",
    }, docStatus: 1);

// ============================================================================
// Generate SQL script
// ============================================================================
var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var sb = new StringBuilder();
sb.AppendLine("-- ============================================================================");
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 7 (Masters & settings: payment terms, CRM, grades, expense types, asset categories,");
sb.AppendLine("-- FX rates, projects, Finance Settings, leave allocations 2026, employment contracts)");
sb.AppendLine("-- Tenant: acme | Run after phases 1-6. Scenarios: docs/ERP_TEST_SCENARIOS.md");
sb.AppendLine("-- ============================================================================");
sb.AppendLine("BEGIN;");
sb.AppendLine();
foreach (var (docType, name, docStatus, data) in records)
{
    var dataJson = WithDefaults(docType, data, jsonOptions).ToJsonString(jsonOptions).Replace("'", "''");
    var nameEscaped = name.Replace("'", "''");
    sb.AppendLine($"""
        INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
        VALUES ('{tenant}', '{docType}', '{nameEscaped}', '{owner}', {docStatus}, NOW(), NOW(), '{owner}', 1, 1, '{dataJson}'::jsonb)
        ON CONFLICT (tenant_id, doctype, name) DO UPDATE
        SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;
        """);
    sb.AppendLine();
}
sb.AppendLine(SeriesSync);
sb.AppendLine("COMMIT;");
Directory.CreateDirectory("deploy/sql");
File.WriteAllText("deploy/sql/seed-phase-7.sql", sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Successfully generated deploy/sql/seed-phase-7.sql with {records.Count} records.");

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
