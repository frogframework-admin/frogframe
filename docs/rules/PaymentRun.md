<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Payment Run (PaymentRun)

Aturan yang dijalankan kode saat dokumen Payment Run (PaymentRun) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PaymentRunHook**: Payment Run (SAP F110 / NetSuite Pay Bills). Simpan: tiap baris dicek (dokumen submit/disetujui, milik pihak, tidak ditahan, mata uang rekening, jumlah ≤ sisa utang dari buku besar) dan rekening tujuan diisi dari data pihak. Submit: event membuat satu Payment Entry per pihak (tipe Pay, referensi = baris run) lalu men-submit-nya; buku besar ditulis oleh Payment Entry, bukan oleh run.
- **PaymentRunGetItemsAction**: Aksi "Get open items": utang jatuh tempo per pemilihan run (faktur pemasok, klaim & uang muka disetujui, gaji dari Payroll Run).

## Validasi (dokumen ditolak): Saat cancel

- `name`: "{live.Count} payment entries were created by this run; cancel them first ({string.Join(", ", live.Take(5).Select(p => p.Name))})" — terjadi bila `live.Count > 0` (PaymentRunHook, PaymentRunHooks.cs:107)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `bank_account`: "Bank account {bank.Name} belongs to another company" — terjadi bila `bank.Data.Str("company") != company.Name` (PaymentRunHook, PaymentRunHooks.cs:54)
- `bank_account`: "Payment runs from foreign-currency bank accounts are not supported yet" — terjadi bila `currency != company.BaseCurrency` (PaymentRunHook, PaymentRunHooks.cs:56)
- `{p}.reference_name`: "{refName} is listed more than once" — terjadi bila `!seen.Add((refType, refName))` (PaymentRunHook, PaymentRunHooks.cs:66)
- `{p}.reference_name`: "{refName} is not submitted/approved or has been cancelled" — terjadi bila `doc is null || doc.DocStatus != DocStatus.Submitted || (doc.WorkflowState is not null && doc.WorkflowState != Approval.Approved)` (PaymentRunHook, PaymentRunHooks.cs:69)
- `{p}.party_type`: "{refType} is paid to a {expectedType}" — terjadi bila `partyType != expectedType` (PaymentRunHook, PaymentRunHooks.cs:71)
- `{p}.party`: "{refName} does not belong to {party}" — terjadi bila `PayRun.PartyField.TryGetValue(refType, out var pf) && doc.Data.Str(pf) != party` (PaymentRunHook, PaymentRunHooks.cs:72)
- `{p}.reference_name`: "{refName} belongs to another company" — terjadi bila `(doc.Data.Str("company") ?? company.Name) != company.Name` (PaymentRunHook, PaymentRunHooks.cs:73)
- `{p}.reference_name`: "{refName} is not in {currency}" — terjadi bila `(doc.Data.Str("currency") ?? currency) != currency` (PaymentRunHook, PaymentRunHooks.cs:74)
- `{p}.reference_name`: "Payments for {refName} are on hold" — terjadi bila `doc.Data.Bool("on_hold") || (partyType == "Vendor" && (await m.GetAsync("Vendor", party, ct))?.Data.Bool("on_hold") == true)` (PaymentRunHook, PaymentRunHooks.cs:76)
- `{p}.reference_name`: "{refName} has nothing left to pay" — terjadi bila `outstanding <= 0` (PaymentRunHook, PaymentRunHooks.cs:81)
- `{p}.amount`: "Amount must be greater than 0" — terjadi bila `r.Dec("amount") <= 0` (PaymentRunHook, PaymentRunHooks.cs:82)
- `{p}.amount`: "Exceeds the outstanding amount of {refName} ({outstanding})" — terjadi bila `r.Dec("amount") > outstanding` (PaymentRunHook, PaymentRunHooks.cs:83)
- `items`: "Add at least one item; use 'Get open items'" — terjadi bila `!(refType is null || refName is null || partyType is null || party is null) && c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft && rows.Count == 0` (PaymentRunHook, PaymentRunHooks.cs:92)

## Tombol aksi gagal: Saat tombol aksi diklik

- "Set the company first" — terjadi bila `company is null` (PaymentRunGetItemsAction, PaymentRunHooks.cs:175)

## Tombol aksi: Get open items

Tombol **Get open items** di form Payment Run hanya tampil bila `doc.DocStatus == DocStatus.Draft && user.HasRole(ErpRoles.FinanceTreasury)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Get open items": utang jatuh tempo per pemilihan run (faktur pemasok, klaim & uang muka disetujui, gaji dari Payroll Run).

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
