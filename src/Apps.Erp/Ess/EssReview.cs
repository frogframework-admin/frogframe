using System.Globalization;
using System.Text.Json;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Ess;

/// <summary>A finding shown to the approver. <c>Tone</c>: positive, info, warning or negative.</summary>
public sealed record ReviewCheck(string Tone, string Text);

/// <summary>Where the request stands and who decides next (an employee or a role).</summary>
public sealed record ReviewStatus(string State, string? WaitingFor, bool Decided);

public sealed record FaceReview(
    ReviewStatus Status, IReadOnlyList<string> Photos, decimal? Quality, decimal? Liveness, bool Consent, DateTimeOffset? ConsentAt,
    string? Model, string? ModelVersion, int? Dimensions, string? DeviceId, string? AppVersion, string? EmployeePhoto,
    FacePrevious? Previous, bool RequireFace, decimal MinMatchScore, string? VerifiedBy, DateTimeOffset? VerifiedAt,
    IReadOnlyList<ReviewCheck> Checks);

public sealed record FacePrevious(string Name, string State, IReadOnlyList<string> Photos, string? Model, DateTimeOffset? VerifiedAt);

/// <summary>Times of one work day (local time) with the attendance code they give.</summary>
public sealed record DayTimes(DateTime? CheckIn, DateTime? CheckOut, string? Code, int LateMinutes, int EarlyOutMinutes, string? Reason, decimal? Hours);

public sealed record PunchRow(DateTime Time, string Type, string Status, string Reasons, decimal? DistanceM, string? Location, bool Mock, bool Offline);

public sealed record CorrectionReview(
    ReviewStatus Status, string? AttendanceName, string? ShiftType, string? ScheduleType, DateTime? ScheduledStart, DateTime? ScheduledEnd,
    DayTimes Before, DayTimes? After, bool AfterIsPreview, IReadOnlyList<PunchRow> Punches, int CorrectionsThisMonth,
    string? Attachment, IReadOnlyList<ReviewCheck> Checks);

/// <summary>One row of a table value: <c>Kind</c> is added, removed, changed or same; cells follow <see cref="BiodataDiff.Columns"/>.</summary>
public sealed record TableRowDiff(string Kind, IReadOnlyList<string?> Old, IReadOnlyList<string?> New);

public sealed record BiodataDiff(string Field, string Label, bool IsTable, string? Current, string? Requested, bool ChangedSinceRequest,
    IReadOnlyList<string> Columns, IReadOnlyList<TableRowDiff> Rows);

public sealed record BiodataReview(ReviewStatus Status, string Category, IReadOnlyList<BiodataDiff> Changes, string? Attachment, IReadOnlyList<ReviewCheck> Checks);

/// <summary>
/// What an approver needs to decide a self-service request on the desk form: the evidence next to the request and the
/// checks that would otherwise be done by hand (previous face registration, the day's schedule and mobile punches, the
/// attendance code the correction would give, the employee's current data). Reads the request with the user's
/// permissions first, so only users who may open the request see the review.
/// </summary>
public sealed class EssReviewService(DocumentService docs, IDocumentStore store, IMetadataProvider metadata, IEssPunchStore punches, TimeProvider? clock = null)
{
    private static string? FileUrl(string? id) => string.IsNullOrWhiteSpace(id) ? null : $"/files/{Uri.EscapeDataString(id)}";
    private static DateTimeOffset? Moment(object? v) => DatetimeFieldType.ToZone(v);

