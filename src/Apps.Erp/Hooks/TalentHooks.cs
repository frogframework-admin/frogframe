using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

public sealed class Appraisal { }
public sealed class TalentReview { }
public sealed class SuccessionPlan { }
public sealed class DevelopmentPlan { }
public sealed class TrainingRequest { }
public sealed class TrainingEvent { }
public sealed class JobProfile { }

/// <summary>
/// Who besides HR sees talent records (Talent Review, Succession Plan, Development Plan). The Manager permission is
/// scoped to the stored field <c>manager_viewer</c> (match:manager_viewer=employee), so list, report, export and API
/// follow the same rule as opening one record. <c>manager_viewer</c> = the employee's direct manager when
/// HR Settings › <c>allow_manager_talent_access</c> is on, else empty (HR-Talent only). Hooks set it on save;
/// <see cref="RefreshAsync"/> rewrites it on existing (also submitted) records when the setting or a reporting line
/// changes (HR Settings save, daily job).
/// </summary>
internal static class TalentAccess
{
    /// <summary>
    /// DocType, the field with the employee whose manager is meant, and whether the record has <c>manager_viewer</c>
    /// (Development Plans have not: the direct manager always reads and approves them through reports_to).
    /// </summary>
    public static readonly (string DocType, string EmployeeField, bool HasViewer)[] Targets =
        [("TalentReview", "employee", true), ("SuccessionPlan", "incumbent_employee", true), ("DevelopmentPlan", "employee", false)];

    public static async Task<bool> ManagerAccessAsync(Masters m, CancellationToken ct) =>
        (await m.SingleAsync("HRSettings", ct)).Bool("allow_manager_talent_access");

    /// <summary>Sets <c>reports_to</c> and <c>manager_viewer</c> on a record being saved.</summary>
    public static async Task ApplyAsync(Masters m, IDictionary<string, object?> d, string employeeField, CancellationToken ct, bool hasViewer = true)
    {
        var emp = d.Str(employeeField);
        var boss = string.IsNullOrEmpty(emp) ? null : await m.StrAsync("Employee", emp, "reports_to", ct);
        d["reports_to"] = boss;
        if (hasViewer) d["manager_viewer"] = boss is not null && await ManagerAccessAsync(m, ct) ? boss : null;
    }

    /// <summary>Rewrites <c>reports_to</c> / <c>manager_viewer</c> where they differ from the current rule. Returns the number of records changed.</summary>
    /// <param name="allowOverride">The setting as just saved (AfterSave runs before the HR Settings commit); null = read it.</param>
    public static async Task<int> RefreshAsync(IDocumentStore store, string tenant, DateTimeOffset now, CancellationToken ct, bool? allowOverride = null)
    {
        var m = new Masters(store, tenant);
        var allow = allowOverride ?? await ManagerAccessAsync(m, ct);
        var changed = 0;
        foreach (var (docType, employeeField, hasViewer) in Targets)
        {
            foreach (var doc in await m.QueryAsync(docType, [], ct, take: 20000))
            {
                var emp = doc.Data.Str(employeeField);
                var boss = string.IsNullOrEmpty(emp) ? null : await m.StrAsync("Employee", emp, "reports_to", ct);
                var viewer = allow ? boss : null;
                if (doc.Data.Str("reports_to") == boss && (!hasViewer || doc.Data.Str("manager_viewer") == viewer)) continue;

                var clone = doc.Clone();
                clone.Data["reports_to"] = boss;
                if (hasViewer) clone.Data["manager_viewer"] = viewer;
                clone.Version = doc.Version + 1;
                clone.Modified = now;
                clone.ModifiedBy = "system";
                var uow = new UnitOfWork();
                uow.Upserts.Add((clone, doc.Version));
                try { await store.CommitAsync(uow, ct); changed++; }
                catch (ConcurrencyException) { /* edited meanwhile: its own save already applied the rule */ }
            }
        }
        return changed;
    }
}

