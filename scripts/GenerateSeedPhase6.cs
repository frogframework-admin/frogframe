#:property PublishAot=false
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

const string tenant = "acme";
const string owner = "admin";

var records = new List<(string DocType, string Name, int DocStatus, object Data)>();

// ============================================================================
// 1. General Ledger: Opening Balance Journal Entry
// ============================================================================

var openingJournalLines = new[]
{
    new Dictionary<string, object?>
    {
        ["account"] = "1111",
        ["account_name"] = "Kas Kecil (Petty Cash)",
        ["debit"] = 50000000m,
        ["credit"] = 0m,
        ["base_debit"] = 50000000m,
        ["base_credit"] = 0m,
        ["cost_center"] = "CC-300",
        ["description"] = "Saldo Awal Kas Kecil Kantor Pusat"
    },
    new Dictionary<string, object?>
    {
        ["account"] = "1112",
        ["account_name"] = "Bank BCA Operasional",
        ["debit"] = 500000000m,
        ["credit"] = 0m,
        ["base_debit"] = 500000000m,
        ["base_credit"] = 0m,
        ["cost_center"] = "CC-300",
        ["description"] = "Saldo Awal Rekening Giro BCA Operasional"
    },
    new Dictionary<string, object?>
    {
        ["account"] = "1121",
        ["account_name"] = "Piutang Usaha IDR",
        ["debit"] = 75000000m,
        ["credit"] = 0m,
        ["base_debit"] = 75000000m,
        ["base_credit"] = 0m,
        ["party_type"] = "Customer",
        ["party"] = "CUST-000001",
        ["cost_center"] = "CC-200",
        ["description"] = "Saldo Awal Piutang PT Telekomunikasi Indonesia Tbk"
    },
    new Dictionary<string, object?>
    {
        ["account"] = "1131",
        ["account_name"] = "Persediaan Barang Dagang",
        ["debit"] = 125000000m,
        ["credit"] = 0m,
        ["base_debit"] = 125000000m,
        ["base_credit"] = 0m,
        ["cost_center"] = "CC-110",
        ["description"] = "Saldo Awal Persediaan Barang Dagang Gudang Cikarang"
    },
    new Dictionary<string, object?>
    {
        ["account"] = "2110",
        ["account_name"] = "Hutang Usaha IDR",
        ["debit"] = 0m,
        ["credit"] = 150000000m,
        ["base_debit"] = 0m,
        ["base_credit"] = 150000000m,
        ["party_type"] = "Vendor",
        ["party"] = "SUPP-000001",
        ["cost_center"] = "CC-300",
        ["description"] = "Saldo Awal Hutang Usaha PT Metrodata Electronics Tbk"
    },
    new Dictionary<string, object?>
    {
        ["account"] = "3100",
        ["account_name"] = "Modal Disetor",
        ["debit"] = 0m,
        ["credit"] = 500000000m,
        ["base_debit"] = 0m,
        ["base_credit"] = 500000000m,
        ["cost_center"] = "CC-300",
        ["description"] = "Modal Saham Disetor Awal"
    },
    new Dictionary<string, object?>
    {
        ["account"] = "3200",
        ["account_name"] = "Laba Ditahan (Retained Earnings)",
        ["debit"] = 0m,
        ["credit"] = 100000000m,
        ["base_debit"] = 0m,
        ["base_credit"] = 100000000m,
        ["cost_center"] = "CC-300",
        ["description"] = "Saldo Laba Ditahan Tahun Lalu"
    }
};

records.Add(("JournalEntry", "JV-2026-000001", 1, new Dictionary<string, object?>
{
    ["company"] = "ACME",
    ["voucher_type"] = "Opening Balance",
    ["posting_date"] = "2026-01-01",
    ["reference_no"] = "REF-OPENING-2026",
    ["reference_date"] = "2026-01-01",
    ["currency"] = "IDR",
    ["exchange_rate"] = 1.0,
    ["remarks"] = "Saldo Awal Pembukuan Tahun Fiskal 2026 ACME Corporation",
    ["accounts"] = openingJournalLines,
    ["total_debit"] = 750000000m,
    ["total_credit"] = 750000000m,
    ["difference"] = 0m,
    ["base_total_debit"] = 750000000m,
    ["base_total_credit"] = 750000000m,
    ["auto_reverse"] = false
}));

