#:property PublishAot=false
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

const string tenant = "acme";
const string owner = "admin";

var records = new List<(string DocType, string Name, object Data)>();

// 1. Currency
var currencies = new[]
{
    new { currency_code = "IDR", currency_name = "Indonesian Rupiah", symbol = "Rp", decimals = 2, is_active = true },
    new { currency_code = "USD", currency_name = "US Dollar", symbol = "$", decimals = 2, is_active = true },
    new { currency_code = "EUR", currency_name = "Euro", symbol = "€", decimals = 2, is_active = true },
    new { currency_code = "SGD", currency_name = "Singapore Dollar", symbol = "S$", decimals = 2, is_active = true },
    new { currency_code = "JPY", currency_name = "Japanese Yen", symbol = "¥", decimals = 0, is_active = true },
};
foreach (var c in currencies)
{
    records.Add(("Currency", c.currency_code, c));
}

// 2. HolidayList
var holidays = new[]
{
    new { idx = 1, holiday_date = "2026-01-01", description = "Tahun Baru 2026 Masehi", holiday_type = "Public holiday" },
    new { idx = 2, holiday_date = "2026-01-16", description = "Isra Mi'raj Nabi Muhammad SAW", holiday_type = "Public holiday" },
    new { idx = 3, holiday_date = "2026-02-17", description = "Tahun Baru Imlek 2577 Kongzili", holiday_type = "Public holiday" },
    new { idx = 4, holiday_date = "2026-03-20", description = "Hari Suci Nyepi (Tahun Baru Saka 1948)", holiday_type = "Public holiday" },
    new { idx = 5, holiday_date = "2026-03-21", description = "Hari Raya Idul Fitri 1447 H (Hari Pertama)", holiday_type = "Public holiday" },
    new { idx = 6, holiday_date = "2026-03-22", description = "Hari Raya Idul Fitri 1447 H (Hari Kedua)", holiday_type = "Public holiday" },
    new { idx = 7, holiday_date = "2026-04-03", description = "Wafat Yesus Kristus (Jumat Agung)", holiday_type = "Public holiday" },
    new { idx = 8, holiday_date = "2026-04-05", description = "Hari Paskah", holiday_type = "Public holiday" },
    new { idx = 9, holiday_date = "2026-05-01", description = "Hari Buruh Internasional", holiday_type = "Public holiday" },
    new { idx = 10, holiday_date = "2026-05-14", description = "Kenaikan Yesus Kristus", holiday_type = "Public holiday" },
    new { idx = 11, holiday_date = "2026-05-27", description = "Hari Raya Idul Adha 1447 H", holiday_type = "Public holiday" },
    new { idx = 12, holiday_date = "2026-05-31", description = "Hari Raya Waisak 2570 BE", holiday_type = "Public holiday" },
    new { idx = 13, holiday_date = "2026-06-01", description = "Hari Lahir Pancasila", holiday_type = "Public holiday" },
    new { idx = 14, holiday_date = "2026-06-16", description = "Tahun Baru Islam 1448 H", holiday_type = "Public holiday" },
    new { idx = 15, holiday_date = "2026-08-17", description = "Hari Kemerdekaan RI ke-81", holiday_type = "Public holiday" },
    new { idx = 16, holiday_date = "2026-08-25", description = "Maulid Nabi Muhammad SAW", holiday_type = "Public holiday" },
    new { idx = 17, holiday_date = "2026-12-25", description = "Hari Raya Natal", holiday_type = "Public holiday" },
};
var holidayList = new
{
    holiday_list_name = "Libur Nasional 2026",
    from_date = "2026-01-01",
    to_date = "2026-12-31",
    weekly_off = "Saturday & Sunday",
    total_holidays = holidays.Length,
    holidays
};
records.Add(("HolidayList", "Libur Nasional 2026", holidayList));

// 3. Bank
var banks = new[]
{
    new { bank_code = "014", bank_name = "PT Bank Central Asia Tbk", swift_code = "CENAIDJA", is_active = true },
    new { bank_code = "008", bank_name = "PT Bank Mandiri (Persero) Tbk", swift_code = "BMRIIDJA", is_active = true },
    new { bank_code = "002", bank_name = "PT Bank Rakyat Indonesia (Persero) Tbk", swift_code = "BRINIDJA", is_active = true },
    new { bank_code = "009", bank_name = "PT Bank Negara Indonesia (Persero) Tbk", swift_code = "BNINIDJA", is_active = true },
    new { bank_code = "451", bank_name = "PT Bank Syariah Indonesia Tbk", swift_code = "BSMDIDJA", is_active = true },
    new { bank_code = "022", bank_name = "PT Bank CIMB Niaga Tbk", swift_code = "BNGAIDJA", is_active = true },
};
foreach (var b in banks)
{
    records.Add(("Bank", b.bank_code, b));
}