/// <summary>HR Settings: turning manager access to talent records on or off updates existing records at once.</summary>
public sealed class HRSettingsTalentAccessHook(IDocumentStore store, TimeProvider? clock = null) : IAfterSave<Ess.HRSettings>
{
    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Previous is not null && c.Previous.Data.Bool("allow_manager_talent_access") == c.Doc.Data.Bool("allow_manager_talent_access")) return;
        await TalentAccess.RefreshAsync(store, c.Doc.TenantId, (clock ?? TimeProvider.System).GetUtcNow(), ct,
            c.Doc.Data.Bool("allow_manager_talent_access"));
    }
}

/// <summary>Daily: keeps manager access to talent records in step with reporting lines (job changes, resignations).</summary>
public sealed class TalentAccessRefreshJob : IScheduledJob
{
    public string Name => "Erp.TalentAccessRefresh";
    public string Schedule => "@daily";

    public Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct) =>
        TalentAccess.RefreshAsync(context.Services.GetRequiredService<IDocumentStore>(), context.TenantId, DateTimeOffset.UtcNow, ct);
}

/// <summary>Job Profile: isi nama dan kategori kompetensi di child table JobProfileCompetency.</summary>
public sealed class JobProfileHook(IDocumentStore store) : IBeforeValidate<JobProfile>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        foreach (var row in c.Doc.Data.Rows("competencies"))
        {
            var code = row.Str("competency");
            if (string.IsNullOrEmpty(code)) continue;
            var comp = await m.GetAsync("Competency", code, ct);
            if (comp is null) continue;
            row["competency_name"] = comp.Data.Str("competency_name");
            row["category"] = comp.Data.Str("category");
        }
    }
}

