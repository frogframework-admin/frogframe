<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Budget (Budget)

Aturan yang dijalankan kode saat dokumen Budget (Budget) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **BudgetHook**: Anggaran: satu anggaran aktif per (perusahaan, tahun buku, cost center); akun tidak boleh dobel.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `accounts`: "{g.Key} appears more than once" (BudgetHook, BudgetHooks.cs:65)
- `cost_center`: "{dup.Name} is already the active budget for this fiscal year and cost center; amend it instead" — terjadi bila `dup is not null` (BudgetHook, BudgetHooks.cs:69)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