    public async Task<FaceReview> FaceAsync(UserContext user, string name, CancellationToken ct = default)
    {
        var doc = await docs.GetAsync(user, "FaceRegistration", name, ct);
        var m = new Masters(store, doc.TenantId);
        var d = doc.Data;
        var emp = await m.GetAsync("Employee", d.Str("employee"), ct);
        var settings = await m.SingleAsync("HRSettings", ct);
        var photos = new[] { "photo_1", "photo_2", "photo_3" }.Select(f => FileUrl(d.Str(f))).OfType<string>().ToList();

        var others = await m.QueryAsync("FaceRegistration", [new("employee", "=", d.Str("employee"))], ct, take: 50);
        var prev = others.Where(o => o.Name != doc.Name && o.WorkflowState == "Active").OrderByDescending(o => o.Modified).FirstOrDefault();
        FacePrevious? previous = prev is null ? null : new(prev.Name, prev.WorkflowState ?? "",
            new[] { "photo_1", "photo_2", "photo_3" }.Select(f => FileUrl(prev.Data.Str(f))).OfType<string>().ToList(),
            prev.Data.Str("model_name"), Moment(prev.Data.GetValueOrDefault("verified_at")));

        var checks = new List<ReviewCheck>();
        if (!d.Bool("consent")) checks.Add(new("negative", "No consent recorded: biometric data needs the employee's explicit consent."));
        else checks.Add(new("positive", "Consent recorded."));
        checks.Add(photos.Count >= 3 ? new("positive", $"{photos.Count} photos to compare.")
            : new("warning", $"Only {photos.Count} photo(s) were sent."));
        if (emp?.Data.Str("photo") is null) checks.Add(new("info", "The employee record has no photo to compare with."));
        if (previous is not null)
        {
            checks.Add(new("info", $"Verifying replaces the active registration {previous.Name}."));
            if (previous.Model is { } pm && d.Str("model_name") is { } nm && pm != nm)
                checks.Add(new("info", $"Face model changes from {pm} to {nm}."));
        }
        if (d.Str("template_sha256") is { } sha)
        {
            var same = (await m.QueryAsync("FaceRegistration", [new("template_sha256", "=", sha)], ct, take: 20))
                .Where(o => o.Name != doc.Name && o.Data.Str("employee") != d.Str("employee") && o.WorkflowState is "Active" or "Pending Verification").ToList();
            if (same.Count > 0)
                checks.Add(new("negative", $"The same face template is registered for {string.Join(", ", same.Select(o => o.Data.Str("employee")))}."));
        }
        if (d.Str("device_id") is { } device)
        {
            var sameDevice = (await m.QueryAsync("FaceRegistration", [new("device_id", "=", device)], ct, take: 20))
                .Where(o => o.Data.Str("employee") != d.Str("employee") && o.WorkflowState is "Active" or "Pending Verification")
                .Select(o => o.Data.Str("employee")).Distinct().ToList();
            if (sameDevice.Count > 0)
                checks.Add(new("warning", $"The same phone was used to register {string.Join(", ", sameDevice)}."));
        }

        return new FaceReview(await StatusAsync(m, doc, ct), photos, NullableDec(d, "quality_score"), NullableDec(d, "liveness_score"),
            d.Bool("consent"), Moment(d.GetValueOrDefault("consent_at")), d.Str("model_name"), d.Str("model_version"),
            NullableDec(d, "template_dimensions") is { } dim ? (int)dim : null, d.Str("device_id"), d.Str("app_version"),
            FileUrl(emp?.Data.Str("photo")), previous, settings.Bool("ess_require_face"),
            settings.ContainsKey("ess_min_face_score") && settings.Dec("ess_min_face_score") > 0 ? settings.Dec("ess_min_face_score") : 0.8m,
            d.Str("verified_by"), Moment(d.GetValueOrDefault("verified_at")), checks);
    }

