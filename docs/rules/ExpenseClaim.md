<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Expense Claim (ExpenseClaim)

Aturan yang dijalankan kode saat dokumen Expense Claim (ExpenseClaim) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ExpenseClaimHook**: Klaim biaya. Simpan: akun & cost center baris dari jenis biaya/karyawan, batas & bukti, uang muka yang dipotong. Disetujui: Dr beban / Cr utang ke karyawan (net, open item klaim ini) / Cr uang muka (open item uang muka).

## Validasi (dokumen ditolak): Pemeriksaan AdvanceAsync

- `advance_settled`: "Choose the advance being settled" — terjadi bila `settle != 0` (ExpenseClaimHook, EmployeeFinanceHooks.cs:100)
- `employee_advance`: "{adv.Name} belongs to another employee" — terjadi bila `adv.Data.Str("employee") != d.Str("employee")` (ExpenseClaimHook, EmployeeFinanceHooks.cs:101)
- `employee_advance`: "{adv.Name} is not approved" — terjadi bila `adv.WorkflowState != Approval.Approved` (ExpenseClaimHook, EmployeeFinanceHooks.cs:102)
- `advance_settled`: "The advance deduction cannot exceed the total claimed; return the unused advance through a Payment Entry" — terjadi bila `settle > total` (ExpenseClaimHook, EmployeeFinanceHooks.cs:103)
- `advance_settled`: "Exceeds the open balance of advance {adv.Name} ({(open is null ? 0m : open.NetBase):N2})" — terjadi bila `settle > 0 && (open is null || settle > open.NetBase)` (ExpenseClaimHook, EmployeeFinanceHooks.cs:105)

## Validasi (dokumen ditolak): Saat cancel

- `name`: "{c.Doc.Name} has been paid; cancel the payment first" — terjadi bila `await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct)` (ExpenseClaimHook, EmployeeFinanceHooks.cs:171)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `{p}.receipt`: "{type.Name} requires a receipt" — terjadi bila `!(type is null) && type.Data.Bool("requires_receipt") && r.Str("receipt") is null` (ExpenseClaimHook, EmployeeFinanceHooks.cs:126)
- `{p}.amount`: "Exceeds the {type.Name} limit ({max})" — terjadi bila `!(type is null) && max > 0 && r.Dec("amount") > max` (ExpenseClaimHook, EmployeeFinanceHooks.cs:128)
- `{p}.amount`: "Amount must be greater than 0" — terjadi bila `!(type is null) && r.Dec("amount") <= 0` (ExpenseClaimHook, EmployeeFinanceHooks.cs:129)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `payable_account`: "The employee payable account is not set" — terjadi bila `!(!Approval.JustApproved(c))` (ExpenseClaimHook, EmployeeFinanceHooks.cs:150)
- `items`: "The line expense account is not set" — terjadi bila `g.Key.Acc tidak ditemukan/kosong` (ExpenseClaimHook, EmployeeFinanceHooks.cs:155)
- `employee_advance`: "The advance account is not set" — terjadi bila `!(!Approval.JustApproved(c)) && settle > 0 && adv is not null` (ExpenseClaimHook, EmployeeFinanceHooks.cs:161)

## Aturan bersama yang juga berlaku

- Accounts: lihat `_shared-Accounts.md`
- Approval: lihat `_shared-Approval.md`
- BudgetControl: lihat `_shared-BudgetControl.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
