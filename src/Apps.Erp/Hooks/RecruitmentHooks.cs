using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Jobs;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

public sealed class JobRequisition { }
public sealed class JobApplicant { }
public sealed class Interview { }
public sealed class JobOffer { }

/// <summary>Requisition: data jabatan dari posisi (harus Vacant saat draft); status, jumlah pelamar & yang diterima dihitung saat dibuka.</summary>
public sealed class JobRequisitionHook(IDocumentStore store) : IBeforeValidate<JobRequisition>, IBeforeTransition<JobRequisition>, IOnLoad<JobRequisition>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("Position", d.Str("position"), ct) is { } pos)
        {
            if (c.Doc.DocStatus == DocStatus.Draft && pos.Data.Str("status") != "Vacant")
                throw Errors.One("position", $"Position {pos.Name} is {pos.Data.Str("status")}, not Vacant");
            d.SetIfEmpty("job_profile", pos.Data.Str("job_profile"));
            d.SetIfEmpty("organization_unit", pos.Data.Str("organization_unit"));
            d.SetIfEmpty("grade", pos.Data.Str("grade"));
            d.SetIfEmpty("company", pos.Data.Str("company"));
        }
        if (string.IsNullOrEmpty(d.Str("unit_head")) && d.Str("organization_unit") is { } unitId)
        {
            var headPos = await m.StrAsync("OrganizationUnit", unitId, "manager_position", ct);
            if (!string.IsNullOrEmpty(headPos))
                d["unit_head"] = (await Hr.HolderAsync(m, headPos, null, ct))?.Name;
        }
        if (await m.GetAsync("Grade", d.Str("grade"), ct) is { } grade && d.Dec("salary_max") > 0)
        {
            var max = grade.Data.Rows("pay_ranges").Where(r => r.Str("company") is null || r.Str("company") == d.Str("company")).Select(r => r.Dec("max_salary")).DefaultIfEmpty(0).Max();
            if (max > 0 && d.Dec("salary_max") > max) c.Messages.Add($"The salary range exceeds the maximum of grade {grade.Name} ({max:N0})");
        }
        if (c.Doc.DocStatus == DocStatus.Draft) d["requisition_status"] = "Draft";

        // While waiting for approval the request must not change under the approver; HR may still correct it.
        if (c.Previous?.WorkflowState is PendingUnitHead or PendingHr
            && !(c.User.IsAdmin || c.User.HasRole(ErpRoles.HrRecruitment) || c.User.HasRole(ErpRoles.HrAdmin)))
        {
            var changed = LockedWhilePending.FirstOrDefault(f => !SameValue(c.Previous.Data.GetValueOrDefault(f), d.GetValueOrDefault(f)));
            if (changed is not null)
                throw Errors.One(changed, $"The requisition is {c.Previous.WorkflowState}; it cannot be changed until it is approved or rejected");
        }
    }

    private static bool SameValue(object? a, object? b)
    {
        static decimal? Num(object? v) => v switch
        {
            null => null,
            string str => decimal.TryParse(str, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : null,
            IConvertible conv when v is not bool => conv.ToDecimal(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        if (Num(a) is { } x && Num(b) is { } y) return x == y;
        var sa = a?.ToString(); var sb = b?.ToString();
        return string.IsNullOrEmpty(sa) ? string.IsNullOrEmpty(sb) : sa == sb;
    }

    public const string PendingUnitHead = "Pending Unit Head", PendingHr = "Pending HR";
    private static readonly string[] LockedWhilePending =
        ["company", "organization_unit", "position", "job_profile", "grade", "employment_type", "openings", "salary_min", "salary_max", "hiring_manager"];

    /// <summary>
    /// Openings still to be filled by other requisitions of the unit and job profile: requisitions waiting for approval
    /// or approved, minus their hired applicants. Filled, rejected and cancelled requisitions do not count.
    /// </summary>
    private static async Task<int> CommittedOpeningsAsync(Masters m, string unitId, string? jobProfile, string self, CancellationToken ct)
    {
        var total = 0;
        foreach (var r in await m.QueryAsync("JobRequisition", [new FilterClause("organization_unit", "=", unitId)], ct, take: 5000))
        {
            if (r.Name == self || (jobProfile is not null && r.Data.Str("job_profile") != jobProfile)) continue;
            var inFlight = r.WorkflowState is PendingUnitHead or PendingHr
                || (r.DocStatus == DocStatus.Submitted && r.WorkflowState is null or "Approved");
            if (!inFlight) continue;
            var hired = (await m.QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", r.Name), new FilterClause("status", "=", "Hired")], ct, take: 5000)).Count;
            total += Math.Max(0, (int)r.Data.Dec("openings") - hired);
        }
        return total;
    }

    public async Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        if (c.ToState is not ("Pending Unit Head" or "Approved")) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("Position", d.Str("position"), ct) is { } pos && pos.Data.Str("status") != "Vacant")
            throw Errors.One("position", $"Position {pos.Name} is {pos.Data.Str("status")}, not Vacant");

        if (d.Str("position") is null && d.Str("organization_unit") is { } unitId)
        {
            var unitDoc = await m.GetAsync("OrganizationUnit", unitId, ct);
            if (unitDoc?.Data.Str("staffing_model") == "Position")
            {
                var vacantCount = (await m.QueryAsync("Position",
                        [new FilterClause("organization_unit", "=", unitId), new FilterClause("status", "=", "Vacant")], ct, take: 5000))
                    .Count(p => d.Str("job_profile") is null || p.Data.Str("job_profile") == d.Str("job_profile"));
                var openReqs = await CommittedOpeningsAsync(m, unitId, d.Str("job_profile"), c.Doc.Name, ct);
                var requested = (int)d.Dec("openings");
                if (openReqs + requested > vacantCount)
                    throw Errors.One("openings", $"Requested openings ({requested}) exceed available Vacant positions in {unitId} ({Math.Max(0, vacantCount - openReqs)} remaining)");
            }
        }
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var applicants = await new Masters(store, c.Doc.TenantId).QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", c.Doc.Name)], ct, take: 5000);
        var hired = applicants.Count(a => a.Data.Str("status") == "Hired");
        d["applicant_count"] = (long)applicants.Count;
        d["hired_count"] = (long)hired;
        d["requisition_status"] = c.Doc.DocStatus switch
        {
            DocStatus.Draft => "Draft",
            DocStatus.Cancelled => "Cancelled",
            _ => hired >= d.Dec("openings") ? "Filled" : "Open",
        };
    }
}

/// <summary>Pelamar: requisition harus terbuka; email unik per requisition; status Hired hanya lewat aksi 'Create employee'.</summary>
public sealed class JobApplicantHook(IDocumentStore store, IServiceProvider? services = null, TimeProvider? clock = null)
    : IBeforeValidate<JobApplicant>, IAfterSave<JobApplicant>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var req = await m.RequireAsync("JobRequisition", d.Str("job_requisition"), "job_requisition", "Job requisition", ct);
        if (c.Previous is null && req.DocStatus != DocStatus.Submitted) throw Errors.One("job_requisition", $"{req.Name} is not open");
        var email = d.Str("email")?.Trim().ToLowerInvariant();
        d["email"] = email;
        var dup = (await m.QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", req.Name), new FilterClause("email", "=", email)], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null) throw Errors.One("email", $"{email} already applied to {req.Name} ({dup.Name})");
        if (d.Str("status") == "Hired" && d.Str("employee") is null && !c.User.IsAdmin)
            throw Errors.One("status", "'Hired' is set by 'Create employee' on an accepted job offer");

        var stage = d.Str("status") ?? "Applied";
        if (c.Previous is null || c.Previous.Data.Str("status") != stage)
        {
            var history = d.Rows("stage_history").Select(r => new Dictionary<string, object?>(r)).ToList();
            history.Add(new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["changed_at"] = (clock ?? TimeProvider.System).GetUtcNow().ToString("O"),
                ["changed_by"] = c.User.Id,
                ["notes"] = stage == "Rejected" ? d.Str("rejection_reason") : null,
            });
            d["stage_history"] = history;
        }
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Previous is not null) return;
        var users = services?.GetService<IUserDirectory>();
        var inbox = services?.GetService<INotificationStore>();
        if (users is null || inbox is null) return;
        var m = new Masters(store, c.Doc.TenantId);
        var req = await m.GetAsync("JobRequisition", c.Doc.Data.Str("job_requisition"), ct);
        var subject = $"New applicant: {c.Doc.Data.Str("applicant_name")} for {req?.Data.Str("job_title") ?? c.Doc.Data.Str("job_requisition")}";
        var body = $"Review application {c.Doc.Name} in Recruitment.";
        var targets = new[] { req?.Data.Str("hiring_manager"), req?.Data.Str("recruiter") }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        if (targets.Count > 0)
            await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, targets, subject, body, "JobApplicant", c.Doc.Name, $"new-app|{c.Doc.Name}", ct);
        await Notify.ToRoleAsync(users, inbox, c.Doc.TenantId, ErpRoles.HrRecruitment, subject, body, "JobApplicant", c.Doc.Name, $"new-app|{c.Doc.Name}", ct);
    }
}

