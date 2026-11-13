using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

public sealed class Appraisal { }
public sealed class TrainingEvent { }

/// <summary>
/// Appraisal: satu per (karyawan, siklus); karyawan hanya mengubah nilai diri, atasan hanya nilai atasan (HR boleh keduanya);
/// rating dari skor akhir. Penyelesaian hanya saat siklus In progress dan semua nilai atasan terisi.
/// </summary>
public sealed class AppraisalHook(IDocumentStore store) : IBeforeValidate<Appraisal>, IBeforeTransition<Appraisal>
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

        var isHr = c.User.HasRole("HR") || c.User.IsAdmin;
        var me = c.User.Attr("employee");
        if (!isHr && c.Previous is not null)
        {
            var before = c.Previous.Data.Rows("goals"); var after = d.Rows("goals");
            string Sig(IEnumerable<Dictionary<string, object?>> rows, string f) => string.Join("|", rows.Select(r => $"{r.Str("goal")}={r.Dec(f)}"));
            if (me == d.Str("employee") && Sig(before, "manager_score") != Sig(after, "manager_score"))
                throw Errors.One("goals", "Only the reviewer fills the manager scores");
            if (me == d.Str("reviewer") && me != d.Str("employee") && Sig(before, "self_score") != Sig(after, "self_score"))
                throw Errors.One("goals", "Only the employee fills the self scores");
        }
        var score = d.Rows("goals").Sum(r => r.Dec("weight") * r.Dec("manager_score") / 100m);
        d["rating"] = d.Rows("goals").Any(r => r.Dec("manager_score") > 0) ? Rating(score) : null;
    }

    public async Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        if (c.ToState != "Completed") return;
        var d = c.Doc.Data;
        if (d.Rows("goals").Any(r => r.Dec("manager_score") <= 0)) throw Errors.One("goals", "Fill the manager score for every goal before completing");
        var status = await new Masters(store, c.Doc.TenantId).StrAsync("AppraisalCycle", d.Str("appraisal_cycle"), "status", ct);
        if (status != "In progress") throw Errors.One("appraisal_cycle", $"The appraisal cycle is {status}; appraisals can be completed only while it is In progress");
    }
}

/// <summary>Pelatihan: peserta unik, nama peserta, dan jumlah hadir.</summary>
public sealed class TrainingEventHook(IDocumentStore store) : IBeforeValidate<TrainingEvent>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var rows = d.Rows("attendees");
        foreach (var g in rows.GroupBy(r => r.Str("employee")).Where(g => g.Key is not null && g.Count() > 1))
            throw Errors.One("attendees", $"{g.Key} is listed more than once");
        foreach (var r in rows) r["employee_name"] = await m.StrAsync("Employee", r.Str("employee"), "full_name", ct);
        d["attended_count"] = (long)rows.Count(r => r.Str("attendance") == "Attended");
    }
}
