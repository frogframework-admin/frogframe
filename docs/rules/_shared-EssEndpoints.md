<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama EssEndpoints

Dipakai oleh: beberapa proses (job/event).

ESS (Employee Self-Service) Mobile Application API, version 1: /api/ess/v1. Documentation: docs/guides/ess-mobile-api.md. All endpoints use Bearer tokens from POST /api/auth/login (refreshed via POST /api/auth/refresh) and always operate in the context of the employee bound to the account (employee claim); API keys are rejected.

## Validasi (dokumen ditolak)

- `to`: "Date range cannot exceed 62 days and 'to' cannot be earlier than 'from'" — terjadi bila `end < start || end.DayNumber - start.DayNumber > 62` (EssEndpoints, EssEndpoints.cs:111)
- `photo`: "Upload a photo in the 'photo' form field" — terjadi bila `form.Files.GetFile("photo") ?? form.Files.FirstOrDefault() tidak ditemukan/kosong` (EssEndpoints, EssEndpoints.cs:191)
- `to`: "Date range cannot exceed 62 days and 'to' cannot be earlier than 'from'" — terjadi bila `end < start || end.DayNumber - start.DayNumber > 62` (EssEndpoints, EssEndpoints.cs:209)
- `decision`: "decision must be 'approve' or 'reject'" (EssEndpoints, EssEndpoints.cs:318)
- `comment`: "Rejection reason is required" — terjadi bila `!(!ApprovalDocTypes.Contains(doctype)) && action == "Reject" && string.IsNullOrWhiteSpace(body?.Comment)` (EssEndpoints, EssEndpoints.cs:321)
- `{field}`: "Date format must be yyyy-MM-dd" — terjadi bila `"/api/ess/v1"; private static readonly string[] ApprovalDocTypes = ["LeaveApplication", "OvertimeRequest", "ExpenseClaim", "EmployeeAdvance"]; public static IEndpointRouteBuilder MapEssApi(this IEndpointRouteBuilder app) { var g = app.MapGroup(Prefix).RequireRateLimiting(ServiceCollectionExtensions.ApiRateLimit); // ---------------- Profile & Home ---------------- g.MapGet("/me", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) => { var svc = sp.GetRequiredService<EssAttendanceService>(); var p = await svc.GetProfileAsync(user.TenantId, emp, ct) tidak ditemukan/kosong` (EssEndpoints, EssEndpoints.cs:346)
