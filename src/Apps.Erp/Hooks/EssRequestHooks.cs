using System.Globalization;
using System.Text.Json;
using Apps.Erp.Ess;
using Framework.Documents;
using Framework.Files;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Apps.Erp.Hooks;

public sealed class FaceRegistration { }
public sealed class AttendanceCorrection { }
public sealed class BiodataChangeRequest { }

/// <summary>
/// Face registration: consent, encrypted template check (only templates stored through the ESS API decrypt), one active
/// registration per employee (verifying a new one revokes the previous one, see <see cref="ErpEvents.FaceActivated"/>).
/// </summary>
public sealed class FaceRegistrationHook(IDocumentStore store, IFileStore files, IServiceProvider services, TimeProvider? clock = null)
    : IBeforeValidate<FaceRegistration>, IBeforeTransition<FaceRegistration>, IAfterSave<FaceRegistration>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        if (d.Bool("consent")) d.SetIfEmpty("consent_at", Now());
        else d["consent_at"] = null;

        var templateId = d.Str("template_file");
        if (templateId is not null && templateId != c.Previous?.Data.Str("template_file"))
        {
            // Resolved lazily: hosts without Data Protection (tools, some tests) can still save other documents.
            var dataProtection = services.GetService(typeof(IDataProtectionProvider)) as IDataProtectionProvider
                                 ?? throw Errors.One("template_file", "Face templates need ASP.NET Data Protection, which is not configured");
            var stored = await files.GetAsync(c.Doc.TenantId, templateId, ct);
            var vector = stored is null ? null : FaceTemplates.Unprotect(dataProtection, stored.Value.Content);
            if (vector is null) throw Errors.One("template_file", "The face template must be registered from the ESS mobile app");
            var canonical = FaceTemplates.Canonical(vector);
            d["template_dimensions"] = (long)vector.Length;
            d["template_sha256"] = FaceTemplates.Fingerprint(canonical);
        }
        if (templateId is null) { d["template_dimensions"] = null; d["template_sha256"] = null; }
    }

    public Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        switch (c.ToState)
        {
            case "Pending Verification":
                if (!d.Bool("consent")) throw Errors.One("consent", "The employee's consent is required to register a face");
                if (d.Str("photo_1") is null) throw Errors.One("photo_1", "At least one face photo is required");
                if (d.Str("template_file") is null) throw Errors.One("template_file", "The face template is missing; register again from the ESS app");
                break;
            case "Active":
                d["is_active"] = true;
                d["verified_by"] = c.User.FullName is { Length: > 0 } n ? $"{n} ({c.User.Id})" : c.User.Id;
                d["verified_at"] = Now();
                break;
            case "Revoked" or "Rejected" or "Cancelled":
                d["is_active"] = false;
                break;
        }
        return Task.CompletedTask;
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState != c.Previous?.WorkflowState && c.Doc.WorkflowState is "Active" or "Revoked")
            ErpEvents.Emit(c, ErpEvents.FaceActivated, new() { ["employee"] = c.Doc.Data.Str("employee"), ["state"] = c.Doc.WorkflowState });
        return Task.CompletedTask;
    }

    private string Now() => (clock ?? TimeProvider.System).GetUtcNow().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);
}

