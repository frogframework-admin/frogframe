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
// 1. HR Taxonomy: JobFamilyGroup -> JobFamily -> JobProfile
// ============================================================================

var familyGroups = new[]
{
    new { group_name = "Technology & Engineering", description = "IT and Software Development" },
    new { group_name = "Finance & Accounting", description = "Finance, Accounting, and Tax" },
    new { group_name = "Human Resources", description = "People Operations and Talent Management" },
    new { group_name = "Operations & Supply Chain", description = "Logistics, Warehousing, and Procurement" },
    new { group_name = "Commercial & Sales", description = "Sales, Marketing, and Account Management" }
};
foreach (var fg in familyGroups)
{
    records.Add(("JobFamilyGroup", fg.group_name, fg));
}

var jobFamilies = new[]
{
    new { family_name = "Software Engineering", job_family_group = "Technology & Engineering", description = "Software design & infrastructure" },
    new { family_name = "Finance & Accounting", job_family_group = "Finance & Accounting", description = "General ledger, AP, AR, and reporting" },
    new { family_name = "Human Resource Management", job_family_group = "Human Resources", description = "HR strategy, relations, and operations" },
    new { family_name = "Warehouse & Logistics", job_family_group = "Operations & Supply Chain", description = "Inventory handling and distribution" },
    new { family_name = "Sales & Business Development", job_family_group = "Commercial & Sales", description = "Direct sales and client relationships" }
};
foreach (var jf in jobFamilies)
{
    records.Add(("JobFamily", jf.family_name, jf));
}

var jobProfiles = new[]
{
    new { job_code = "JOB-EXEC-DIR", job_title = "Managing Director", job_family = "Human Resource Management", job_level = "Executive", is_active = true },
    new { job_code = "JOB-IT-MGR", job_title = "IT Manager", job_family = "Software Engineering", job_level = "Manager", is_active = true },
    new { job_code = "JOB-IT-STAFF", job_title = "IT Support Specialist", job_family = "Software Engineering", job_level = "Staff", is_active = true },
    new { job_code = "JOB-HR-MGR", job_title = "HR Manager", job_family = "Human Resource Management", job_level = "Manager", is_active = true },
    new { job_code = "JOB-HR-STAFF", job_title = "HR Specialist", job_family = "Human Resource Management", job_level = "Staff", is_active = true },
    new { job_code = "JOB-FIN-STAFF", job_title = "Finance & Accounting Specialist", job_family = "Finance & Accounting", job_level = "Staff", is_active = true },
    new { job_code = "JOB-PURCH-STAFF", job_title = "Procurement Specialist", job_family = "Warehouse & Logistics", job_level = "Staff", is_active = true },
    new { job_code = "JOB-WH-OFFICER", job_title = "Warehouse Officer", job_family = "Warehouse & Logistics", job_level = "Staff", is_active = true },
    new { job_code = "JOB-SALES-MGR", job_title = "Sales Manager", job_family = "Sales & Business Development", job_level = "Manager", is_active = true },
    new { job_code = "JOB-SALES-STAFF", job_title = "Sales Specialist", job_family = "Sales & Business Development", job_level = "Staff", is_active = true }
};
foreach (var jp in jobProfiles)
{
    records.Add(("JobProfile", jp.job_code, jp));
}

// ============================================================================
// 2. Organization Units (Departments & Divisions)
// ============================================================================

