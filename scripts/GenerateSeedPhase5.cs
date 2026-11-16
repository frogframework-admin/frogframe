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
// 1. Price Lists & Item Prices
// ============================================================================

var priceLists = new[]
{
    new Dictionary<string, object?>
    {
        ["price_list_name"] = "Standard Selling IDR",
        ["price_list_type"] = "Sales",
        ["currency"] = "IDR",
        ["price_includes_tax"] = false,
        ["is_active"] = true
    },
    new Dictionary<string, object?>
    {
        ["price_list_name"] = "Standard Buying IDR",
        ["price_list_type"] = "Purchase",
        ["currency"] = "IDR",
        ["price_includes_tax"] = false,
        ["is_active"] = true
    },
    new Dictionary<string, object?>
    {
        ["price_list_name"] = "Standard Selling USD",
        ["price_list_type"] = "Sales",
        ["currency"] = "USD",
        ["price_includes_tax"] = false,
        ["is_active"] = true
    }
};
foreach (var pl in priceLists)
{
    records.Add(("PriceList", (string)pl["price_list_name"]!, pl));
}

var itemPrices = new[]
{
    // Server Rack
    new Dictionary<string, object?> { ["item"] = "ITM-SRV-001", ["price_list"] = "Standard Selling IDR", ["uom"] = "UNT", ["rate"] = 45000000m, ["valid_from"] = "2026-01-01" },
    new Dictionary<string, object?> { ["item"] = "ITM-SRV-001", ["price_list"] = "Standard Buying IDR", ["uom"] = "UNT", ["rate"] = 35000000m, ["valid_from"] = "2026-01-01" },
    // Laptop Business
    new Dictionary<string, object?> { ["item"] = "ITM-LAP-001", ["price_list"] = "Standard Selling IDR", ["uom"] = "UNT", ["rate"] = 22500000m, ["valid_from"] = "2026-01-01" },
    new Dictionary<string, object?> { ["item"] = "ITM-LAP-001", ["price_list"] = "Standard Buying IDR", ["uom"] = "UNT", ["rate"] = 18000000m, ["valid_from"] = "2026-01-01" },
    // Monitor
    new Dictionary<string, object?> { ["item"] = "ITM-MNT-001", ["price_list"] = "Standard Selling IDR", ["uom"] = "UNT", ["rate"] = 6500000m, ["valid_from"] = "2026-01-01" },
    new Dictionary<string, object?> { ["item"] = "ITM-MNT-001", ["price_list"] = "Standard Buying IDR", ["uom"] = "UNT", ["rate"] = 4800000m, ["valid_from"] = "2026-01-01" },
    // Kabel UTP Box
    new Dictionary<string, object?> { ["item"] = "ITM-CAB-001", ["price_list"] = "Standard Selling IDR", ["uom"] = "BOX", ["rate"] = 1850000m, ["valid_from"] = "2026-01-01" },
    new Dictionary<string, object?> { ["item"] = "ITM-CAB-001", ["price_list"] = "Standard Buying IDR", ["uom"] = "BOX", ["rate"] = 1350000m, ["valid_from"] = "2026-01-01" },
    // Services
    new Dictionary<string, object?> { ["item"] = "ITM-SVC-001", ["price_list"] = "Standard Selling IDR", ["uom"] = "DAY", ["rate"] = 5000000m, ["valid_from"] = "2026-01-01" },
    new Dictionary<string, object?> { ["item"] = "ITM-SVC-002", ["price_list"] = "Standard Selling IDR", ["uom"] = "MTH", ["rate"] = 15000000m, ["valid_from"] = "2026-01-01" },
};
for (var i = 0; i < itemPrices.Length; i++)
{
    var ipName = $"IP-{(i + 1):D6}";
    records.Add(("ItemPrice", ipName, itemPrices[i]));
}

// ============================================================================
// 2. Accounting Periods (12 Monthly Periods for FY 2026)
// ============================================================================