/// <summary>
/// Appraisal: satu per (karyawan, siklus); karyawan hanya mengubah nilai diri, atasan hanya nilai atasan (HR boleh keduanya);
/// rating dari skor akhir; skor kompetensi rata-rata dari nilai atasan dan disinkronkan ke profil Employee saat selesai.
/// </summary>
public sealed class AppraisalHook(IDocumentStore store, IServiceProvider? services = null, TimeProvider? clock = null)
    : IBeforeValidate<Appraisal>, IBeforeTransition<Appraisal>, IAfterSave<Appraisal>
{
    public static string Rating(decimal score) => score switch
    {
        >= 4.5m => "Outstanding", >= 3.5m => "Exceeds expectations", >= 2.5m => "Meets expectations", >= 1.5m => "Needs improvement", _ => "Unsatisfactory",
    };

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var dup = (await m.QueryAsync("Appraisal", [new FilterClause("employee", "=", d.Str("employee")), new FilterClause("appraisal_cycle", "=", d.Str("appraisal_cycle"))], ct, take: 10))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled && x.Name != d.Str("amended_from"));
        if (dup is not null) throw Errors.One("appraisal_cycle", $"{d.Str("employee")} already has appraisal {dup.Name} in this cycle");

        var isHr = c.User.HasRole(ErpRoles.HrTalent);
        var me = c.User.Attr("employee");
        if (!isHr && c.Previous is not null)
        {
            static string Sig(IEnumerable<Dictionary<string, object?>> rows, string key, string f) =>
                string.Join("|", rows.Select(r => $"{r.Str(key)}={r.Dec(f)}"));

            var beforeGoals = c.Previous.Data.Rows("goals");
            var afterGoals = d.Rows("goals");
            if (me == d.Str("employee") && Sig(beforeGoals, "goal", "manager_score") != Sig(afterGoals, "goal", "manager_score"))
                throw Errors.One("goals", "Only the reviewer fills the manager scores");
            if (me == d.Str("reviewer") && me != d.Str("employee") && Sig(beforeGoals, "goal", "self_score") != Sig(afterGoals, "goal", "self_score"))
                throw Errors.One("goals", "Only the employee fills the self scores");

            var beforeComp = c.Previous.Data.Rows("competencies");
            var afterComp = d.Rows("competencies");
            if (me == d.Str("employee") && Sig(beforeComp, "competency", "manager_score") != Sig(afterComp, "competency", "manager_score"))
                throw Errors.One("competencies", "Only the reviewer fills the manager competency scores");
            if (me == d.Str("reviewer") && me != d.Str("employee") && Sig(beforeComp, "competency", "self_score") != Sig(afterComp, "competency", "self_score"))
                throw Errors.One("competencies", "Only the employee fills the self competency scores");
        }

        if (c.Previous is null && !d.Rows("competencies").Any() && d.Str("employee") is { } empId && !string.IsNullOrEmpty(empId))
        {
            var jobProfileId = await m.StrAsync("Employee", empId, "job_profile", ct);
            if (!string.IsNullOrEmpty(jobProfileId) && await m.GetAsync("JobProfile", jobProfileId, ct) is { } jp)
            {
                var prefill = new List<Dictionary<string, object?>>();
                foreach (var jpc in jp.Data.Rows("competencies"))
                {
                    var code = jpc.Str("competency");
                    if (string.IsNullOrEmpty(code)) continue;
                    prefill.Add(new Dictionary<string, object?>
                    {
                        ["competency"] = code,
                        ["competency_name"] = jpc.Str("competency_name") ?? await m.StrAsync("Competency", code, "competency_name", ct),
                        ["required_level"] = (long)jpc.Dec("required_level"),
                    });
                }
                if (prefill.Count > 0) d["competencies"] = prefill;
            }
        }

        foreach (var row in d.Rows("competencies"))
        {
            var code = row.Str("competency");
            if (!string.IsNullOrEmpty(code))
                row["competency_name"] = await m.StrAsync("Competency", code, "competency_name", ct);
        }

        var score = d.Rows("goals").Sum(r => r.Dec("weight") * r.Dec("manager_score") / 100m);
        d["rating"] = d.Rows("goals").Any(r => r.Dec("manager_score") > 0) ? Rating(score) : null;

        var compScored = d.Rows("competencies").Where(r => r.Dec("manager_score") > 0).ToList();
        d["competency_score"] = compScored.Count > 0 ? Math.Round(compScored.Average(r => r.Dec("manager_score")), 2) : null;
    }

    public async Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        if (c.ToState != "Completed") return;
        var d = c.Doc.Data;
        if (d.Rows("goals").Any(r => r.Dec("manager_score") <= 0))
            throw Errors.One("goals", "Fill the manager score for every goal before completing");
        if (d.Rows("competencies").Any(r => r.Dec("manager_score") <= 0))
            throw Errors.One("competencies", "Fill the manager score for every competency before completing");
        var status = await new Masters(store, c.Doc.TenantId).StrAsync("AppraisalCycle", d.Str("appraisal_cycle"), "status", ct);
        if (status != "In progress")
            throw Errors.One("appraisal_cycle", $"The appraisal cycle is {status}; appraisals can be completed only while it is In progress");
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState != "Completed" || c.Previous?.WorkflowState == "Completed") return;
        var compRows = c.Doc.Data.Rows("competencies").Where(r => r.Str("competency") is not null && r.Dec("manager_score") > 0).ToList();
        if (compRows.Count == 0) return;
        var empId = c.Doc.Data.Str("employee");
        var docs = services?.GetService<DocumentService>();
        if (string.IsNullOrEmpty(empId) || docs is null) return;

        var sys = new SystemDocs(docs, c.Doc.TenantId);
        var emp = await sys.TryGetAsync("Employee", empId, ct);
        if (emp is null) return;

        var today = D.Iso(Hr.Today(clock ?? TimeProvider.System));
        var existing = emp.Data.Rows("competencies").Select(r => new Dictionary<string, object?>(r)).ToList();
        foreach (var row in compRows)
        {
            var code = row.Str("competency")!;
            var target = existing.FirstOrDefault(r => r.Str("competency") == code);
            if (target is null)
            {
                target = new Dictionary<string, object?> { ["competency"] = code };
                existing.Add(target);
            }
            target["competency_name"] = row.Str("competency_name");
            target["proficiency_level"] = (long)row.Dec("manager_score");
            target["last_assessed_on"] = today;
        }
        await docs.UpdateAsync(sys.User, "Employee", empId, emp.Version,
            new Dictionary<string, object?> { ["competencies"] = existing }, ct);
    }
}

