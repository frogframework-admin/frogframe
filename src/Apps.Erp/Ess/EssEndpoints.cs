using System.Globalization;
using Apps.Erp.Hooks;
using Framework.Documents;
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
    private static readonly string[] ApprovalDocTypes = ["LeaveApplication", "OvertimeRequest", "ExpenseClaim", "EmployeeAdvance"];

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
            foreach (var dt in ApprovalDocTypes)
                myPending += (await m.QueryAsync(dt, [new("employee", "=", emp)], ct, take: 500))
                    .Count(d => d.DocStatus == DocStatus.Draft && d.WorkflowState is { } s && s.StartsWith("Pending", StringComparison.Ordinal));
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
                pendingApprovals = (await PendingApprovalsAsync(m, emp, ct)).Count,
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

        // ---------------- Approvals (Manager) ----------------
        g.MapGet("/approvals", (Func<HttpContext, Task<IResult>>)(http => Ess(http, async (user, emp, sp, ct) =>
        {
            var m = new Masters(sp.GetRequiredService<IDocumentStore>(), user.TenantId);
            return Results.Ok(new { items = (await PendingApprovalsAsync(m, emp, ct)).Select(ApprovalDto) });
        })));

        g.MapPost("/approvals/{doctype}/{name}/{decision}", (HttpContext http, string doctype, string name, string decision, EssDecisionBody? body) =>
            Ess(http, async (user, emp, sp, ct) =>
            {
                if (!ApprovalDocTypes.Contains(doctype)) throw new DocumentNotFoundException(doctype, name);
                var action = decision.ToLowerInvariant() switch
                {
                    "approve" => "Approve",
                    "reject" => "Reject",
                    _ => throw new ValidationFailedException([new FieldError("decision", "decision must be 'approve' or 'reject'")]),
                };
                if (action == "Reject" && string.IsNullOrWhiteSpace(body?.Comment))
                    throw new ValidationFailedException([new FieldError("comment", "Rejection reason is required")]);
                var docs = sp.GetRequiredService<DocumentService>();
                var doc = await docs.GetAsync(user, doctype, name, ct);
                // Workflow guard conditions (approver == current employee and requester != current employee) are verified by DocumentService.
                doc = await docs.RunActionAsync(user, doctype, name, action, doc.Version, body?.Comment, ct);
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
        source = d.Data.Str("source"), notes = d.Data.Str("ess_flags"),
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

    private static async Task<List<object>> LeaveTypesAsync(Masters m, string emp, DateOnly today, CancellationToken ct)
    {
        var apps = (await m.QueryAsync("LeaveApplication", [new("employee", "=", emp)], ct, take: 1000)).Where(LeaveMath.Active).ToList();
        var allocations = (await m.QueryAsync("LeaveAllocation", [new("employee", "=", emp)], ct, take: 200))
            .Where(a => a.DocStatus == DocStatus.Submitted).ToList();
        var result = new List<object>();
        foreach (var type in await m.QueryAsync("LeaveType", [], ct, take: 200))
        {
            if (type.Data.ContainsKey("is_active") && type.Data["is_active"] is false) continue;
            var quota = type.Data.Bool("is_annual_quota");
            var alloc = quota ? allocations.FirstOrDefault(a => a.Data.Str("leave_type") == type.Name
                                                                && a.Data.Date("from_date") <= today && today <= a.Data.Date("to_date")) : null;
            decimal? allocated = null, used = null, pending = null, balance = null;
            if (alloc is not null)
            {
                var from = alloc.Data.Date("from_date"); var to = alloc.Data.Date("to_date");
                var inWindow = apps.Where(a => a.Data.Str("leave_type") == type.Name && Hr.Overlaps(from, to, a.Data.Date("from_date"), a.Data.Date("to_date"))).ToList();
                allocated = alloc.Data.Dec("total_leaves");
                used = inWindow.Where(a => a.DocStatus == DocStatus.Submitted).Sum(a => a.Data.Dec("total_days"));
                pending = inWindow.Where(a => a.DocStatus == DocStatus.Draft && a.WorkflowState is { } s && s.StartsWith("Pending", StringComparison.Ordinal))
                    .Sum(a => a.Data.Dec("total_days"));
                balance = allocated - used;
            }
            result.Add(new
            {
                leaveType = type.Name, isPaid = type.Data.Bool("is_paid"), isAnnualQuota = quota, requiresAttachment = type.Data.Bool("requires_attachment"),
                maxConsecutiveDays = type.Data.Dec("max_consecutive_days"), allocated, used, pending, balance,
                validFrom = alloc?.Data.Str("from_date"), validTo = alloc?.Data.Str("to_date"),
            });
        }
        return result;
    }

    private static readonly (string DocType, string ApproverField, string State)[] ApprovalSources =
    [
        ("LeaveApplication", "leave_approver", "Pending Approval"),
        ("OvertimeRequest", "approver", "Pending Approval"),
        ("ExpenseClaim", "expense_approver", "Pending Manager"),
        ("EmployeeAdvance", "expense_approver", "Pending Manager"),
    ];

    private static async Task<List<Document>> PendingApprovalsAsync(Masters m, string emp, CancellationToken ct)
    {
        var list = new List<Document>();
        foreach (var (docType, field, state) in ApprovalSources)
            list.AddRange((await m.QueryAsync(docType, [new(field, "=", emp)], ct, take: 500))
                .Where(d => d.DocStatus == DocStatus.Draft && d.WorkflowState == state && d.Data.Str("employee") != emp));
        return list.OrderBy(d => d.Modified).ToList();
    }

    private static object ApprovalDto(Document d)
    {
        var x = d.Data;
        var (title, amount) = d.DocType switch
        {
            "LeaveApplication" => ($"{x.Str("leave_type")} {x.Str("from_date")} to {x.Str("to_date")} ({x.Dec("total_days"):0.#} days)", (decimal?)null),
            "OvertimeRequest" => ($"Overtime {x.Str("overtime_date")} {x.Dec("hours"):0.#} hours", (decimal?)null),
            "ExpenseClaim" => ($"Expense Claim: {x.Str("purpose")}", (decimal?)x.Dec("total_claimed")),
            _ => ($"Employee Advance: {x.Str("purpose")}", (decimal?)x.Dec("advance_amount")),
        };
        return new
        {
            doctype = d.DocType, name = d.Name, employee = x.Str("employee"), employeeName = x.Str("employee_name"), title, amount,
            reason = x.Str("reason") ?? x.Str("purpose"), state = d.WorkflowState, submittedAt = d.Modified, actions = new[] { "approve", "reject" },
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
}

public sealed record EssLeaveRequest(string? LeaveType, string? FromDate, string? ToDate, bool HalfDay, string? Reason, bool? Submit);
public sealed record EssShiftSwapRequest(string TargetEmployee, string SwapDate, string? TargetSwapDate, string? Reason, bool? Submit);
public sealed record EssDecisionBody(string? Comment);
