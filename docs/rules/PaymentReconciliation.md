<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Payment Reconciliation (PaymentReconciliation)

Aturan yang dijalankan kode saat dokumen Payment Reconciliation (PaymentReconciliation) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PaymentReconciliationHook**: Payment Reconciliation (alokasi uang muka pihak ke faktur, tanpa mutasi kas; NetSuite Apply Credit / SAP F-32, F-44). Simpan: tiap baris dicek (pembayaran & faktur sudah submit, milik pihak & perusahaan yang sama, akun kontrol sama, mata uang fungsional), sisa uang muka & sisa tagihan diisi dari buku besar, total per pembayaran/faktur tidak boleh lebih. Submit: akun kontrol yang sama di-Dr/Cr dua kali: sisi pembayaran (against = PaymentEntry) menutup uang muka, sisi faktur (against = faktur) mengurangi sisa tagihan. Pelanggan: Dr vs pembayaran, Cr vs faktur; pemasok: kebalikannya. Cancel: baris pembalik standar.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `allocations`: "Add at least one allocation" — terjadi bila `!(partyType is null || party is null) && rows.Count == 0` (PaymentReconciliationHook, PaymentHooks.cs:249)
- `{p}.invoice_type`: "A {partyType} advance can only be allocated to a {invoiceType}" — terjadi bila `invType != invoiceType` (PaymentReconciliationHook, PaymentHooks.cs:259)
- `{p}.payment_entry`: "{payName} is not submitted or has been cancelled" — terjadi bila `pay is null || pay.DocStatus != DocStatus.Submitted` (PaymentReconciliationHook, PaymentHooks.cs:262)
- `{p}.payment_entry`: "{payName} does not belong to {party}" — terjadi bila `pay.Data.Str("party_type") != partyType || pay.Data.Str("party") != party` (PaymentReconciliationHook, PaymentHooks.cs:263)
- `{p}.payment_entry`: "{payName} belongs to another company" — terjadi bila `pay.Data.Str("company") != company.Name` (PaymentReconciliationHook, PaymentHooks.cs:264)
- `{p}.invoice`: "{invName} is not submitted or has been cancelled" — terjadi bila `inv is null || inv.DocStatus != DocStatus.Submitted` (PaymentReconciliationHook, PaymentHooks.cs:266)
- `{p}.invoice`: "{invName} does not belong to {party}" — terjadi bila `inv.Data.Str(partyField) != party` (PaymentReconciliationHook, PaymentHooks.cs:267)
- `{p}.invoice`: "{invName} belongs to another company" — terjadi bila `inv.Data.Str("company") != company.Name` (PaymentReconciliationHook, PaymentHooks.cs:268)
- `{p}.payment_entry`: "Foreign-currency advances cannot be allocated here yet; use a journal with exchange gain/loss" — terjadi bila `(pay.Data.Str("currency") ?? company.BaseCurrency) != company.BaseCurrency || (inv.Data.Str("currency") ?? company.BaseCurrency) != company.BaseCurrency` (PaymentReconciliationHook, PaymentHooks.cs:270)
- `{p}.payment_entry`: "{payName} has no unallocated advance balance" — terjadi bila `payItem is null || advance <= 0` (PaymentReconciliationHook, PaymentHooks.cs:278)
- `{p}.invoice`: "{invName} has no outstanding amount" — terjadi bila `invItem is null || outstanding <= 0` (PaymentReconciliationHook, PaymentHooks.cs:279)
- `{p}.invoice`: "{payName} and {invName} use different control accounts ({payItem.Account} vs {invItem.Account})" — terjadi bila `payItem.Account != invItem.Account` (PaymentReconciliationHook, PaymentHooks.cs:281)
- `{p}.payment_entry`: "All allocations must use the same control account ({control})" — terjadi bila `payItem.Account != control` (PaymentReconciliationHook, PaymentHooks.cs:283)
- `{p}.allocated_amount`: "The allocated amount must be greater than 0" — terjadi bila `amount <= 0` (PaymentReconciliationHook, PaymentHooks.cs:286)
- `{p}.allocated_amount`: "Exceeds the advance balance of {payName} ({advance})" — terjadi bila `!(payName is null || invType is null || invName is null) && perPayment[payName] > advance` (PaymentReconciliationHook, PaymentHooks.cs:289)
- `{p}.allocated_amount`: "Exceeds the outstanding amount of {invName} ({outstanding})" — terjadi bila `!(payName is null || invType is null || invName is null) && perInvoice[invName] > outstanding` (PaymentReconciliationHook, PaymentHooks.cs:290)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `posting_date`: "The posting date is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft)` (PaymentReconciliationHook, PaymentHooks.cs:303)
- `receivable_payable_account`: "The receivable/payable account is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft)` (PaymentReconciliationHook, PaymentHooks.cs:305)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