/// <summary>
/// Appraisal → Job Change: opens a new Job Change form prefilled with the employee, reason Promotion, today's date and
/// the appraisal result. HR fills the new grade/position or salary and saves; nothing is created until then, because a
/// Job Change needs at least one change (rule has_change).
/// </summary>
public sealed class AppraisalCreateJobChangeAction(TimeProvider clock) : IFormAction<Appraisal>
{
    public string ActionId => "erp.appraisal.create_job_change";
    public string Label => "Propose Job Change";
    public string Icon => "arrow-up-right";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.WorkflowState == "Completed"
        && (user.IsAdmin || user.HasRole(ErpRoles.HrTalent) || user.HasRole(ErpRoles.HrEmployeeManagement));

    public Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var d = ctx.Doc.Data;
        if (d.Str("employee") is not { Length: > 0 } empId) return Task.FromResult(FormActionResult.Fail("Employee is required."));
        return Task.FromResult(FormActionResult.NavigateTo(PrefillUrl(ctx.Doc, empId, Hr.Today(clock)), "Fill in the new grade, position or salary, then save."));
    }

    public static string PrefillUrl(Document appraisal, string employee, DateOnly today)
    {
        var d = appraisal.Data;
        var remarks = $"Proposed from Appraisal {appraisal.Name} (rating {d.Str("rating") ?? "-"}, score {d.Dec("final_score"):0.##})";
        static string Q(string k, string v) => $"prefill.{k}={Uri.EscapeDataString(v)}";
        return "/app/JobChange/new?" + string.Join("&",
            Q("employee", employee), Q("change_reason", "Promotion"), Q("effective_date", D.Iso(today)), Q("remarks", remarks));
    }
}

/// <summary>Talent Review: satu per (karyawan, siklus); hitung posisi 9-Box Grid dari Performance vs Potential.</summary>
public sealed class TalentReviewHook(IDocumentStore store) : IBeforeValidate<TalentReview>
{
    private static bool NineBoxInputsChanged(IDictionary<string, object?> before, IDictionary<string, object?> after) =>
        new[] { "aspiration_score", "learning_agility_score", "leadership_score" }.Any(f => before.Dec(f) != after.Dec(f));

    public static string NineBox(string? performance, string? potential) => (performance, potential) switch
    {
        ("High", "High") => "1A - Star / Future Leader",
        ("Moderate", "High") => "1B - High Potential",
        ("Low", "High") => "1C - Rough Diamond",
        ("High", "Moderate") => "2A - High Performer",
        ("Moderate", "Moderate") => "2B - Core Player",
        ("Low", "Moderate") => "2C - Inconsistent",
        ("High", "Low") => "3A - Trusted Professional",
        ("Moderate", "Low") => "3B - Effective",
        ("Low", "Low") => "3C - Underperformer",
        _ => "2B - Core Player"
    };

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var empId = d.Str("employee");
        await TalentAccess.ApplyAsync(m, d, "employee", ct);

