<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Training Event (TrainingEvent)

Aturan yang dijalankan kode saat dokumen Training Event (TrainingEvent) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **TrainingEventHook**: Pelatihan: peserta unik, nama peserta, dan jumlah hadir.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `attendees`: "{g.Key} is listed more than once" (TrainingEventHook, TalentHooks.cs:63)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
