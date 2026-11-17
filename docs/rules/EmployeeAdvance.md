<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee Advance (EmployeeAdvance)

Aturan yang dijalankan kode saat dokumen Employee Advance (EmployeeAdvance) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeAdvanceHook**: Uang muka karyawan. Disetujui: Dr akun uang muka / Cr utang ke karyawan (keduanya open item milik dokumen ini). Payment Entry "Pay" melunasi utangnya; Expense Claim mempertanggungjawabkan uang muka; sisa dikembalikan lewat Payment Entry "Receive".

## Validasi (dokumen ditolak): Saat cancel

- `name`: "{c.Doc.Name} has been paid or settled; cancel those documents first" — terjadi bila `await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct)` (EmployeeAdvanceHook, EmployeeFinanceHooks.cs:66)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `advance_account`: "The advance account is not set" — terjadi bila `!(!Approval.JustApproved(c))` (EmployeeAdvanceHook, EmployeeFinanceHooks.cs:53)
- `company`: "The company's default payable account is not set" — terjadi bila `!(!Approval.JustApproved(c))` (EmployeeAdvanceHook, EmployeeFinanceHooks.cs:54)

## Aturan bersama yang juga berlaku

- Accounts: lihat `_shared-Accounts.md`
- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