    public async Task<CorrectionReview> CorrectionAsync(UserContext user, string name, CancellationToken ct = default)
    {
        var doc = await docs.GetAsync(user, "AttendanceCorrection", name, ct);
        var m = new Masters(store, doc.TenantId);
        var d = doc.Data;
        var employee = d.Str("employee");
        var date = d.Date("attendance_date") ?? DateOnly.FromDateTime(DateTime.Today);
        var emp = await m.GetAsync("Employee", employee, ct);
        var att = (await m.QueryAsync("Attendance", [new("employee", "=", employee), new("attendance_date", "=", D.Iso(date))], ct, take: 1)).FirstOrDefault();
        var a = att?.Data;

        var before = new DayTimes(AttendanceHook.LocalTime(d.GetValueOrDefault("current_check_in")), AttendanceHook.LocalTime(d.GetValueOrDefault("current_check_out")),
            d.Str("current_code"), a is null ? 0 : (int)a.Dec("late_minutes"), a is null ? 0 : (int)a.Dec("early_out_minutes"), a?.Str("code_reason"), a is null ? null : NullableDec(a, "working_hours"));

        // Approved: the attendance already carries the corrected times. Otherwise preview them on a copy of the attendance.
        var applied = doc.WorkflowState == Approval.Approved;
        DayTimes? after = null;
        Dictionary<string, object?>? schedule = a;
        if (applied && a is not null)
            after = Times(a);
        else if (!applied && emp is not null && doc.WorkflowState is not ("Rejected" or "Cancelled"))
        {
            var copy = a is null
                ? new Dictionary<string, object?> { ["employee"] = employee, ["attendance_date"] = D.Iso(date), ["status"] = "Present", ["source"] = "Manual" }
                : Document.DeepCopy(a);
            if (d.GetValueOrDefault("requested_check_in") is { } ci) copy["check_in"] = ci;
            if (d.GetValueOrDefault("requested_check_out") is { } co) copy["check_out"] = co;
            copy["ess_flags"] = null;
            try
            {
                await new AttendanceHook(store, clock).ClassifyAsync(m, emp, date, copy, null, ct);
                after = Times(copy);
                schedule = copy;
            }
            catch (ValidationFailedException) { after = null; }
        }

        var dayPunches = employee is null ? [] : await punches.ListDayAsync(doc.TenantId, employee, date, ct);
        var rows = dayPunches.OrderBy(p => p.PunchTime).Select(p => new PunchRow(
            TimeZoneInfo.ConvertTime(p.PunchTime, FieldTypeSettings.DefaultTimeZone).DateTime, p.PunchType, p.Status, string.Join(", ", p.Reasons),
            p.DistanceM, p.LocationCode, p.IsMockLocation, p.IsOffline)).ToList();

        var month = (await m.QueryAsync("AttendanceCorrection", [new("employee", "=", employee), new("period", "=", $"{date:yyyy-MM}")], ct, take: 100))
            .Count(x => x.WorkflowState is not ("Draft" or "Cancelled"));

        var checks = new List<ReviewCheck>();
        if (await Overtime.SubmittedPayslipAsync(m, employee, $"{date:yyyy-MM}", ct) is { } slip)
            checks.Add(new("negative", $"Payslip {slip.Name} for {date:yyyy-MM} is already submitted; the correction cannot be approved."));
        if (att is null) checks.Add(new("info", "There is no attendance record for this day yet; approving creates one."));
        if (a?.Bool("code_manual") == true) checks.Add(new("warning", "HR set this day's code by hand; the code stays as HR set it."));
        var wantIn = d.GetValueOrDefault("requested_check_in") is not null;
        var wantOut = d.GetValueOrDefault("requested_check_out") is not null;
        var refused = rows.Where(p => p.Status is "rejected" or "flagged" && ((wantIn && p.Type == "in") || (wantOut && p.Type == "out"))).ToList();
        if (refused.Count > 0)
            checks.Add(new("info", $"The app recorded {refused.Count} refused or flagged punch(es) that day: " +
                string.Join("; ", refused.Select(p => $"{p.Type} {p.Time:HH:mm} ({(p.Reasons.Length > 0 ? p.Reasons : p.Status)})")) + "."));
        else if (rows.Count == 0) checks.Add(new("info", "No mobile punches were recorded that day."));
        if (month >= 3) checks.Add(new("warning", $"This is correction {month} for {employee} in {date:yyyy-MM}."));
        if (after is { Code: { } newCode } && newCode == AttendanceCodes.Invalid)
            checks.Add(new("warning", $"The corrected times still give {newCode}: {after.Reason}."));
        if (d.Str("attachment") is null) checks.Add(new("info", "No supporting document attached."));

        return new CorrectionReview(await StatusAsync(m, doc, ct), att?.Name,
            schedule?.Str("shift_type"), schedule?.Str("schedule_type"),
            AttendanceHook.LocalTime(schedule?.GetValueOrDefault("scheduled_start_datetime")), AttendanceHook.LocalTime(schedule?.GetValueOrDefault("scheduled_end_datetime")),
            before, after, !applied, rows, month, FileUrl(d.Str("attachment")), checks);
    }