        var dup = (await m.QueryAsync("TalentReview", [new FilterClause("employee", "=", empId), new FilterClause("appraisal_cycle", "=", d.Str("appraisal_cycle"))], ct, take: 10))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled && x.Name != d.Str("amended_from"));
        if (dup is not null) throw Errors.One("appraisal_cycle", $"{empId} already has talent review {dup.Name} in this cycle");

        var appraisalId = d.Str("appraisal");
        if (!string.IsNullOrEmpty(appraisalId) && (c.Previous is null || c.Previous.Data.Str("appraisal") != appraisalId))
        {
            var apr = await m.GetAsync("Appraisal", appraisalId, ct);
            if (apr is not null)
            {
                if (apr.Data.Str("employee") != empId)
                    throw Errors.One("appraisal", $"Appraisal {appraisalId} belongs to a different employee");
                if (apr.WorkflowState != "Completed" && apr.DocStatus != DocStatus.Submitted)
                    throw Errors.One("appraisal", $"Appraisal {appraisalId} must be Completed before linking to a Talent Review");
                if (apr.Data.Str("appraisal_cycle") != d.Str("appraisal_cycle"))
                    throw Errors.One("appraisal", $"Appraisal {appraisalId} belongs to a different appraisal cycle");
                if (apr.Data.Str("rating") is not null)
                {
                    var hr = await m.SingleAsync("HRSettings", ct);
                    var highThresh = hr.Dec("talent_high_perf_threshold");
                    if (highThresh <= 0m) highThresh = 3.75m;
                    var modThresh = hr.Dec("talent_mod_perf_threshold");
                    if (modThresh <= 0m) modThresh = 2.5m;
                    var score = apr.Data.Dec("final_score");
                    d["performance_level"] = score >= highThresh ? "High" : score >= modThresh ? "Moderate" : "Low";
                }
            }
        }

        if (!string.IsNullOrEmpty(d.Str("calibration_session")))
        {
            var cal = await m.RequireAsync("CalibrationSession", d.Str("calibration_session"), "calibration_session", "Calibration session", ct);
            var linkedNow = c.Previous?.Data.Str("calibration_session") != cal.Name;
            if (linkedNow)
            {
                if (cal.DocStatus == DocStatus.Cancelled)
                    throw Errors.One("calibration_session", $"Calibration session {cal.Name} is cancelled");
                if (cal.DocStatus == DocStatus.Submitted)
                    throw Errors.One("calibration_session", $"Calibration session {cal.Name} is finalized; reviews cannot be added to it");
                if (cal.Data.Str("appraisal_cycle") != d.Str("appraisal_cycle"))
                    throw Errors.One("calibration_session", $"Calibration session {cal.Name} is for another appraisal cycle");
                if (cal.Data.Str("organization_unit") is { } calUnit
                    && !(await Announcements.UnitChainAsync(m, await m.StrAsync("Employee", empId, "organization_unit", ct), ct)).Contains(calUnit))
                    throw Errors.One("calibration_session", $"{empId} is not in unit {calUnit} of calibration session {cal.Name}");
            }
            else if (cal.DocStatus == DocStatus.Submitted
                && (c.Previous!.Data.Str("performance_level") != d.Str("performance_level")
                    || c.Previous.Data.Str("potential_level") != d.Str("potential_level")
                    || NineBoxInputsChanged(c.Previous.Data, d)))
                throw Errors.One("calibration_session", $"Calibration session {cal.Name} is finalized; 9-box levels are locked");
        }
        else if (c.Previous?.Data.Str("calibration_session") is { } oldCal
            && (await m.GetAsync("CalibrationSession", oldCal, ct))?.DocStatus == DocStatus.Submitted)
            throw Errors.One("calibration_session", $"Calibration session {oldCal} is finalized; the review cannot be removed from it");

        var potScores = new[] { d.Dec("aspiration_score"), d.Dec("learning_agility_score"), d.Dec("leadership_score") }.Where(s => s > 0m).ToList();
        if (potScores.Count > 0)
        {
            var avgPot = potScores.Average();
            d["potential_level"] = avgPot >= 3.75m ? "High" : avgPot >= 2.5m ? "Moderate" : "Low";
        }

        d["nine_box_position"] = NineBox(d.Str("performance_level"), d.Str("potential_level"));
    }
}

