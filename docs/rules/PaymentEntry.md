<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Payment (PaymentEntry)

Aturan yang dijalankan kode saat dokumen Payment (PaymentEntry) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PaymentEntryHook**: Payment Entry (Terima / Bayar / Transfer Internal). Simpan: validasi rekening & pihak, isi sisa tagihan tiap referensi dari buku besar, akun pihak default. Submit: Terima = Dr bank + Dr potongan / Cr piutang per referensi (against = referensi) + Cr uang muka (against = pembayaran ini). Bayar = kebalikannya. Transfer = Dr rekening tujuan / Cr rekening sumber. Valas: referensi dilunasi pada kurs aslinya, selisih ke akun laba/rugi selisih kurs perusahaan. Batas: nota kredit/debit melunasi faktur asal, bukan dibayar langsung. Uang muka pelanggan/pemasok dialokasikan ke faktur berikutnya lewat Payment Reconciliation.

## Validasi (dokumen ditolak): Pemeriksaan ResolveAsync

- `{p}.reference_name`: "{refName} is allocated more than once" — terjadi bila `result.Any(a => a.DocType == refType && a.Name == refName)` (PaymentEntryHook, PaymentHooks.cs:53)
- `{p}.reference_name`: "{refName} not found" — terjadi bila `doc is null` (PaymentEntryHook, PaymentHooks.cs:56)
- `{p}.reference_name`: "{refName} is not submitted/approved or has been cancelled" — terjadi bila `doc.DocStatus != DocStatus.Submitted || (doc.WorkflowState is not null && doc.WorkflowState != Approval.Approved)` (PaymentEntryHook, PaymentHooks.cs:58)
- `{p}.reference_name`: "Payments for {refName} are on hold" — terjadi bila `type == Pay && doc.Data.Bool("on_hold")` (PaymentEntryHook, PaymentHooks.cs:59)
- `{p}.reference_name`: "{refName} does not belong to {party}" — terjadi bila `PartyFieldOf.TryGetValue(refType, out var pf) && doc.Data.Str(pf) != party` (PaymentEntryHook, PaymentHooks.cs:61)
- `{p}.reference_name`: "The currency of {refName} ({doc.Data.Str("currency")}) differs from the payment ({currency})" — terjadi bila `(doc.Data.Str("currency") ?? currency) != currency` (PaymentEntryHook, PaymentHooks.cs:63)
- `{p}.reference_name`: "{refName} has no outstanding amount to {(type == Receive ? "receive" : "pay")} for {party}" — terjadi bila `open is null || open.NetBase == 0 || (type == Receive ? open.NetBase < 0 : open.NetBase > 0)` (PaymentEntryHook, PaymentHooks.cs:68)
- `{p}.allocated_amount`: "The allocated amount must be greater than 0" — terjadi bila `allocated <= 0` (PaymentEntryHook, PaymentHooks.cs:77)
- `{p}.allocated_amount`: "Exceeds the outstanding amount of {refName} ({outstanding})" — terjadi bila `allocated > outstanding` (PaymentEntryHook, PaymentHooks.cs:78)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `bank_account`: "Bank account {bank.Name} belongs to another company" — terjadi bila `bank.Data.Str("company") != company.Name` (PaymentEntryHook, PaymentHooks.cs:110)
- `bank_account`: "Bank account {bank.Name} is inactive" — terjadi bila `bank.Data.TryGetValue("is_active", out var active) && active is false` (PaymentEntryHook, PaymentHooks.cs:111)
- `currency`: "The payment currency must match the bank account ({bank.Data.Str("currency")})" — terjadi bila `bank.Data.Str("currency") != currency` (PaymentEntryHook, PaymentHooks.cs:112)
- `to_bank_account`: "Intercompany transfers are not supported yet" — terjadi bila `to.Data.Str("company") != company.Name` (PaymentEntryHook, PaymentHooks.cs:118)
- `to_bank_account`: "Cross-currency transfers are not supported yet" — terjadi bila `to.Data.Str("currency") != currency` (PaymentEntryHook, PaymentHooks.cs:119)
- `references`: "Internal transfers do not use allocations or deductions" — terjadi bila `d.Rows("references").Count > 0 || d.Rows("deductions").Count > 0` (PaymentEntryHook, PaymentHooks.cs:121)
- `party`: "Payments to {partyName} are on hold" — terjadi bila `type == Pay && partyType == "Vendor" && party.Data.Bool("on_hold")` (PaymentEntryHook, PaymentHooks.cs:131)
- `party_account`: "The party's receivable/payable account is not set" — terjadi bila `!(deductions[i].Str("account") is not { } acc) && d.Str("party_account") is null` (PaymentEntryHook, PaymentHooks.cs:145)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `posting_date`: "The posting date is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft)` (PaymentEntryHook, PaymentHooks.cs:156)
- `bank_account`: "The bank account is not linked to a GL account" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft)` (PaymentEntryHook, PaymentHooks.cs:171)
- `to_bank_account`: "The target bank account is not linked to a GL account" — terjadi bila `type == Transfer` (PaymentEntryHook, PaymentHooks.cs:178)
- `party`: "The party is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && partyType is null || party is null` (PaymentEntryHook, PaymentHooks.cs:184)
- `references`: "Total allocated exceeds paid amount + deductions" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(partyType is null || party is null) && unallocated < 0` (PaymentEntryHook, PaymentHooks.cs:208)
- `party_account`: "The party account is required for an advance" — terjadi bila `unallocated > 0` (PaymentEntryHook, PaymentHooks.cs:211)

## Aturan bersama yang juga berlaku

- Accounts: lihat `_shared-Accounts.md`
- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Payroll: lihat `_shared-Payroll.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
- Rates: lihat `_shared-Rates.md`