/// <summary>Interview: prefill competency scorecard from Job Profile and notify interviewer.</summary>
public sealed class InterviewHook(IDocumentStore store, IServiceProvider? services = null)
    : IBeforeValidate<Interview>, IAfterSave<Interview>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (c.Previous is null && !d.Rows("scorecard").Any() && d.Str("job_applicant") is { } appId)
        {
            var reqId = await m.StrAsync("JobApplicant", appId, "job_requisition", ct);
            var jpId = !string.IsNullOrEmpty(reqId) ? await m.StrAsync("JobRequisition", reqId, "job_profile", ct) : null;
            if (!string.IsNullOrEmpty(jpId) && await m.GetAsync("JobProfile", jpId, ct) is { } jp)
            {
                var cards = new List<Dictionary<string, object?>>();
                foreach (var jpc in jp.Data.Rows("competencies"))
                {
                    var code = jpc.Str("competency");
                    if (string.IsNullOrEmpty(code)) continue;
                    cards.Add(new Dictionary<string, object?>
                    {
                        ["competency"] = code,
                        ["competency_name"] = jpc.Str("competency_name") ?? await m.StrAsync("Competency", code, "competency_name", ct),
                    });
                }
                if (cards.Count > 0) d["scorecard"] = cards;
            }
        }
        foreach (var row in d.Rows("scorecard"))
        {
            var code = row.Str("competency");
            if (!string.IsNullOrEmpty(code))
                row["competency_name"] = await m.StrAsync("Competency", code, "competency_name", ct);
        }
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Previous is not null) return;
        var users = services?.GetService<IUserDirectory>();
        var inbox = services?.GetService<INotificationStore>();
        if (users is null || inbox is null) return;
        if (c.Doc.Data.Str("interviewer") is { } interviewer && !string.IsNullOrEmpty(interviewer))
        {
            await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [interviewer],
                $"Interview scheduled: {c.Doc.Data.Str("applicant_name") ?? c.Doc.Data.Str("job_applicant")}",
                $"Round: {c.Doc.Data.Str("round")} at {c.Doc.Data.Str("scheduled_at")}.",
                "Interview", c.Doc.Name, $"interview|{c.Doc.Name}|{interviewer}", ct);
        }
    }
}

