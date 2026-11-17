<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Bank Statement (BankStatement)

Aturan yang dijalankan kode saat dokumen Bank Statement (BankStatement) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **BankStatementHook**: Rekening koran: cocokkan otomatis baris ke Payment Entry (jumlah sama, arah sama, tanggal ±3 hari, utamakan nomor referensi).

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
