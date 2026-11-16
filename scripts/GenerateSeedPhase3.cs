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
// Phase 3: Legal Entity (Company)
// ============================================================================

var companies = new[]
{
    new Dictionary<string, object?>
    {
        ["company_code"] = "ACME",
        ["company_name"] = "PT Acme Nusantara Teknologi",
        ["legal_name"] = "PT Acme Nusantara Teknologi Tbk",
        ["is_group"] = false,
        ["parent_company"] = null,
        ["base_currency"] = "IDR",
        ["country"] = "ID",
        ["fiscal_year_start_month"] = 1,
        ["default_holiday_list"] = "Libur Nasional 2026",
        ["tax_id"] = "0123456789012345",
        ["nitku"] = "0123456789012345000000",
        ["is_pkp"] = true,
        ["email"] = "corporate@acme.co.id",
        ["phone"] = "+622151501234",
        ["website"] = "https://acme.co.id",
        // Default Accounts (linked to COA Phase 2)
        ["default_receivable_account"] = "1121",
        ["default_payable_account"] = "2110",
        ["default_cash_account"] = "1111",
        ["default_income_account"] = "4100",
        ["default_expense_account"] = "6100",
        ["retained_earnings_account"] = "3200",
        ["exchange_gain_loss_account"] = "8200",
        ["round_off_account"] = "8300",
        // Default Cost Centers (linked to CostCenter Phase 2)
        ["default_cost_center"] = "CC-300",
        ["round_off_cost_center"] = "CC-300",
        ["is_active"] = true
    },
    new Dictionary<string, object?>
    {
        ["company_code"] = "GLOBEX",
        ["company_name"] = "PT Globex Indonesia",
        ["legal_name"] = "PT Globex Indonesia",
        ["is_group"] = false,
        ["parent_company"] = null,
        ["base_currency"] = "IDR",
        ["country"] = "ID",
        ["fiscal_year_start_month"] = 1,
        ["default_holiday_list"] = "Libur Nasional 2026",
        ["tax_id"] = "0234567890123000",
        ["nitku"] = "0234567890123000000000",
        ["is_pkp"] = true,
        ["email"] = "info@globex.co.id",
        ["phone"] = "+62315678900",
        ["website"] = "https://globex.co.id",
        // Default Accounts
        ["default_receivable_account"] = "1121",
        ["default_payable_account"] = "2110",
        ["default_cash_account"] = "1111",
        ["default_income_account"] = "4100",
        ["default_expense_account"] = "6100",
        ["retained_earnings_account"] = "3200",
        ["exchange_gain_loss_account"] = "8200",
        ["round_off_account"] = "8300",
        // Default Cost Centers
        ["default_cost_center"] = "CC-300",
        ["round_off_cost_center"] = "CC-300",
        ["is_active"] = true
    }
};

foreach (var c in companies)
{
    var code = (string)c["company_code"]!;
    records.Add(("Company", code, c));
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
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 3 (Legal Entity - Company)");
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
File.WriteAllText("deploy/sql/seed-phase-3.sql", sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"Successfully generated deploy/sql/seed-phase-3.sql with {records.Count} records.");

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
