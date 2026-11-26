using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>
/// Compensation budget per worksheet with its unit: budget, planned, remaining and use for merit and bonus, lines
/// outside the guideline and the approval state. Uses the reader's permissions on Compensation Worksheet, so a manager
/// sees only their own worksheets and HR-Comben / HR-Payroll see all. The unit filter includes the units below.
/// </summary>
public sealed class CompensationBudgetReport(IDocumentStore store) : IReportProvider
{
    public string Name => "erp-compensation-budget";
    public string Title => "Compensation Budget by Unit";
    public string Description => "Merit and bonus budget, planned and remaining per manager worksheet and unit.";
    public string DocType => "CompensationWorksheet";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("compensation_cycle", "Compensation cycle", "Data"),
        new("organization_unit", "Organization unit (with units below)", "Data"),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var cycle = Rpt.Opt(f, "compensation_cycle");
        var unit = Rpt.Opt(f, "organization_unit");
        var sheets = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(s => cycle is null || s.Data.Str("compensation_cycle") == cycle).ToList();
        if (unit is not null)
        {
            var m = new Masters(store, user.TenantId);
            var kept = new List<Document>();
            foreach (var s in sheets)
                if ((await Announcements.UnitChainAsync(m, s.Data.Str("organization_unit"), ct)).Contains(unit)) kept.Add(s);
            sheets = kept;
        }

        static decimal? Use(decimal planned, decimal budget) => budget > 0m ? Math.Round(100m * planned / budget, 1) : null;
        var rows = sheets
            .OrderBy(s => s.Data.Str("organization_unit"), StringComparer.CurrentCulture)
            .ThenBy(s => s.Data.Str("manager_name"), StringComparer.CurrentCulture)
            .Select(s =>
            {
                var d = s.Data;
                return Rpt.Row(
                    ("worksheet", s.Name), ("compensation_cycle", d.Str("compensation_cycle")), ("organization_unit", d.Str("organization_unit")),
                    ("manager_name", d.Str("manager_name")), ("state", s.WorkflowState ?? Comp.WsDraft), ("employees", d.Rows("lines").Count),
                    ("total_base", d.Dec("total_base")), ("merit_budget", d.Dec("merit_budget")), ("merit_planned", d.Dec("merit_planned")),
                    ("merit_remaining", d.Dec("merit_remaining")), ("merit_use", Use(d.Dec("merit_planned"), d.Dec("merit_budget"))),
                    ("bonus_budget", d.Dec("bonus_budget")), ("bonus_planned", d.Dec("bonus_planned")), ("bonus_remaining", d.Dec("bonus_remaining")),
                    ("outside_guideline", d.Rows("lines").Count(l => l.Bool("outside_guideline"))));
            }).ToList();

        return new ReportResult(
        [
            new("worksheet", "Worksheet"), new("compensation_cycle", "Cycle"), new("organization_unit", "Unit"), new("manager_name", "Manager"),
            new("state", "State"), new("employees", "Employees", "Int"), new("total_base", "Monthly base", "Currency"),
            new("merit_budget", "Merit budget", "Currency"), new("merit_planned", "Merit planned", "Currency"),
            new("merit_remaining", "Merit remaining", "Currency"), new("merit_use", "Merit used (%)", "Float"),
            new("bonus_budget", "Bonus budget", "Currency"), new("bonus_planned", "Bonus planned", "Currency"),
            new("bonus_remaining", "Bonus remaining", "Currency"), new("outside_guideline", "Outside guideline", "Int"),
        ], rows);
    }
}

/// <summary>
/// One row per employee of the approved worksheets: old and new base, merit %, bonus and the salary assignment created.
/// Payroll uses it to pay the bonuses (they are not posted to payroll automatically). Reader permissions as above.
/// </summary>
public sealed class CompensationAwardsReport : IReportProvider
{
    public string Name => "erp-compensation-awards";
    public string Title => "Compensation Awards";
    public string Description => "Approved merit increases and bonuses per employee, with the salary assignment created.";
    public string DocType => "CompensationWorksheet";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("compensation_cycle", "Compensation cycle", "Data")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var cycle = Rpt.Opt(f, "compensation_cycle");
        var rows = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(s => s.WorkflowState == Comp.WsApproved && (cycle is null || s.Data.Str("compensation_cycle") == cycle))
            .SelectMany(s => s.Data.Rows("lines").Select(l => Rpt.Row(
                ("compensation_cycle", s.Data.Str("compensation_cycle")), ("employee", l.Str("employee")), ("employee_name", l.Str("employee_name")),
                ("organization_unit", s.Data.Str("organization_unit")), ("rating", l.Str("rating")), ("current_base", l.Dec("current_base")),
                ("merit_percent", l.Dec("merit_percent")), ("new_base", l.Dec("new_base")), ("bonus_amount", l.Dec("bonus_amount")),
                ("effective_date", s.Data.Str("effective_date")), ("salary_assignment", l.Str("salary_assignment")))))
            .OrderBy(r => (string?)r["employee_name"], StringComparer.CurrentCulture)
            .ToList();

        return new ReportResult(
        [
            new("compensation_cycle", "Cycle"), new("employee", "Employee"), new("employee_name", "Name"), new("organization_unit", "Unit"),
            new("rating", "Rating"), new("current_base", "Current base", "Currency"), new("merit_percent", "Merit (%)", "Float"),
            new("new_base", "New base", "Currency"), new("bonus_amount", "Bonus", "Currency"), new("effective_date", "Effective", "Date"),
            new("salary_assignment", "Salary assignment"),
        ], rows);
    }
}
