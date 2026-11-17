<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Organization Unit (OrganizationUnit)

Aturan yang dijalankan kode saat dokumen Organization Unit (OrganizationUnit) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **OrganizationUnitHook**: Unit organisasi: pergantian posisi pimpinan memicu pembaruan atasan karyawan unit (lewat event).