/// <summary>Succession Plan: satu rencana aktif per posisi; kandidat unik dan bukan pemegang jabatan saat ini.</summary>
public sealed class SuccessionPlanHook(IDocumentStore store) : IBeforeValidate<SuccessionPlan>, IOnLoad<SuccessionPlan>
{
    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        foreach (var r in c.Doc.Data.Rows("candidates"))
        {
            var emp = r.Str("employee");
            if (string.IsNullOrEmpty(emp)) continue;
            var latestApr = (await m.QueryAsync("Appraisal", [new FilterClause("employee", "=", emp)], ct, "modified", "desc", 5))
                .FirstOrDefault(x => x.DocStatus == DocStatus.Submitted);
            r["latest_rating"] = latestApr?.Data.Str("rating");
            var latestReview = (await m.QueryAsync("TalentReview", [new FilterClause("employee", "=", emp)], ct, "modified", "desc", 5))
                .FirstOrDefault(x => x.DocStatus == DocStatus.Submitted);
            r["nine_box_position"] = latestReview?.Data.Str("nine_box_position");
        }
    }

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var pos = d.Str("position");
        if (d.Str("status") == "Active" && !string.IsNullOrEmpty(pos))
        {
            var dup = (await m.QueryAsync("SuccessionPlan", [new FilterClause("position", "=", pos), new FilterClause("status", "=", "Active")], ct, take: 5))
                .FirstOrDefault(x => x.Name != c.Doc.Name);
            if (dup is not null) throw Errors.One("position", $"Position {pos} already has active succession plan {dup.Name}");
        }

        if (string.IsNullOrEmpty(d.Str("incumbent_employee")) && !string.IsNullOrEmpty(pos))
            d["incumbent_employee"] = (await Hr.HolderAsync(m, pos, null, ct))?.Name;

        var incumbent = d.Str("incumbent_employee");
        await TalentAccess.ApplyAsync(m, d, "incumbent_employee", ct);

        var rows = d.Rows("candidates");
        foreach (var g in rows.GroupBy(r => r.Str("employee")).Where(g => g.Key is not null && g.Count() > 1))
            throw Errors.One("candidates", $"{g.Key} is listed more than once");

        foreach (var r in rows)
        {
            var emp = r.Str("employee");
            if (string.IsNullOrEmpty(emp)) continue;
            if (emp == incumbent)
                throw Errors.One("candidates", $"Current incumbent {emp} cannot be listed as a succession candidate");
            var empDoc = await m.GetAsync("Employee", emp, ct);
            if (empDoc?.Data.Str("status") == Hr.Left)
                throw Errors.One("candidates", $"Candidate {emp} has already left the company");
            r["employee_name"] = empDoc?.Data.Str("full_name");

            var latestApr = (await m.QueryAsync("Appraisal", [new FilterClause("employee", "=", emp)], ct, "modified", "desc", 5))
                .FirstOrDefault(x => x.DocStatus == DocStatus.Submitted);
            r["latest_rating"] = latestApr?.Data.Str("rating");

            var latestReview = (await m.QueryAsync("TalentReview", [new FilterClause("employee", "=", emp)], ct, "modified", "desc", 5))
                .FirstOrDefault(x => x.DocStatus == DocStatus.Submitted);
            r["nine_box_position"] = latestReview?.Data.Str("nine_box_position");
        }
    }
}

/// <summary>Development Plan (IDP): hitung progres rata-rata sasaran 70-20-10 dan validasi kelengkapan saat Completed.</summary>
public sealed class DevelopmentPlanHook(IDocumentStore store) : IBeforeValidate<DevelopmentPlan>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        await TalentAccess.ApplyAsync(new Masters(store, c.Doc.TenantId), d, "employee", ct, hasViewer: false);
        var isHrOrAdmin = c.User.IsAdmin || c.User.HasRole(ErpRoles.HrTalent);
        if (c.Previous?.Data.Str("status") == "Completed" && !isHrOrAdmin)
            throw Errors.One("status", "Completed development plans are locked and can only be modified by HR-Talent");

        var goals = d.Rows("goals").ToList();
        d["overall_progress"] = goals.Count > 0 ? Math.Round(goals.Average(g => g.Dec("progress_percent")), 1) : 0m;

        if (d.Str("status") == "Completed")
        {
            if (!isHrOrAdmin)
            {
                var me = c.User.Attr("employee");
                var isApprover = !string.IsNullOrEmpty(me) && (me == d.Str("mentor") || me == d.Str("reports_to"));
                if (!isApprover)
                    throw Errors.One("status", "Only the mentor, manager, or HR-Talent can mark a development plan Completed");
            }
            if (goals.Count == 0)
                throw Errors.One("goals", "Add at least one development goal before completing");
            if (goals.Any(g => g.Str("status") != "Completed" && g.Dec("progress_percent") < 100m))
                throw Errors.One("status", "All development goals must be Completed before marking the plan Completed");
        }
    }
}