var daysInMonth = new[] { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
for (var m = 1; m <= 12; m++)
{
    var periodName = $"2026-{m:D2}";
    var startDate = $"2026-{m:D2}-01";
    var endDate = $"2026-{m:D2}-{daysInMonth[m - 1]:D2}";
    var ap = new Dictionary<string, object?>
    {
        ["period_name"] = periodName,
        ["fiscal_year"] = "2026",
        ["start_date"] = startDate,
        ["end_date"] = endDate,
        ["status"] = "Open",
        ["lock_receivable"] = false,
        ["lock_payable"] = false,
        ["lock_fixed_asset"] = false,
        ["lock_payroll"] = false
    };
    records.Add(("AccountingPeriod", periodName, ap));
}

// ============================================================================
// 3. Leave Configuration (LeaveType & LeavePolicy)
// ============================================================================

var leaveTypes = new[]
{
    new Dictionary<string, object?>
    {
        ["leave_type_name"] = "Annual Leave",
        ["is_paid"] = true,
        ["is_annual_quota"] = true,
        ["max_consecutive_days"] = 12,
        ["include_holidays"] = false,
        ["requires_attachment"] = false
    },
    new Dictionary<string, object?>
    {
        ["leave_type_name"] = "Sick Leave",
        ["is_paid"] = true,
        ["is_annual_quota"] = false,
        ["max_consecutive_days"] = 14,
        ["include_holidays"] = false,
        ["requires_attachment"] = true
    },
    new Dictionary<string, object?>
    {
        ["leave_type_name"] = "Maternity Leave",
        ["is_paid"] = true,
        ["is_annual_quota"] = false,
        ["max_consecutive_days"] = 90,
        ["include_holidays"] = true,
        ["requires_attachment"] = true
    },
    new Dictionary<string, object?>
    {
        ["leave_type_name"] = "Compassionate Leave",
        ["is_paid"] = true,
        ["is_annual_quota"] = false,
        ["max_consecutive_days"] = 3,
        ["include_holidays"] = false,
        ["requires_attachment"] = false
    },
    new Dictionary<string, object?>
    {
        ["leave_type_name"] = "Unpaid Leave",
        ["is_paid"] = false,
        ["is_annual_quota"] = false,
        ["max_consecutive_days"] = 30,
        ["include_holidays"] = false,
        ["requires_attachment"] = false
    }
};
foreach (var lt in leaveTypes)
{
    records.Add(("LeaveType", (string)lt["leave_type_name"]!, lt));
}

var leavePolicy = new Dictionary<string, object?>
{
    ["policy_name"] = "Standard Leave Policy PKWTT",
    ["is_active"] = true,
    ["details"] = new[]
    {
        new Dictionary<string, object?> { ["idx"] = 1, ["leave_type"] = "Annual Leave", ["annual_allocation"] = 12.0, ["min_service_months"] = 12 },
        new Dictionary<string, object?> { ["idx"] = 2, ["leave_type"] = "Compassionate Leave", ["annual_allocation"] = 3.0, ["min_service_months"] = 0 }
    }
};
records.Add(("LeavePolicy", "Standard Leave Policy PKWTT", leavePolicy));

// ============================================================================
// 4. Compensation & Payroll (SalaryComponent & SalaryAssignment)
// ============================================================================

var salaryComponents = new[]
{
    new Dictionary<string, object?>
    {
        ["component_name"] = "Gaji Pokok",
        ["component_type"] = "Earning",
        ["abbreviation"] = "GP",
        ["is_active"] = true,
        ["is_taxable"] = true,
        ["is_regular"] = true,
        ["in_bpjs_base"] = true,
        ["account"] = "6100"
    },
    new Dictionary<string, object?>
    {
        ["component_name"] = "Tunjangan Jabatan",
        ["component_type"] = "Earning",
        ["abbreviation"] = "TJ",
        ["is_active"] = true,
        ["is_taxable"] = true,
        ["is_regular"] = true,
        ["in_bpjs_base"] = true,
        ["account"] = "6100"
    },
    new Dictionary<string, object?>
    {
        ["component_name"] = "Tunjangan Transport & Makan",
        ["component_type"] = "Earning",
        ["abbreviation"] = "TTM",
        ["is_active"] = true,
        ["is_taxable"] = true,
        ["is_regular"] = true,
        ["in_bpjs_base"] = false,
        ["account"] = "6100"
    },
    new Dictionary<string, object?>
    {
        ["component_name"] = "Uang Lembur",
        ["component_type"] = "Earning",
        ["abbreviation"] = "OT",
        ["is_active"] = true,
        ["is_taxable"] = true,
        ["is_regular"] = false,
        ["in_bpjs_base"] = false,
        ["account"] = "6100"
    },
    new Dictionary<string, object?>
    {
        ["component_name"] = "Bonus Kinerja",
        ["component_type"] = "Earning",
        ["abbreviation"] = "BONUS",
        ["is_active"] = true,
        ["is_taxable"] = true,
        ["is_regular"] = false,
        ["in_bpjs_base"] = false,
        ["account"] = "6100"
    },
    new Dictionary<string, object?>
    {
        ["component_name"] = "Potongan Pinjaman Karyawan",
        ["component_type"] = "Deduction",
        ["abbreviation"] = "LOAN",
        ["is_active"] = true,
        ["account"] = "1124" // Piutang Karyawan (akun kontrol, pihak = karyawan)
    }
};
foreach (var sc in salaryComponents)
{
    records.Add(("SalaryComponent", (string)sc["component_name"]!, sc));
}

// Salary Assignments for Key Employees
var salaryAssignments = new[]
{
    // EMP-0001: IT Staff (Total: 8,500,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0001",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 6500000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 2000000m }
        }
    },
    // EMP-0003: IT Manager (Total: 18,000,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0003",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 12000000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Jabatan", ["amount"] = 4000000m },
            new Dictionary<string, object?> { ["idx"] = 3, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 2000000m }
        }
    },
    // EMP-0005: Finance Staff (Total: 9,000,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0005",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 7000000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 2000000m }
        }
    },
    // EMP-0008: HR Manager (Total: 19,000,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0008",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 13000000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Jabatan", ["amount"] = 4000000m },
            new Dictionary<string, object?> { ["idx"] = 3, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 2000000m }
        }
    },
    // EMP-0002: Sales Staff (Total: 7,500,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0002",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 6000000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 1500000m }
        }
    },
    // EMP-0004: Sales Manager (Total: 20,000,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0004",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 14000000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Jabatan", ["amount"] = 4000000m },
            new Dictionary<string, object?> { ["idx"] = 3, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 2000000m }
        }
    },
    // EMP-0006: Purchasing Staff (Total: 8,000,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0006",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 6500000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 1500000m }
        }
    },
    // EMP-0009: HR Staff (Total: 7,500,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0009",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 6000000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 1500000m }
        }
    },
    // EMP-0010: Warehouse Officer (Total: 10,000,000)
    new Dictionary<string, object?>
    {
        ["employee"] = "EMP-0010",
        ["from_date"] = "2026-01-01",
        ["is_active"] = true,
        ["bpjs_employment"] = true,
        ["pension_member"] = true,
        ["bpjs_health"] = true,
        ["earnings"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Gaji Pokok", ["amount"] = 7500000m },
            new Dictionary<string, object?> { ["idx"] = 2, ["salary_component"] = "Tunjangan Jabatan", ["amount"] = 1000000m },
            new Dictionary<string, object?> { ["idx"] = 3, ["salary_component"] = "Tunjangan Transport & Makan", ["amount"] = 1500000m }
        },
        ["deductions"] = new[]
        {
            new Dictionary<string, object?> { ["idx"] = 1, ["salary_component"] = "Potongan Pinjaman Karyawan", ["amount"] = 500000m }
        },
        ["remarks"] = "Cicilan pinjaman karyawan Rp500.000/bulan (saldo awal pinjaman Rp3.000.000 di jurnal pembukaan)"
    }
};
for (var i = 0; i < salaryAssignments.Length; i++)
{
    var saName = $"SA-{(i + 1):D5}";
    records.Add(("SalaryAssignment", saName, salaryAssignments[i]));
}