// ============================================================================
// 2. Inventory: Material Receipt for Opening Stock
// ============================================================================

var stockEntryItems = new[]
{
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-LAP-001",
        ["item_name"] = "Laptop Business Ultra 14 Core Ultra 7",
        ["qty"] = 5.0,
        ["uom"] = "UNT",
        ["t_warehouse"] = "Gudang Utama Cikarang",
        ["basic_rate"] = 18000000m,
        ["valuation_rate"] = 18000000m,
        ["amount"] = 90000000m,
        ["expense_account"] = "3200",
        ["cost_center"] = "CC-110"
    },
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-MNT-001",
        ["item_name"] = "Monitor 27 Inch 4K UHD IPS USB-C",
        ["qty"] = 5.0,
        ["uom"] = "UNT",
        ["t_warehouse"] = "Gudang Utama Cikarang",
        ["basic_rate"] = 4800000m,
        ["valuation_rate"] = 4800000m,
        ["amount"] = 24000000m,
        ["expense_account"] = "3200",
        ["cost_center"] = "CC-110"
    },
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-CAB-001",
        ["item_name"] = "Kabel UTP Cat6 305 Meter Box",
        ["qty"] = 8.0,
        ["uom"] = "BOX",
        ["t_warehouse"] = "Gudang Utama Cikarang",
        ["basic_rate"] = 1375000m,
        ["valuation_rate"] = 1375000m,
        ["amount"] = 11000000m,
        ["expense_account"] = "3200",
        ["cost_center"] = "CC-110"
    }
};

records.Add(("StockEntry", "STE-2026-000001", 1, new Dictionary<string, object?>
{
    ["purpose"] = "Material Receipt",
    ["company"] = "ACME",
    ["posting_date"] = "2026-01-02",
    ["to_warehouse"] = "Gudang Utama Cikarang",
    ["offset_account"] = "3200",
    ["cost_center"] = "CC-110",
    ["items"] = stockEntryItems,
    ["total_value"] = 125000000m,
    ["remarks"] = "Penerimaan Saldo Awal Persediaan Fisik Gudang Utama Cikarang"
}));

// ============================================================================
// 3. Sales Workflow: Quotation -> SalesOrder -> SalesInvoice -> PaymentEntry
// ============================================================================

// a. Quotation
var quotationItems = new[]
{
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-LAP-001",
        ["item_name"] = "Laptop Business Ultra 14 Core Ultra 7",
        ["description"] = "Laptop kerja bisnis 14 inch prosesor Core Ultra 7",
        ["qty"] = 2.0,
        ["uom"] = "UNT",
        ["rate"] = 22500000m,
        ["discount_percent"] = 0m,
        ["amount"] = 45000000m,
        ["is_vat_applicable"] = true,
        ["taxable_amount"] = 45000000m
    },
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-MNT-001",
        ["item_name"] = "Monitor 27 Inch 4K UHD IPS USB-C",
        ["description"] = "Monitor kantor 27 inch 4K IPS warna presisi USB-C",
        ["qty"] = 2.0,
        ["uom"] = "UNT",
        ["rate"] = 6500000m,
        ["discount_percent"] = 0m,
        ["amount"] = 13000000m,
        ["is_vat_applicable"] = true,
        ["taxable_amount"] = 13000000m
    }
};

records.Add(("Quotation", "QTN-2026-000001", 1, new Dictionary<string, object?>
{
    ["company"] = "ACME",
    ["customer"] = "CUST-000001",
    ["customer_name"] = "PT Telekomunikasi Indonesia Tbk",
    ["quotation_date"] = "2026-01-10",
    ["valid_until"] = "2026-02-10",
    ["currency"] = "IDR",
    ["price_list"] = "Standard Selling IDR",
    ["sales_rep"] = "EMP-0002",
    ["organization_unit"] = "DEPT-SALES",
    ["vat_code"] = "PPN-12",
    ["vat_rate"] = 12.0m,
    ["vat_dpp_factor"] = 1.0,
    ["total"] = 58000000m,
    ["tax_base"] = 58000000m,
    ["vat_amount"] = 6960000m,
    ["grand_total"] = 64960000m,
    ["terms"] = "<p>1. Penawaran harga berlaku hingga 10 Februari 2026.<br>2. Syarat pembayaran 30 hari kalender sejak faktur diterima.<br>3. Garansi unit 1 tahun.</p>",
    ["items"] = quotationItems
}));

