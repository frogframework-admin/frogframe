using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Competency gap analysis: compares JobProfile required competencies against each Employee's current proficiency.</summary>
public sealed class CompetencyGapReport : IReportProvider
{
    public string Name => "erp-competency-gap";
    public string Title => "Competency Gap Analysis";
    public string Description => "Compares required competencies on each employee's Job Profile against their assessed Employee Competency levels.";
    public string DocType => "Employee";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("organization_unit", "Organization unit", "Data")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var unitFilter = f.TryGetValue("organization_unit", out var u) && !string.IsNullOrWhiteSpace(u) ? u.Trim() : null;
        var employees = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(e => e.Data.Str("status") != Hr.Left && (unitFilter is null || string.Equals(e.Data.Str("organization_unit"), unitFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        IReadOnlyList<Document> profiles;
        try { profiles = await documents.QueryForReportAsync(user, "JobProfile", [], ct: ct); }
        catch (PermissionDeniedException) { profiles = []; }
        var profileMap = profiles.ToDictionary(p => p.Name, StringComparer.Ordinal);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var emp in employees.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var jpId = emp.Data.Str("job_profile");
            if (string.IsNullOrEmpty(jpId) || !profileMap.TryGetValue(jpId, out var jp)) continue;

            var empComps = emp.Data.Rows("competencies")
                .Where(r => r.Str("competency") is not null)
                .ToDictionary(r => r.Str("competency")!, r => (int)r.Dec("proficiency_level"), StringComparer.Ordinal);

            foreach (var req in jp.Data.Rows("competencies"))
            {
                var code = req.Str("competency");
                if (string.IsNullOrEmpty(code)) continue;
                var required = (int)req.Dec("required_level");
                var has = empComps.TryGetValue(code, out var cur);
                var current = has ? cur : 0;
                var gap = current - required;
                var status = !has ? "Missing" : gap < 0 ? "Gap" : "Met";
                rows.Add(Rpt.Row(
                    ("employee", emp.Name),
                    ("employee_name", emp.Data.Str("full_name")),
                    ("organization_unit", emp.Data.Str("organization_unit")),
                    ("job_profile", jpId),
                    ("competency", code),
                    ("competency_name", req.Str("competency_name")),
                    ("required_level", required),
                    ("current_level", current),
                    ("gap", gap),
                    ("status", status)));
            }
        }

        return new ReportResult(
        [
            new("employee", "Employee"), new("employee_name", "Name"), new("organization_unit", "Unit"),
            new("job_profile", "Job profile"), new("competency", "Competency"), new("competency_name", "Competency name"),
            new("required_level", "Required (1–5)", "Int"), new("current_level", "Current (1–5)", "Int"),
            new("gap", "Gap", "Int"), new("status", "Status"),
        ], rows);
    }
}