/// <summary>Training Request: isi approver dari atasan langsung, cek anggaran, dan daftarkan ke Training Event saat Approved.</summary>
public sealed class TrainingRequestHook(IDocumentStore store, IServiceProvider? services = null, TimeProvider? clock = null)
    : IBeforeValidate<TrainingRequest>, IBeforeTransition<TrainingRequest>, IAfterSave<TrainingRequest>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (string.IsNullOrEmpty(d.Str("approver")) && d.Str("employee") is { } empId)
            d["approver"] = await m.StrAsync("Employee", empId, "reports_to", ct);

        var progId = d.Str("training_program");
        if (!string.IsNullOrEmpty(progId))
        {
            var prog = await m.GetAsync("TrainingProgram", progId, ct);
            if (prog is not null)
            {
                if (string.IsNullOrEmpty(d.Str("training_title")) || c.Previous?.Data.Str("training_program") != progId)
                    d["training_title"] = prog.Data.Str("program_name");
                if (c.Previous is null || c.Previous.Data.Str("training_program") != progId)
                {
                    if (prog.Data.Str("training_type") is { } tt) d["training_type"] = tt;
                    if (d.Dec("estimated_cost") == 0m && prog.Data.Dec("estimated_cost") > 0m)
                        d["estimated_cost"] = prog.Data.Dec("estimated_cost");
                }
            }
        }
    }

    public async Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        if (c.ToState != "Approved") return;
        var m = new Masters(store, c.Doc.TenantId);
        var budget = (await m.SingleAsync("HRSettings", ct)).Dec("annual_training_budget_per_unit");
        if (budget <= 0m) return;

        var empId = c.Doc.Data.Str("employee");
        var unit = !string.IsNullOrEmpty(empId) ? await m.StrAsync("Employee", empId, "organization_unit", ct) : null;
        if (string.IsNullOrEmpty(unit)) return;

        var year = Hr.Today(clock ?? TimeProvider.System).Year;
        var unitEmps = (await m.QueryAsync("Employee", [new FilterClause("organization_unit", "=", unit)], ct, take: 5000))
            .Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var spent = (await m.QueryAsync("TrainingRequest", [], ct, take: 5000))
            .Where(r => r.Name != c.Doc.Name && r.WorkflowState == "Approved" && r.Created.Year == year
                && r.Data.Str("employee") is { } re && unitEmps.Contains(re))
            .Sum(r => r.Data.Dec("estimated_cost"));
        var requested = c.Doc.Data.Dec("estimated_cost");
        if (spent + requested > budget)
            throw Errors.One("estimated_cost", $"Annual training budget for {unit} exceeded ({spent + requested:N0} > {budget:N0})");
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState != "Approved" || c.Previous?.WorkflowState == "Approved") return;
        var docs = services?.GetService<DocumentService>();
        var progId = c.Doc.Data.Str("training_program");
        var empId = c.Doc.Data.Str("employee");
        if (docs is null || string.IsNullOrEmpty(progId) || string.IsNullOrEmpty(empId)) return;

        var m = new Masters(store, c.Doc.TenantId);
        var sys = new SystemDocs(docs, c.Doc.TenantId);
        var openEvent = (await m.QueryAsync("TrainingEvent", [new FilterClause("training_program", "=", progId)], ct, "modified", "desc", 10))
            .FirstOrDefault(e => e.DocStatus == DocStatus.Draft);

        if (openEvent is not null)
        {
            var attendees = openEvent.Data.Rows("attendees").Select(r => new Dictionary<string, object?>(r)).ToList();
            if (attendees.All(a => a.Str("employee") != empId))
            {
                attendees.Add(new Dictionary<string, object?> { ["employee"] = empId, ["attendance"] = "Invited" });
                await docs.UpdateAsync(sys.User, "TrainingEvent", openEvent.Name, openEvent.Version,
                    new Dictionary<string, object?> { ["attendees"] = attendees }, ct);
            }
        }
        else
        {
            var start = (c.Doc.Data.Date("preferred_date") ?? Hr.Today(clock ?? TimeProvider.System).AddDays(7));
            var company = await m.StrAsync("Employee", empId, "company", ct);
            var payload = new Dictionary<string, object?>
            {
                ["training_program"] = progId,
                ["event_name"] = c.Doc.Data.Str("training_title") ?? progId,
                ["start_at"] = $"{D.Iso(start)}T09:00:00",
                ["end_at"] = $"{D.Iso(start)}T17:00:00",
                ["cost"] = c.Doc.Data.Dec("estimated_cost"),
                ["attendees"] = new List<Dictionary<string, object?>>
                {
                    new() { ["employee"] = empId, ["attendance"] = "Invited" },
                },
            };
            if (!string.IsNullOrEmpty(company)) payload["company"] = company;
            try { await docs.InsertAsync(sys.User, "TrainingEvent", payload, ct); }
            catch (ValidationFailedException) { /* skip auto-event if required master is absent */ }
        }
    }
}