// b. Sales Order
records.Add(("SalesOrder", "SO-2026-000001", 1, new Dictionary<string, object?>
{
    ["company"] = "ACME",
    ["customer"] = "CUST-000001",
    ["customer_name"] = "PT Telekomunikasi Indonesia Tbk",
    ["customer_po_no"] = "PO-TLK-2026-0042",
    ["quotation"] = "QTN-2026-000001",
    ["order_date"] = "2026-01-12",
    ["delivery_date"] = "2026-01-16",
    ["currency"] = "IDR",
    ["price_list"] = "Standard Selling IDR",
    ["sales_rep"] = "EMP-0002",
    ["organization_unit"] = "DEPT-SALES",
    ["vat_code"] = "PPN-12",
    ["vat_rate"] = 12.0m,
    ["vat_dpp_factor"] = 1.0,
    ["total"] = 58000000m,
    ["tax_base"] = 58000000m,
    ["vat_amount"] = 6960000m,
    ["grand_total"] = 64960000m,
    ["billed_amount"] = 58000000m,
    ["billing_status"] = "Fully billed",
    ["terms"] = "<p>Pengiriman ke Telkom Landmark Tower Lt. 15, Jl. Jend. Gatot Subroto Kav. 52, Jakarta Selatan.</p>",
    ["items"] = quotationItems
}));

// c. Sales Invoice
var salesInvoiceItems = new[]
{
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-LAP-001",
        ["item_name"] = "Laptop Business Ultra 14 Core Ultra 7",
        ["description"] = "Laptop kerja bisnis 14 inch prosesor Core Ultra 7",
        ["qty"] = 2.0,
        ["uom"] = "UNT",
        ["rate"] = 22500000m,
        ["discount_percent"] = 0m,
        ["amount"] = 45000000m,
        ["is_vat_applicable"] = true,
        ["taxable_amount"] = 45000000m,
        ["income_account"] = "4100",
        ["cost_center"] = "CC-200"
    },
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-MNT-001",
        ["item_name"] = "Monitor 27 Inch 4K UHD IPS USB-C",
        ["description"] = "Monitor kantor 27 inch 4K IPS warna presisi USB-C",
        ["qty"] = 2.0,
        ["uom"] = "UNT",
        ["rate"] = 6500000m,
        ["discount_percent"] = 0m,
        ["amount"] = 13000000m,
        ["is_vat_applicable"] = true,
        ["taxable_amount"] = 13000000m,
        ["income_account"] = "4100",
        ["cost_center"] = "CC-200"
    }
};

records.Add(("SalesInvoice", "SINV-2026-000001", 1, new Dictionary<string, object?>
{
    ["company"] = "ACME",
    ["customer"] = "CUST-000001",
    ["customer_name"] = "PT Telekomunikasi Indonesia Tbk",
    ["customer_po_no"] = "PO-TLK-2026-0042",
    ["quotation"] = "QTN-2026-000001",
    ["sales_order"] = "SO-2026-000001",
    ["sales_rep"] = "EMP-0002",
    ["posting_date"] = "2026-01-16",
    ["due_date"] = "2026-02-15",
    ["currency"] = "IDR",
    ["exchange_rate"] = 1.0,
    ["debit_to"] = "1121",
    ["cost_center"] = "CC-200",
    ["vat_code"] = "PPN-12",
    ["vat_rate"] = 12.0m,
    ["vat_dpp_factor"] = 1.0,
    ["tax_invoice_no"] = "010.002-26.00000001",
    ["total"] = 58000000m,
    ["tax_base"] = 58000000m,
    ["vat_amount"] = 6960000m,
    ["grand_total"] = 64960000m,
    ["base_grand_total"] = 64960000m,
    ["outstanding_amount"] = 0m,
    ["payment_status"] = "Paid",
    ["remarks"] = "Faktur Penjualan Pengadaan Laptop & Monitor untuk PT Telekomunikasi Indonesia Tbk",
    ["items"] = salesInvoiceItems
}));