/// <summary>
/// Attendance correction: resolves the approver and the recorded attendance, checks the requested times and the payroll
/// lock; once approved the attendance is updated (<see cref="ErpEvents.AttendanceCorrectionApply"/>) and its code recalculated.
/// </summary>
public sealed class AttendanceCorrectionHook(IDocumentStore store, TimeProvider? clock = null)
    : IBeforeValidate<AttendanceCorrection>, IBeforeTransition<AttendanceCorrection>, IAfterSave<AttendanceCorrection>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var date = d.Date("attendance_date") ?? throw Errors.One("attendance_date", "The date is required");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), FieldTypeSettings.DefaultTimeZone).DateTime);
        if (date > today) throw Errors.One("attendance_date", "Attendance can only be corrected for today or past days");
        d["period"] = $"{date:yyyy-MM}";
        if (c.Doc.DocStatus == DocStatus.Draft)
        {
            await Overtime.CheckNotPaidAsync(m, emp.Name, $"{date:yyyy-MM}", "attendance_date", ct);
            d["approver"] = (await Hr.ResolveApproversAsync(m, emp, null, today, ct, "leave_approver")).LineApprover;
        }

        var att = (await m.QueryAsync("Attendance", [new("employee", "=", emp.Name), new("attendance_date", "=", D.Iso(date))], ct, take: 1)).FirstOrDefault();
        if (c.Doc.DocStatus == DocStatus.Draft)
        {
            d["attendance"] = att?.Name;
            d["current_check_in"] = att?.Data.GetValueOrDefault("check_in");
            d["current_check_out"] = att?.Data.GetValueOrDefault("check_out");
            d["current_code"] = att?.Data.Str("attendance_code");
        }

        // Requested times must belong to this work day: check-in from the day before (night shifts) to the day itself,
        // check-out up to the next day.
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        if (AttendanceHook.LocalTime(d.GetValueOrDefault("requested_check_in")) is { } ci && (ci < dayStart.AddHours(-6) || ci >= dayStart.AddDays(1)))
            throw Errors.One("requested_check_in", $"Check-in must be on {D.Iso(date)}");
        if (AttendanceHook.LocalTime(d.GetValueOrDefault("requested_check_out")) is { } co && (co <= dayStart || co >= dayStart.AddDays(2)))
            throw Errors.One("requested_check_out", $"Check-out must be on {D.Iso(date)} or the next morning");

        var open = (await m.QueryAsync("AttendanceCorrection", [new("employee", "=", emp.Name), new("attendance_date", "=", D.Iso(date))], ct, take: 20))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.WorkflowState is "Draft" or "Pending Approval" or "Pending HR Approval");
        if (open is not null) throw Errors.One("attendance_date", $"Correction {open.Name} for {D.Iso(date)} is still open");
    }

    public async Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        if (c.ToState != Approval.Approved) return;
        var d = c.Doc.Data;
        await Overtime.CheckNotPaidAsync(new Masters(store, c.Doc.TenantId), d.Str("employee"), d.Str("period") ?? "", "attendance_date", ct);
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState == Approval.Approved && c.Previous?.WorkflowState != Approval.Approved)
            ErpEvents.Emit(c, ErpEvents.AttendanceCorrectionApply, new());
        return Task.CompletedTask;
    }

    /// <summary>Applies an approved correction (idempotent: the attendance remarks record which correction was applied).</summary>
    internal static async Task ApplyAsync(SystemDocs sys, Masters m, Document corr, CancellationToken ct)
    {
        var d = corr.Data;
        var marker = $"[{corr.Name}]";
        var att = (await m.QueryAsync("Attendance", [new("employee", "=", d.Str("employee")), new("attendance_date", "=", d.Str("attendance_date"))], ct, take: 1)).FirstOrDefault();
        if (att?.Data.Str("remarks")?.Contains(marker, StringComparison.Ordinal) == true) return;
        var note = $"{marker} corrected: {d.Str("correction_type")}. {d.Str("reason")}".Trim();
        var patch = new Dictionary<string, object?>();
        if (d.GetValueOrDefault("requested_check_in") is { } inAt) patch["check_in"] = inAt;
        if (d.GetValueOrDefault("requested_check_out") is { } outAt) patch["check_out"] = outAt;
        if (att is null)
        {
            patch["employee"] = d.Str("employee");
            patch["attendance_date"] = d.Str("attendance_date");
            patch["source"] = "Manual";
            patch["status"] = "Present";
            patch["remarks"] = note;
            await sys.InsertAsync("Attendance", patch, $"Attendance correction {corr.Name}", ct);
            return;
        }
        // A correction resolves the punch review: the corrected times replace the flagged ones.
        patch["ess_flags"] = null;
        patch["remarks"] = string.IsNullOrWhiteSpace(att.Data.Str("remarks")) ? note : att.Data.Str("remarks") + "\n" + note;
        await sys.UpdateAsync("Attendance", att.Name, patch, ct);
    }
}

