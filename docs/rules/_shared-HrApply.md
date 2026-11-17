<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama HrApply

Dipakai oleh: beberapa proses (job/event).

Penerapan event HR ke Employee, dipakai handler event (langsung) dan job harian (tanggal efektif di masa depan).

## Validasi (dokumen ditolak)

- "Creating the user for {emp.Name} failed: {string.Join("; ", result.Errors.Select(e => e.Description))}" — terjadi bila `!result.Succeeded` (HrApply, HrHooks.cs:431)