// d. Payment Entry (Customer Payment)
records.Add(("PaymentEntry", "PAY-2026-000001", 1, new Dictionary<string, object?>
{
    ["payment_type"] = "Receive",
    ["company"] = "ACME",
    ["posting_date"] = "2026-01-22",
    ["mode_of_payment"] = "Bank transfer",
    ["party_type"] = "Customer",
    ["party"] = "CUST-000001",
    ["bank_account"] = "BCA-OPS-01",
    ["party_account"] = "1121",
    ["currency"] = "IDR",
    ["exchange_rate"] = 1.0,
    ["paid_amount"] = 64960000m,
    ["base_paid_amount"] = 64960000m,
    ["reference_no"] = "TRF-BCA-20260122-8812",
    ["reference_date"] = "2026-01-22",
    ["total_allocated"] = 64960000m,
    ["total_deductions"] = 0m,
    ["unallocated_amount"] = 0m,
    ["remarks"] = "Penerimaan Pelunasan Faktur SINV-2026-000001 dari PT Telekomunikasi Indonesia Tbk",
    ["references"] = new[]
    {
        new Dictionary<string, object?>
        {
            ["reference_doctype"] = "SalesInvoice",
            ["reference_name"] = "SINV-2026-000001",
            ["due_date"] = "2026-02-15",
            ["outstanding_amount"] = 64960000m,
            ["allocated_amount"] = 64960000m
        }
    }
}));

// ============================================================================
// 4. Procurement Workflow: PurchaseRequest -> PurchaseOrder -> PurchaseInvoice -> PaymentEntry
// ============================================================================

// a. Purchase Request
var prItems = new[]
{
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-SRV-001",
        ["item_name"] = "Server Rack 2U Enterprise Xeon 64GB",
        ["description"] = "Server Rack Enterprise untuk ekspansi node data center DC Cikarang",
        ["qty"] = 1.0,
        ["uom"] = "UNT",
        ["estimated_rate"] = 35000000m,
        ["amount"] = 35000000m,
        ["required_by"] = "2026-01-25"
    }
};

records.Add(("PurchaseRequest", "PRQ-2026-00001", 1, new Dictionary<string, object?>
{
    ["requested_by"] = "EMP-0001",
    ["employee_name"] = "Ahmad Fadilah",
    ["approver"] = "EMP-0003",
    ["organization_unit"] = "DEPT-IT",
    ["company"] = "ACME",
    ["cost_center"] = "CC-400",
    ["required_by"] = "2026-01-25",
    ["suggested_vendor"] = "SUPP-000001",
    ["purpose"] = "Pengadaan Server Rack Enterprise tambahan untuk data center DC Cikarang",
    ["order_status"] = "Ordered",
    ["total_estimate"] = 35000000m,
    ["items"] = prItems
}));

// b. Purchase Order
var poItems = new[]
{
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-SRV-001",
        ["item_name"] = "Server Rack 2U Enterprise Xeon 64GB",
        ["description"] = "Server Rack Enterprise untuk ekspansi node data center DC Cikarang",
        ["qty"] = 1.0,
        ["uom"] = "UNT",
        ["rate"] = 35000000m,
        ["discount_percent"] = 0m,
        ["amount"] = 35000000m,
        ["is_vat_applicable"] = true,
        ["taxable_amount"] = 35000000m,
        ["cost_center"] = "CC-400"
    }
};

records.Add(("PurchaseOrder", "PO-2026-000001", 1, new Dictionary<string, object?>
{
    ["company"] = "ACME",
    ["vendor"] = "SUPP-000001",
    ["vendor_name"] = "PT Metrodata Electronics Tbk",
    ["purchase_request"] = "PRQ-2026-00001",
    ["order_date"] = "2026-01-18",
    ["delivery_date"] = "2026-01-24",
    ["currency"] = "IDR",
    ["vat_code"] = "PPN-MASUKAN-12",
    ["vat_rate"] = 12.0m,
    ["vat_dpp_factor"] = 1.0,
    ["receipt_status"] = "Fully received",
    ["billing_status"] = "Fully billed",
    ["received_amount"] = 35000000m,
    ["billed_amount"] = 35000000m,
    ["total"] = 35000000m,
    ["tax_base"] = 35000000m,
    ["vat_amount"] = 4200000m,
    ["grand_total"] = 39200000m,
    ["terms"] = "<p>Pengiriman langsung ke Gudang Utama Cikarang. Pembayaran jatuh tempo 30 hari kalender.</p>",
    ["items"] = poItems
}));