/// <summary>Penawaran: data jabatan dari requisition; satu penawaran aktif per pelamar; karyawan hasilnya dibaca saat dibuka.</summary>
public sealed class JobOfferHook(IDocumentStore store) : IBeforeValidate<JobOffer>, IOnLoad<JobOffer>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var applicant = await m.RequireAsync("JobApplicant", d.Str("job_applicant"), "job_applicant", "Applicant", ct);
        if (c.Doc.DocStatus == DocStatus.Draft && applicant.Data.Str("status") is "Rejected" or "Withdrawn" or "Hired")
            throw Errors.One("job_applicant", $"{applicant.Name} is {applicant.Data.Str("status")}");
        if (await m.GetAsync("JobRequisition", applicant.Data.Str("job_requisition"), ct) is { } req)
        {
            d.SetIfEmpty("position", req.Data.Str("position"));
            d.SetIfEmpty("job_profile", req.Data.Str("job_profile"));
            d.SetIfEmpty("organization_unit", req.Data.Str("organization_unit"));
            d.SetIfEmpty("grade", req.Data.Str("grade"));
            d.SetIfEmpty("employment_type", req.Data.Str("employment_type"));
            d.SetIfEmpty("proposed_start_date", req.Data.Str("target_start_date"));
        }
        if (c.Previous is null && !(await m.SingleAsync("HRSettings", ct)).Bool("allow_offer_without_interview"))
        {
            var interviews = await m.QueryAsync("Interview", [new FilterClause("job_applicant", "=", applicant.Name)], ct, take: 50);
            var assessed = interviews.Any(i => i.Data.Str("result") == "Pass"
                && (!string.IsNullOrWhiteSpace(i.Data.Str("feedback")) || i.Data.Rows("scorecard").Any(r => r.Dec("score") > 0)));
            if (!assessed)
                throw Errors.One("job_applicant", $"{applicant.Name} has no passed interview with feedback; record the interview result and feedback first");
        }
        var other = (await m.QueryAsync("JobOffer", [new FilterClause("job_applicant", "=", applicant.Name)], ct, take: 20))
            .FirstOrDefault(o => o.Name != c.Doc.Name && o.DocStatus != DocStatus.Cancelled && o.WorkflowState is not ("Declined" or "Expired" or "Cancelled"));
        if (other is not null) throw Errors.One("job_applicant", $"{applicant.Name} already has an active offer {other.Name}");
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct) =>
        c.Doc.Data["employee"] = await new Masters(store, c.Doc.TenantId).StrAsync("JobApplicant", c.Doc.Data.Str("job_applicant"), "employee", ct);
}

