using System.Globalization;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Files;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Ess;

/// <summary>
/// ESS (Employee Self-Service) Mobile Application API, version 1: <c>/api/ess/v1</c>. Documentation: docs/guides/ess-mobile-api.md.
/// All endpoints use Bearer tokens from <c>POST /api/auth/login</c> (refreshed via <c>POST /api/auth/refresh</c>) and always operate
/// in the context of the employee bound to the account (<c>employee</c> claim); API keys are rejected.
/// </summary>
public static class EssEndpoints
{
    public const string Prefix = "/api/ess/v1";

    public static IEndpointRouteBuilder MapEssApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Prefix).RequireRateLimiting(ServiceCollectionExtensions.ApiRateLimit);

        // ---------------- Profile & Home ----------------
        g.MapGet("/me", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssAttendanceService>();
            var p = await svc.GetProfileAsync(user.TenantId, emp, ct) ?? throw new DocumentNotFoundException("Employee", emp);
            var policy = await svc.GetPolicyAsync(user.TenantId, ct);
            var sites = EssAttendanceRules.CandidateSites(p, await svc.GetSitesAsync(user.TenantId, ct));
            return Results.Ok(new
            {
                employee = p.Employee, fullName = p.FullName, jobTitle = p.JobTitle, position = p.Position, organizationUnit = p.OrganizationUnit,
                company = p.Company, reportsTo = p.ReportsTo, location = p.LocationCode, status = p.Status,
                shift = new { name = p.ShiftType, start = p.ShiftStart, end = p.ShiftEnd },
                attendance = new
                {
                    mode = p.AttendanceMode,
                    deviceBinding = policy.BindDevice,
                    deviceRegistered = !string.IsNullOrWhiteSpace(p.DeviceId),
                    requirePhoto = policy.RequirePhoto,
                    maxAccuracyMeters = policy.MaxAccuracyM,
                    minPunchIntervalMinutes = policy.MinPunchIntervalMinutes,
                    maxOfflineHours = policy.MaxOfflineHours,
                    rejectOutsideGeofence = policy.RejectOutsideGeofence,
                    rejectMockLocation = policy.RejectMockLocation,
                    sites = sites.Select(s => new { code = s.Code, name = s.Name, latitude = s.Latitude, longitude = s.Longitude, radiusMeters = s.RadiusM }),
                },
                timeZone = svc.TimeZone.Id,
                serverTime = Now(sp),
            });
        })));

        g.MapGet("/home", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssAttendanceService>();
            var store = sp.GetRequiredService<IDocumentStore>();
            var m = new Masters(store, user.TenantId);
            var now = Now(sp);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, svc.TimeZone).DateTime);
            var punches = await sp.GetRequiredService<IEssPunchStore>().ListAsync(user.TenantId, emp, today.AddDays(-1), today, ct);
            var todayPunches = punches.Where(p => p.WorkDate == today || (p.WorkDate == today.AddDays(-1) && p.PunchTime > now.AddHours(-16))).ToList();
            var attendance = (await m.QueryAsync("Attendance", [new("employee", "=", emp), new("attendance_date", "=", D.Iso(today))], ct, take: 1)).FirstOrDefault();
            var todaySched = (await m.QueryAsync("EmployeeDailySchedule", [new("employee", "=", emp), new("work_date", "=", D.Iso(today))], ct, take: 1)).FirstOrDefault();
            var payslip = (await m.QueryAsync("Payslip", [new("employee", "=", emp)], ct, take: 200))
                .Where(p => p.DocStatus == DocStatus.Submitted).OrderByDescending(p => p.Data.Str("period")).FirstOrDefault();
            var myPending = 0;
            foreach (var dt in EssRequestService.ApprovalDocTypes)
                myPending += (await m.QueryAsync(dt, [new(dt == "ShiftSwapRequest" ? "requester" : "employee", "=", emp)], ct, take: 500))
                    .Count(d => d.WorkflowState is { } s && s.StartsWith("Pending", StringComparison.Ordinal));
            return Results.Ok(new
            {
                serverTime = now,
                today = new
                {
                    workDate = D.Iso(today),
                    lastPunchType = todayPunches.LastOrDefault(p => p.Counts)?.PunchType,
                    punches = todayPunches.Select(p => PunchDto(p, svc.TimeZone)),
                    attendance = attendance is null ? null : AttendanceDto(attendance),
                    schedule = todaySched is null ? null : ScheduleDto(todaySched),
                },
                leave = await LeaveTypesAsync(m, emp, today, ct),
                latestPayslip = payslip is null ? null : PayslipSummary(payslip),
                pendingApprovals = (await sp.GetRequiredService<EssRequestService>().ApprovalInboxAsync(user, includeOverrides: false, ct)).Count,
                myPendingRequests = myPending,
            });
        })));

        // ---------------- Roster & Shift Swaps ----------------
        g.MapGet("/schedules", (HttpContext http, string? from, string? to, string? month) => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssAttendanceService>();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now(sp), svc.TimeZone).DateTime);
            DateOnly start, end;
            if (!string.IsNullOrWhiteSpace(month) && DateOnly.TryParseExact(month.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mStart))
            {
                start = mStart;
                end = mStart.AddMonths(1).AddDays(-1);
            }
            else
            {
                start = ParseDate(from, "from") ?? new DateOnly(today.Year, today.Month, 1);
                end = ParseDate(to, "to") ?? start.AddMonths(1).AddDays(-1);
            }
            if (end < start || end.DayNumber - start.DayNumber > 62)
                throw new ValidationFailedException([new FieldError("to", "Date range cannot exceed 62 days and 'to' cannot be earlier than 'from'")]);

            var m = new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId);
            var rows = await m.QueryAsync("EmployeeDailySchedule",
                [new("employee", "=", emp), new("work_date", ">=", D.Iso(start)), new("work_date", "<=", D.Iso(end))],
                ct, "work_date", "asc", 100);
            return Results.Ok(new { from = D.Iso(start), to = D.Iso(end), schedules = rows.Select(ScheduleDto) });
        }));

        g.MapGet("/shift-swaps", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var m = new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId);
            var outgoing = await m.QueryAsync("ShiftSwapRequest", [new("requester", "=", emp)], ct, "swap_date", "desc", 50);
            var incoming = await m.QueryAsync("ShiftSwapRequest", [new("target_employee", "=", emp)], ct, "swap_date", "desc", 50);
            return Results.Ok(new
            {
                outgoing = outgoing.Select(SwapDto),
                incoming = incoming.Select(SwapDto),
            });
        })));

        g.MapPost("/shift-swaps", (HttpContext http, EssShiftSwapRequest body) => Ess(http, async (user, emp, sp, ct) =>
        {
            var docs = sp.GetRequiredService<DocumentService>();
            var doc = await docs.InsertAsync(user, "ShiftSwapRequest", new Dictionary<string, object?>
            {
                ["requester"] = emp,
                ["target_employee"] = body.TargetEmployee,
                ["swap_date"] = body.SwapDate,
                ["target_swap_date"] = string.IsNullOrWhiteSpace(body.TargetSwapDate) ? body.SwapDate : body.TargetSwapDate,
                ["reason"] = body.Reason,
            }, "Created via ESS Mobile", ct);
            if (body.Submit == true)
                doc = await docs.RunActionAsync(user, "ShiftSwapRequest", doc.Name, "Request Swap", doc.Version, null, ct);
            return Results.Created($"{Prefix}/shift-swaps/{doc.Name}", SwapDto(doc));
        }));

        g.MapGet("/shift-preferences", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var m = new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId);
            var rows = await m.QueryAsync("ShiftPreference", [new("employee", "=", emp)], ct, "from_date", "desc", 100);
            return Results.Ok(new { items = rows.Select(PreferenceDto) });
        })));

        g.MapPost("/shift-preferences", (HttpContext http, EssShiftPreferenceRequest body) => Ess(http, async (user, emp, sp, ct) =>
        {
            var docs = sp.GetRequiredService<DocumentService>();
            var doc = await docs.InsertAsync(user, "ShiftPreference", new Dictionary<string, object?>
            {
                ["employee"] = emp,
                ["preference_type"] = string.IsNullOrWhiteSpace(body.PreferenceType) ? "Prefer" : body.PreferenceType,
                ["shift_type"] = body.ShiftType,
                ["from_date"] = body.FromDate,
                ["to_date"] = body.ToDate,
                ["days_of_week"] = body.DaysOfWeek,
                ["weight"] = body.Weight ?? 3,
                ["reason"] = body.Reason,
                ["status"] = "Active",
            }, "Created via ESS Mobile", ct);
            return Results.Created($"{Prefix}/shift-preferences/{doc.Name}", PreferenceDto(doc));
        }));

        // ---------------- Attendance ----------------
        g.MapPost("/attendance/punches", (HttpContext http, EssPunchRequest body) => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssAttendanceService>();
            var result = await svc.PunchAsync(user, emp, body, ct);
            var dto = PunchResultDto(result, svc.TimeZone);
            return result.Punch.Status == EssPunchStatus.Rejected
                ? Results.Json(dto, statusCode: StatusCodes.Status422UnprocessableEntity)
                : result.Duplicate ? Results.Ok(dto) : Results.Json(dto, statusCode: StatusCodes.Status201Created);
        }));

        g.MapPost("/attendance/punches/{id:guid}/photo", (HttpContext http, Guid id) => Ess(http, async (user, emp, sp, ct) =>
        {
            Stream content;
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("photo") ?? form.Files.FirstOrDefault()
                           ?? throw new ValidationFailedException([new FieldError("photo", "Upload a photo in the 'photo' form field")]);
                content = file.OpenReadStream();
            }
            else content = http.Request.Body;
            await using (content)
            {
                var fileId = await sp.GetRequiredService<EssAttendanceService>().AttachPhotoAsync(user, emp, id, content, ct);
                return Results.Ok(new { id, photoFileId = fileId });
            }
        })).DisableAntiforgery();

        g.MapGet("/attendance", (HttpContext http, string? from, string? to) => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssAttendanceService>();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now(sp), svc.TimeZone).DateTime);
            var start = ParseDate(from, "from") ?? new DateOnly(today.Year, today.Month, 1);
            var end = ParseDate(to, "to") ?? today;
            if (end < start || end.DayNumber - start.DayNumber > 62)
                throw new ValidationFailedException([new FieldError("to", "Date range cannot exceed 62 days and 'to' cannot be earlier than 'from'")]);
            var punches = await sp.GetRequiredService<IEssPunchStore>().ListAsync(user.TenantId, emp, start, end, ct);
            var docs = (await new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId).QueryAsync("Attendance",
                    [new("employee", "=", emp), new("attendance_date", ">=", D.Iso(start)), new("attendance_date", "<=", D.Iso(end))], ct, take: 100))
                .ToDictionary(d => d.Data.Str("attendance_date") ?? "", d => d);
            var days = new List<object>();
            for (var day = end; day >= start; day = day.AddDays(-1))
            {
                var key = D.Iso(day);
                var dayPunches = punches.Where(p => p.WorkDate == day).ToList();
                if (dayPunches.Count == 0 && !docs.ContainsKey(key)) continue;
                days.Add(new
                {
                    date = key,
                    attendance = docs.TryGetValue(key, out var doc) ? AttendanceDto(doc) : null,
                    punches = dayPunches.Select(p => PunchDto(p, svc.TimeZone)),
                });
            }
            return Results.Ok(new { from = D.Iso(start), to = D.Iso(end), days });
        }));

        // ---------------- Leave ----------------
        g.MapGet("/leave/types", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssAttendanceService>();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now(sp), svc.TimeZone).DateTime);
            return Results.Ok(new { items = await LeaveTypesAsync(new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId), emp, today, ct) });
        })));

        g.MapGet("/leave/applications", (HttpContext http, int? year) => Ess(http, async (user, emp, sp, ct) =>
        {
            var m = new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId);
            var y = year ?? Now(sp).Year;
            var items = (await m.QueryAsync("LeaveApplication", [new("employee", "=", emp)], ct, take: 1000))
                .Where(d => d.Data.Date("from_date")?.Year == y || d.Data.Date("to_date")?.Year == y)
                .OrderByDescending(d => d.Data.Str("from_date"))
                .Select(LeaveDto);
            return Results.Ok(new { year = y, items });
        }));

        g.MapPost("/leave/applications", (HttpContext http, EssLeaveRequest body) => Ess(http, async (user, emp, sp, ct) =>
        {
            var docs = sp.GetRequiredService<DocumentService>();
            var payload = new Dictionary<string, object?>
            {
                ["employee"] = emp, ["leave_type"] = body.LeaveType, ["from_date"] = body.FromDate, ["to_date"] = body.ToDate,
                ["half_day"] = body.HalfDay, ["reason"] = body.Reason,
            };
            if (!string.IsNullOrWhiteSpace(body.AttachmentFileId)) payload["attachment"] = body.AttachmentFileId;
            var doc = await docs.InsertAsync(user, "LeaveApplication", payload, ct);
            if (body.Submit != false)
                doc = await docs.RunActionAsync(user, "LeaveApplication", doc.Name, "Submit for approval", doc.Version, null, ct);
            return Results.Json(LeaveDto(doc), statusCode: StatusCodes.Status201Created);
        }));

        g.MapPost("/leave/applications/{name}/withdraw", (HttpContext http, string name) => Ess(http, async (user, emp, sp, ct) =>
        {
            var docs = sp.GetRequiredService<DocumentService>();
            var doc = await OwnAsync(docs, user, "LeaveApplication", name, emp, ct);
            doc = await docs.RunActionAsync(user, "LeaveApplication", name, "Withdraw", doc.Version, null, ct);
            return Results.Ok(LeaveDto(doc));
        }));

        // ---------------- Payslips ----------------
        g.MapGet("/payslips", (HttpContext http, int? year) => Ess(http, async (user, emp, sp, ct) =>
        {
            var m = new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId);
            var y = (year ?? Now(sp).Year).ToString(CultureInfo.InvariantCulture);
            var items = (await m.QueryAsync("Payslip", [new("employee", "=", emp)], ct, take: 500))
                .Where(p => p.DocStatus == DocStatus.Submitted && (p.Data.Str("period") ?? "").StartsWith(y, StringComparison.Ordinal))
                .OrderByDescending(p => p.Data.Str("period"))
                .Select(PayslipSummary);
            return Results.Ok(new { year = y, items });
        }));

        g.MapGet("/payslips/{name}", (HttpContext http, string name) => Ess(http, async (user, emp, sp, ct) =>
        {
            var doc = await new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId).GetAsync("Payslip", name, ct);
            if (doc is null || doc.Data.Str("employee") != emp || doc.DocStatus != DocStatus.Submitted)
                throw new DocumentNotFoundException("Payslip", name);
            var d = doc.Data;
            static object Lines(List<Dictionary<string, object?>> rows) =>
                rows.Select(r => new { component = r.Str("salary_component"), amount = r.Dec("amount") });
            return Results.Ok(new
            {
                summary = PayslipSummary(doc),
                earnings = Lines(d.Rows("earnings")),
                deductions = Lines(d.Rows("deductions")),
                workingDays = d.Dec("working_days"), payableDays = d.Dec("payable_days"), unpaidLeaveDays = d.Dec("unpaid_leave_days"),
                employeeContributions = d.Dec("employee_contributions"), pph21 = d.Dec("pph21_withheld"), otherDeductions = d.Dec("other_deductions"),
                bpjs = new { jht = d.Dec("jht_employee"), jp = d.Dec("jp_employee"), kesehatan = d.Dec("kes_employee") },
                ptkpStatus = d.Str("ptkp_status"), terCategory = d.Str("ter_category"),
            });
        }));

        // ---------------- Shift swap: peer answer & withdraw ----------------
        g.MapPost("/shift-swaps/{name}/{answer}", (HttpContext http, string name, string answer, EssDecisionBody? body) => Ess(http, async (user, emp, sp, ct) =>
        {
            var docs = sp.GetRequiredService<DocumentService>();
            var doc = await docs.GetAsync(user, "ShiftSwapRequest", name, ct);
            var (action, field) = answer.ToLowerInvariant() switch
            {
                "accept" => ("Accept swap", "target_employee"),
                "decline" => ("Decline swap", "target_employee"),
                "withdraw" => ("Withdraw", "requester"),
                _ => throw new ValidationFailedException([new FieldError("answer", "Use accept, decline or withdraw")]),
            };
            if (doc.Data.Str(field) != emp) throw new DocumentNotFoundException("ShiftSwapRequest", name);
            if (action == "Decline swap" && string.IsNullOrWhiteSpace(body?.Comment))
                throw new ValidationFailedException([new FieldError("comment", "A reason is required to decline")]);
            doc = await docs.RunActionAsync(user, "ShiftSwapRequest", name, action, doc.Version, body?.Comment, ct);
            return Results.Ok(SwapDto(doc));
        }));

        // ---------------- Face registration (face recognition on the phone) ----------------
        g.MapGet("/face", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var svc = sp.GetRequiredService<EssRequestService>();
            var (all, active) = await svc.FaceStatusAsync(user, emp, ct);
            var policy = await sp.GetRequiredService<EssAttendanceService>().GetPolicyAsync(user.TenantId, ct);
            return Results.Ok(new
            {
                required = policy.RequireFace, minScore = policy.MinFaceScore, rejectMismatch = policy.RejectFaceMismatch,
                status = active is not null ? "active" : all.FirstOrDefault()?.WorkflowState switch
                {
                    "Pending Verification" => "pending",
                    "Rejected" => "rejected",
                    _ => "none",
                },
                active = active is null ? null : FaceDto(active),
                registrations = all.Select(FaceDto),
            });
        })));

        g.MapGet("/face/template", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var result = await sp.GetRequiredService<EssRequestService>().ActiveTemplateAsync(user, emp, ct);
            if (result is not { } r)
                return DocApi.ProblemResult(404, "No verified face registration", "Register your face; HR verifies it before it can be used.");
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                registration = r.Registration.Name, modelName = r.Registration.Data.Str("model_name"), modelVersion = r.Registration.Data.Str("model_version"),
                sha256 = r.Registration.Data.Str("template_sha256"), template = r.Template,
            });
        })));

        g.MapPost("/face/registrations", (HttpContext http) => Ess(http, async (user, emp, sp, ct) =>
        {
            if (!http.Request.HasFormContentType)
                throw new ValidationFailedException([new FieldError("photos", "Send multipart/form-data with photo files and a 'template' field")]);
            var form = await http.Request.ReadFormAsync(ct);
            var photos = new List<(byte[], string)>();
            foreach (var file in form.Files.Where(f => f.Name.StartsWith("photo", StringComparison.OrdinalIgnoreCase)).Take(EssRequestService.MaxFacePhotos + 1))
            {
                if (file.Length > FileService.MaxImageBytes)
                    throw new ValidationFailedException([new FieldError(file.Name, "Each photo can be at most 2 MB")]);
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                photos.Add((ms.ToArray(), file.FileName));
            }
            double[]? template;
            try { template = System.Text.Json.JsonSerializer.Deserialize<double[]>(form["template"].ToString()); }
            catch (System.Text.Json.JsonException) { template = null; }
            decimal? Num(string key) => decimal.TryParse(form[key].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            var doc = await sp.GetRequiredService<EssRequestService>().RegisterFaceAsync(user, emp, new EssFaceRegistrationInput
            {
                Photos = photos, Template = template ?? [],
                ModelName = form["modelName"].ToString(), ModelVersion = form["modelVersion"].ToString(),
                Consent = bool.TryParse(form["consent"].ToString(), out var consent) && consent,
                QualityScore = Num("qualityScore"), LivenessScore = Num("livenessScore"),
                DeviceId = form["deviceId"].ToString(), AppVersion = form["appVersion"].ToString(),
            }, ct);
            return Results.Json(FaceDto(doc), statusCode: StatusCodes.Status201Created);
        })).DisableAntiforgery();

        g.MapPost("/face/registrations/{name}/withdraw", (HttpContext http, string name) => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(FaceDto(await sp.GetRequiredService<EssRequestService>().WithdrawAsync(user, "FaceRegistration", "employee", emp, name, ct)))));

        // ---------------- Overtime ----------------
        g.MapGet("/overtime", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(new { items = (await sp.GetRequiredService<EssRequestService>().MineAsync(user, "OvertimeRequest", "employee", emp, 100, ct)).Select(OvertimeDto) }))));

        g.MapPost("/overtime", (HttpContext http, EssOvertimeRequest body) => Ess(http, async (user, emp, sp, ct) =>
            Results.Json(OvertimeDto(await sp.GetRequiredService<EssRequestService>().CreateOvertimeAsync(user, emp, body, ct)), statusCode: StatusCodes.Status201Created)));

        g.MapPost("/overtime/{name}/withdraw", (HttpContext http, string name) => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(OvertimeDto(await sp.GetRequiredService<EssRequestService>().WithdrawAsync(user, "OvertimeRequest", "employee", emp, name, ct)))));

        // ---------------- Attendance correction ----------------
        g.MapGet("/attendance/corrections", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(new { items = (await sp.GetRequiredService<EssRequestService>().MineAsync(user, "AttendanceCorrection", "employee", emp, 100, ct)).Select(CorrectionDto) }))));

        g.MapPost("/attendance/corrections", (HttpContext http, EssCorrectionRequest body) => Ess(http, async (user, emp, sp, ct) =>
            Results.Json(CorrectionDto(await sp.GetRequiredService<EssRequestService>().CreateCorrectionAsync(user, emp, body, ct)), statusCode: StatusCodes.Status201Created)));

        g.MapPost("/attendance/corrections/{name}/withdraw", (HttpContext http, string name) => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(CorrectionDto(await sp.GetRequiredService<EssRequestService>().WithdrawAsync(user, "AttendanceCorrection", "employee", emp, name, ct)))));

        // ---------------- Biodata change ----------------
        g.MapGet("/profile/biodata", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(new { categories = await sp.GetRequiredService<EssRequestService>().BiodataAsync(user, emp, ct) }))));

        g.MapGet("/profile/change-requests", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(new { items = (await sp.GetRequiredService<EssRequestService>().MineAsync(user, "BiodataChangeRequest", "employee", emp, 50, ct)).Select(BiodataDto) }))));

        g.MapPost("/profile/change-requests", (HttpContext http, EssBiodataRequest body) => Ess(http, async (user, emp, sp, ct) =>
            Results.Json(BiodataDto(await sp.GetRequiredService<EssRequestService>().CreateBiodataChangeAsync(user, emp, body, ct)), statusCode: StatusCodes.Status201Created)));

        g.MapPost("/profile/change-requests/{name}/withdraw", (HttpContext http, string name) => Ess(http, async (user, emp, sp, ct) =>
            Results.Ok(BiodataDto(await sp.GetRequiredService<EssRequestService>().WithdrawAsync(user, "BiodataChangeRequest", "employee", emp, name, ct)))));

        // ---------------- Attachments for requests ----------------
        g.MapPost("/files/{doctype}/{field}", (HttpContext http, string doctype, string field) => Ess(http, async (user, emp, sp, ct) =>
        {
            if (!http.Request.HasFormContentType)
                throw new ValidationFailedException([new FieldError("file", "Send multipart/form-data with a 'file' field")]);
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault()
                       ?? throw new ValidationFailedException([new FieldError("file", "Upload a file in the 'file' form field")]);
            await using var stream = file.OpenReadStream();
            var id = await sp.GetRequiredService<EssRequestService>().UploadAsync(user, doctype, field, file.FileName, stream, ct);
            return Results.Json(new { fileId = id, fileName = file.FileName, size = file.Length }, statusCode: StatusCodes.Status201Created);
        })).DisableAntiforgery();

        // ---------------- Approvals (Manager) ----------------
        g.MapGet("/approvals", (HttpContext http, string? stage, bool? includeOverrides) => Ess(http, async (user, emp, sp, ct) =>
        {
            var inbox = await sp.GetRequiredService<EssRequestService>().ApprovalInboxAsync(user, includeOverrides == true, ct);
            var items = inbox.Where(i => string.IsNullOrWhiteSpace(stage) || string.Equals(i.Stage, stage, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Ok(new
            {
                counts = inbox.GroupBy(i => i.Stage).ToDictionary(g => g.Key, g => g.Count()),
                items = items.Select(ApprovalDto),
            });
        }));

        g.MapGet("/approvals/{doctype}/{name}", (HttpContext http, string doctype, string name) => Ess(http, async (user, emp, sp, ct) =>
        {
            var item = (await sp.GetRequiredService<EssRequestService>().ApprovalInboxAsync(user, includeOverrides: true, ct))
                .FirstOrDefault(i => i.Meta.Name == doctype && i.Doc.Name == name) ?? throw new DocumentNotFoundException(doctype, name);
            return Results.Ok(new { summary = ApprovalDto(item), details = ApprovalDetails(item.Doc) });
        }));

        g.MapPost("/approvals/{doctype}/{name}/{decision}", (HttpContext http, string doctype, string name, string decision, EssDecisionBody? body) =>
            Ess(http, async (user, emp, sp, ct) =>
            {
                // The workflow decides who may act (supervisor, project approver, HR); the action matching the decision is
                // chosen from the actions available to this user in the document's current state.
                var doc = await sp.GetRequiredService<EssRequestService>().DecideAsync(user, doctype, name, decision, body?.Action, body?.Comment, ct);
                return Results.Ok(new { doctype, name, state = doc.WorkflowState, docstatus = (int)doc.DocStatus });
            }));

        return app;
    }

    // ------------------------------------------------------------------ helpers

    private static Task<IResult> Ess(HttpContext http, Func<UserContext, string, IServiceProvider, CancellationToken, Task<IResult>> action) =>
        DocApi.RunAuthenticated(http, async (user, sp) =>
            user.Attr("employee") is { Length: > 0 } emp
                ? await action(user, emp, sp, http.RequestAborted)
                : DocApi.ProblemResult(403, "Account not linked to an employee",
                    "This account has no linked employee record ('employee' claim). Ask HR to link your account to an employee profile."));

    private static DateTimeOffset Now(IServiceProvider sp) => (sp.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    private static DateOnly? ParseDate(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : throw new ValidationFailedException([new FieldError(field, "Date format must be yyyy-MM-dd")]);

    private static async Task<Document> OwnAsync(DocumentService docs, UserContext user, string docType, string name, string emp, CancellationToken ct)
    {
        var doc = await docs.GetAsync(user, docType, name, ct);
        return doc.Data.Str("employee") == emp ? doc : throw new DocumentNotFoundException(docType, name);
    }

    private static object PunchDto(EssPunch p, TimeZoneInfo zone) => new
    {
        id = p.Id, type = p.PunchType, status = p.Status,
        reasons = p.Reasons.Select(r => new { code = r, message = EssReason.Message(r) }),
        punchTime = p.PunchTime, localTime = TimeZoneInfo.ConvertTime(p.PunchTime, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        workDate = D.Iso(p.WorkDate), location = p.LocationCode, distanceMeters = p.DistanceM,
        isOffline = p.IsOffline, hasPhoto = p.PhotoFileId is not null, attendance = p.AttendanceName, processError = p.ProcessError,
    };

    private static object PunchResultDto(EssPunchResult r, TimeZoneInfo zone) => new
    {
        id = r.Punch.Id, type = r.Punch.PunchType, status = r.Punch.Status, duplicate = r.Duplicate,
        reasons = r.Punch.Reasons.Select(code => new { code, message = EssReason.Message(code) }),
        punchTime = r.Punch.PunchTime, serverTime = r.Punch.ServerTime,
        localTime = TimeZoneInfo.ConvertTime(r.Punch.PunchTime, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        workDate = D.Iso(r.Punch.WorkDate),
        location = r.Site is null ? null : new { code = r.Site.Code, name = r.Site.Name, distanceMeters = r.Punch.DistanceM, radiusMeters = r.Site.RadiusM },
    };

    private static object AttendanceDto(Document d) => new
    {
        name = d.Name, date = d.Data.Str("attendance_date"), status = d.Data.Str("status"),
        checkIn = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("check_in")), checkOut = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("check_out")),
        workingHours = d.Data.Dec("working_hours"), lateMinutes = d.Data.Dec("late_minutes"), leaveApplication = d.Data.Str("leave_application"),
        code = d.Data.Str("attendance_code"), codeFinal = d.Data.Bool("code_final"), earlyOutMinutes = d.Data.Dec("early_out_minutes"),
        source = d.Data.Str("source"), notes = d.Data.Str("ess_flags"),
    };

    private static object FaceDto(Document d) => new
    {
        name = d.Name, state = d.WorkflowState ?? "Draft", active = d.Data.Bool("is_active"),
        photos = new[] { d.Data.Str("photo_1"), d.Data.Str("photo_2"), d.Data.Str("photo_3") }.Where(p => p is not null).Count(),
        modelName = d.Data.Str("model_name"), modelVersion = d.Data.Str("model_version"), sha256 = d.Data.Str("template_sha256"),
        dimensions = d.Data.Dec("template_dimensions"), verifiedAt = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("verified_at")),
        created = d.Created,
    };

    private static object OvertimeDto(Document d) => new
    {
        name = d.Name, date = d.Data.Str("overtime_date"), hours = d.Data.Dec("hours"), dayType = d.Data.Str("day_type"),
        weightedHours = d.Data.Dec("weighted_hours"), project = d.Data.Str("project"), reason = d.Data.Str("reason"),
        approver = d.Data.Str("approver"), projectApprover = d.Data.Str("project_approver"), period = d.Data.Str("period"),
        state = d.WorkflowState ?? "Draft", docstatus = (int)d.DocStatus,
    };

    private static object CorrectionDto(Document d) => new
    {
        name = d.Name, date = d.Data.Str("attendance_date"), correctionType = d.Data.Str("correction_type"),
        recordedCheckIn = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("current_check_in")),
        recordedCheckOut = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("current_check_out")), recordedCode = d.Data.Str("current_code"),
        checkIn = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("requested_check_in")),
        checkOut = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("requested_check_out")),
        reason = d.Data.Str("reason"), attachment = d.Data.Str("attachment"), approver = d.Data.Str("approver"),
        state = d.WorkflowState ?? "Draft", docstatus = (int)d.DocStatus,
    };

    private static object BiodataDto(Document d) => new
    {
        name = d.Name, category = d.Data.Str("category"), reason = d.Data.Str("reason"), attachment = d.Data.Str("attachment"),
        changes = d.Data.Rows("changes").Select(r => new { field = r.Str("field"), label = r.Str("label"), currentValue = r.Str("current_value"), newValue = r.Str("new_value") }),
        state = d.WorkflowState ?? "Draft", docstatus = (int)d.DocStatus, created = d.Created,
    };

    private static object LeaveDto(Document d) => new
    {
        name = d.Name, leaveType = d.Data.Str("leave_type"), fromDate = d.Data.Str("from_date"), toDate = d.Data.Str("to_date"),
        halfDay = d.Data.Bool("half_day"), totalDays = d.Data.Dec("total_days"), reason = d.Data.Str("reason"),
        approver = d.Data.Str("leave_approver"), state = d.WorkflowState ?? "Draft", docstatus = (int)d.DocStatus,
    };

    private static object PayslipSummary(Document p) => new
    {
        name = p.Name, period = p.Data.Str("period"), grossEarnings = p.Data.Dec("gross_earnings"), netPay = p.Data.Dec("net_pay"),
        pph21 = p.Data.Dec("pph21_withheld"), pdfUrl = $"/api/print/Payslip/{Uri.EscapeDataString(p.Name)}/pdf",
    };

    private static async Task<List<object>> LeaveTypesAsync(Masters m, string emp, DateOnly today, CancellationToken ct) =>
        (await EssLeave.BalancesAsync(m, emp, today, ct)).Select(b => (object)new
        {
            leaveType = b.LeaveType, isPaid = b.IsPaid, isAnnualQuota = b.IsAnnualQuota, requiresAttachment = b.RequiresAttachment,
            maxConsecutiveDays = b.MaxConsecutiveDays, allocated = b.Allocated, used = b.Used, pending = b.Pending, balance = b.Balance,
            validFrom = b.ValidFrom, validTo = b.ValidTo,
        }).ToList();

    private static object ApprovalDto(EssApproval item)
    {
        var d = item.Doc;
        var x = d.Data;
        var (title, amount) = d.DocType switch
        {
            "LeaveApplication" => ($"{x.Str("leave_type")} {x.Str("from_date")} to {x.Str("to_date")} ({x.Dec("total_days"):0.#} days)", (decimal?)null),
            "OvertimeRequest" => ($"Overtime {x.Str("overtime_date")} {x.Dec("hours"):0.#} hours", (decimal?)null),
            "ShiftSwapRequest" => ($"Shift swap {x.Str("swap_date")} with {x.Str("target_employee_name") ?? x.Str("target_employee")}", (decimal?)null),
            "AttendanceCorrection" => ($"Attendance correction {x.Str("attendance_date")}: {x.Str("correction_type")}", (decimal?)null),
            "FaceRegistration" => ("Face registration", (decimal?)null),
            "BiodataChangeRequest" => ($"Biodata change: {x.Str("category")}", (decimal?)null),
            "ExpenseClaim" => ($"Expense Claim: {x.Str("purpose")}", (decimal?)x.Dec("total_claimed")),
            _ => ($"Employee Advance: {x.Str("purpose")}", (decimal?)x.Dec("advance_amount")),
        };
        return new
        {
            doctype = d.DocType, doctypeTitle = item.Meta.Title, name = d.Name,
            employee = x.Str("employee") ?? x.Str("requester"), employeeName = x.Str("employee_name") ?? x.Str("requester_name"),
            title, amount, reason = x.Str("reason") ?? x.Str("purpose"), state = d.WorkflowState, stage = item.Stage, isOverride = item.IsOverride,
            submittedAt = d.Modified,
            actions = item.Actions.Select(a => new { action = a.Action, kind = a.Kind, toState = a.ToState, requireComment = a.RequireComment }),
        };
    }

    /// <summary>What the approver needs to decide: requested vs recorded values, photos and attachments (as <c>/files/{id}</c> URLs).</summary>
    private static object ApprovalDetails(Document d)
    {
        var x = d.Data;
        string? Url(string field) => x.Str(field) is { } id ? $"/files/{Uri.EscapeDataString(id)}" : null;
        return d.DocType switch
        {
            "FaceRegistration" => new
            {
                photos = new[] { Url("photo_1"), Url("photo_2"), Url("photo_3") }.Where(u => u is not null),
                modelName = x.Str("model_name"), modelVersion = x.Str("model_version"), dimensions = x.Dec("template_dimensions"),
                qualityScore = x.Dec("quality_score"), livenessScore = x.Dec("liveness_score"),
                consentAt = DatetimeFieldType.ToZone(x.GetValueOrDefault("consent_at")), deviceId = x.Str("device_id"),
            },
            "AttendanceCorrection" => CorrectionDto(d),
            "BiodataChangeRequest" => BiodataDto(d) is var b ? new { request = b, attachment = Url("attachment") } : null,
            "OvertimeRequest" => OvertimeDto(d),
            "LeaveApplication" => LeaveDto(d) is var l ? new { request = l, attachment = Url("attachment") } : null,
            "ShiftSwapRequest" => SwapDto(d),
            _ => (object)x.Where(kv => kv.Value is string or decimal or bool or long or int).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    private static object ScheduleDto(Document d) => new
    {
        name = d.Name,
        workDate = d.Data.Str("work_date"),
        scheduleType = d.Data.Str("schedule_type"),
        dayCategory = d.Data.Str("day_category"),
        shiftType = d.Data.Str("shift_type"),
        scheduledStart = d.Data.Str("scheduled_start_datetime"),
        scheduledEnd = d.Data.Str("scheduled_end_datetime"),
        isPublicHoliday = d.Data.Bool("is_public_holiday"),
        isRestDay = d.Data.Bool("is_rest_day"),
        isLocked = d.Data.Bool("is_locked"),
        sourceType = d.Data.Str("source_type"),
    };

    private static object SwapDto(Document d) => new
    {
        name = d.Name,
        requester = d.Data.Str("requester"),
        requesterName = d.Data.Str("requester_name"),
        targetEmployee = d.Data.Str("target_employee"),
        targetEmployeeName = d.Data.Str("target_employee_name"),
        swapDate = d.Data.Str("swap_date"),
        targetSwapDate = d.Data.Str("target_swap_date"),
        requesterShift = d.Data.Str("requester_shift_type"),
        targetShift = d.Data.Str("target_shift_type"),
        reason = d.Data.Str("reason"),
        state = d.WorkflowState,
        modified = d.Modified,
    };

    private static object PreferenceDto(Document d) => new
    {
        name = d.Name,
        employee = d.Data.Str("employee"),
        preferenceType = d.Data.Str("preference_type"),
        shiftType = d.Data.Str("shift_type"),
        fromDate = d.Data.Str("from_date"),
        toDate = d.Data.Str("to_date"),
        daysOfWeek = d.Data.Str("days_of_week"),
        weight = (int)d.Data.Dec("weight"),
        reason = d.Data.Str("reason"),
        status = d.Data.Str("status"),
    };
}

public sealed record EssLeaveRequest(string? LeaveType, string? FromDate, string? ToDate, bool HalfDay, string? Reason, bool? Submit, string? AttachmentFileId = null);
public sealed record EssShiftSwapRequest(string TargetEmployee, string SwapDate, string? TargetSwapDate, string? Reason, bool? Submit);
public sealed record EssShiftPreferenceRequest(string? PreferenceType, string? ShiftType, string? FromDate, string? ToDate, string? DaysOfWeek, int? Weight, string? Reason);
/// <param name="Action">Optional: the exact workflow action when the state offers several (e.g. "Approve (HR)").</param>
public sealed record EssDecisionBody(string? Comment, string? Action = null);