// c. Purchase Invoice
var piItems = new[]
{
    new Dictionary<string, object?>
    {
        ["item"] = "ITM-SRV-001",
        ["item_name"] = "Server Rack 2U Enterprise Xeon 64GB",
        ["description"] = "Server Rack Enterprise untuk ekspansi node data center DC Cikarang",
        ["qty"] = 1.0,
        ["uom"] = "UNT",
        ["rate"] = 35000000m,
        ["discount_percent"] = 0m,
        ["amount"] = 35000000m,
        ["is_vat_applicable"] = true,
        ["taxable_amount"] = 35000000m,
        ["is_wht_applicable"] = false,
        ["wht_base"] = 0m,
        ["expense_account"] = "1131",
        ["cost_center"] = "CC-400"
    }
};

records.Add(("PurchaseInvoice", "PINV-2026-000001", 1, new Dictionary<string, object?>
{
    ["company"] = "ACME",
    ["vendor"] = "SUPP-000001",
    ["vendor_name"] = "PT Metrodata Electronics Tbk",
    ["bill_no"] = "INV-MDE-2026-00451",
    ["bill_date"] = "2026-01-24",
    ["purchase_order"] = "PO-2026-000001",
    ["posting_date"] = "2026-01-24",
    ["due_date"] = "2026-02-23",
    ["currency"] = "IDR",
    ["exchange_rate"] = 1.0,
    ["credit_to"] = "2110",
    ["cost_center"] = "CC-400",
    ["vat_code"] = "PPN-MASUKAN-12",
    ["vat_rate"] = 12.0m,
    ["vat_dpp_factor"] = 1.0,
    ["tax_invoice_no"] = "010.001-26.99887766",
    ["total"] = 35000000m,
    ["tax_base"] = 35000000m,
    ["vat_amount"] = 4200000m,
    ["grand_total"] = 39200000m,
    ["payable_amount"] = 39200000m,
    ["base_grand_total"] = 39200000m,
    ["outstanding_amount"] = 0m,
    ["payment_status"] = "Paid",
    ["remarks"] = "Faktur Pembelian Server Rack Enterprise dari PT Metrodata Electronics Tbk",
    ["items"] = piItems
}));

// d. Payment Entry (Vendor Payment)
records.Add(("PaymentEntry", "PAY-2026-000002", 1, new Dictionary<string, object?>
{
    ["payment_type"] = "Pay",
    ["company"] = "ACME",
    ["posting_date"] = "2026-01-28",
    ["mode_of_payment"] = "Bank transfer",
    ["party_type"] = "Vendor",
    ["party"] = "SUPP-000001",
    ["bank_account"] = "BCA-OPS-01",
    ["party_account"] = "2110",
    ["currency"] = "IDR",
    ["exchange_rate"] = 1.0,
    ["paid_amount"] = 39200000m,
    ["base_paid_amount"] = 39200000m,
    ["reference_no"] = "TRF-OUT-BCA-20260128-001",
    ["reference_date"] = "2026-01-28",
    ["total_allocated"] = 39200000m,
    ["total_deductions"] = 0m,
    ["unallocated_amount"] = 0m,
    ["remarks"] = "Pembayaran Pelunasan Faktur Pembelian PINV-2026-000001 ke PT Metrodata Electronics Tbk",
    ["references"] = new[]
    {
        new Dictionary<string, object?>
        {
            ["reference_doctype"] = "PurchaseInvoice",
            ["reference_name"] = "PINV-2026-000001",
            ["due_date"] = "2026-02-23",
            ["outstanding_amount"] = 39200000m,
            ["allocated_amount"] = 39200000m
        }
    }
}));

// ============================================================================
// 5. HR Workflows: Attendance & Leave Application
// ============================================================================

