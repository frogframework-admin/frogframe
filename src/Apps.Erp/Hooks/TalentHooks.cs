using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

public sealed class Appraisal { }
public sealed class TalentReview { }
public sealed class SuccessionPlan { }
public sealed class DevelopmentPlan { }
public sealed class TrainingRequest { }
public sealed class TrainingEvent { }

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

/// <summary>Talent Review: satu per (karyawan, siklus); hitung posisi 9-Box Grid dari Performance vs Potential.</summary>
public sealed class TalentReviewHook(IDocumentStore store) : IBeforeValidate<TalentReview>
{
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
        var dup = (await m.QueryAsync("TalentReview", [new FilterClause("employee", "=", d.Str("employee")), new FilterClause("appraisal_cycle", "=", d.Str("appraisal_cycle"))], ct, take: 10))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled && x.Name != d.Str("amended_from"));
        if (dup is not null) throw Errors.One("appraisal_cycle", $"{d.Str("employee")} already has talent review {dup.Name} in this cycle");

        var appraisalId = d.Str("appraisal");
        if (!string.IsNullOrEmpty(appraisalId) && (c.Previous is null || c.Previous.Data.Str("appraisal") != appraisalId))
        {
            var apr = await m.GetAsync("Appraisal", appraisalId, ct);
            if (apr is not null)
            {
                if (apr.Data.Str("employee") != d.Str("employee"))
                    throw Errors.One("appraisal", $"Appraisal {appraisalId} belongs to a different employee");
                if (apr.Data.Str("rating") is not null)
                {
                    var score = apr.Data.Dec("final_score");
                    d["performance_level"] = score >= 3.75m ? "High" : score >= 2.5m ? "Moderate" : "Low";
                }
            }
        }

        d["nine_box_position"] = NineBox(d.Str("performance_level"), d.Str("potential_level"));
    }
}

/// <summary>Succession Plan: satu rencana aktif per posisi; kandidat unik dan bukan pemegang jabatan saat ini.</summary>
public sealed class SuccessionPlanHook(IDocumentStore store) : IBeforeValidate<SuccessionPlan>
{
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

        var incumbent = d.Str("incumbent_employee");
        var rows = d.Rows("candidates");
        foreach (var g in rows.GroupBy(r => r.Str("employee")).Where(g => g.Key is not null && g.Count() > 1))
            throw Errors.One("candidates", $"{g.Key} is listed more than once");

        foreach (var r in rows)
        {
            var emp = r.Str("employee");
            if (string.IsNullOrEmpty(emp)) continue;
            if (emp == incumbent)
                throw Errors.One("candidates", $"Current incumbent {emp} cannot be listed as a succession candidate");

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
public sealed class DevelopmentPlanHook : IBeforeValidate<DevelopmentPlan>
{
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var goals = d.Rows("goals").ToList();
        d["overall_progress"] = goals.Count > 0 ? Math.Round(goals.Average(g => g.Dec("progress_percent")), 1) : 0m;

        if (d.Str("status") == "Completed")
        {
            if (goals.Count == 0)
                throw Errors.One("goals", "Add at least one development goal before completing");
            if (goals.Any(g => g.Str("status") != "Completed" && g.Dec("progress_percent") < 100m))
                throw Errors.One("status", "All development goals must be Completed before marking the plan Completed");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Training Request: isi approver dari atasan langsung dan lengkapi detail dari katalog Training Program.</summary>
public sealed class TrainingRequestHook(IDocumentStore store) : IBeforeValidate<TrainingRequest>
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
}

/// <summary>Pelatihan: peserta unik, nama peserta, jumlah hadir, dan auto-fill dari Training Program.</summary>
public sealed class TrainingEventHook(IDocumentStore store) : IBeforeValidate<TrainingEvent>
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
}