// ============================================================================
// 5. Singleton System Settings
// ============================================================================

// GlobalDefaults
records.Add(("GlobalDefaults", "GlobalDefaults", new Dictionary<string, object?>
{
    ["default_company"] = "ACME",
    ["default_currency"] = "IDR",
    ["default_country"] = "ID",
    ["default_holiday_list"] = "Libur Nasional 2026"
}));

// AccountingSettings
records.Add(("AccountingSettings", "AccountingSettings", new Dictionary<string, object?>
{
    ["allow_future_posting"] = false,
    ["enforce_cost_center_on_pl"] = true,
    ["default_exchange_rate_type"] = "BI Middle Rate"
}));

// StockSettings
records.Add(("StockSettings", "StockSettings", new Dictionary<string, object?>
{
    ["default_warehouse"] = "Gudang Utama Cikarang",
    ["allow_negative_stock"] = false,
    ["inventory_account"] = "1131",
    ["cogs_account"] = "5100",
    ["stock_adjustment_account"] = "5200",
    ["grni_account"] = "2170" // Barang diterima belum ditagih: GR (Cr) dan faktur pemasok dengan PO (Dr)
}));

// CRMSettings
records.Add(("CRMSettings", "CRMSettings", new Dictionary<string, object?>
{
    ["default_sales_stage"] = "Prospecting",
    ["default_customer_group"] = "Commercial & Corporate",
    ["create_opportunity_on_convert"] = true,
    ["duplicate_check"] = "Email & mobile"
}));

// HRSettings
records.Add(("HRSettings", "HRSettings", new Dictionary<string, object?>
{
    ["default_holiday_list"] = "Libur Nasional 2026",
    ["default_shift_type"] = "Office 08-17",
    ["default_leave_policy"] = "Standard Leave Policy PKWTT",
    ["default_probation_months"] = 3,
    ["retirement_age"] = 58
}));

// ============================================================================
// Generate SQL script
// ============================================================================

var jsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var sb = new StringBuilder();
sb.AppendLine("-- ============================================================================");
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 5 (Pricing, Policies & System Settings)");
sb.AppendLine("-- Tenant: acme | PriceLists, ItemPrices, AccountingPeriods, Leaves, Salaries, Settings");
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
File.WriteAllText("deploy/sql/seed-phase-5.sql", sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"Successfully generated deploy/sql/seed-phase-5.sql with {records.Count} records.");

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