/// <summary>
/// Aksi "Create employee" (Workday Hire): dari penawaran Accepted dibuat Employee (data pelamar + jabatan penawaran), lalu Salary Assignment
/// gaji pokok bila komponennya diatur di Payroll Settings, dan pelamar ditandai Hired. Employee dibuat sebagai user HR (izin berlaku).
/// </summary>
public sealed class JobOfferCreateEmployeeAction(IDocumentStore store) : IFormAction<JobOffer>
{
    public string ActionId => "erp.joboffer.create_employee";
    public string Label => "Create employee";
    public string Icon => "user-plus";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) => doc.WorkflowState == "Accepted" && (user.HasRole(ErpRoles.HrRecruitment) || user.HasRole(ErpRoles.HrEmployeeManagement));

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var m = new Masters(store, ctx.Doc.TenantId);
        var o = ctx.Doc.Data;
        var applicant = await m.GetAsync("JobApplicant", o.Str("job_applicant"), ct);
        if (applicant is null) return FormActionResult.Fail("The applicant no longer exists");
        if (applicant.Data.Str("employee") is { } already) return FormActionResult.NavigateTo($"/app/Employee/{already}", $"Already hired as {already}");
        var number = o.Str("employee_number");
        if (number is null) return FormActionResult.Fail("The offer has no employee number");
        var a = applicant.Data;
        if (a.Str("gender") is null || a.Str("date_of_birth") is null) return FormActionResult.Fail($"Fill gender and date of birth on applicant {applicant.Name} first");
        var req = await m.GetAsync("JobRequisition", a.Str("job_requisition"), ct);

        try
        {
            var emp = await ctx.Docs.InsertAsync(ctx.User, "Employee", new Dictionary<string, object?>
            {
                ["employee_number"] = number, ["full_name"] = a.Str("applicant_name"), ["gender"] = a.Str("gender"), ["date_of_birth"] = a.Str("date_of_birth"),
                ["personal_email"] = a.Str("email"), ["mobile"] = a.Str("mobile"),
                ["company"] = req?.Data.Str("company"), ["organization_unit"] = o.Str("organization_unit"), ["position"] = o.Str("position"),
                ["job_profile"] = o.Str("job_profile"), ["grade"] = o.Str("grade"), ["employment_type"] = o.Str("employment_type"),
                ["hire_date"] = o.Str("proposed_start_date"), ["status"] = "Active",
            }, $"Hired from {ctx.Doc.Name}", ct);

            // Salary Assignment milik role Payroll; dibuat sebagai user sistem karena gaji sudah disetujui HR di penawaran.
            var sys = UserContext.System(ctx.Doc.TenantId);
            var note = "";
            if ((await m.SingleAsync("PayrollSettings", ct)).Str("base_salary_component") is { } baseComponent && o.Dec("base_salary") > 0)
            {
                var sa = await ctx.Docs.InsertAsync(sys, "SalaryAssignment", new Dictionary<string, object?>
                {
                    ["employee"] = emp.Name, ["from_date"] = o.Str("proposed_start_date"), ["is_active"] = true,
                    ["earnings"] = new List<Dictionary<string, object?>> { new() { ["salary_component"] = baseComponent, ["amount"] = o.Dec("base_salary") } },
                }, $"From {ctx.Doc.Name}", ct);
                note = $", salary assignment {sa.Name}";
            }
            else note = " (no salary assignment: set 'Base salary component' in Payroll Settings)";

            var fresh = await ctx.Docs.GetAsync(sys, "JobApplicant", applicant.Name, ct);
            await ctx.Docs.UpdateAsync(sys, "JobApplicant", applicant.Name, fresh.Version,
                new Dictionary<string, object?> { ["status"] = "Hired", ["employee"] = emp.Name }, ct);

            // Onboarding list for the new hire (checklist from the default template); a failure here must not hide that
            // the employee was created, so it is reported in the message instead.
            try
            {
                var existingOnb = (await m.QueryAsync("OnboardingList", [new FilterClause("employee", "=", emp.Name)], ct, take: 1)).Count > 0;
                if (!existingOnb)
                {
                    var onb = await ctx.Docs.InsertAsync(sys, "OnboardingList", new Dictionary<string, object?>
                    {
                        ["employee"] = emp.Name,
                        ["start_date"] = o.Str("proposed_start_date"),
                        ["organization_unit"] = o.Str("organization_unit"),
                        ["position"] = o.Str("position"),
                    }, $"Auto-created from {ctx.Doc.Name}", ct);
                    note += $", onboarding {onb.Name}";
                }
            }
            catch (Exception ex) when (ex is ValidationFailedException or InvalidPayloadException)
            {
                note += $" (onboarding list not created: {ex.Message})";
            }

            return FormActionResult.NavigateTo($"/app/Employee/{emp.Name}", $"Employee {emp.Name} created{note}");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Creating the employee failed: {ex.Message}");
        }
    }
}