/// <summary>
/// Biodata change: only the fields of the chosen category, values checked against the employee field (options, e-mail,
/// child-table columns); once approved by HR the employee record is updated (<see cref="ErpEvents.BiodataApply"/>).
/// </summary>
public sealed class BiodataChangeRequestHook(IDocumentStore store, IMetadataProvider metadata)
    : IBeforeValidate<BiodataChangeRequest>, IAfterSave<BiodataChangeRequest>
{
    /// <summary>Employee fields an employee may ask to change, per category.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["Contact & address"] = ["mobile", "personal_email", "current_address", "permanent_address", "emergency_contacts"],
        ["Marital status & dependents"] = ["marital_status", "ptkp_status", "dependents"],
        ["Bank account"] = ["bank_accounts"],
        ["Identity numbers"] = ["nik", "npwp", "bpjs_health_no", "bpjs_employment_no", "identity_documents"],
    };

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var category = d.Str("category") ?? "";
        if (!Fields.TryGetValue(category, out var allowed)) throw Errors.One("category", "Choose a category");
        var empMeta = metadata.Current.Get("Employee");
        var rows = d.Rows("changes");
        if (rows.Count == 0) throw Errors.One("changes", "Add at least one change");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var path = $"changes[{i}].new_value";
            var field = r.Str("field") ?? "";
            if (!allowed.Contains(field) || !empMeta.ByName.TryGetValue(field, out var f))
                throw Errors.One($"changes[{i}].field", $"'{field}' cannot be changed under {category}. Allowed: {string.Join(", ", allowed)}");
            if (!seen.Add(field)) throw Errors.One($"changes[{i}].field", $"'{field}' appears twice");
            r["label"] = f.Label;
            if (c.Doc.DocStatus == DocStatus.Draft) r["current_value"] = Display(emp.Data.GetValueOrDefault(field));
            r["new_value"] = NormalizeValue(metadata, f, r.Str("new_value"), path);
        }
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState == Approval.Approved && c.Previous?.WorkflowState != Approval.Approved)
            ErpEvents.Emit(c, ErpEvents.BiodataApply, new());
        return Task.CompletedTask;
    }

    /// <summary>Value as text for the request; tables as a JSON array of rows with the child DocType's columns only.</summary>
    internal static string? NormalizeValue(IMetadataProvider metadata, CompiledField f, string? value, string path)
    {
        value = value?.Trim();
        if (f.Def.Type == "Table")
        {
            var child = metadata.Current.Get(f.Def.ChildDocType!);
            List<Dictionary<string, JsonElement>>? parsed;
            try { parsed = string.IsNullOrEmpty(value) ? [] : JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(value); }
            catch (JsonException) { throw Errors.One(path, $"{f.Label}: send the rows as a JSON array"); }
            var rows = new List<Dictionary<string, object?>>();
            foreach (var row in parsed ?? [])
            {
                var clean = new Dictionary<string, object?>();
                foreach (var (k, v) in row)
                {
                    if (!child.ByName.TryGetValue(k, out var cf) || cf.IsLayout) throw Errors.One(path, $"{f.Label}: unknown column '{k}'");
                    clean[k] = v.ValueKind switch
                    {
                        JsonValueKind.Null or JsonValueKind.Undefined => null,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => v.GetDecimal(),
                        _ => v.ToString(),
                    };
                }
                rows.Add(clean);
            }
            return JsonSerializer.Serialize(rows);
        }
        if (string.IsNullOrEmpty(value)) return null;
        if (f.Def.Type == "Select" && f.Def.Options is { Count: > 0 } options && !options.Contains(value))
            throw Errors.One(path, $"{f.Label}: choose one of {string.Join(", ", options)}");
        if (f.Def.Type == "Email" && !System.Net.Mail.MailAddress.TryCreate(value, out _))
            throw Errors.One(path, $"{f.Label}: not a valid e-mail address");
        if (f.Name is "nik" && (value.Length != 16 || !value.All(char.IsDigit)))
            throw Errors.One(path, "NIK must be 16 digits");
        return value;
    }

    private static string? Display(object? value) => value switch
    {
        null => null,
        string s => s,
        System.Collections.IEnumerable list => JsonSerializer.Serialize(list),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Applies an approved request to the employee (idempotent: values already equal are skipped).</summary>
    internal static async Task ApplyAsync(SystemDocs sys, IMetadataProvider metadata, Document req, CancellationToken ct)
    {
        var empMeta = metadata.Current.Get("Employee");
        var employeePatch = new Dictionary<string, object?>();
        foreach (var r in req.Data.Rows("changes"))
        {
            if (r.Str("field") is not { } field || !empMeta.ByName.TryGetValue(field, out var f)) continue;
            var value = r.Str("new_value");
            employeePatch[field] = f.Def.Type == "Table"
                ? JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(value ?? "[]")!
                    .Select(row => row.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind switch
                    {
                        JsonValueKind.Null => (object?)null,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => kv.Value.GetDecimal(),
                        _ => kv.Value.ToString(),
                    })).ToList()
                : value;
        }
        if (employeePatch.Count > 0) await sys.UpdateAsync("Employee", req.Data.Str("employee")!, employeePatch, ct);
    }
}

