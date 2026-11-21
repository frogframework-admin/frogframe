<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Schedule Generation (ScheduleGeneration)

Aturan yang dijalankan kode saat dokumen Schedule Generation (ScheduleGeneration) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ScheduleGenerationHook**: Schedule Generation: validates date window and materializes daily schedules on save or via the Generate form action.
- **ScheduleGenerationRunAction**: Form action on ScheduleGeneration to re-run daily schedule materialization for its configured scope and period.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `to_date`: "The end date must be on or after the start date" — terjadi bila `!(from is null || to is null) && to < from` (ScheduleGenerationHook, WsmHooks.cs:714)
- `to_date`: "A single schedule generation batch cannot exceed {MaxBatchDays} days (requested {span} days)" — terjadi bila `!(from is null || to is null) && !(to < from) && span > MaxBatchDays` (ScheduleGenerationHook, WsmHooks.cs:717)

## Tombol aksi: Generate daily schedules

Tombol **Generate daily schedules** di form Schedule Generation hanya tampil bila `user.HasRole(ErpRoles.HrTimeAttendance)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Form action on ScheduleGeneration to re-run daily schedule materialization for its configured scope and period.

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