var orgUnits = new[]
{
    new { org_unit_code = "BOD", org_unit_name = "Dewan Direksi (BOD)", unit_type = "Board of Directors", parent_org_unit = (string?)null, company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DIV-TECH", org_unit_name = "Direktorat Teknologi & Operasi", unit_type = "Directorate", parent_org_unit = "BOD", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DIV-FIN", org_unit_name = "Direktorat Keuangan & HR", unit_type = "Directorate", parent_org_unit = "BOD", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DIV-COMM", org_unit_name = "Direktorat Komersial", unit_type = "Directorate", parent_org_unit = "BOD", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DEPT-IT", org_unit_name = "Departemen IT", unit_type = "Department", parent_org_unit = "DIV-TECH", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DEPT-WH", org_unit_name = "Departemen Gudang & Logistik", unit_type = "Department", parent_org_unit = "DIV-TECH", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DEPT-FIN", org_unit_name = "Departemen Keuangan & Akuntansi", unit_type = "Department", parent_org_unit = "DIV-FIN", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DEPT-HR", org_unit_name = "Departemen Human Resources", unit_type = "Department", parent_org_unit = "DIV-FIN", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DEPT-SALES", org_unit_name = "Departemen Penjualan & Pemasaran", unit_type = "Department", parent_org_unit = "DIV-COMM", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" },
    new { org_unit_code = "DEPT-PURCH", org_unit_name = "Departemen Pengadaan (Purchasing)", unit_type = "Department", parent_org_unit = "DIV-COMM", company = "ACME", is_active = true, staffing_model = "Position", effective_from = "2026-01-01" }
};
foreach (var ou in orgUnits)
{
    records.Add(("OrganizationUnit", ou.org_unit_code, ou));
}

// ============================================================================
// 3. Positions (Headcount budget slots)
// ============================================================================

var positions = new[]
{
    new { position_title = "Managing Director", job_profile = "JOB-EXEC-DIR", organization_unit = "BOD", reports_to_position = (string?)null, is_manager_position = true, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "IT Manager", job_profile = "JOB-IT-MGR", organization_unit = "DEPT-IT", reports_to_position = "POS-000001", is_manager_position = true, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "IT Staff", job_profile = "JOB-IT-STAFF", organization_unit = "DEPT-IT", reports_to_position = "POS-000002", is_manager_position = false, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "HR Manager", job_profile = "JOB-HR-MGR", organization_unit = "DEPT-HR", reports_to_position = "POS-000001", is_manager_position = true, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "HR Staff", job_profile = "JOB-HR-STAFF", organization_unit = "DEPT-HR", reports_to_position = "POS-000004", is_manager_position = false, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "Finance Staff", job_profile = "JOB-FIN-STAFF", organization_unit = "DEPT-FIN", reports_to_position = "POS-000001", is_manager_position = false, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "Purchasing Staff", job_profile = "JOB-PURCH-STAFF", organization_unit = "DEPT-PURCH", reports_to_position = "POS-000001", is_manager_position = false, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "Warehouse Officer", job_profile = "JOB-WH-OFFICER", organization_unit = "DEPT-WH", reports_to_position = "POS-000001", is_manager_position = false, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "Sales Manager", job_profile = "JOB-SALES-MGR", organization_unit = "DEPT-SALES", reports_to_position = "POS-000001", is_manager_position = true, status = "Filled", effective_from = "2026-01-01" },
    new { position_title = "Sales Staff", job_profile = "JOB-SALES-STAFF", organization_unit = "DEPT-SALES", reports_to_position = "POS-000009", is_manager_position = false, status = "Filled", effective_from = "2026-01-01" }
};
for (var i = 0; i < positions.Length; i++)
{
    var posName = $"POS-{(i + 1):D6}";
    records.Add(("Position", posName, positions[i]));
}

// ============================================================================
// 4. Employees (Personnel records matching Demo Users)
// ============================================================================

var employees = new[]
{
    new
    {
        employee_number = "EMP-0001", full_name = "Ahmad Fadilah", user_id = "it01", gender = "Male",
        date_of_birth = "1994-06-15", religion = "Islam", nationality = "ID", work_email = "it01@acme.co.id",
        company = "ACME", organization_unit = "DEPT-IT", position = "POS-000003", job_profile = "JOB-IT-STAFF",
        reports_to = "EMP-0003", employment_type = "Permanent (PKWTT)", hire_date = "2021-03-01", status = "Active"
    },
    new
    {
        employee_number = "EMP-0002", full_name = "Citra Dewi", user_id = "marketing01", gender = "Female",
        date_of_birth = "1996-08-20", religion = "Islam", nationality = "ID", work_email = "marketing01@acme.co.id",
        company = "ACME", organization_unit = "DEPT-SALES", position = "POS-000010", job_profile = "JOB-SALES-STAFF",
        reports_to = "EMP-0004", employment_type = "Permanent (PKWTT)", hire_date = "2022-01-10", status = "Active"
    },
    new
    {
        employee_number = "EMP-0003", full_name = "Budi Santoso", user_id = "it02", gender = "Male",
        date_of_birth = "1988-11-05", religion = "Islam", nationality = "ID", work_email = "it02@acme.co.id",
        company = "ACME", organization_unit = "DEPT-IT", position = "POS-000002", job_profile = "JOB-IT-MGR",
        reports_to = "EMP-0008", employment_type = "Permanent (PKWTT)", hire_date = "2019-06-01", status = "Active"
    },
    new
    {
        employee_number = "EMP-0004", full_name = "Dian Pratama", user_id = "marketing02", gender = "Male",
        date_of_birth = "1987-04-12", religion = "Islam", nationality = "ID", work_email = "marketing02@acme.co.id",
        company = "ACME", organization_unit = "DEPT-SALES", position = "POS-000009", job_profile = "JOB-SALES-MGR",
        reports_to = "EMP-0008", employment_type = "Permanent (PKWTT)", hire_date = "2018-09-15", status = "Active"
    },
    new
    {
        employee_number = "EMP-0005", full_name = "Eko Prasetyo", user_id = "finance01", gender = "Male",
        date_of_birth = "1992-12-01", religion = "Islam", nationality = "ID", work_email = "finance01@acme.co.id",
        company = "ACME", organization_unit = "DEPT-FIN", position = "POS-000006", job_profile = "JOB-FIN-STAFF",
        reports_to = "EMP-0008", employment_type = "Permanent (PKWTT)", hire_date = "2020-07-01", status = "Active"
    },
    new
    {
        employee_number = "EMP-0006", full_name = "Fajar Hidayat", user_id = "purchasing01", gender = "Male",
        date_of_birth = "1993-02-18", religion = "Islam", nationality = "ID", work_email = "purchasing01@acme.co.id",
        company = "ACME", organization_unit = "DEPT-PURCH", position = "POS-000007", job_profile = "JOB-PURCH-STAFF",
        reports_to = "EMP-0008", employment_type = "Permanent (PKWTT)", hire_date = "2021-05-15", status = "Active"
    },
    new
    {
        employee_number = "EMP-0008", full_name = "Gita Pertiwi", user_id = "hr02", gender = "Female",
        date_of_birth = "1985-09-25", religion = "Islam", nationality = "ID", work_email = "hr02@acme.co.id",
        company = "ACME", organization_unit = "DEPT-HR", position = "POS-000004", job_profile = "JOB-HR-MGR",
        reports_to = (string?)null, employment_type = "Permanent (PKWTT)", hire_date = "2017-02-01", status = "Active"
    },
    new
    {
        employee_number = "EMP-0009", full_name = "Hendra Gunawan", user_id = "hr01", gender = "Male",
        date_of_birth = "1995-07-30", religion = "Islam", nationality = "ID", work_email = "hr01@acme.co.id",
        company = "ACME", organization_unit = "DEPT-HR", position = "POS-000005", job_profile = "JOB-HR-STAFF",
        reports_to = "EMP-0008", employment_type = "Permanent (PKWTT)", hire_date = "2022-04-01", status = "Active"
    },
    new
    {
        employee_number = "EMP-0010", full_name = "Indra Kusuma", user_id = "warehouse01", gender = "Male",
        date_of_birth = "1991-10-10", religion = "Islam", nationality = "ID", work_email = "warehouse01@acme.co.id",
        company = "ACME", organization_unit = "DEPT-WH", position = "POS-000008", job_profile = "JOB-WH-OFFICER",
        reports_to = "EMP-0008", employment_type = "Permanent (PKWTT)", hire_date = "2020-11-01", status = "Active"
    }
};
// Data pajak, payroll & persetujuan per karyawan. PTKP menentukan kategori TER (A: TK/0, TK/1, K/0; B: TK/2, TK/3, K/1, K/2; C: K/3).
// Approver cuti & klaim = atasan langsung (diisi juga oleh hook Employee saat disimpan lewat aplikasi; SQL melewati hook).
var employeeExtras = new Dictionary<string, (string Marital, string Ptkp, string CostCenter, string Grade, string Location, string Shift, string Bank, string AccountNo, string Nik, string Npwp)>
{
    ["EMP-0001"] = ("Single", "TK/0", "CC-400", "G2", "JKT-HQ", "Office 08-17", "014", "5270011001", "3174061506940001", "0912345670011000"),
    ["EMP-0002"] = ("Single", "TK/0", "CC-200", "G2", "JKT-HQ", "Office 08-17", "014", "5270011002", "3174066008960002", "0912345670012000"),
    ["EMP-0003"] = ("Married", "K/2", "CC-400", "G4", "JKT-HQ", "Office 08-17", "008", "1230011003", "3174060511880003", "0912345670013000"),
    ["EMP-0004"] = ("Married", "K/3", "CC-200", "G4", "JKT-HQ", "Office 08-17", "014", "5270011004", "3174061204870004", "0912345670014000"),
    ["EMP-0005"] = ("Married", "K/1", "CC-300", "G2", "JKT-HQ", "Office 08-17", "014", "5270011005", "3174060112920005", "0912345670015000"),
    ["EMP-0006"] = ("Married", "K/0", "CC-300", "G2", "JKT-HQ", "Office 08-17", "008", "1230011006", "3174061802930006", "0912345670016000"),
    ["EMP-0008"] = ("Married", "TK/0", "CC-300", "G5", "JKT-HQ", "Office 08-17", "014", "5270011008", "3174066509850008", "0912345670018000"),
    ["EMP-0009"] = ("Single", "TK/1", "CC-300", "G2", "JKT-HQ", "Office 08-17", "002", "0330011009", "3174063007950009", "0912345670019000"),
    ["EMP-0010"] = ("Married", "K/2", "CC-110", "G3", "CKR-WH", "Shift Pagi 06-14", "014", "5270011010", "3216061010910010", "0912345670010000"),
};
foreach (var emp in employees)
{
    var x = employeeExtras[emp.employee_number];
    var e = JsonSerializer.SerializeToNode(emp)!.AsObject();
    e["marital_status"] = x.Marital;
    e["ptkp_status"] = x.Ptkp;
    e["cost_center"] = x.CostCenter;
    e["grade"] = x.Grade;
    e["location"] = x.Location;
    e["shift_type"] = x.Shift;
    e["holiday_list"] = "Libur Nasional 2026";
    e["leave_policy"] = "Standard Leave Policy PKWTT";
    e["leave_approver"] = emp.reports_to;
    e["expense_approver"] = emp.reports_to;
    e["nik"] = x.Nik;
    e["npwp"] = x.Npwp;
    e["bpjs_health_no"] = "000" + x.AccountNo[2..];
    e["bpjs_employment_no"] = "26" + x.AccountNo[1..];
    e["mobile"] = "+6281" + x.AccountNo[2..];
    e["bank_accounts"] = new JsonArray(new JsonObject
    {
        ["bank"] = x.Bank, ["account_no"] = x.AccountNo, ["account_holder"] = emp.full_name, ["currency"] = "IDR", ["is_default"] = true
    });
    e["identity_documents"] = new JsonArray(
        new JsonObject { ["document_type"] = "KTP", ["document_no"] = x.Nik },
        new JsonObject { ["document_type"] = "NPWP", ["document_no"] = x.Npwp });
    records.Add(("Employee", emp.employee_number, e));
}

// ============================================================================
// 5. Customer Groups & Customers
// ============================================================================

var custGroups = new[]
{
    new { group_name = "Commercial & Corporate", is_group = false },
    new { group_name = "Government & BUMN", is_group = false },
    new { group_name = "Retail & Individual", is_group = false }
};
foreach (var cg in custGroups)
{
    records.Add(("CustomerGroup", cg.group_name, cg));
}

var customers = new[]
{
    new
    {
        customer_name = "PT Telekomunikasi Indonesia Tbk", customer_type = "BUMN",
        customer_group = "Government & BUMN", tax_id = "0010000130093000",
        receivable_account = "1121", email = "procurement@telkom.co.id", phone = "+62215215123", is_active = true
    },
    new
    {
        customer_name = "PT Bank Central Asia Tbk", customer_type = "Company",
        customer_group = "Commercial & Corporate", tax_id = "0013084497092000",
        receivable_account = "1121", email = "vendor.management@bca.co.id", phone = "+622123588000", is_active = true
    },
    new
    {
        customer_name = "PT Astra International Tbk", customer_type = "Company",
        customer_group = "Commercial & Corporate", tax_id = "0013028387091000",
        receivable_account = "1121", email = "corpsupply@astra.co.id", phone = "+62216522555", is_active = true
    }
};
// PKP: NITKU = NPWP + 000000 (kantor pusat). Wilayah, termin & plafon kredit dari master seed fase 7.
var customerExtras = new[]
{
    (Territory: "Jabodetabek", Term: "Net 30", Credit: 500000000m, Owner: "EMP-0004"),
    (Territory: "Jabodetabek", Term: "Net 30", Credit: 300000000m, Owner: "EMP-0002"),
    (Territory: "Jawa Barat", Term: "Net 45", Credit: 100000000m, Owner: "EMP-0002"),
};
for (var i = 0; i < customers.Length; i++)
{
    var custId = $"CUST-{(i + 1):D6}";
    var c = JsonSerializer.SerializeToNode(customers[i])!.AsObject();
    var x = customerExtras[i];
    c["tax_id_type"] = "NPWP";
    c["is_pkp"] = true;
    c["tax_name"] = customers[i].customer_name;
    c["nitku"] = customers[i].tax_id + "000000";
    c["territory"] = x.Territory;
    c["payment_term"] = x.Term;
    c["credit_limit"] = x.Credit;
    c["account_owner"] = x.Owner;
    c["default_price_list"] = "Standard Selling IDR";
    records.Add(("Customer", custId, c));
}

// ============================================================================
// 6. Vendor Groups & Vendors
// ============================================================================

var vendGroups = new[]
{
    new { group_name = "Technology & Hardware", is_group = false },
    new { group_name = "Office & Operations", is_group = false },
    new { group_name = "Logistics & Freight", is_group = false }
};
foreach (var vg in vendGroups)
{
    records.Add(("VendorGroup", vg.group_name, vg));
}

var vendors = new[]
{
    new
    {
        vendor_name = "PT Metrodata Electronics Tbk", vendor_type = "Company",
        vendor_group = "Technology & Hardware", tax_id = "0013098398092000",
        payable_account = "2110", email = "enterprise@metrodata.co.id", phone = "+622129345800", is_active = true
    },
    new
    {
        vendor_name = "PT Indofood CBP Sukses Makmur Tbk", vendor_type = "Company",
        vendor_group = "Office & Operations", tax_id = "0013374245091000",
        payable_account = "2110", email = "corporate.sales@icbp.indofood.co.id", phone = "+622157958822", is_active = true
    },
    new
    {
        vendor_name = "PT Jalur Nugraha Ekakurir (JNE)", vendor_type = "Company",
        vendor_group = "Logistics & Freight", tax_id = "0013484937092000",
        payable_account = "2110", email = "corpsales@jne.co.id", phone = "+622129278888", is_active = true
    }
};
var vendorExtras = new[]
{
    (Term: "Net 30", Bank: "014", AccountNo: "0063001001", Withholding: "None"),
    (Term: "Net 30", Bank: "008", AccountNo: "1220001002", Withholding: "None"),
    (Term: "Net 15", Bank: "014", AccountNo: "0063001003", Withholding: "PPh 23"), // jasa pengiriman: PPh 23 2%
};
for (var i = 0; i < vendors.Length; i++)
{
    var suppId = $"SUPP-{(i + 1):D6}";
    var v = JsonSerializer.SerializeToNode(vendors[i])!.AsObject();
    var x = vendorExtras[i];
    v["tax_id_type"] = "NPWP";
    v["is_pkp"] = true;
    v["tax_name"] = vendors[i].vendor_name;
    v["nitku"] = vendors[i].tax_id + "000000";
    v["payment_term"] = x.Term;
    v["default_withholding"] = x.Withholding;
    v["bank_accounts"] = new JsonArray(new JsonObject
    {
        ["bank"] = x.Bank, ["account_no"] = x.AccountNo, ["account_holder"] = vendors[i].vendor_name, ["currency"] = "IDR", ["is_default"] = true
    });
    records.Add(("Vendor", suppId, v));
}

// ============================================================================
// 7. Bank Accounts (Company Treasury)
// ============================================================================

var bankAccounts = new[]
{
    new
    {
        bank_account_code = "BCA-OPS-01", account_name = "BCA Operasional ACME", company = "ACME",
        bank = "014", account_no = "1234567890", branch = "KCP Sudirman", currency = "IDR",
        gl_account = "1112", is_default = true, is_active = true
    },
    new
    {
        bank_account_code = "MANDIRI-OPS-01", account_name = "Mandiri Operasional ACME", company = "ACME",
        bank = "008", account_no = "9876543210", branch = "KCP Thamrin", currency = "IDR",
        gl_account = "1113", is_default = false, is_active = true
    }
};
foreach (var ba in bankAccounts)
{
    records.Add(("BankAccount", ba.bank_account_code, ba));
}

// ============================================================================
// 8. Warehouses
// ============================================================================

var warehouses = new[]
{
    new { warehouse_name = "Gudang Utama Cikarang", company = "ACME", is_group = false, inventory_account = "1131", is_active = true },
    new { warehouse_name = "Gudang Transit Jakarta", company = "ACME", is_group = false, inventory_account = "1131", is_active = true },
    new { warehouse_name = "Gudang Retur Cikarang", company = "ACME", is_group = false, inventory_account = "1131", is_active = true }
};
foreach (var wh in warehouses)
{
    records.Add(("Warehouse", wh.warehouse_name, wh));
}

// ============================================================================
// 9. Item Groups & Items
// ============================================================================

var itemGroups = new[]
{
    new { group_name = "Perangkat Komputer & Hardware", is_group = false, income_account = "4100", expense_account = "5100" },
    new { group_name = "Aksesoris & Peralatan IT", is_group = false, income_account = "4100", expense_account = "5100" },
    new { group_name = "Jasa Konsultasi & Maintenance", is_group = false, income_account = "4200", expense_account = "6100" }
};
foreach (var ig in itemGroups)
{
    records.Add(("ItemGroup", ig.group_name, ig));
}

var items = new[]
{
    new
    {
        item_code = "ITM-SRV-001", item_name = "Server Rack 2U Enterprise Xeon 64GB", item_type = "Goods",
        item_group = "Perangkat Komputer & Hardware", uom = "UNT", is_sales_item = true, is_purchase_item = true,
        is_stock_item = true, default_warehouse = "Gudang Utama Cikarang", is_active = true,
        income_account = "4100", expense_account = "5100"
    },
    new
    {
        item_code = "ITM-LAP-001", item_name = "Laptop Business Ultra 14 Core Ultra 7", item_type = "Goods",
        item_group = "Perangkat Komputer & Hardware", uom = "UNT", is_sales_item = true, is_purchase_item = true,
        is_stock_item = true, default_warehouse = "Gudang Utama Cikarang", is_active = true,
        income_account = "4100", expense_account = "5100"
    },
    new
    {
        item_code = "ITM-MNT-001", item_name = "Monitor 27 Inch 4K UHD IPS USB-C", item_type = "Goods",
        item_group = "Perangkat Komputer & Hardware", uom = "UNT", is_sales_item = true, is_purchase_item = true,
        is_stock_item = true, default_warehouse = "Gudang Utama Cikarang", is_active = true,
        income_account = "4100", expense_account = "5100"
    },
    new
    {
        item_code = "ITM-CAB-001", item_name = "Kabel UTP Cat6 305 Meter Box", item_type = "Goods",
        item_group = "Aksesoris & Peralatan IT", uom = "BOX", is_sales_item = true, is_purchase_item = true,
        is_stock_item = true, default_warehouse = "Gudang Utama Cikarang", is_active = true,
        income_account = "4100", expense_account = "5100"
    },
    new
    {
        item_code = "ITM-SVC-001", item_name = "Jasa Implementasi & Setup Sistem ERP", item_type = "Service",
        item_group = "Jasa Konsultasi & Maintenance", uom = "DAY", is_sales_item = true, is_purchase_item = false,
        is_stock_item = false, default_warehouse = (string?)null, is_active = true,
        income_account = "4200", expense_account = "6100"
    },
    new
    {
        item_code = "ITM-SVC-002", item_name = "Jasa Maintenance & Support Bulanan", item_type = "Service",
        item_group = "Jasa Konsultasi & Maintenance", uom = "MTH", is_sales_item = true, is_purchase_item = false,
        is_stock_item = false, default_warehouse = (string?)null, is_active = true,
        income_account = "4200", expense_account = "6100"
    }
};
foreach (var itm in items)
{
    records.Add(("Item", itm.item_code, itm));
}

// ============================================================================
// 10. Tax Codes (Indonesian Tax Configuration)
// ============================================================================

var taxCodes = new[]
{
    new
    {
        tax_code = "PPN-12", tax_name = "PPN Keluaran 12%", tax_type = "Output VAT",
        rate = 12.0m, dpp_factor = 1.0, is_withholding = false, account = "2151", prepaid_account = (string?)null, tax_object_code = (string?)null
    },
    new
    {
        tax_code = "PPN-MASUKAN-12", tax_name = "PPN Masukan 12%", tax_type = "Input VAT",
        rate = 12.0m, dpp_factor = 1.0, is_withholding = false, account = "1151", prepaid_account = (string?)null, tax_object_code = (string?)null
    },
    new
    {
        tax_code = "PPH23-JASA-2", tax_name = "PPh 23 Jasa 2%", tax_type = "PPh 23",
        rate = 2.0m, dpp_factor = 1.0, is_withholding = true, account = "2152", prepaid_account = (string?)"1150", tax_object_code = (string?)null
    },
    new
    {
        tax_code = "PPH21-TER", tax_name = "PPh 21 Potongan Payroll", tax_type = "PPh 21",
        rate = 0.0m, dpp_factor = 1.0, is_withholding = true, account = "2150", prepaid_account = (string?)null, tax_object_code = (string?)null
    }
};
foreach (var tc in taxCodes)
{
    records.Add(("TaxCode", tc.tax_code, tc));
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
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 4 (Operational Master Data)");
sb.AppendLine("-- Tenant: acme | HR, Customers, Vendors, Banks, Warehouses, Items, TaxCodes");
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
File.WriteAllText("deploy/sql/seed-phase-4.sql", sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"Successfully generated deploy/sql/seed-phase-4.sql with {records.Count} records.");

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
