<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Commission Statement (CommissionStatement)

Aturan yang dijalankan kode saat dokumen Commission Statement (CommissionStatement) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **CommissionStatementHook**: Laporan komisi per sales rep per bulan: Σ (total sebelum PPN × kurs) faktur penjualan submit milik sales rep − nota kredit atas faktur miliknya, × tarif rencana (atau tarif khusus rep). Submit: Dr beban komisi / Cr utang komisi (pihak = karyawan, against = statement).

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `commission_plan`: "{plan.Name} is not valid in {period}" — terjadi bila `!(rep is null || period is null || company is null) && plan.Data.Date("valid_from") > end || (plan.Data.Date("valid_to") is { } to && to < start)` (CommissionStatementHook, CrmExtraHooks.cs:209)
- `sales_rep`: "{rep} is not a participant of {plan.Name}" — terjadi bila `!(rep is null || period is null || company is null) && reps.Count > 0 && mine is null` (CommissionStatementHook, CrmExtraHooks.cs:212)
- `period`: "{dup.Name} already covers {rep} for {period}" — terjadi bila `!(rep is null || period is null || company is null) && dup is not null` (CommissionStatementHook, CrmExtraHooks.cs:218)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `total_commission`: "Commission is negative (credit notes exceed invoices); carry it to the next period instead" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(total == 0) && total < 0` (CommissionStatementHook, CrmExtraHooks.cs:249)
- `commission_plan`: "Set the commission expense account on {plan.Name}" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(total == 0) && !(total < 0)` (CommissionStatementHook, CrmExtraHooks.cs:251)
- `commission_plan`: "Set the commission payable account on {plan.Name}" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(total == 0) && !(total < 0)` (CommissionStatementHook, CrmExtraHooks.cs:252)
- `posting_date`: "The posting date is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(total == 0) && !(total < 0)` (CommissionStatementHook, CrmExtraHooks.cs:253)

## Aturan bersama yang juga berlaku

- BudgetControl: lihat `_shared-BudgetControl.md`
- Masters: lihat `_shared-Masters.md`
- Payroll: lihat `_shared-Payroll.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
