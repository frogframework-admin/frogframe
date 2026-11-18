<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama EssAttendanceService

Dipakai oleh: beberapa proses (job/event).

Mobile attendance service: evaluates punches against cached profile, location, and policy data, then executes a single INSERT into IEssPunchStore. Avoids heavy document creation during peak rush hours; EssAttendanceAggregationJob subsequently materializes daily Attendance documents through DocumentService.

## Validasi (dokumen ditolak)

- `id`: "id (UUID from mobile app) is required" — terjadi bila `req.Id == Guid.Empty` (EssAttendanceService, EssAttendanceService.cs:142)
- `type`: "type must be 'in' or 'out'" — terjadi bila `!(req.Id == Guid.Empty) && type is not ("in" or "out")` (EssAttendanceService, EssAttendanceService.cs:143)
- `id`: "This punch ID is already in use" — terjadi bila `!(req.Id == Guid.Empty) && !(type is not ("in" or "out")) && await punches.GetAsync(user.TenantId, req.Id, ct) is { } existing` (EssAttendanceService, EssAttendanceService.cs:149)
- `id`: "This punch ID is already in use" — terjadi bila `!(req.Id == Guid.Empty) && !(type is not ("in" or "out")) && !(await punches.GetAsync(user.TenantId, req.Id, ct) is { } existing) && saved.TenantId != user.TenantId || saved.Employee != employee` (EssAttendanceService, EssAttendanceService.cs:169)
- `photo`: "Photo size cannot exceed 1 MB" — terjadi bila `ms.Length > MaxPhotoBytes` (EssAttendanceService, EssAttendanceService.cs:212)
- `photo`: "Photo format must be JPEG, PNG, or WebP" — terjadi bila `!(punch is null || punch.Employee != employee) && !(punch.PhotoFileId is { } already) && !(ms.Length > MaxPhotoBytes)` (EssAttendanceService, EssAttendanceService.cs:216)
