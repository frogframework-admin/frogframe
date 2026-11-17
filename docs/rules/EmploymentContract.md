<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employment Contract (EmploymentContract)

Aturan yang dijalankan kode saat dokumen Employment Contract (EmploymentContract) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmploymentContractHook**: Kontrak: tidak tumpang tindih; total PKWT termasuk perpanjangan maksimal 60 bulan (PP 35/2021, verifikasi); status dihitung saat dibuka.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `start_date`: "Overlaps with contract {o.Name}" — terjadi bila `Hr.Overlaps(start, end, o.Data.Date("start_date"), o.Data.Date("end_date"))` (EmploymentContractHook, HrHooks.cs:240)
- `end_date`: "Total fixed-term (PKWT) duration including renewals is {months} months, exceeding {MaxPkwtMonths} months" — terjadi bila `months > MaxPkwtMonths` (EmploymentContractHook, HrHooks.cs:248)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- AssetBook: lihat `_shared-AssetBook.md`
- Masters: lihat `_shared-Masters.md`