    public async Task<BiodataReview> BiodataAsync(UserContext user, string name, CancellationToken ct = default)
    {
        var doc = await docs.GetAsync(user, "BiodataChangeRequest", name, ct);
        var m = new Masters(store, doc.TenantId);
        var d = doc.Data;
        var emp = await m.GetAsync("Employee", d.Str("employee"), ct);
        var empMeta = metadata.Current.Get("Employee");
        var decided = doc.WorkflowState is Approval.Approved or "Rejected" or "Cancelled";
        var checks = new List<ReviewCheck>();
        var changes = new List<BiodataDiff>();

        foreach (var r in d.Rows("changes"))
        {
            var field = r.Str("field") ?? "";
            empMeta.ByName.TryGetValue(field, out var f);
            var label = r.Str("label") ?? f?.Label ?? field;
            var snapshot = r.Str("current_value");
            var requested = r.Str("new_value");
            var live = Display(emp?.Data.GetValueOrDefault(field));
            var changedSince = !decided && !SameValue(live, snapshot);
            if (changedSince) checks.Add(new("warning", $"{label} was changed on the employee record after this request was made."));
            if (f?.Def.Type == "Table" && f.Def.ChildDocType is { } childName)
            {
                var child = metadata.Current.Get(childName);
                var oldRows = ParseRows(decided ? snapshot : live);
                var newRows = ParseRows(requested);
                var cols = child.Fields.Where(cf => !cf.IsLayout && (oldRows.Concat(newRows).Any(x => x.ContainsKey(cf.Name) && x[cf.Name] is not null)))
                    .Take(6).ToList();
                var tableRows = new List<TableRowDiff>();
                for (var i = 0; i < Math.Max(oldRows.Count, newRows.Count); i++)
                {
                    var o = i < oldRows.Count ? cols.Select(c => Cell(oldRows[i], c.Name)).ToList() : null;
                    var n = i < newRows.Count ? cols.Select(c => Cell(newRows[i], c.Name)).ToList() : null;
                    var kind = o is null ? "added" : n is null ? "removed" : o.SequenceEqual(n) ? "same" : "changed";
                    tableRows.Add(new(kind, o ?? [], n ?? []));
                }
                changes.Add(new(field, label, true, snapshot, requested, changedSince, cols.Select(c => c.Label).ToList(), tableRows));
            }
            else
            {
                changes.Add(new(field, label, false, decided ? snapshot : live, requested, changedSince, [], []));
                if (field == "nik" && requested is not null)
                {
                    var dup = (await m.QueryAsync("Employee", [new("nik", "=", requested)], ct, take: 5)).FirstOrDefault(e => e.Name != d.Str("employee"));
                    if (dup is not null) checks.Add(new("negative", $"NIK {requested} already belongs to {dup.Name} ({dup.Data.Str("employee_name")})."));
                }
            }
        }
        if (d.Str("attachment") is null && d.Str("category") is "Bank account" or "Identity numbers" or "Marital status & dependents")
            checks.Add(new("warning", "No supporting document attached (bank book, ID card, family card or marriage certificate)."));

        return new BiodataReview(await StatusAsync(m, doc, ct), d.Str("category") ?? "", changes, FileUrl(d.Str("attachment")), checks);
    }

    private static DayTimes Times(Dictionary<string, object?> a) => new(
        AttendanceHook.LocalTime(a.GetValueOrDefault("check_in")), AttendanceHook.LocalTime(a.GetValueOrDefault("check_out")),
        a.Str("attendance_code"), (int)a.Dec("late_minutes"), (int)a.Dec("early_out_minutes"), a.Str("code_reason"), NullableDec(a, "working_hours"));

    private static async Task<ReviewStatus> StatusAsync(Masters m, Document doc, CancellationToken ct)
    {
        var state = doc.WorkflowState ?? "";
        string? waiting = null;
        if (EssApprovalNotifier.NextApprover.TryGetValue((doc.DocType, state), out var next))
        {
            if (next.Field is { } field && doc.Data.Str(field) is { } approver)
                waiting = (await m.GetAsync("Employee", approver, ct))?.Data.Str("employee_name") is { } n ? $"{n} ({approver})" : approver;
            else waiting = next.Role;
        }
        return new ReviewStatus(state, waiting, state is Approval.Approved or "Active" or "Rejected" or "Cancelled" or "Revoked");
    }

    private static decimal? NullableDec(IDictionary<string, object?> d, string key) =>
        !d.TryGetValue(key, out var v) || v is null ? null : d.Dec(key);

    private static string? Display(object? value) => value switch
    {
        null => null,
        string s => s,
        System.Collections.IEnumerable list => JsonSerializer.Serialize(list),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static bool SameValue(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal)
        || (a?.TrimStart().StartsWith('[') == true && b?.TrimStart().StartsWith('[') == true && Canon(a) == Canon(b));

    private static string Canon(string json) =>
        string.Join("|", ParseRows(json).Select(r => string.Join(",", r.Where(kv => kv.Value is not null && kv.Key is not ("name" or "idx" or "parent" or "parentfield" or "parenttype"))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))));

    private static List<Dictionary<string, string?>> ParseRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json) ?? [])
                .Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => kv.Value.GetString(),
                    _ => kv.Value.ToString(),
                })).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static string? Cell(Dictionary<string, string?> row, string column) => row.TryGetValue(column, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
