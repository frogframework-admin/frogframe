<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama HrApply

Dipakai oleh: beberapa proses (job/event).

Application of HR events to Employee, shared by the event handler (immediate) and daily job (future effective dates).

## Validasi (dokumen ditolak)

- "Creating the user for {emp.Name} failed: {string.Join("; ", result.Errors.Select(e => e.Description))}" — terjadi bila `!result.Succeeded` (HrApply, HrHooks.cs:1059)