// 4. UOM
var uoms = new[]
{
    new { uom_code = "PCS", uom_name = "Pieces", must_be_whole_number = true },
    new { uom_code = "BOX", uom_name = "Box", must_be_whole_number = true },
    new { uom_code = "KG", uom_name = "Kilogram", must_be_whole_number = false },
    new { uom_code = "MTR", uom_name = "Meter", must_be_whole_number = false },
    new { uom_code = "UNT", uom_name = "Unit", must_be_whole_number = true },
    new { uom_code = "SET", uom_name = "Set", must_be_whole_number = true },
    new { uom_code = "LTR", uom_name = "Liter", must_be_whole_number = false },
    new { uom_code = "HRS", uom_name = "Hours", must_be_whole_number = false },
    new { uom_code = "DAY", uom_name = "Days", must_be_whole_number = false },
    new { uom_code = "MTH", uom_name = "Months", must_be_whole_number = false },
};
foreach (var u in uoms)
{
    records.Add(("UOM", u.uom_code, u));
}

// 5. Industry
var industries = new[]
{
    new { industry_name = "Information Technology", is_active = true },
    new { industry_name = "Manufacturing", is_active = true },
    new { industry_name = "Retail & Distribution", is_active = true },
    new { industry_name = "Financial Services", is_active = true },
    new { industry_name = "Healthcare & Pharmaceuticals", is_active = true },
    new { industry_name = "Logistics & Transportation", is_active = true },
};
foreach (var ind in industries)
{
    records.Add(("Industry", ind.industry_name, ind));
}

// 6. SalesStage
var salesStages = new[]
{
    new { stage_name = "Prospecting", sequence = 1, stage_type = "Open", probability = 10, forecast_category = "Pipeline", is_active = true },
    new { stage_name = "Qualification", sequence = 2, stage_type = "Open", probability = 25, forecast_category = "Pipeline", is_active = true },
    new { stage_name = "Proposal", sequence = 3, stage_type = "Open", probability = 50, forecast_category = "Best Case", is_active = true },
    new { stage_name = "Negotiation", sequence = 4, stage_type = "Open", probability = 80, forecast_category = "Commit", is_active = true },
    new { stage_name = "Closed Won", sequence = 5, stage_type = "Won", probability = 100, forecast_category = "Closed", is_active = true },
    new { stage_name = "Closed Lost", sequence = 6, stage_type = "Lost", probability = 0, forecast_category = "Omitted", is_active = true },
};
foreach (var s in salesStages)
{
    records.Add(("SalesStage", s.stage_name, s));
}

// 7. ShiftType
var shifts = new[]
{
    new
    {
        shift_name = "Office 08-17",
        start_time = "08:00:00",
        end_time = "17:00:00",
        break_minutes = 60,
        working_hours_per_day = 8.0,
        working_days_per_week = "5",
        late_grace_minutes = 15,
        is_night_shift = false
    },
    new
    {
        shift_name = "Shift Pagi 06-14",
        start_time = "06:00:00",
        end_time = "14:00:00",
        break_minutes = 60,
        working_hours_per_day = 7.0,
        working_days_per_week = "6",
        late_grace_minutes = 10,
        is_night_shift = false
    },
    new
    {
        shift_name = "Shift Siang 14-22",
        start_time = "14:00:00",
        end_time = "22:00:00",
        break_minutes = 60,
        working_hours_per_day = 7.0,
        working_days_per_week = "6",
        late_grace_minutes = 10,
        is_night_shift = false
    },
    new
    {
        shift_name = "Shift Malam 22-06",
        start_time = "22:00:00",
        end_time = "06:00:00",
        break_minutes = 60,
        working_hours_per_day = 7.0,
        working_days_per_week = "6",
        late_grace_minutes = 10,
        is_night_shift = true
    }
};
foreach (var sh in shifts)
{
    records.Add(("ShiftType", sh.shift_name, sh));
}

