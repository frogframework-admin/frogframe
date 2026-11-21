<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama InvoiceCore

Dipakai oleh: PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice.

Logika bersama SalesInvoice, SalesCreditNote, PurchaseInvoice, PurchaseDebitNote.

## Validasi (dokumen ditolak)

- `{k.PartyField}`: "{party.Name} nonaktif" — terjadi bila `party.Data.TryGetValue("is_active", out var active) && active is false` (InvoiceCore, InvoiceHooks.cs:66)
- `return_against`: "{origin.Name} is not submitted or has been cancelled" — terjadi bila `origin.DocStatus != DocStatus.Submitted` (InvoiceCore, InvoiceHooks.cs:73)
- `return_against`: "{origin.Name} does not belong to {party.Name}" — terjadi bila `origin.Data.Str(k.PartyField) != party.Name` (InvoiceCore, InvoiceHooks.cs:74)
- `return_against`: "{origin.Name} belongs to another company" — terjadi bila `origin.Data.Str("company") != company.Name` (InvoiceCore, InvoiceHooks.cs:75)
- `{k.PartyAccountField}`: "The {k.ControlType.ToLowerInvariant()} account is not set on the party, group, or company" — terjadi bila `partyAccount is null` (InvoiceCore, InvoiceHooks.cs:90)
- `{k.PartyAccountField}`: "Account {partyAccount} is not a {k.ControlType} account" — terjadi bila `acc?.Data.Str("account_type") != k.ControlType` (InvoiceCore, InvoiceHooks.cs:96)
- `items`: "At least one item line is required" — terjadi bila `rows.Count == 0` (InvoiceCore, InvoiceHooks.cs:113)
- `{p}.asset_category`: "The fixed asset account of category {assetCategory} is not set" — terjadi bila `fa is null` (InvoiceCore, InvoiceHooks.cs:130)
- `{p}.qty`: "Quantity must be greater than 0 (returns use a credit/debit note)" — terjadi bila `r.Dec("qty") <= 0` (InvoiceCore, InvoiceHooks.cs:136)
- `{p}.{k.LineAccountField}`: "The line account is not set on the item, item group, or company" — terjadi bila `r.Str(k.LineAccountField) is null` (InvoiceCore, InvoiceHooks.cs:137)
- `items`: "The total must be greater than 0" — terjadi bila `totals.GrandTotal <= 0` (InvoiceCore, InvoiceHooks.cs:142)
- `wht_code`: "Some lines are subject to withholding tax: choose a withholding tax code" — terjadi bila `!k.IsSales && totals.WhtBase > 0 && d.Str("wht_code") is null` (InvoiceCore, InvoiceHooks.cs:143)
- `bill_no`: "Supplier invoice {billNo} is already recorded in {dup.Name}" — terjadi bila `dup is not null` (InvoiceCore, InvoiceHooks.cs:154)
- `posting_date`: "The posting date is required" — terjadi bila `d.Date("posting_date") tidak ditemukan/kosong` (InvoiceCore, InvoiceHooks.cs:200)
- `{k.PartyAccountField}`: "The receivable/payable account is required" — terjadi bila `d.Str(k.PartyAccountField) tidak ditemukan/kosong` (InvoiceCore, InvoiceHooks.cs:210)
- `vat_code`: "The VAT account is not set on the tax code" — terjadi bila `t.Vat != 0 && vatAccount is null` (InvoiceCore, InvoiceHooks.cs:214)
- `wht_code`: "The withholding tax account is not set on the tax code" — terjadi bila `!(t.Vat != 0 && vatAccount is null) && t.Wht != 0 && whtAccount is null` (InvoiceCore, InvoiceHooks.cs:215)
- `customer`: "Receivable of {customer.Name} becomes {open + thisInvoice}, exceeding the credit limit of {limit}" — terjadi bila `!(customer is null || limit <= 0) && !(open + thisInvoice <= limit) && (await m.SingleAsync("FinanceSettings", ct)).Str("credit_limit_action") == "Block" && !c.User.HasRole(ErpRoles.FinanceAdmin)` (InvoiceCore, InvoiceHooks.cs:256)

## Peringatan

- "Receivable of {customer.Name} becomes {open + thisInvoice}, exceeding the credit limit of {limit}" — terjadi bila `!(customer is null || limit <= 0) && !(open + thisInvoice <= limit) && !((await m.SingleAsync("FinanceSettings", ct)).Str("credit_limit_action") == "Block" && !c.User.HasRole(ErpRoles.FinanceAdmin))` (InvoiceCore, InvoiceHooks.cs:257)
