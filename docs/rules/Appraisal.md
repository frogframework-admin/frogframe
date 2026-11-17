<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Appraisal (Appraisal)

Aturan yang dijalankan kode saat dokumen Appraisal (Appraisal) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **AppraisalHook**: Appraisal: satu per (karyawan, siklus); karyawan hanya mengubah nilai diri, atasan hanya nilai atasan (HR boleh keduanya); rating dari skor akhir. Penyelesaian hanya saat siklus In progress dan semua nilai atasan terisi.

## Validasi (dokumen ditolak): Saat aksi workflow (approve/reject/...)

- `goals`: "Fill the manager score for every goal before completing" — terjadi bila `!(c.ToState != "Completed") && d.Rows("goals").Any(r => r.Dec("manager_score") <= 0)` (AppraisalHook, TalentHooks.cs:48)
- `appraisal_cycle`: "The appraisal cycle is {status}; appraisals can be completed only while it is In progress" — terjadi bila `!(c.ToState != "Completed") && !(d.Rows("goals").Any(r => r.Dec("manager_score") <= 0)) && status != "In progress"` (AppraisalHook, TalentHooks.cs:50)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `appraisal_cycle`: "{d.Str("employee")} already has appraisal {dup.Name} in this cycle" — terjadi bila `dup is not null` (AppraisalHook, TalentHooks.cs:27)
- `goals`: "Only the reviewer fills the manager scores" — terjadi bila `me == d.Str("employee") && Sig(before, "manager_score") != Sig(after, "manager_score")` (AppraisalHook, TalentHooks.cs:36)
- `goals`: "Only the employee fills the self scores" — terjadi bila `!(me == d.Str("employee") && Sig(before, "manager_score") != Sig(after, "manager_score")) && me == d.Str("reviewer") && me != d.Str("employee") && Sig(before, "self_score") != Sig(after, "self_score")` (AppraisalHook, TalentHooks.cs:38)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