// 8. Address & Contact (pre-linked to Company ACME)
records.Add(("Address", "ADDR-000001", new
{
    address_title = "Kantor Pusat Sudirman",
    address_type = "Office",
    address_line1 = "Jl. Jenderal Sudirman Kav. 52-53",
    address_line2 = "Gedung Bursa Efek Indonesia Tower 2 Lt. 17",
    city = "Jakarta Selatan",
    province = "DKI Jakarta",
    postal_code = "12190",
    country = "ID",
    phone = "+622151501234",
    email = "info@acme.co.id",
    is_primary = true,
    is_tax_address = true,
    links = new[] { new { idx = 1, link_doctype = "Company", link_name = "ACME" } }
}));

records.Add(("Address", "ADDR-000002", new
{
    address_title = "Gudang Logistik Cikarang",
    address_type = "Warehouse",
    address_line1 = "Kawasan Industri GIIC Blok AA No. 5",
    address_line2 = "Kota Deltamas",
    city = "Bekasi",
    province = "Jawa Barat",
    postal_code = "17530",
    country = "ID",
    phone = "+622189901234",
    email = "warehouse.cikarang@acme.co.id",
    is_primary = false,
    is_tax_address = false,
    links = new[] { new { idx = 1, link_doctype = "Company", link_name = "ACME" } }
}));

records.Add(("Contact", "CONT-000001", new
{
    salutation = "Mr.",
    full_name = "Bambang Pristiwanto",
    designation = "Direktur Utama",
    department = "Executive Management",
    email = "bpristiwanto@gmail.com",
    phone = "+6281234567890",
    is_primary = true,
    is_active = true,
    links = new[] { new { idx = 1, link_doctype = "Company", link_name = "ACME" } }
}));

// 9. PayrollSettings (Singleton)
var ptkpRates = new[]
{
    new { idx = 1, ptkp_status = "TK/0", amount = 54000000m, ter_category = "A" },
    new { idx = 2, ptkp_status = "TK/1", amount = 58500000m, ter_category = "A" },
    new { idx = 3, ptkp_status = "TK/2", amount = 63000000m, ter_category = "B" },
    new { idx = 4, ptkp_status = "TK/3", amount = 67500000m, ter_category = "B" },
    new { idx = 5, ptkp_status = "K/0", amount = 58500000m, ter_category = "A" },
    new { idx = 6, ptkp_status = "K/1", amount = 63000000m, ter_category = "B" },
    new { idx = 7, ptkp_status = "K/2", amount = 67500000m, ter_category = "B" },
    new { idx = 8, ptkp_status = "K/3", amount = 72000000m, ter_category = "C" },
};

var taxBrackets = new[]
{
    new { idx = 1, up_to = 60000000m, rate = 5m },
    new { idx = 2, up_to = 250000000m, rate = 15m },
    new { idx = 3, up_to = 500000000m, rate = 25m },
    new { idx = 4, up_to = 5000000000m, rate = 30m },
    new { idx = 5, up_to = 0m, rate = 35m },
};

(decimal UpTo, decimal Rate)[] terARaw =
[
    (5_400_000m, 0m), (5_650_000m, 0.25m), (5_950_000m, 0.5m), (6_300_000m, 0.75m), (6_750_000m, 1m), (7_500_000m, 1.25m), (8_550_000m, 1.5m),
    (9_650_000m, 1.75m), (10_050_000m, 2m), (10_350_000m, 2.25m), (10_700_000m, 2.5m), (11_050_000m, 3m), (11_600_000m, 3.5m), (12_500_000m, 4m),
    (13_750_000m, 5m), (15_100_000m, 6m), (16_950_000m, 7m), (19_750_000m, 8m), (24_150_000m, 9m), (26_450_000m, 10m), (28_000_000m, 11m),
    (30_050_000m, 12m), (32_400_000m, 13m), (35_400_000m, 14m), (39_100_000m, 15m), (43_850_000m, 16m), (47_800_000m, 17m), (51_400_000m, 18m),
    (56_300_000m, 19m), (62_200_000m, 20m), (68_600_000m, 21m), (77_500_000m, 22m), (89_000_000m, 23m), (103_000_000m, 24m), (125_000_000m, 25m),
    (157_000_000m, 26m), (206_000_000m, 27m), (337_000_000m, 28m), (454_000_000m, 29m), (550_000_000m, 30m), (695_000_000m, 31m), (910_000_000m, 32m),
    (1_400_000_000m, 33m), (0m, 34m)
];