/// <summary>Pelatihan: peserta unik, nama peserta, jumlah hadir, auto-fill dari Training Program, dan sinkron kompetensi saat selesai.</summary>
public sealed class TrainingEventHook(IDocumentStore store, IServiceProvider? services = null, TimeProvider? clock = null)
    : IBeforeValidate<TrainingEvent>, IAfterSave<TrainingEvent>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var progId = d.Str("training_program");
        if (!string.IsNullOrEmpty(progId) && (c.Previous is null || c.Previous.Data.Str("training_program") != progId))
        {
            var prog = await m.GetAsync("TrainingProgram", progId, ct);
            if (prog is not null)
            {
                if (string.IsNullOrEmpty(d.Str("event_name"))) d["event_name"] = prog.Data.Str("program_name");
                if (string.IsNullOrEmpty(d.Str("trainer"))) d["trainer"] = prog.Data.Str("provider");
                if (prog.Data.Str("training_type") is { } tt) d["training_type"] = tt;
            }
        }

        var rows = d.Rows("attendees");
        foreach (var g in rows.GroupBy(r => r.Str("employee")).Where(g => g.Key is not null && g.Count() > 1))
            throw Errors.One("attendees", $"{g.Key} is listed more than once");
        foreach (var r in rows) r["employee_name"] = await m.StrAsync("Employee", r.Str("employee"), "full_name", ct);
        d["attended_count"] = (long)rows.Count(r => r.Str("attendance") == "Attended");
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus == DocStatus.Submitted) return;
        var docs = services?.GetService<DocumentService>();
        var progId = c.Doc.Data.Str("training_program");
        if (docs is null || string.IsNullOrEmpty(progId)) return;

        var m = new Masters(store, c.Doc.TenantId);
        var targetComp = await m.StrAsync("TrainingProgram", progId, "target_competency", ct);
        if (string.IsNullOrEmpty(targetComp)) return;
        var compName = await m.StrAsync("Competency", targetComp, "competency_name", ct);
        var sys = new SystemDocs(docs, c.Doc.TenantId);
        var today = D.Iso(Hr.Today(clock ?? TimeProvider.System));

        foreach (var row in c.Doc.Data.Rows("attendees"))
        {
            if (row.Str("attendance") != "Attended") continue;
            var score = row.Dec("score");
            if (score > 0m && score < 70m) continue;
            var empId = row.Str("employee");
            if (string.IsNullOrEmpty(empId)) continue;
            var emp = await sys.TryGetAsync("Employee", empId, ct);
            if (emp is null) continue;

            var comps = emp.Data.Rows("competencies").Select(r => new Dictionary<string, object?>(r)).ToList();
            var existing = comps.FirstOrDefault(r => r.Str("competency") == targetComp);
            if (existing is null)
            {
                comps.Add(new Dictionary<string, object?>
                {
                    ["competency"] = targetComp,
                    ["competency_name"] = compName,
                    ["proficiency_level"] = 3L,
                    ["last_assessed_on"] = today,
                    ["notes"] = $"Completed training {c.Doc.Name}",
                });
            }
            else
            {
                var cur = (long)existing.Dec("proficiency_level");
                existing["proficiency_level"] = Math.Max(cur, 3L);
                existing["last_assessed_on"] = today;
            }
            await docs.UpdateAsync(sys.User, "Employee", empId, emp.Version,
                new Dictionary<string, object?> { ["competencies"] = comps }, ct);
        }
    }
}
