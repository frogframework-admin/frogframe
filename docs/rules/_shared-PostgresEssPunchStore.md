<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama PostgresEssPunchStore

Dipakai oleh: beberapa proses (job/event).

PostgreSQL store: executes a single lean INSERT ... ON CONFLICT DO NOTHING per punch (bypassing document hooks, naming series, and document audit tables during peak hours).

## Validasi (dokumen ditolak)

- "Punch conflict could not be resolved" — terjadi bila `!(await cmd.ExecuteNonQueryAsync(ct) == 1)` (PostgresEssPunchStore, EssPunches.cs:157)