/// <summary>
/// Inbox notifications for self-service requests: the next approver (supervisor, project approver or the HR role) when a
/// request reaches their stage, and the requester when it is decided. The ESS app shows them and links to the approval.
/// </summary>
public sealed class EssApprovalNotifier(Framework.Security.IUserDirectory? users = null, Framework.Persistence.INotificationStore? inbox = null)
    : IAfterSave<LeaveApplication>, IAfterSave<OvertimeRequest>, IAfterSave<AttendanceCorrection>, IAfterSave<FaceRegistration>, IAfterSave<BiodataChangeRequest>
{
    /// <summary>(DocType, state) → who decides next: an employee field of the document, or a role.</summary>
    internal static readonly IReadOnlyDictionary<(string DocType, string State), (string? Field, string? Role)> NextApprover =
        new Dictionary<(string, string), (string?, string?)>
        {
            [("LeaveApplication", "Pending Approval")] = ("leave_approver", null),
            [("OvertimeRequest", "Pending Project Approval")] = ("project_approver", null),
            [("OvertimeRequest", "Pending Approval")] = ("approver", null),
            [("AttendanceCorrection", "Pending Approval")] = ("approver", null),
            [("AttendanceCorrection", "Pending HR Approval")] = (null, "HR-Time-Attendance"),
            [("FaceRegistration", "Pending Verification")] = (null, "HR-Time-Attendance"),
            [("BiodataChangeRequest", "Pending HR Approval")] = (null, "HR-Employee-Management"),
        };

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (users is null || inbox is null) return;
        var state = c.Doc.WorkflowState;
        if (state is null || state == c.Previous?.WorkflowState) return;
        var d = c.Doc.Data;
        var who = d.Str("employee_name") ?? d.Str("employee") ?? "";
        var title = c.Meta.Title;
        var key = $"ess-approval|{c.Doc.DocType}|{c.Doc.Name}|{state}";

        if (NextApprover.TryGetValue((c.Doc.DocType, state), out var next))
        {
            var subject = $"{title} from {who} awaits your approval";
            var body = $"{c.Doc.Name} is waiting for your decision ({state}).";
            if (next.Field is { } field && d.Str(field) is { } approver)
                await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [approver], subject, body, c.Doc.DocType, c.Doc.Name, key, ct);
            else if (next.Role is { } role)
                await Notify.ToRoleAsync(users, inbox, c.Doc.TenantId, role, subject, body, c.Doc.DocType, c.Doc.Name, key, ct);
            return;
        }
        if (state is Approval.Approved or "Active" or "Rejected")
        {
            var outcome = state == "Rejected" ? "rejected" : "approved";
            await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [d.Str("employee")],
                $"Your {title.ToLowerInvariant()} {c.Doc.Name} was {outcome}", $"{c.Doc.Name}: {state}.", c.Doc.DocType, c.Doc.Name, key, ct);
        }
    }
}
