<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Bank Reconciliation (BankReconciliation)

Aturan yang dijalankan kode saat dokumen Bank Reconciliation (BankReconciliation) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **BankReconciliationHook**: Rekonsiliasi bank: saldo buku dari ledger (GetBalancesAsync) per tanggal; daftar voucher bank (Payment Entry & Jurnal) yang belum kliring di rekonsiliasi sebelumnya; ditandai kliring bila sudah dicocokkan di rekening koran s.d. tanggal itu. Submit hanya bila selisih = 0. Rekonsiliasi valas belum didukung (ledger dalam mata uang fungsional).

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `bank_account`: "Reconciling foreign-currency bank accounts is not supported yet" — terjadi bila `bank.Data.Str("currency") != company.BaseCurrency` (BankReconciliationHook, TaxBankHooks.cs:141)
- `bank_account`: "The bank account is not linked to a GL account" — terjadi bila `!(bank.Data.Str("currency") != company.BaseCurrency)` (BankReconciliationHook, TaxBankHooks.cs:142)
- `period_end`: "The date is required" — terjadi bila `!(bank.Data.Str("currency") != company.BaseCurrency)` (BankReconciliationHook, TaxBankHooks.cs:143)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `difference`: "There is still a difference of {c.Doc.Data.Dec("difference")}; the reconciliation cannot be submitted yet" — terjadi bila `c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft && c.Doc.Data.Dec("difference") != 0` (BankReconciliationHook, TaxBankHooks.cs:192)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
