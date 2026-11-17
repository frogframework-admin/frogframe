<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama PaymentRunExecutor

Dipakai oleh: beberapa proses (job/event).

Event: satu Payment Entry per pihak dari run yang sudah submit. Idempoten per (run, pihak); gagal sebagian dilempar ulang agar di-retry outbox.

## Validasi (dokumen ditolak)

- "{runName}: {failures.Count} payments failed — {string.Join("; ", failures.Take(5))}" — terjadi bila `!(run is null || run.DocStatus != DocStatus.Submitted) && failures.Count > 0` (PaymentRunExecutor, PaymentRunHooks.cs:155)