(decimal UpTo, decimal Rate)[] terBRaw =
[
    (6_200_000m, 0m), (6_500_000m, 0.25m), (6_850_000m, 0.5m), (7_300_000m, 0.75m), (9_200_000m, 1m), (10_750_000m, 1.5m), (11_250_000m, 2m),
    (11_600_000m, 2.5m), (12_600_000m, 3m), (13_600_000m, 4m), (14_950_000m, 5m), (16_400_000m, 6m), (18_450_000m, 7m), (21_850_000m, 8m),
    (26_000_000m, 9m), (27_700_000m, 10m), (29_350_000m, 11m), (31_450_000m, 12m), (33_950_000m, 13m), (37_100_000m, 14m), (41_100_000m, 15m),
    (45_800_000m, 16m), (49_500_000m, 17m), (53_800_000m, 18m), (58_500_000m, 19m), (64_000_000m, 20m), (71_000_000m, 21m), (80_000_000m, 22m),
    (93_000_000m, 23m), (109_000_000m, 24m), (129_000_000m, 25m), (163_000_000m, 26m), (211_000_000m, 27m), (374_000_000m, 28m), (459_000_000m, 29m),
    (555_000_000m, 30m), (704_000_000m, 31m), (957_000_000m, 32m), (1_405_000_000m, 33m), (0m, 34m)
];

(decimal UpTo, decimal Rate)[] terCRaw =
[
    (6_600_000m, 0m), (6_950_000m, 0.25m), (7_350_000m, 0.5m), (7_800_000m, 0.75m), (8_850_000m, 1m), (9_800_000m, 1.25m), (10_950_000m, 1.5m),
    (11_200_000m, 1.75m), (12_050_000m, 2m), (12_950_000m, 3m), (14_150_000m, 4m), (15_550_000m, 5m), (17_050_000m, 6m), (19_500_000m, 7m),
    (22_700_000m, 8m), (24_700_000m, 9m), (26_350_000m, 10m), (28_000_000m, 11m), (30_100_000m, 12m), (32_600_000m, 13m), (35_400_000m, 14m),
    (38_900_000m, 15m), (43_200_000m, 16m), (46_850_000m, 17m), (51_000_000m, 18m), (55_500_000m, 19m), (60_800_000m, 20m), (67_100_000m, 21m),
    (75_700_000m, 22m), (85_500_000m, 23m), (101_100_000m, 24m), (120_300_000m, 25m), (154_400_000m, 26m), (197_700_000m, 27m), (241_100_000m, 28m),
    (327_500_000m, 29m), (414_000_000m, 30m), (497_700_000m, 31m), (657_900_000m, 32m), (895_300_000m, 33m), (1_411_400_000m, 34m), (0m, 35m)
];

var terRates = new List<object>();
var idx = 1;
foreach (var (cat, raw) in new[] { ("A", terARaw), ("B", terBRaw), ("C", terCRaw) })
{
    foreach (var (upTo, rate) in raw)
    {
        terRates.Add(new { idx, ter_category = cat, up_to = upTo, rate });
        idx++;
    }
}

var payrollSettings = new
{
    jht_employer_rate = 3.7m,
    jht_employee_rate = 2.0m,
    jkk_rate = 0.24m,
    jkm_rate = 0.3m,
    jp_employer_rate = 2.0m,
    jp_employee_rate = 1.0m,
    jp_wage_cap = 11086300m,
    kes_employer_rate = 4.0m,
    kes_employee_rate = 1.0m,
    kes_wage_cap = 12000000m,
    position_cost_rate = 5.0m,
    position_cost_monthly_cap = 500000m,
    deduct_unpaid_leave = true,
    deduct_absence = false,
    ptkp_rates = ptkpRates,
    tax_brackets = taxBrackets,
    ter_rates = terRates
};
records.Add(("PayrollSettings", "PayrollSettings", payrollSettings));

// Generate SQL script
var jsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var sb = new StringBuilder();
sb.AppendLine("-- ============================================================================");
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 1 (Standalone Master Data)");
sb.AppendLine("-- Tenant: acme | Generated for initial setup & unit testing");
sb.AppendLine("-- ============================================================================");
sb.AppendLine("BEGIN;");
sb.AppendLine();

foreach (var (docType, name, data) in records)
{
    var dataJson = JsonSerializer.Serialize(data, jsonOptions).Replace("'", "''");
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
File.WriteAllText("deploy/sql/seed-phase-1.sql", sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"Successfully generated deploy/sql/seed-phase-1.sql with {records.Count} records.");
