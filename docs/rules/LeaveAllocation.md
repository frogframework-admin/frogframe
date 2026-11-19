<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Leave Allocation (LeaveAllocation)

Aturan yang dijalankan kode saat dokumen Leave Allocation (LeaveAllocation) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `from_date`: "Overlaps with allocation {other.Name}" — terjadi bila `other is not null` (LeaveAllocationHook, HrHooks.cs:866)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
