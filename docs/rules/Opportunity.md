<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Opportunity (Opportunity)

Aturan yang dijalankan kode saat dokumen Opportunity (Opportunity) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **OpportunityTerritoryHook**: Opportunity & Lead: territory dari customer bila kosong.
- **OpportunityHook**: Opportunity: nilai dari total produk bila ada baris; tanggal closing saat tahap Menang/Kalah.

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