var attendanceRecords = new[]
{
    new
    {
        id = "ATT-2026-0000001",
        employee = "EMP-0001",
        name = "Ahmad Fadilah",
        unit = "DEPT-IT",
        date = "2026-01-05",
        status = "Present",
        shift = "Office 08-17",
        checkIn = "2026-01-05T07:55:00",
        checkOut = "2026-01-05T17:05:00",
        hours = 8.0,
        late = 0
    },
    new
    {
        id = "ATT-2026-0000002",
        employee = "EMP-0002",
        name = "Citra Dewi",
        unit = "DEPT-SALES",
        date = "2026-01-05",
        status = "Present",
        shift = "Office 08-17",
        checkIn = "2026-01-05T07:58:00",
        checkOut = "2026-01-05T17:02:00",
        hours = 8.0,
        late = 0
    },
    new
    {
        id = "ATT-2026-0000003",
        employee = "EMP-0005",
        name = "Eko Prasetyo",
        unit = "DEPT-FIN",
        date = "2026-01-05",
        status = "Present",
        shift = "Office 08-17",
        checkIn = "2026-01-05T08:02:00",
        checkOut = "2026-01-05T17:15:00",
        hours = 8.0,
        late = 2
    },
    new
    {
        id = "ATT-2026-0000004",
        employee = "EMP-0006",
        name = "Fajar Hidayat",
        unit = "DEPT-PURCH",
        date = "2026-01-05",
        status = "Present",
        shift = "Office 08-17",
        checkIn = "2026-01-05T07:50:00",
        checkOut = "2026-01-05T17:00:00",
        hours = 8.0,
        late = 0
    }
};

foreach (var att in attendanceRecords)
{
    records.Add(("Attendance", att.id, 0, new Dictionary<string, object?>
    {
        ["employee"] = att.employee,
        ["employee_name"] = att.name,
        ["organization_unit"] = att.unit,
        ["attendance_date"] = att.date,
        ["status"] = att.status,
        ["shift_type"] = att.shift,
        ["check_in"] = att.checkIn,
        ["check_out"] = att.checkOut,
        ["working_hours"] = att.hours,
        ["late_minutes"] = att.late,
        ["remarks"] = "Hadir tepat waktu dan absen tercatat otomatis"
    }));
}

records.Add(("LeaveApplication", "LV-2026-000001", 1, new Dictionary<string, object?>
{
    ["employee"] = "EMP-0001",
    ["employee_name"] = "Ahmad Fadilah",
    ["organization_unit"] = "DEPT-IT",
    ["leave_approver"] = "EMP-0003",
    ["leave_type"] = "CUTI-TAHUNAN",
    ["requires_attachment"] = false,
    ["from_date"] = "2026-01-19",
    ["to_date"] = "2026-01-20",
    ["half_day"] = false,
    ["total_days"] = 2.0,
    ["balance_before"] = 12.0,
    ["reason"] = "Keperluan keluarga di luar kota"
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
sb.AppendLine("-- FrogFrame ERP Seed Data: Phase 6 (Transactional Workflows)");
sb.AppendLine("-- Tenant: acme | GL Opening, Initial Stock, Sales, Procurement, Attendance & Leave");
sb.AppendLine("-- ============================================================================");
sb.AppendLine("BEGIN;");
sb.AppendLine();

foreach (var (docType, name, docStatus, data) in records)
{
    var dataJson = JsonSerializer.Serialize(data, jsonOptions).Replace("'", "''");
    var nameEscaped = name.Replace("'", "''");
    sb.AppendLine($"""
        INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
        VALUES ('{tenant}', '{docType}', '{nameEscaped}', '{owner}', {docStatus}, NOW(), NOW(), '{owner}', 1, 1, '{dataJson}'::jsonb)
        ON CONFLICT (tenant_id, doctype, name) DO UPDATE 
        SET data = EXCLUDED.data, docstatus = EXCLUDED.docstatus, modified = NOW(), modified_by = EXCLUDED.modified_by;
        """);
    sb.AppendLine();
}

sb.AppendLine("COMMIT;");

Directory.CreateDirectory("deploy/sql");
File.WriteAllText("deploy/sql/seed-phase-6.sql", sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"Successfully generated deploy/sql/seed-phase-6.sql with {records.Count} records.");
